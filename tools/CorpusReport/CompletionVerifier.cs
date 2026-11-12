using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using WebForms.Designer;
using WebForms.LanguageServer;
using WebForms.LanguageServer.Completion;
using WebForms.LanguageServer.Protocol;
using WebForms.Markup;

/// <summary>
/// Completion half of <c>--verify-lsp</c>: asks for completion at every tag and attribute that is actually in the
/// corpus, as if the user were typing it, and checks the real one is offered:
/// <list type="bullet">
/// <item>at the start of each prefixed tag (and inner property tag) name: that tag;</item>
/// <item>at the start of each attribute name of those tags: the attribute, when it is a property or event of the
/// type (other attributes are expando HTML attributes, rendered as they are; counted, not errors);</item>
/// <item>at the start of each event attribute's value whose handler exists: that handler (the signature filter);</item>
/// <item>at the start of each enum / boolean property value: that value.</item>
/// </list>
/// </summary>
internal sealed class CompletionVerifier(WebFormsWorkspace workspace)
{
    private readonly CompletionProvider _provider = new(workspace);
    private readonly ClientOptions _client = new(Snippets: true, Markdown: true);
    private readonly List<double> _timings = [];

    public int Tags { get; private set; }
    public int EndTags { get; private set; }
    public List<string> MissedEndTags { get; } = [];
    public int EndTagsAfterOpenHtml { get; private set; }
    public int Attributes { get; private set; }
    public int Expando { get; private set; }
    public int Handlers { get; private set; }
    public int Values { get; private set; }
    public List<string> MissedTags { get; } = [];
    public List<string> MissedAttributes { get; } = [];
    public List<string> MissedHandlers { get; } = [];
    public List<string> MissedValues { get; } = [];
    public Dictionary<string, int> ExpandoNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int InComments { get; private set; }

    /// <summary>Markup handlers that are parameterless Subs: valid (relaxed delegates) but not offered by default.</summary>
    public List<string> ZeroArgHandlers { get; } = [];

    public int HandlerLists { get; private set; }
    public int HandlerItems { get; private set; }

    /// <summary>Private, Friend or parameterless Subs offered as handlers: a failure (exit 1).</summary>
    public List<string> BadHandlerOffers { get; } = [];

    private readonly Dictionary<string, ILookup<string, MethodBlockSyntax>> _subsByFile = new(StringComparer.OrdinalIgnoreCase);

    public void Check(string rel, MarkupAnalysis analysis, Func<MarkupElement, MarkupAttribute, bool> hasHandler)
    {
        Func<MarkupToken, bool>? isServer = null;
        foreach (var el in analysis.Tree.AllElements())
        {
            bool prefixed = el.Prefix is not null;
            bool property = el.Role == ElementRole.Property;
            if (!prefixed && !property)
            {
                continue; // HTML elements: the HTML server's job
            }

            string where = $"{rel}({el.StartTag.NamePosition.Line},{el.StartTag.NamePosition.Column})";
            if (el.Annotation is ResolvedControl { Error: not null } or PropertyAnnotation { Property: null })
            {
                continue; // unresolved: reported by the designer check, nothing to offer
            }

            if (MarkupCursor.IsInHtmlComment(analysis.Document.Text, analysis.Tokens, el.StartTag.NamePosition.Offset))
            {
                InComments++; // still parsed by ASP.NET (designer fields), but completion is off inside <!-- -->
                continue;
            }

            Tags++;
            if (!Offers(analysis, el.StartTag.NamePosition.Offset, el.Name))
            {
                MissedTags.Add($"{where}: <{el.Name}> (parent <{el.Parent?.Name ?? "page"}>)");
            }

            // "</" right where the existing end tag is: the open-element stack must name this element.
            if (el.EndTag is { } endTag)
            {
                EndTags++;
                isServer ??= CompletionProvider.IsServerStartTag(analysis);
                var open = CompletionProvider.InnermostOpenTag(analysis.Tokens, endTag.Start.Offset, isServer);
                if (open is not null && !isServer(open) && open.Start.Offset >= el.StartTag.End)
                {
                    // HTML left open inside the element (<HeaderTemplate><table> closed in the FooterTemplate): "</"
                    // there is the HTML server's; ASP.NET closes it with the element.
                    EndTagsAfterOpenHtml++;
                }
                else if (open is null || open.Start.Offset != el.StartTag.Start.Offset)
                {
                    MissedEndTags.Add($"{rel}({endTag.Start.Line},{endTag.Start.Column}): </{el.Name}> (innermost open: <{open?.Name ?? "none"}> at {(open is null ? "-" : $"{open.Start.Line},{open.Start.Column}")})");
                }
            }

            var type = el.Annotation switch
            {
                ResolvedControl rc => rc.Type,
                PropertyAnnotation pa => pa.Property?.PropertyType,
                _ => null,
            };
            bool userControl = el.Annotation is ResolvedControl { IsUserControl: true };

            foreach (var attr in el.StartTag.Attributes)
            {
                if (type is null || userControl || attr.Name.Equals("runat", StringComparison.OrdinalIgnoreCase) || attr.Name.Equals("ID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var known = Known(type, attr.Name);
                if (known is null)
                {
                    Expando++;
                    ExpandoNames[attr.Name] = ExpandoNames.GetValueOrDefault(attr.Name) + 1;
                    continue;
                }

                Attributes++;
                string aWhere = $"{rel}({attr.NamePosition.Line},{attr.NamePosition.Column})";
                if (!Offers(analysis, attr.NamePosition.Offset, attr.Name))
                {
                    MissedAttributes.Add($"{aWhere}: {attr.Name} on <{el.Name}>");
                }

                if (known == "event" && attr.ValuePosition is { } hvp && !attr.IsDataBound && Navigator.IsEventAttribute(el, attr))
                {
                    CheckHandlerList(analysis, hvp.Offset, $"{aWhere}: {attr.Name}");
                }

                if (attr.ValuePosition is not { } vp || attr.Value is not { } value || attr.IsDataBound || value.Trim().Length == 0)
                {
                    continue;
                }

                if (known == "event" && Navigator.IsEventAttribute(el, attr) && hasHandler(el, attr))
                {
                    Handlers++;
                    if (!Offers(analysis, vp.Offset, value.Trim()))
                    {
                        bool parameterless = SubsOf(analysis) is { } subs && subs[value.Trim()].Any() &&
                            subs[value.Trim()].All(m => (m.SubOrFunctionStatement.ParameterList?.Parameters.Count ?? 0) == 0);
                        (parameterless ? ZeroArgHandlers : MissedHandlers).Add($"{aWhere}: {attr.Name}=\"{value}\"");
                    }
                }
                else if (known == "value" && !value.Contains(',', StringComparison.Ordinal))
                {
                    Values++;
                    if (!Offers(analysis, vp.Offset, value.Trim()))
                    {
                        MissedValues.Add($"{aWhere}: {attr.Name}=\"{value}\"");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every Sub offered at an event attribute's value must be one markup can wire: looked up again in the
    /// code-behind's syntax tree (not through the provider's own filter), some Sub of that name must have two
    /// parameters and be neither Private nor Friend (the page class compiled from the markup inherits it from
    /// another assembly). Parameterless Subs are off by default (<c>completion.zeroArgHandlers</c>).
    /// </summary>
    private void CheckHandlerList(MarkupAnalysis analysis, int offset, string where)
    {
        if (SubsOf(analysis) is not { } subs)
        {
            return;
        }

        HandlerLists++;
        var list = _provider.Complete(analysis.Path, analysis.Document.ToPosition(offset), _client);
        foreach (var item in list?.Items ?? [])
        {
            HandlerItems++;
            var declarations = subs[item.Label].ToList();
            if (!declarations.Any(Wireable))
            {
                BadHandlerOffers.Add($"{where}: offers {(declarations.Count == 0 ? item.Label + " (no such Sub)" : string.Join(" / ", declarations.Select(d => d.SubOrFunctionStatement.ToString().Trim())))}");
            }
        }

        static bool Wireable(MethodBlockSyntax m)
        {
            var s = m.SubOrFunctionStatement;
            bool isPrivate = s.Modifiers.Any(SyntaxKind.PrivateKeyword);
            bool friendOnly = s.Modifiers.Any(SyntaxKind.FriendKeyword) && !s.Modifiers.Any(SyntaxKind.ProtectedKeyword);
            return !isPrivate && !friendOnly && s.ParameterList?.Parameters.Count == 2;
        }
    }

    /// <summary>The Subs of the code-behind file by name, from its syntax tree.</summary>
    private ILookup<string, MethodBlockSyntax>? SubsOf(MarkupAnalysis analysis)
    {
        if (analysis.CodeBehindPath is not { } cb || workspace.GetVbFile(cb) is not { } vb)
        {
            return null;
        }

        if (!_subsByFile.TryGetValue(cb, out var subs))
        {
            subs = vb.Tree.GetRoot().DescendantNodes().OfType<MethodBlockSyntax>()
                .Where(m => m.SubOrFunctionStatement.SubOrFunctionKeyword.IsKind(SyntaxKind.SubKeyword))
                .ToLookup(m => m.SubOrFunctionStatement.Identifier.ValueText, StringComparer.OrdinalIgnoreCase);
            _subsByFile[cb] = subs;
        }

        return subs;
    }

    /// <returns>"event", "value" (enum or boolean property), "property", or null for an expando attribute.</returns>
    private static string? Known(ControlType type, string name)
    {
        if (name.Length > 2 && name.StartsWith("On", StringComparison.OrdinalIgnoreCase) && type.GetEvent(name[2..]) is not null)
        {
            return "event";
        }

        var p = type.ResolvePropertyPath(name);
        if (p is null || !p.Settable || p.IsTemplate)
        {
            return null;
        }

        return p.Type is { IsEnum: true } || p.Type?.FullName == "System.Boolean" ? "value" : "property";
    }

    /// <summary>Completion at <paramref name="offset"/> (the start of the existing word) offers <paramref name="label"/>.</summary>
    private bool Offers(MarkupAnalysis analysis, int offset, string label)
    {
        var sw = Stopwatch.StartNew();
        var list = _provider.Complete(analysis.Path, analysis.Document.ToPosition(offset), _client);
        _timings.Add(sw.Elapsed.TotalMilliseconds);
        return list?.Items.Any(i => i.Label.Equals(label, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public void Summarize(StringBuilder summary)
    {
        _timings.Sort();
        double P(double q) => _timings.Count == 0 ? 0 : _timings[(int)Math.Min(_timings.Count - 1, q * _timings.Count)];
        summary.AppendLine(CultureInfo.InvariantCulture, $"  completion: tags      {Tags,5}  not offered {MissedTags.Count}; inside HTML comments (not completed) {InComments}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"              end tags  {EndTags,5}  not the innermost open element at its '</' {MissedEndTags.Count}; HTML left open inside (the HTML server's) {EndTagsAfterOpenHtml}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"              attributes{Attributes,6}  not offered {MissedAttributes.Count}; expando (not a property/event) {Expando}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"              handlers  {Handlers,5}  not offered {MissedHandlers.Count}; parameterless (off by default) {ZeroArgHandlers.Count}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"              values    {Values,5}  not offered {MissedValues.Count}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"              handler lists {HandlerLists} ({HandlerItems} items): Private / Friend / parameterless offered {BadHandlerOffers.Count}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"              {_timings.Count} requests: median {P(0.5):F1} ms, p99 {P(0.99):F1} ms, max {P(1):F1} ms");
    }
}

/// <summary>
/// The cost of one keystroke on the largest pages, as the server sees it: a full-text didChange (parsed from JSON),
/// a fresh analysis of the new buffer, and completion on an attribute list (the biggest response).
/// </summary>
internal static class KeystrokeProbe
{
    public static string Run(IEnumerable<string> files)
    {
        var sb = new StringBuilder();
        foreach (var file in files)
        {
            using var workspace = new WebFormsWorkspace();
            var provider = new CompletionProvider(workspace);
            var text = File.ReadAllText(file);
            // A new, unfinished GridView tag before the first control: the largest attribute list (properties, styles, Font).
            int at = text.IndexOf("<asp:", StringComparison.OrdinalIgnoreCase);
            if (at <= 0)
            {
                continue;
            }

            workspace.Open(file, text, 0);
            provider.Complete(file, new SourceDocument(file, text, null).ToPosition(at), new ClientOptions(true, true, true)); // warm: project metadata

            var times = new List<(double Sync, double Total)>();
            int bytes = 0, items = 0;
            for (int v = 1; v <= 10; v++)
            {
                var typed = "<asp:GridView " + new string('x', v) + " ";
                var edited = text.Insert(at, typed);
                var json = System.Text.Json.JsonSerializer.Serialize(
                    new DidChangeTextDocumentParams(new VersionedTextDocumentIdentifier(DocumentUri.FromPath(file), v), [new TextDocumentContentChangeEvent(null, edited)]),
                    LspJsonContext.Default.DidChangeTextDocumentParams);
                var sw = Stopwatch.StartNew();
                var change = System.Text.Json.JsonSerializer.Deserialize(json, LspJsonContext.Default.DidChangeTextDocumentParams)!;
                workspace.Change(file, change.ContentChanges[0].Text, v);
                double sync = sw.Elapsed.TotalMilliseconds;
                var list = provider.Complete(file, new SourceDocument(file, edited, null).ToPosition(at + typed.Length), new ClientOptions(true, true, true));
                times.Add((sync, sw.Elapsed.TotalMilliseconds));
                if (list is not null)
                {
                    items = list.Items.Length;
                    bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(list, LspJsonContext.Default.CompletionList).Length;
                }
            }

            var median = times.OrderBy(t => t.Total).ElementAt(times.Count / 2);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  keystroke on {Path.GetFileName(file)} ({text.Length / 1024} KB): {median.Total:F1} ms median ({median.Sync:F1} ms of it parsing the full-text didChange); {items} items, {bytes / 1024} KB JSON");
        }

        return sb.ToString();
    }
}
