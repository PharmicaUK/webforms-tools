using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
using WebForms.Designer;
using WebForms.LanguageServer.Completion;
using WebForms.LanguageServer.Protocol;
using WebForms.Markup;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using RoslynCompletionService = Microsoft.CodeAnalysis.Completion.CompletionService;
using RoslynCompletionTrigger = Microsoft.CodeAnalysis.Completion.CompletionTrigger;

namespace WebForms.LanguageServer.CodeBlocks;

/// <summary>
/// Full VB in markup code blocks (<c>&lt;% %&gt;</c>, <c>&lt;%= %&gt;</c>, <c>&lt;%: %&gt;</c>, <c>&lt;%# %&gt;</c>,
/// <c>&lt;script runat="server"&gt;</c>): each markup file is projected into a hidden VB document
/// (<see cref="Projection"/>) inside a Roslyn solution of its project (<see cref="CodeBlockProject"/>), and
/// completion, hover, definition, references and BC errors come from Roslyn, mapped back to the markup.
/// Not thread-safe: used from the server's handler loop only.
/// </summary>
public sealed partial class CodeBlockService(WebFormsWorkspace workspace)
{
    /// <summary>Above this many items (18,000 at the start of an expression in a large project) the list is filtered by the typed prefix and capped.</summary>
    public const int MaxItems = 1000;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a request in a block (completion, hover, definition, references) waits for the project's first build
    /// (~1 s for a 500-file project). Answering nothing meanwhile left blink's menu empty until the next key (Neovim test of 0.7.0).
    /// </summary>
    private static readonly TimeSpan BuildWait = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, CodeBlockProject> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (MarkupAnalysis Analysis, Projection? Projection)> _projections = new(StringComparer.OrdinalIgnoreCase);
    private (Document Document, ImmutableArray<RoslynCompletionItem> Items)? _lastCompletion;

    public bool Enabled { get; set; } = true;

    /// <summary>Called (on a background thread) when a project's compilation is ready: refresh diagnostics.</summary>
    public Action? ProjectReady { get; set; }

    public Action<string>? Log { get; set; }

    /// <summary>The projection of a markup file (cached per analysis); null for files without a code-behind class.</summary>
    public Projection? ProjectionOf(MarkupAnalysis analysis)
    {
        if (_projections.TryGetValue(analysis.Path, out var cached) && ReferenceEquals(cached.Analysis, analysis))
        {
            return cached.Projection;
        }

        Projection? projection = null;
        if (analysis.Result.SkipReason is null && BaseType(analysis) is { } baseType)
        {
            // A block being typed (no %> yet) swallows the markup after it: build from a copy where it is closed.
            var source = analysis;
            int repairAt = int.MaxValue;
            if (Projection.RepairPoint(analysis.Document.Text, analysis.Tokens) is { } cut)
            {
                repairAt = cut;
                var repaired = new SourceDocument(analysis.Path, analysis.Document.Text.Insert(cut, Projection.Repair), analysis.Document.Version);
                source = MarkupAnalysis.Create(repaired, analysis.Project, workspace.ScanCodeBehind);
            }

            projection = Projection.Build(source, baseType, analysis.Project.GetWebConfigNamespaces(analysis.Path), repairAt);
        }

        _projections[analysis.Path] = (analysis, projection);
        return projection;
    }

    /// <summary>Whether a markup position is inside the code of a block.</summary>
    public bool IsInBlock(string path, LspPosition position) =>
        workspace.Analyze(path) is { } analysis && ProjectionOf(analysis)?.ToProjection(analysis.Document.ToOffset(position)) is not null;

    /// <summary>BC errors inside the code blocks of a markup file; empty while its project is still compiling.</summary>
    public IReadOnlyList<LspDiagnostic> Diagnostics(string path)
    {
        if (Open(path) is not var (analysis, document, projection))
        {
            return [];
        }

        var model = document.GetSemanticModelAsync().GetAwaiter().GetResult()!;
        var baseType = model.Compilation.GetTypeByMetadataName(BaseType(analysis)!);
        if (baseType is null)
        {
            Log?.Invoke($"code blocks: {Path.GetFileName(path)}: base class {BaseType(analysis)} not found; no BC diagnostics");
            return [];
        }

        var results = new List<LspDiagnostic>();
        foreach (var d in model.GetDiagnostics())
        {
            if (d.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error || MapSpan(projection, d.Location.SourceSpan) is not var (start, length))
            {
                continue; // warnings, and errors in the scaffolding (reported by other means or not ours)
            }

            results.Add(new LspDiagnostic(analysis.Document.ToRange(start, length), Protocol.DiagnosticSeverity.Error, d.Id,
                MarkupDiagnostics.Source, d.GetMessage(CultureInfo.InvariantCulture)));
        }

        return [.. results.Distinct()];
    }

    /// <summary>
    /// Completion inside a code block. <paramref name="inBlock"/> says whether the position is in one at all (the
    /// markup completion must then stay out); the result is null there while the project is still compiling.
    /// </summary>
    public CompletionList? Complete(string path, LspPosition position, CompletionContext? context, ClientOptions client, out bool inBlock)
    {
        inBlock = false;
        if (!Enabled || workspace.Analyze(path) is not { } analysis || ProjectionOf(analysis) is not { } projection ||
            projection.ToProjection(analysis.Document.ToOffset(position)) is not { } offset)
        {
            return null;
        }

        inBlock = true;
        if (Open(path, BuildWait) is not var (_, document, _))
        {
            return null;
        }

        var service = RoslynCompletionService.GetService(document);
        if (service is null)
        {
            return null;
        }

        if (EvalFields(analysis, document, projection, offset, client) is { } fields)
        {
            return fields;
        }

        var text = document.GetTextAsync().GetAwaiter().GetResult();
        var trigger = RoslynCompletionTrigger.Invoke;
        if (context is { TriggerKind: 2, TriggerCharacter: [var c] })
        {
            trigger = RoslynCompletionTrigger.CreateInsertionTrigger(c);
            if (!service.ShouldTriggerCompletion(text, offset, trigger))
            {
                return null;
            }
        }

        var list = service.GetCompletionsAsync(document, offset, trigger).GetAwaiter().GetResult();
        if (list.ItemsList.Count == 0 || MapSpan(projection, list.Span) is not var (start, length))
        {
            return null;
        }

        var range = analysis.Document.ToRange(start, length);
        string typed = text.ToString(TextSpan.FromBounds(list.Span.Start, Math.Max(list.Span.Start, offset)));
        var items = list.ItemsList;
        IEnumerable<RoslynCompletionItem> chosen = items;
        bool incomplete = false;
        if (items.Count > MaxItems)
        {
            incomplete = true;
            chosen = items.Where(i => i.FilterText.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.SortText, StringComparer.OrdinalIgnoreCase)
                .Take(MaxItems);
        }

        var kept = chosen.ToImmutableArray();
        _lastCompletion = (document, kept);
        var result = new CompletionItem[kept.Length];
        for (int i = 0; i < kept.Length; i++)
        {
            var item = kept[i];
            string insert = item.Properties.TryGetValue("InsertionText", out var insertion) && insertion is { Length: > 0 } ? insertion : item.DisplayText;
            result[i] = new CompletionItem
            {
                Label = item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix,
                Kind = Kind(item.Tags),
                SortText = item.SortText,
                FilterText = item.FilterText,
                Detail = item.InlineDescription is { Length: > 0 } d ? d : null,
                TextEdit = client.EditRangeDefault ? null : new TextEdit(range, insert),
                TextEditText = client.EditRangeDefault ? insert : null,
                Data = -(i + 1),
            };
        }

        return new CompletionList(incomplete, result, client.EditRangeDefault ? new CompletionItemDefaults(range) : null);
    }

    /// <summary>
    /// Field names inside <c>Eval("|")</c>: <c>Eval</c> is late-bound (it reads the property of
    /// <c>Container.DataItem</c> by name at runtime), but when the template's data control is bound in the
    /// code-behind (<c>rptX.DataSource = list</c>) to a typed collection, its item type's properties are the
    /// candidates. Untyped sources (DataTable, SqlDataReader, Object) give nothing. Completion only: a control can be
    /// bound in several places, so an unknown name is never reported.
    /// </summary>
    /// <summary>
    /// The markup offset where the field name starts when the cursor is in the first (string) argument of <c>Eval</c>.
    /// A string being typed has no closing quote yet, and VB 14 strings span lines, so it runs on through the
    /// projection and the syntax tree no longer shows an argument: then the block's text before the cursor decides.
    /// </summary>
    private static int? EvalFieldStart(Document document, Projection projection, int offset, string markup, int markupOffset)
    {
        var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult()!;
        var token = root.FindToken(offset);
        if (token.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.StringLiteralToken) && offset > token.SpanStart &&
            token.Parent?.Parent is Microsoft.CodeAnalysis.VisualBasic.Syntax.SimpleArgumentSyntax argument &&
            argument.Parent is Microsoft.CodeAnalysis.VisualBasic.Syntax.ArgumentListSyntax { Arguments: [var first, ..] } args && first == argument &&
            args.Parent is Microsoft.CodeAnalysis.VisualBasic.Syntax.InvocationExpressionSyntax invocation)
        {
            return invocation.Expression.ToString().Split('.')[^1].Equals("Eval", StringComparison.OrdinalIgnoreCase) ? projection.ToMarkup(token.SpanStart + 1) : null;
        }

        foreach (var s in projection.Spans)
        {
            if (markupOffset >= s.MarkupStart && markupOffset <= s.MarkupStart + s.Length &&
                UnterminatedEval().Match(markup, s.MarkupStart, markupOffset - s.MarkupStart) is { Success: true } m)
            {
                return m.Groups["field"].Index;
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<![\w.])(Me\.)?Eval\(\s*""(?<field>[^""\r\n]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnterminatedEval();

    private static CompletionList? EvalFields(MarkupAnalysis analysis, Document document, Projection projection, int offset, ClientOptions client)
    {
        if (projection.ToMarkup(offset) is not { } markupOffset || EvalFieldStart(document, projection, offset, analysis.Document.Text, markupOffset) is not { } contentStart)
        {
            return null;
        }

        var types = DataSourceItemTypes(projection.Source, document.Project, projection.ToSource(markupOffset));
        if (types.Count == 0)
        {
            return null;
        }

        var range = analysis.Document.ToRange(contentStart, markupOffset - contentStart);
        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            {
                foreach (var member in t.GetMembers())
                {
                    var (memberType, kind) = member switch
                    {
                        IPropertySymbol { IsIndexer: false, IsStatic: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null } p => (p.Type, CompletionItemKind.Property),
                        IFieldSymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, IsImplicitlyDeclared: false } f => (f.Type, CompletionItemKind.Field),
                        _ => (null, 0),
                    };
                    if (memberType is null || !seen.Add(member.Name))
                    {
                        continue;
                    }

                    items.Add(new CompletionItem
                    {
                        Label = member.Name,
                        Kind = kind,
                        Detail = $"{(type.IsAnonymousType ? "anonymous type" : type.Name)}.{member.Name} As {memberType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}",
                        TextEdit = client.EditRangeDefault ? null : new TextEdit(range, member.Name),
                        TextEditText = client.EditRangeDefault ? member.Name : null,
                    });
                }
            }
        }

        return items.Count == 0 ? null : new CompletionList(false, [.. items], client.EditRangeDefault ? new CompletionItemDefaults(range) : null);
    }

    /// <summary>The item types of the typed collections the code-behind assigns to the DataSource of the data control whose template contains the offset.</summary>
    private static List<ITypeSymbol> DataSourceItemTypes(MarkupAnalysis analysis, Project project, int markupOffset)
    {
        MarkupElement? template = null;
        foreach (var el in analysis.Tree.AllElements())
        {
            if (el.Annotation is PropertyAnnotation { IsTemplate: true } && el.EndTag is { } end && markupOffset >= el.StartTag.End &&
                markupOffset < end.Start.Offset && (template is null || el.StartTag.Start.Offset > template.StartTag.Start.Offset))
            {
                template = el;
            }
        }

        // The template's data control: its nearest control ancestor (a TemplateField is not a control: the grid is).
        // Without an ID (a nested repeater bound in markup) there is nothing to look up.
        string? id = null;
        for (var el = template?.Parent; el is not null; el = el.Parent)
        {
            if (el.Annotation is ResolvedControl { IsControl: true })
            {
                id = el.Id?.Trim() is { Length: > 0 } elementId && !elementId.Contains("<%", StringComparison.Ordinal) ? elementId : null;
                break;
            }
        }

        var codeBehind = project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, analysis.CodeBehindPath, StringComparison.OrdinalIgnoreCase));
        if (id is null || codeBehind is null)
        {
            return [];
        }

        var model = codeBehind.GetSemanticModelAsync().GetAwaiter().GetResult()!;
        var result = new List<ITypeSymbol>();
        foreach (var assignment in model.SyntaxTree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.AssignmentStatementSyntax>())
        {
            if (assignment.Left is not Microsoft.CodeAnalysis.VisualBasic.Syntax.MemberAccessExpressionSyntax { Name.Identifier.ValueText: var property } access ||
                !property.Equals("DataSource", StringComparison.OrdinalIgnoreCase) ||
                !(access.Expression?.ToString() is { } target && (target.Equals(id, StringComparison.OrdinalIgnoreCase) || target.Equals("Me." + id, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            if (ItemType(model.GetTypeInfo(assignment.Right).Type) is { } itemType && !result.Contains(itemType, SymbolEqualityComparer.Default))
            {
                result.Add(itemType);
            }
        }

        return result;
    }

    /// <summary>T of an array or IEnumerable(Of T), unless T is untyped (Object, DataRow / DataRowView / IDataRecord rows).</summary>
    private static ITypeSymbol? ItemType(ITypeSymbol? type)
    {
        static bool IsEnumerableOfT(INamedTypeSymbol t) => t.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;
        ITypeSymbol? item = type switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol named when IsEnumerableOfT(named) => named.TypeArguments[0],
            INamedTypeSymbol named => named.AllInterfaces.FirstOrDefault(IsEnumerableOfT)?.TypeArguments[0],
            _ => null,
        };
        return item is null || item.SpecialType == SpecialType.System_Object || item.Name is "DataRow" or "DataRowView" or "IDataRecord" or "DbDataRecord" ? null : item;
    }

    /// <summary>Documentation of an item from the last code-block list (data ids are negative).</summary>
    public CompletionItem Resolve(CompletionItem item, ClientOptions client)
    {
        if (item.Data is not { } data || data >= 0 || _lastCompletion is not var (document, items) || -data - 1 >= items.Length)
        {
            return item;
        }

        var service = RoslynCompletionService.GetService(document);
        var description = service?.GetDescriptionAsync(document, items[-data - 1]).GetAwaiter().GetResult();
        if (description is null || description.Text.Length == 0)
        {
            return item;
        }

        return item with
        {
            Documentation = client.Markdown
                ? new MarkupContent("markdown", "```vb\n" + description.Text + "\n```")
                : new MarkupContent("plaintext", description.Text),
        };
    }

    /// <summary>Quick info for a position inside a code block; null elsewhere.</summary>
    public (LspRange Range, string Text)? Hover(string path, LspPosition position)
    {
        if (Locate(path, position) is not var (analysis, document, projection, offset))
        {
            return null;
        }

        var service = QuickInfoService.GetService(document);
        var info = service?.GetQuickInfoAsync(document, offset).GetAwaiter().GetResult();
        if (info is null || info.Sections.Length == 0)
        {
            return null;
        }

        var text = string.Join("\n\n", info.Sections.Select(s => s.Text).Where(s => s.Length > 0));
        var span = MapSpan(projection, info.Span) ?? (analysis.Document.ToOffset(position), 0);
        return (analysis.Document.ToRange(span.Start, span.Length), text);
    }

    /// <summary>Where the symbol under the cursor in a code block is declared; null when the position is not in a block.</summary>
    public List<LspLocation>? Definition(string path, LspPosition position)
    {
        if (Locate(path, position) is not var (_, document, _, offset))
        {
            return null;
        }

        var symbol = SymbolFinder.FindSymbolAtPositionAsync(document, offset).GetAwaiter().GetResult();
        if (symbol is null)
        {
            return [];
        }

        var results = new List<LspLocation>();
        foreach (var location in symbol.Locations)
        {
            if (ToLsp(document.Project.Solution, location) is { } l && !results.Contains(l))
            {
                results.Add(l);
            }
        }

        return results;
    }

    /// <summary>References of the symbol under the cursor in a code block (the project's .vb files and the touched markup files).</summary>
    public List<LspLocation>? References(string path, LspPosition position, bool includeDeclaration)
    {
        if (Locate(path, position) is not var (_, document, _, offset))
        {
            return null;
        }

        var solution = document.Project.Solution;
        var symbol = SymbolFinder.FindSymbolAtPositionAsync(document, offset).GetAwaiter().GetResult();
        if (symbol is null)
        {
            return [];
        }

        var results = new List<LspLocation>();
        foreach (var reference in SymbolFinder.FindReferencesAsync(symbol, solution).GetAwaiter().GetResult())
        {
            var locations = reference.Locations.Select(l => l.Location);
            if (includeDeclaration)
            {
                locations = reference.Definition.Locations.Concat(locations);
            }

            foreach (var location in locations)
            {
                if (ToLsp(solution, location) is { } l && !results.Contains(l))
                {
                    results.Add(l);
                }
            }
        }

        return results;
    }

    /// <summary>Starts the compilation of the markup file's project if needed and waits for it (tests, the corpus check).</summary>
    public void WaitUntilReady(string path)
    {
        _ = Open(path);
        if (workspace.Analyze(path) is { } analysis && _projects.TryGetValue(analysis.Project.ProjectDirectory, out var project))
        {
            project.Wait();
        }
    }

    /// <summary>Forgets projects that have been idle for 10 minutes with none of their files open.</summary>
    public void DropIdle()
    {
        foreach (var (dir, project) in _projects.ToList())
        {
            if (DateTime.UtcNow - project.LastUsedUtc > IdleTimeout &&
                !workspace.OpenPaths.Any(p => p.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                Log?.Invoke($"code blocks: {Path.GetFileName(project.File.Path)} idle; dropped");
                _projects.Remove(dir);
            }
        }
    }

    /// <summary>The solution document of a markup file's projection, starting the project's build if needed.</summary>
    private (MarkupAnalysis Analysis, Document Document, Projection Projection)? Open(string path, TimeSpan wait = default)
    {
        if (!Enabled || workspace.Analyze(path) is not { } analysis || ProjectionOf(analysis) is not { BlockCount: > 0 } projection ||
            analysis.Project.ProjectFile is null)
        {
            return null;
        }

        var context = analysis.Project;
        if (!_projects.TryGetValue(context.ProjectDirectory, out var project) || !ReferenceEquals(project.Context, context))
        {
            project = CodeBlockProject.Start(context, () => ProjectReady?.Invoke(), Log);
            _projects[context.ProjectDirectory] = project;
        }

        if (wait > TimeSpan.Zero && !project.IsReady)
        {
            project.Wait(wait);
        }

        if (project.Failure is not null)
        {
            return null; // logged by the build; retried when the project file or bin changes
        }

        return project.Get(workspace, analysis, projection) is var (document, p) ? (analysis, document, p) : null;
    }

    private (MarkupAnalysis Analysis, Document Document, Projection Projection, int Offset)? Locate(string path, LspPosition position)
    {
        if (!Enabled || workspace.Analyze(path) is not { } analysis || ProjectionOf(analysis)?.ToProjection(analysis.Document.ToOffset(position)) is not { } offset ||
            Open(path, BuildWait) is not var (_, document, projection))
        {
            return null;
        }

        return (analysis, document, projection, offset);
    }

    private LspLocation? ToLsp(Solution solution, Location location)
    {
        if (!location.IsInSource || location.SourceTree is not { } tree)
        {
            return null;
        }

        var documentId = solution.GetDocumentId(tree);
        foreach (var project in _projects.Values)
        {
            if (documentId is not null && project.ProjectionOf(documentId) is var (markupPath, projection))
            {
                if (MapSpan(projection, location.SourceSpan) is not var (start, length) || workspace.GetDocument(markupPath) is not { } markup)
                {
                    return null; // scaffolding
                }

                return new LspLocation(DocumentUri.FromPath(markupPath), markup.ToRange(start, length));
            }
        }

        var span = location.GetLineSpan().Span;
        return new LspLocation(DocumentUri.FromPath(tree.FilePath),
            new LspRange(new LspPosition(span.Start.Line, span.Start.Character), new LspPosition(span.End.Line, span.End.Character)));
    }

    /// <summary>A projection span in markup terms: inside one block, or starting in one (clamped to it).</summary>
    private static (int Start, int Length)? MapSpan(Projection projection, TextSpan span)
    {
        if (projection.ToMarkup(span.Start, span.Length) is { } exact)
        {
            return exact;
        }

        foreach (var s in projection.Spans)
        {
            if (span.Start >= s.ProjectionStart && span.Start <= s.ProjectionStart + s.Length)
            {
                int start = s.MarkupStart + (span.Start - s.ProjectionStart);
                return (start, Math.Min(span.Length, s.MarkupStart + s.Length - start));
            }
        }

        return null;
    }

    /// <summary>The class the page compiles from: the code-behind class the designer is generated for.</summary>
    private static string? BaseType(MarkupAnalysis analysis)
    {
        if (analysis.Result.Model is { } model)
        {
            return string.Join('.', new[] { analysis.Project.RootNamespace, model.Namespace, model.ClassName }.Where(s => !string.IsNullOrEmpty(s)));
        }

        return analysis.MainDirective?.GetAttributeValue("Inherits")?.Trim() is { Length: > 0 } inherits ? inherits : null;
    }

    private static int Kind(ImmutableArray<string> tags)
    {
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case "Class" or "Delegate":
                    return CompletionItemKind.Class;
                case "Structure":
                    return CompletionItemKind.Struct;
                case "Interface":
                    return CompletionItemKind.Interface;
                case "Enum":
                    return CompletionItemKind.Enum;
                case "EnumMember":
                    return CompletionItemKind.EnumMember;
                case "Module" or "Namespace":
                    return CompletionItemKind.Module;
                case "Method" or "ExtensionMethod":
                    return CompletionItemKind.Method;
                case "Property":
                    return CompletionItemKind.Property;
                case "Field":
                    return CompletionItemKind.Field;
                case "Constant":
                    return CompletionItemKind.Constant;
                case "Event":
                    return CompletionItemKind.Event;
                case "Local" or "Parameter" or "RangeVariable":
                    return CompletionItemKind.Variable;
                case "Keyword" or "Intrinsic":
                    return CompletionItemKind.Keyword;
                case "Operator":
                    return CompletionItemKind.Operator;
                case "TypeParameter":
                    return CompletionItemKind.TypeParameter;
                case "Snippet":
                    return CompletionItemKind.Snippet;
            }
        }

        return CompletionItemKind.Value;
    }
}
