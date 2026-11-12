using WebForms.Markup;

namespace WebForms.Designer;

/// <summary>Annotation on control / item elements: the resolved type.</summary>
public sealed class ResolvedControl
{
    /// <summary>Metadata type used for parsing behaviour (for user controls this is System.Web.UI.UserControl).</summary>
    public ControlType? Type { get; init; }

    /// <summary>The type as written in the designer file, e.g. <c>Global.System.Web.UI.WebControls.Label</c>. Null if unresolved.</summary>
    public string? DesignerTypeName { get; init; }

    public bool IsUserControl { get; init; }

    /// <summary>Physical path of the user control (.ascx) for user controls.</summary>
    public string? UserControlPath { get; init; }

    /// <summary>Why the type could not be resolved (null when resolved).</summary>
    public string? Error { get; init; }

    public bool IsControl => IsUserControl || (Type?.IsControl ?? false);
}

/// <summary>Annotation on inner property elements.</summary>
public sealed class PropertyAnnotation
{
    public InnerProperty? Property { get; init; }
    public ControlType? OwnerType { get; init; }

    public bool IsTemplate => Property?.Kind == InnerPropertyKind.Template;
    public bool IsMultiInstanceTemplate => IsTemplate && !Property!.SingleInstanceTemplate;
}

/// <summary>
/// Implements the ASP.NET ControlBuilder decisions (ParseChildren, inner properties, templates, collection items,
/// implicit table rows) on top of metadata read from the real assemblies.
/// </summary>
public sealed class ControlSchema : IMarkupSchema
{
    // Root web.config of .NET Framework 4.x, plus the parser's hard-coded asp → System.Web.UI.WebControls.
    private static readonly TagRegistration[] BuiltIn =
    [
        new("asp", "System.Web.UI.WebControls", "System.Web", null, null, "built-in"),
        new("asp", "System.Web.UI.WebControls.WebParts", "System.Web", null, null, "built-in"),
        new("asp", "System.Web.UI.WebControls.Expressions", "System.Web.Extensions", null, null, "root web.config"),
        new("asp", "System.Web.UI", "System.Web.Extensions", null, null, "root web.config"),
        new("asp", "System.Web.UI.WebControls", "System.Web.Extensions", null, null, "root web.config"),
        new("asp", "System.Web.DynamicData", "System.Web.DynamicData", null, null, "root web.config"),
        new("asp", "System.Web.UI.WebControls", "System.Web.Entity", null, null, "root web.config"),
        new("asp", "System.Web.UI.DataVisualization.Charting", "System.Web.DataVisualization", null, null, "root web.config"),
    ];

    private static readonly Dictionary<string, string> HtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = "HtmlAnchor",
        ["button"] = "HtmlButton",
        ["form"] = "HtmlForm",
        ["head"] = "HtmlHead",
        ["img"] = "HtmlImage",
        ["link"] = "HtmlLink",
        ["meta"] = "HtmlMeta",
        ["select"] = "HtmlSelect",
        ["table"] = "HtmlTable",
        ["td"] = "HtmlTableCell",
        ["th"] = "HtmlTableCell",
        ["tr"] = "HtmlTableRow",
        ["textarea"] = "HtmlTextArea",
        ["title"] = "HtmlTitle",
        // .NET 4.5+
        ["area"] = "HtmlArea",
        ["audio"] = "HtmlAudio",
        ["embed"] = "HtmlEmbed",
        ["html"] = "HtmlElement",
        ["iframe"] = "HtmlIframe",
        ["source"] = "HtmlSource",
        ["track"] = "HtmlTrack",
        ["video"] = "HtmlVideo",
    };

    private static readonly Dictionary<string, string> InputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = "HtmlInputText",
        ["text"] = "HtmlInputText",
        ["password"] = "HtmlInputPassword",
        ["button"] = "HtmlInputButton",
        ["submit"] = "HtmlInputSubmit",
        ["reset"] = "HtmlInputReset",
        ["image"] = "HtmlInputImage",
        ["checkbox"] = "HtmlInputCheckBox",
        ["radio"] = "HtmlInputRadioButton",
        ["file"] = "HtmlInputFile",
        ["hidden"] = "HtmlInputHidden",
    };

    private const string HtmlNs = "System.Web.UI.HtmlControls.";

    private readonly ProjectContext _project;
    private readonly string _markupFile;
    private readonly List<TagRegistration> _registrations;

    public ControlSchema(ProjectContext project, string markupFile, IEnumerable<TagRegistration> pageRegistrations)
    {
        _project = project;
        _markupFile = markupFile;
        _registrations = [.. pageRegistrations, .. project.GetWebConfigControls(markupFile), .. BuiltIn];
    }

    public IReadOnlyList<TagRegistration> Registrations => _registrations;

    private TypeSystem Types => _project.Types;

    public BodyMode OpenElement(MarkupElement element)
    {
        ResolvedControl resolved;
        if (element.Role == ElementRole.Item)
        {
            var hint = (element.Annotation as ItemHint)?.ItemType;
            resolved = element.Prefix is not null ? ResolvePrefixed(element) : ResolveItemByHint(element, hint);
        }
        else if (element.Prefix is not null)
        {
            resolved = ResolvePrefixed(element);
        }
        else
        {
            resolved = ResolveHtml(element);
        }

        element.Annotation = resolved;

        var type = resolved.Type;
        if (type is null)
        {
            return element.Prefix is null ? BodyMode.Controls : BodyMode.Properties;
        }

        if (type.ControlBuilderTypeName == "System.Web.UI.HtmlControls.HtmlEmptyTagControlBuilder")
        {
            return BodyMode.Empty;
        }

        return type.ParseChildren.AsProperties ? BodyMode.Properties : BodyMode.Controls;
    }

    public bool IsImplicitChildControl(MarkupElement parent, MarkupToken tag)
    {
        if (parent.Annotation is not ResolvedControl { Type: { } pt })
        {
            return false;
        }

        string? builder = pt.ControlBuilderTypeName;
        return builder switch
        {
            // (HtmlTable / HtmlTableRow have no builder: they are ParseChildren(true, "Rows"/"Cells") and their
            // <tr>/<td> children arrive as collection items, see ResolveItemByHint.)
            "System.Web.UI.HtmlControls.HtmlHeadBuilder" => tag.Name is var n && (n.Equals("title", StringComparison.OrdinalIgnoreCase) || n.Equals("link", StringComparison.OrdinalIgnoreCase) || n.Equals("meta", StringComparison.OrdinalIgnoreCase)),
            "System.Web.UI.HtmlControls.HtmlSelectBuilder" => tag.Name.Equals("option", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public PropertyChild ClassifyPropertyChild(MarkupElement parent, MarkupToken tag)
    {
        ControlType? owner;
        switch (parent.Annotation)
        {
            case ResolvedControl rc:
                owner = rc.Type;
                break;
            case PropertyAnnotation { Property.Kind: InnerPropertyKind.Collection } pa:
                return CollectionItem(pa.Property, tag);
            case PropertyAnnotation pa:
                owner = pa.Property?.PropertyType;
                break;
            default:
                owner = null;
                break;
        }

        if (owner is null)
        {
            // Unknown owner (unresolved type): we cannot tell properties from items. Prefixed tags are almost always
            // items/controls; unprefixed ones properties.
            if (tag.Name.Contains(':'))
            {
                return new PropertyChild(ElementRole.Item, BodyMode.Properties, new ItemHint(null));
            }

            return new PropertyChild(ElementRole.Property, BodyMode.Properties, new PropertyAnnotation());
        }

        if (parent.Annotation is ResolvedControl && owner.ParseChildren.DefaultProperty is { } defaultProp)
        {
            // ControlBuilder delegates every child tag to the default property's builder.
            var dp = owner.GetInnerProperty(defaultProp);
            return dp is null ? new PropertyChild(ElementRole.Item, BodyMode.Properties, new ItemHint(null)) : CollectionItem(dp, tag);
        }

        var prop = owner.GetInnerProperty(tag.Name);
        var body = prop?.Kind == InnerPropertyKind.Template ? BodyMode.Controls : BodyMode.Properties;
        return new PropertyChild(ElementRole.Property, body, new PropertyAnnotation { Property = prop, OwnerType = owner });
    }

    /// <summary>
    /// A child tag of a collection property. ASP.NET's CollectionBuilder maps the tag to a type; when that is not the
    /// collection's item type it is a parse error, unless the property has <c>[IgnoreUnknownContent]</c>, in which
    /// case the tag is dropped as literal content (it is not rendered) and its children are still read as items.
    /// That is how <c>&lt;tfoot&gt;</c> / <c>&lt;colgroup&gt;</c> inside <c>&lt;table runat="server"&gt;</c> behave.
    /// </summary>
    private PropertyChild CollectionItem(InnerProperty collection, MarkupToken tag)
    {
        var itemType = collection.CollectionItemType;
        if (collection.IgnoreUnknownContent && itemType is not null && !tag.Name.Contains(':') && !MarkupTokenizer.IsRunatServer(tag))
        {
            var html = FindHtmlType(tag.Name, tag.GetAttributeValue("type"));
            if (html is not null && !html.IsSubclassOf(itemType.Type))
            {
                return PropertyChild.Literal;
            }
        }

        return new PropertyChild(ElementRole.Item, BodyMode.Properties, new ItemHint(itemType));
    }

    private sealed record ItemHint(ControlType? ItemType);

    private ResolvedControl ResolveHtml(MarkupElement element)
    {
        string tag = element.LocalName;

        if (tag.Equals("option", StringComparison.OrdinalIgnoreCase) &&
            element.Parent?.Annotation is ResolvedControl { Type.ControlBuilderTypeName: "System.Web.UI.HtmlControls.HtmlSelectBuilder" })
        {
            var li = Types.FindType("System.Web", "System.Web.UI.WebControls.ListItem");
            return new ResolvedControl { Type = li, DesignerTypeName = null };
        }

        var type = FindHtmlType(tag, element.GetAttributeValue("type"));
        return new ResolvedControl { Type = type, DesignerTypeName = type is null ? null : "Global." + type.FullName };
    }

    /// <summary>The HtmlControls type ASP.NET creates for an HTML tag (by <c>type</c> for &lt;input&gt;).</summary>
    public ControlType? FindHtmlType(string tag, string? inputType)
    {
        string? typeName = null;
        if (tag.Equals("input", StringComparison.OrdinalIgnoreCase))
        {
            typeName = InputTypes.TryGetValue(inputType?.Trim() ?? "", out var it) ? it : "HtmlInputGenericControl";
        }
        else if (HtmlTags.TryGetValue(tag, out var ht))
        {
            typeName = ht;
        }

        return (typeName is null ? null : Types.FindType("System.Web", HtmlNs + typeName))
               ?? Types.FindType("System.Web", HtmlNs + "HtmlGenericControl");
    }

    private ResolvedControl ResolveItemByHint(MarkupElement element, ControlType? hint)
    {
        if (hint is not null && hint.Name.Equals(element.LocalName, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedControl { Type = hint, DesignerTypeName = "Global." + hint.FullName };
        }

        if (element.Parent?.Annotation is ResolvedControl { Type: { } parentType } && parentType.FullName.StartsWith(HtmlNs, StringComparison.Ordinal))
        {
            // HtmlTable rows / HtmlTableRow cells: the HTML builders map tag names (tr, td, th) to control types.
            var html = ResolveHtml(element);
            if (hint is not null && html.Type is not null && !html.Type.IsSubclassOf(hint.Type))
            {
                return new ResolvedControl
                {
                    Type = html.Type,
                    DesignerTypeName = html.DesignerTypeName,
                    Error = $"<{element.Name}> inside <{element.Parent.Name} runat=\"server\"> is not a {hint.Name}; ASP.NET rejects this when it parses the page (the collection only accepts {hint.Name} items).",
                };
            }

            return html;
        }

        if (hint is not null)
        {
            // Unprefixed item in a typed collection: look in the item type's namespace and assembly.
            var ns = hint.FullName[..Math.Max(0, hint.FullName.LastIndexOf('.'))];
            var t = Types.FindType(hint.Type.Assembly.GetName().Name!, ns + "." + element.LocalName);
            if (t is not null)
            {
                return new ResolvedControl { Type = t, DesignerTypeName = "Global." + t.FullName };
            }
        }

        return new ResolvedControl { Error = $"Cannot resolve collection item <{element.Name}>." };
    }

    private ResolvedControl ResolvePrefixed(MarkupElement element)
    {
        string prefix = element.Prefix!;
        string name = element.LocalName;
        bool prefixKnown = false;
        var tried = new List<string>();

        foreach (var reg in _registrations)
        {
            if (!reg.TagPrefix.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            prefixKnown = true;
            if (reg.IsUserControl)
            {
                if (!reg.TagName!.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return ResolveUserControl(reg);
            }

            if (reg.Namespace is null)
            {
                continue;
            }

            string fullName = reg.Namespace + "." + name;
            tried.Add(reg.Assembly is null ? fullName : $"{fullName}, {reg.Assembly.Split(',')[0]}");
            var asm = reg.Assembly ?? _project.AssemblyName;
            if (asm is null)
            {
                continue;
            }

            var t = Types.FindType(asm, fullName);
            if (t is not null)
            {
                return new ResolvedControl { Type = t, DesignerTypeName = "Global." + t.FullName };
            }
        }

        if (!prefixKnown)
        {
            return new ResolvedControl { Error = $"Unknown tag prefix '{prefix}' (no Register directive or web.config <controls> entry)." };
        }

        var missingAsm = _registrations
            .Where(r => r.TagPrefix.Equals(prefix, StringComparison.OrdinalIgnoreCase) && r.Namespace is not null)
            .Select(r => (r.Assembly ?? _project.AssemblyName ?? "?").Split(',')[0].Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(a => !Types.HasAssembly(a))
            .ToList();
        var detail = missingAsm.Count > 0 ? $" Assembly not found: {string.Join(", ", missingAsm)} (build the project or check HintPath references)." : "";
        return new ResolvedControl { Error = $"Cannot resolve type for <{element.Name}>; tried {string.Join("; ", tried.DefaultIfEmpty("no namespaces"))}.{detail}" };
    }

    private ResolvedControl ResolveUserControl(TagRegistration reg)
    {
        var path = _project.ResolveVirtualPath(reg.Src!, reg.Origin == "page" ? _markupFile : reg.Origin);
        var ucBase = Types.FindType("System.Web", "System.Web.UI.UserControl");
        if (!File.Exists(path))
        {
            return new ResolvedControl { Type = ucBase, IsUserControl = true, UserControlPath = path, Error = $"User control '{reg.Src}' not found ({path})." };
        }

        var inherits = _project.GetInherits(path);
        if (string.IsNullOrEmpty(inherits))
        {
            // A code-less user control: Visual Studio types the field as the base class.
            return new ResolvedControl { Type = ucBase, IsUserControl = true, UserControlPath = path, DesignerTypeName = "Global.System.Web.UI.UserControl" };
        }

        return new ResolvedControl { Type = ucBase, IsUserControl = true, UserControlPath = path, DesignerTypeName = "Global." + inherits };
    }
}
