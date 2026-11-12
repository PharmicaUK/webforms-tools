using WebForms.Designer;
using WebForms.LanguageServer.Protocol;
using WebForms.Markup;

namespace WebForms.LanguageServer;

/// <summary>What a position in markup refers to.</summary>
public abstract record NavigationTarget;

/// <summary>A control by ID. <paramref name="IsReference"/>: the position is an attribute naming the control (ControlToValidate, …), not its own ID.</summary>
public sealed record ControlTarget(string Id, bool IsReference) : NavigationTarget;

/// <summary>An event handler named in an <c>On&lt;Event&gt;</c> attribute.</summary>
public sealed record HandlerTarget(string Name) : NavigationTarget;

/// <summary>A file named by a directive or a user control tag.</summary>
public sealed record FileTarget(string Path) : NavigationTarget;

/// <summary>The code-behind class (the Inherits attribute).</summary>
public sealed record ClassTarget(string ClassName) : NavigationTarget;

/// <summary>A ContentPlaceHolder in the master page (the ContentPlaceHolderID of asp:Content).</summary>
public sealed record PlaceholderTarget(string MasterPath, string Id) : NavigationTarget;

/// <summary>Go to definition / find references from markup into the code-behind, the designer and other markup.</summary>
public sealed class Navigator(WebFormsWorkspace workspace)
{
    /// <summary>Attributes whose value is the ID of another control in the same naming container.</summary>
    internal static readonly HashSet<string> ControlReferenceAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ControlToValidate", "ControlToCompare", "AssociatedControlID", "ControlID", "TargetControlID",
        "DefaultButton", "DefaultFocus", "DataSourceID", "PopupControlID", "CancelControlID", "OkControlID",
        "PopupDragHandleControlID", "TriggerControlID", "DynamicControlID",
    };

    /// <summary>Directive attributes whose value is a (virtual) path.</summary>
    private static readonly HashSet<string> PathAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Src", "MasterPageFile", "VirtualPath", "CodeBehind", "CodeFile",
    };

    public IReadOnlyList<LspLocation> Definition(string path, LspPosition position)
    {
        var analysis = workspace.Analyze(path);
        if (analysis is null)
        {
            return [];
        }

        var target = TargetAt(analysis, analysis.Document.ToOffset(position));
        var results = new LocationList();
        switch (target)
        {
            case ControlTarget { IsReference: true } c:
                foreach (var el in analysis.ElementsWithId(c.Id))
                {
                    results.Add(IdLocation(analysis.Document, el));
                }

                break;
            case ControlTarget c:
                AddControlDeclarations(analysis, c.Id, results);
                AddCodeBehindUses(analysis, c.Id, results);
                if (results.Count == 0)
                {
                    // No field and no use (template control, page without designer): the ID is the declaration.
                    foreach (var el in analysis.ElementsWithId(c.Id))
                    {
                        results.Add(IdLocation(analysis.Document, el));
                    }
                }

                break;
            case HandlerTarget h:
                AddHandlerDeclarations(analysis, h.Name, results);
                break;
            case FileTarget f when File.Exists(f.Path):
                results.Add(new LspLocation(DocumentUri.FromPath(f.Path), new LspRange(new LspPosition(0, 0), new LspPosition(0, 0))));
                break;
            case ClassTarget k:
                if (analysis.CodeBehindPath is { } cb && workspace.GetVbFile(cb) is { } vb)
                {
                    results.AddRange(vb.ClassDeclarations(k.ClassName).Select(vb.Location));
                }

                break;
            case PlaceholderTarget p:
                if (workspace.GetDocument(p.MasterPath) is { } master)
                {
                    foreach (var t in MarkupDocument.ParseDirectivesOnly(master.Text).Tokens)
                    {
                        if (t.Kind == TokenKind.StartTag && t.GetAttribute("ID") is { Value: { } v } id && v.Trim().Equals(p.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(AttributeValueLocation(master, id));
                        }
                    }
                }

                break;
        }

        return results.Items;
    }

    public IReadOnlyList<LspLocation> References(string path, LspPosition position, bool includeDeclaration)
    {
        var analysis = workspace.Analyze(path);
        if (analysis is null)
        {
            return [];
        }

        var results = new LocationList();
        switch (TargetAt(analysis, analysis.Document.ToOffset(position)))
        {
            case ControlTarget c:
                if (includeDeclaration)
                {
                    foreach (var el in analysis.ElementsWithId(c.Id))
                    {
                        results.Add(IdLocation(analysis.Document, el));
                    }

                    AddControlDeclarations(analysis, c.Id, results);
                }

                AddCodeBehindUses(analysis, c.Id, results);
                foreach (var el in analysis.Elements)
                {
                    foreach (var a in el.StartTag.Attributes)
                    {
                        if (ControlReferenceAttributes.Contains(a.Name) && a.Value is { } v && v.Trim().Equals(c.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(AttributeValueLocation(analysis.Document, a));
                        }
                    }
                }

                break;
            case HandlerTarget h:
                if (includeDeclaration)
                {
                    AddHandlerDeclarations(analysis, h.Name, results);
                }

                foreach (var el in analysis.Elements)
                {
                    foreach (var a in el.StartTag.Attributes)
                    {
                        if (IsEventAttribute(el, a) && a.Value!.Trim().Equals(h.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(AttributeValueLocation(analysis.Document, a));
                        }
                    }
                }

                break;
            case not null:
                return includeDeclaration ? Definition(path, position) : [];
        }

        return results.Items;
    }

    /// <summary>What the markup at <paramref name="offset"/> refers to, or null.</summary>
    public static NavigationTarget? TargetAt(MarkupAnalysis analysis, int offset)
    {
        var hit = analysis.HitTest(offset);
        if (hit is null)
        {
            return null;
        }

        if (hit.Token.Kind == TokenKind.Directive)
        {
            return DirectiveTarget(analysis, hit);
        }

        var element = hit.Element;
        if (hit.Part == HitPart.Name)
        {
            return element?.Annotation is ResolvedControl { IsUserControl: true, UserControlPath: { } ucPath } ? new FileTarget(ucPath) : null;
        }

        if (hit.Attribute is not { Value: { } value } attr || attr.IsDataBound || hit.Part is not (HitPart.AttributeName or HitPart.AttributeValue))
        {
            return null;
        }

        value = value.Trim();
        if (attr.Name.Equals("ID", StringComparison.OrdinalIgnoreCase))
        {
            return element is not null && IsIdentifier(value) ? new ControlTarget(value, IsReference: false) : null;
        }

        if (IsEventAttribute(element, attr))
        {
            return new HandlerTarget(value);
        }

        if (ControlReferenceAttributes.Contains(attr.Name) && IsIdentifier(value))
        {
            return new ControlTarget(value, IsReference: true);
        }

        if (attr.Name.Equals("ContentPlaceHolderID", StringComparison.OrdinalIgnoreCase) && MasterPagePath(analysis) is { } master)
        {
            return new PlaceholderTarget(master, value);
        }

        return null;
    }

    /// <summary>
    /// An <c>On&lt;Event&gt;</c> attribute naming a handler: the control's type (from the real metadata) has that event.
    /// For user controls and unresolved types, whose events are not known, any <c>On…</c> attribute counts.
    /// </summary>
    public static bool IsEventAttribute(MarkupElement? element, MarkupAttribute attribute)
    {
        if (attribute.Value is not { } value || attribute.IsDataBound || attribute.Name.Length <= 2 ||
            !attribute.Name.StartsWith("On", StringComparison.OrdinalIgnoreCase) || !IsIdentifier(value.Trim()))
        {
            return false;
        }

        if (element?.Annotation is not ResolvedControl rc)
        {
            return false;
        }

        return rc.IsUserControl || rc.Type is null || rc.Type.HasEvent(attribute.Name[2..]);
    }

    private static NavigationTarget? DirectiveTarget(MarkupAnalysis analysis, MarkupHit hit)
    {
        if (hit.Attribute is not { Value: { } value } attr || hit.Part is not (HitPart.AttributeName or HitPart.AttributeValue) || value.Contains("<%", StringComparison.Ordinal))
        {
            return null;
        }

        value = value.Trim();
        if (attr.Name.Equals("Inherits", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
        {
            return new ClassTarget(value[(value.LastIndexOf('.') + 1)..]);
        }

        bool isPath = PathAttributes.Contains(attr.Name) ||
                      (hit.Token.Name.Equals("Reference", StringComparison.OrdinalIgnoreCase) && attr.Name is "Page" or "Control");
        return isPath && value.Length > 0 ? new FileTarget(analysis.Project.ResolveVirtualPath(value, analysis.Path)) : null;
    }

    private static string? MasterPagePath(MarkupAnalysis analysis)
    {
        var master = analysis.MainDirective?.GetAttributeValue("MasterPageFile")?.Trim();
        return string.IsNullOrEmpty(master) || master.Contains("<%", StringComparison.Ordinal)
            ? null
            : analysis.Project.ResolveVirtualPath(master, analysis.Path);
    }

    /// <summary>The field in the designer file, or the member in the code-behind when it is declared there instead.</summary>
    private void AddControlDeclarations(MarkupAnalysis analysis, string id, LocationList results)
    {
        int before = results.Count;
        if (workspace.GetVbFile(analysis.DesignerPath) is { } designer)
        {
            results.AddRange(designer.MemberDeclarations(analysis.ClassName, id).Select(designer.Location));
        }

        if (results.Count == before && analysis.CodeBehindPath is { } cb && workspace.GetVbFile(cb) is { } vb)
        {
            results.AddRange(vb.MemberDeclarations(analysis.ClassName, id).Select(vb.Location));
        }
    }

    /// <summary>Handlers (<c>Handles id.Event</c>) first, then every other use, then <c>FindControl("id")</c>.</summary>
    private void AddCodeBehindUses(MarkupAnalysis analysis, string id, LocationList results)
    {
        if (analysis.CodeBehindPath is not { } cb || workspace.GetVbFile(cb) is not { } vb)
        {
            return;
        }

        var cls = analysis.ClassName;
        results.AddRange(vb.Handles(cls, id).Select(h => vb.Location(h.MethodName)));
        results.AddRange(vb.Uses(cls, id).Select(vb.Location));
        results.AddRange(vb.FindControlLiterals(cls, id).Select(vb.Location));
    }

    private void AddHandlerDeclarations(MarkupAnalysis analysis, string name, LocationList results)
    {
        if (analysis.CodeBehindPath is { } cb && workspace.GetVbFile(cb) is { } vb)
        {
            results.AddRange(vb.Methods(analysis.ClassName, name).Select(vb.Location));
        }
    }

    private static LspLocation IdLocation(SourceDocument document, MarkupElement element) =>
        element.GetAttribute("ID") is { } id ? AttributeValueLocation(document, id) : TagLocation(document, element.StartTag);

    private static LspLocation AttributeValueLocation(SourceDocument document, MarkupAttribute attribute)
    {
        int start = attribute.ValuePosition?.Offset ?? attribute.NamePosition.Offset;
        int length = attribute.ValuePosition is null ? attribute.Name.Length : attribute.Value?.Length ?? 0;
        return new LspLocation(DocumentUri.FromPath(document.Path), document.ToRange(start, length));
    }

    private static LspLocation TagLocation(SourceDocument document, MarkupToken token) =>
        new(DocumentUri.FromPath(document.Path), document.ToRange(token.NamePosition.Offset, token.Name.Length));

    internal static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_'))
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Locations in insertion order without duplicates.</summary>
    private sealed class LocationList
    {
        private readonly List<LspLocation> _items = [];
        private readonly HashSet<LspLocation> _seen = [];

        public IReadOnlyList<LspLocation> Items => _items;
        public int Count => _items.Count;

        public void Add(LspLocation location)
        {
            if (_seen.Add(location))
            {
                _items.Add(location);
            }
        }

        public void AddRange(IEnumerable<LspLocation> locations)
        {
            foreach (var l in locations)
            {
                Add(l);
            }
        }
    }
}
