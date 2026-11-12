using WebForms.Designer;
using WebForms.LanguageServer.Protocol;
using WebForms.Markup;

namespace WebForms.LanguageServer.Completion;

/// <summary>What the client can render, from its initialize capabilities.</summary>
/// <param name="EditRangeDefault">The client takes <c>itemDefaults.editRange</c> (LSP 3.17): the range is sent once per list.</param>
public sealed record ClientOptions(bool Snippets = false, bool Markdown = false, bool EditRangeDefault = false);

/// <summary>
/// Completion in markup: tags (controls from the registrations, inner properties, collection items), attributes
/// (properties, <c>Prop-Sub</c> properties, events), values (enums, booleans, handler Subs with a matching signature,
/// control IDs, ContentPlaceHolder IDs) and directives, all from the same metadata the designer generator reads.
/// <para>
/// Every item carries a <see cref="TextEdit"/> over the partial word, so a client never has to guess where the word
/// starts (<c>:</c> and <c>-</c> are not keyword characters for most). Documentation is left to
/// <see cref="Resolve"/>: the first response stays small and needs no XML documentation.
/// </para>
/// </summary>
public sealed class CompletionProvider(WebFormsWorkspace workspace)
{
    /// <summary>Sent as the server's completion trigger characters.</summary>
    public static readonly string[] TriggerCharacters = ["<", ":", " ", "\"", "'", "=", "@", "/", ".", ","];

    /// <summary>User settings (the server replaces them on initialize and didChangeConfiguration).</summary>
    public ServerSettings Settings { get; set; } = new();

    private static readonly string[] DirectiveNames =
        ["Page", "Control", "Master", "Register", "Import", "Assembly", "MasterType", "PreviousPageType", "Reference", "OutputCache", "Implements"];

    private static readonly string[] TemplateDirectiveAttributes =
    [
        "Language", "AutoEventWireup", "CodeBehind", "CodeFile", "Inherits", "ClassName", "CodeFileBaseClass", "EnableViewState",
        "ViewStateMode", "ClientIDMode", "EnableTheming", "Debug", "Strict", "Explicit", "CompilationMode", "Description",
        "CompilerOptions", "LinePragmas", "WarningLevel", "Src",
    ];

    private static readonly Dictionary<string, string[]> DirectiveAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Page"] =
        [
            .. TemplateDirectiveAttributes, "MasterPageFile", "Title", "Async", "AsyncTimeout", "Buffer", "ContentType", "Culture", "UICulture",
            "EnableEventValidation", "EnableSessionState", "EnableViewStateMac", "ErrorPage", "MaintainScrollPositionOnPostback", "MetaDescription",
            "MetaKeywords", "ResponseEncoding", "StyleSheetTheme", "Theme", "Trace", "TraceMode", "ValidateRequest", "ViewStateEncryptionMode",
            "AspCompat", "CodePage", "LCID", "Transaction",
        ],
        ["Control"] = [.. TemplateDirectiveAttributes, "TargetSchema"],
        ["Master"] = [.. TemplateDirectiveAttributes, "MasterPageFile"],
        ["Register"] = ["TagPrefix", "TagName", "Src", "Namespace", "Assembly"],
        ["Import"] = ["Namespace"],
        ["Assembly"] = ["Name", "Src"],
        ["MasterType"] = ["TypeName", "VirtualPath"],
        ["PreviousPageType"] = ["TypeName", "VirtualPath"],
        ["Reference"] = ["Page", "Control", "VirtualPath"],
        ["OutputCache"] = ["Duration", "Location", "VaryByParam", "VaryByControl", "VaryByCustom", "VaryByHeader", "VaryByContentEncoding", "CacheProfile", "NoStore", "Shared", "SqlDependency", "ProviderName"],
        ["Implements"] = ["Interface"],
    };

    private static readonly string[] Booleans = ["true", "false"];

    private static readonly Dictionary<string, string[]> DirectiveValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Language"] = ["vb", "C#"],
        ["ViewStateMode"] = ["Inherit", "Enabled", "Disabled"],
        ["ClientIDMode"] = ["Inherit", "AutoID", "Predictable", "Static"],
        ["EnableSessionState"] = ["true", "false", "ReadOnly"],
        ["CompilationMode"] = ["Always", "Auto", "Never"],
        ["ViewStateEncryptionMode"] = ["Auto", "Always", "Never"],
        ["TraceMode"] = ["SortByTime", "SortByCategory"],
        ["Location"] = ["Any", "Client", "Downstream", "Server", "None", "ServerAndClient"],
        ["Transaction"] = ["Disabled", "NotSupported", "Supported", "Required", "RequiresNew"],
        ["VaryByParam"] = ["None", "*"],
    };

    private static readonly HashSet<string> BooleanDirectiveAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AutoEventWireup", "EnableViewState", "EnableTheming", "Debug", "Strict", "Explicit", "LinePragmas", "Async", "Buffer",
        "EnableEventValidation", "EnableViewStateMac", "MaintainScrollPositionOnPostback", "Trace", "ValidateRequest", "AspCompat",
        "NoStore", "Shared",
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".claude", "packages", "aspnet_client",
    };

    /// <summary>Completion at a position in a markup file, or null where this server has nothing to offer.</summary>
    public CompletionList? Complete(string path, LspPosition position, ClientOptions client)
    {
        var analysis = workspace.Analyze(path);
        if (analysis is null)
        {
            return null;
        }

        var text = analysis.Document.Text;
        int offset = analysis.Document.ToOffset(position);
        var cursor = MarkupCursor.At(text, analysis.Tokens, offset);
        if (cursor.Kind == CursorKind.None)
        {
            return null;
        }

        var ctx = new Context(analysis, cursor, client, FileId(path), analysis.Document.ToRange(cursor.ReplaceStart, offset - cursor.ReplaceStart));
        var items = cursor.Kind switch
        {
            CursorKind.TagName => TagItems(ctx),
            CursorKind.EndTagName => EndTagItems(ctx),
            CursorKind.AttributeName => AttributeItems(ctx),
            CursorKind.AttributeValue => ValueItems(ctx),
            CursorKind.DirectiveName => DirectiveNames.Select(n => Simple(ctx, n, CompletionItemKind.Keyword)).ToList(),
            CursorKind.DirectiveAttributeName => DirectiveAttributeItems(ctx),
            CursorKind.DirectiveAttributeValue => DirectiveValueItems(ctx),
            _ => [],
        };

        if (items.Count == 0)
        {
            return null;
        }

        var result = new CompletionItem[items.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var item = items[i];
            if (item.Ref is { } r)
            {
                item = item with { Ref = null, Data = Intern(r) };
            }

            if (client.EditRangeDefault && item.TextEdit is { } edit)
            {
                item = item with { TextEdit = null, TextEditText = edit.NewText == item.Label ? null : edit.NewText };
            }

            result[i] = item;
        }

        return new CompletionList(false, result, client.EditRangeDefault ? new CompletionItemDefaults(ctx.Range) : null);
    }

    /// <summary>Adds the detail and documentation of an item from <see cref="Complete"/>.</summary>
    public CompletionItem Resolve(CompletionItem item, ClientOptions client)
    {
        if (item.Data is not { } id || id < 0 || id >= _refs.Count)
        {
            return item; // not ours, or from another server process
        }

        var d = _refs[id];
        var types = workspace.GetProject(_files[d.F]).Types;
        string? detail = item.Detail;
        string memberId;
        switch (d.K)
        {
            case "type":
                memberId = "T:" + d.T;
                detail = "Class " + d.T;
                break;
            case "prop":
                memberId = $"P:{d.T}.{d.M}";
                if (types.FindType(d.A, d.T)?.GetProperty(d.M!) is { } p)
                {
                    detail = $"Property {p.Name} As {p.TypeName}" + (p.DefaultValue is { } dv ? $" (default {dv})" : "");
                }

                break;
            case "event":
                memberId = $"E:{d.T}.{d.M}";
                if (types.FindType(d.A, d.T)?.GetEvent(d.M!) is { } e)
                {
                    detail = $"Event {e.Name} As {e.HandlerTypeName}";
                }

                break;
            case "enum":
                memberId = $"F:{d.T}.{d.M}";
                break;
            default:
                return item;
        }

        var summary = XmlDocumentation.Summary(types.GetAssemblyPath(d.A), memberId);
        return item with
        {
            Detail = detail,
            Documentation = summary is null ? null : new MarkupContent(client.Markdown ? "markdown" : "plaintext", client.Markdown ? summary : summary.Replace("`", "", StringComparison.Ordinal)),
        };
    }

    /// <summary>
    /// Items name their file by a small id rather than its path: a path in each of hundreds of items (a GridView has
    /// over 300 attributes) would be most of the response.
    /// </summary>
    private int FileId(string path)
    {
        if (!_fileIds.TryGetValue(path, out int id))
        {
            id = _files.Count;
            _files.Add(path);
            _fileIds[path] = id;
        }

        return id;
    }

    /// <summary>Resolve data, interned: each distinct (file, member) gets one id for the life of the server.</summary>
    private int Intern(CompletionData data)
    {
        if (!_refIds.TryGetValue(data, out int id))
        {
            id = _refs.Count;
            _refs.Add(data);
            _refIds[data] = id;
        }

        return id;
    }

    private readonly List<CompletionData> _refs = [];
    private readonly Dictionary<CompletionData, int> _refIds = [];
    private readonly List<string> _files = [];
    private readonly Dictionary<string, int> _fileIds = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Context(MarkupAnalysis Analysis, MarkupCursor Cursor, ClientOptions Client, int FileId, LspRange Range)
    {
        public string Path => Analysis.Path;
        public TypeSystem Types => Analysis.Project.Types;
    }

    /// <summary>A tag that can be written in this page: from a namespace registration, or a user control.</summary>
    private sealed record TagEntry(string Tag, ControlType? Type, string? UserControlPath);

    /// <summary>What a tag is: a control, user control, inner property object, collection item, or a plain HTML element.</summary>
    private sealed record TagTarget(ControlType? Type, bool IsControl, bool IsHtml, string? UserControlPath, MarkupElement? Element);

    // ---- tags ---------------------------------------------------------------------------------------------------

    private static List<CompletionItem> TagItems(Context ctx)
    {
        var parent = ctx.Analysis.ElementContaining(ctx.Cursor.TagStart);
        var items = new List<CompletionItem>();
        switch (ChildContext(parent))
        {
            case ChildKind.Controls:
                foreach (var e in Catalog(ctx.Analysis))
                {
                    if (e.UserControlPath is not null || e.Type?.IsControl == true)
                    {
                        items.Add(TagItem(ctx, e, control: true));
                    }
                }

                break;
            case ChildKind.Items:
                if (ItemType(parent!) is { } itemType)
                {
                    foreach (var e in Catalog(ctx.Analysis))
                    {
                        if (e.Type is not null && e.Type.IsSubclassOf(itemType))
                        {
                            items.Add(TagItem(ctx, e, control: e.Type.IsControl));
                        }
                    }
                }

                break;
            case ChildKind.InnerProperties:
                if (OwnerType(parent!) is { } owner)
                {
                    foreach (var p in owner.Properties.Where(p => p.IsInner))
                    {
                        items.Add(Item(ctx, p.Name, CompletionItemKind.Property, p.Name, detail: p.IsTemplate ? "ITemplate" : p.TypeName,
                            data: new CompletionData("prop", ctx.FileId, p.DeclaringAssembly, p.DeclaringType, p.Name)));
                    }
                }

                break;
        }

        return items;
    }

    /// <summary>
    /// After <c>&lt;/</c>: the end tag of the innermost element open at the cursor, when that is a server element
    /// (control, inner property, item); the HTML server closes HTML elements. The open elements are worked out from
    /// the tags before the cursor only, as an editor does, not from the parsed tree: the tree pairs a new
    /// <c>&lt;asp:Panel&gt;</c> inside another Panel with the outer Panel's end tag further down.
    /// </summary>
    private static List<CompletionItem> EndTagItems(Context ctx)
    {
        var isServer = IsServerStartTag(ctx.Analysis);
        if (InnermostOpenTag(ctx.Analysis.Tokens, ctx.Cursor.TagStart, isServer) is not { } open || !isServer(open))
        {
            return [];
        }

        string end = "/" + open.Name + ">";
        return [Item(ctx, end, CompletionItemKind.Keyword, end, detail: "close <" + open.Name + ">")];
    }

    /// <summary>Whether a start tag is one of the parsed tree's elements: a control, inner property or item.</summary>
    public static Func<MarkupToken, bool> IsServerStartTag(MarkupAnalysis analysis)
    {
        var offsets = analysis.Tree.AllElements().Select(e => e.StartTag.Start.Offset).ToHashSet();
        return t => offsets.Contains(t.Start.Offset);
    }

    /// <summary>HTML elements that have no end tag.</summary>
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>
    /// The start tag of the innermost element still open at <paramref name="offset"/>, from the tags before it only:
    /// <list type="bullet">
    /// <item>a start tag opens, unless it is self-closing or a void HTML element;</item>
    /// <item>an end tag closes the nearest open element of that name (case-insensitive, as ASP.NET) and anything left
    /// open inside it; with no open match it is ignored;</item>
    /// <item>an end tag never closes across a server element (<paramref name="isServer"/>): ASP.NET matches an end tag
    /// against the innermost server element only, anything else is literal text, so a stray <c>&lt;/div&gt;</c> in a
    /// Panel leaves the Panel open even when a <c>&lt;div runat="server"&gt;</c> is open outside it (real pages
    /// have HTML unbalanced across <c>&lt;% If %&gt;</c> branches);</item>
    /// <item>in a client <c>&lt;script&gt;</c> / <c>&lt;style&gt;</c> body only server tags count (ASP.NET parses
    /// <c>&lt;asp:ContentPlaceHolder&gt;</c> there, while <c>'&lt;div&gt;'</c> in a JS string is text).</item>
    /// </list>
    /// </summary>
    public static MarkupToken? InnermostOpenTag(IReadOnlyList<MarkupToken> tokens, int offset, Func<MarkupToken, bool> isServer)
    {
        var open = new List<MarkupToken>();
        int raw = -1; // index in `open` of the client script / style whose body we are in
        foreach (var t in tokens)
        {
            if (t.End > offset)
            {
                break;
            }

            if (t.Kind == TokenKind.StartTag)
            {
                bool server = isServer(t);
                if (raw >= 0 && !server)
                {
                    continue;
                }

                if (!t.SelfClosing && (server || !VoidElements.Contains(t.Name)))
                {
                    open.Add(t);
                    if (raw < 0 && !server && (t.Name.Equals("script", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("style", StringComparison.OrdinalIgnoreCase)))
                    {
                        raw = open.Count - 1;
                    }
                }
            }
            else if (t.Kind == TokenKind.EndTag)
            {
                int match = open.FindLastIndex(o => o.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
                if (match < 0)
                {
                    continue;
                }

                bool acrossServer = open.Skip(match + 1).Any(isServer);
                bool inRaw = raw >= 0 && match != raw && !isServer(open[match]);
                if (acrossServer || inRaw)
                {
                    continue;
                }

                open.RemoveRange(match, open.Count - match);
                if (raw >= open.Count)
                {
                    raw = -1;
                }
            }
        }

        return open.Count > 0 ? open[^1] : null;
    }

    private enum ChildKind
    {
        None,
        Controls,
        Items,
        InnerProperties,
    }

    private static ChildKind ChildContext(MarkupElement? parent)
    {
        if (parent is null)
        {
            return ChildKind.Controls;
        }

        return parent.Body switch
        {
            BodyMode.Controls => ChildKind.Controls,
            BodyMode.Properties => parent.Annotation switch
            {
                ResolvedControl { Type: { } t } when t.ParseChildren.DefaultProperty is not null => ChildKind.Items,
                ResolvedControl { Type: not null } => ChildKind.InnerProperties,
                PropertyAnnotation { Property.Kind: InnerPropertyKind.Collection } => ChildKind.Items,
                PropertyAnnotation { Property.PropertyType: not null } => ChildKind.InnerProperties,
                _ => ChildKind.None,
            },
            _ => ChildKind.None,
        };
    }

    private static ControlType? ItemType(MarkupElement parent) => parent.Annotation switch
    {
        ResolvedControl { Type: { } t } when t.ParseChildren.DefaultProperty is { } dp => t.GetInnerProperty(dp)?.CollectionItemType,
        PropertyAnnotation { Property: { } p } => p.CollectionItemType,
        _ => null,
    };

    private static ControlType? OwnerType(MarkupElement parent) => parent.Annotation switch
    {
        ResolvedControl rc => rc.Type,
        PropertyAnnotation pa => pa.Property?.PropertyType,
        _ => null,
    };

    private static CompletionItem TagItem(Context ctx, TagEntry e, bool control)
    {
        // A fresh tag gets ID and runat with snippets; renaming an existing tag (it has attributes) only the name.
        string text = e.Tag;
        int? format = null;
        if (control && ctx.Client.Snippets && ctx.Cursor.Attributes.Count == 0)
        {
            text = e.Tag + " ID=\"$1\" runat=\"server\"$0";
            format = Protocol.InsertTextFormat.Snippet;
        }

        return Item(ctx, e.Tag, CompletionItemKind.Class, text, format,
            detail: e.UserControlPath is { } uc ? VirtualPath(ctx.Analysis.Project, uc) : e.Type?.FullName,
            data: e.Type is { } t && e.UserControlPath is null ? new CompletionData("type", ctx.FileId, t.AssemblyName, t.FullName) : null);
    }

    /// <summary>Every tag the page's registrations make available, in registration order (first registration wins).</summary>
    private static List<TagEntry> Catalog(MarkupAnalysis analysis)
    {
        var project = analysis.Project;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<TagEntry>();
        ControlType? ucBase = null;
        foreach (var reg in analysis.Schema.Registrations)
        {
            if (reg.TagPrefix.Length == 0)
            {
                continue;
            }

            if (reg.IsUserControl)
            {
                var tag = reg.TagPrefix + ":" + reg.TagName;
                if (seen.Add(tag))
                {
                    ucBase ??= project.Types.FindType("System.Web", "System.Web.UI.UserControl");
                    list.Add(new TagEntry(tag, ucBase, project.ResolveVirtualPath(reg.Src!, reg.Origin == "page" ? analysis.Path : reg.Origin)));
                }

                continue;
            }

            if (reg.Namespace is null || (reg.Assembly ?? project.AssemblyName) is not { } assembly)
            {
                continue;
            }

            foreach (var t in project.Types.TypesInNamespace(assembly, reg.Namespace))
            {
                var tag = reg.TagPrefix + ":" + t.Name;
                if (seen.Add(tag))
                {
                    list.Add(new TagEntry(tag, t, null));
                }
            }
        }

        return list;
    }

    // ---- attributes ---------------------------------------------------------------------------------------------

    private List<CompletionItem> AttributeItems(Context ctx)
    {
        var target = Target(ctx);
        var present = new HashSet<string>(ctx.Cursor.Attributes.Where(a => a != ctx.Cursor.Attribute).Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        var items = new List<CompletionItem>();

        void Add(string name, int kind, string sort, string? detail = null, CompletionData? data = null)
        {
            if (present.Add(name))
            {
                items.Add(AttributeItem(ctx, name, kind, sort, detail, data));
            }
        }

        if (target.IsHtml)
        {
            Add("runat", CompletionItemKind.Keyword, "0");
            if (target.Type is { } html && IsRunatServer(ctx.Cursor))
            {
                AddEvents(html);
            }

            return items;
        }

        if (target.IsControl)
        {
            Add("ID", CompletionItemKind.Property, "0");
            Add("runat", CompletionItemKind.Keyword, "0");
        }

        if (target.UserControlPath is { } ucPath && UserControlCodeBehind(ctx.Analysis.Project, ucPath) is var (vbPath, className) && workspace.GetVbFile(vbPath) is { } vb)
        {
            foreach (var (name, kind, typeName) in vb.PublicMembers(className))
            {
                if (kind == "Event")
                {
                    Add("On" + name, CompletionItemKind.Event, "2", typeName);
                }
                else
                {
                    Add(name, CompletionItemKind.Property, "1", typeName);
                }
            }
        }

        if (target.Type is not { } type)
        {
            return items;
        }

        foreach (var p in type.Properties)
        {
            if (p.Settable && !p.IsTemplate)
            {
                Add(p.Name, CompletionItemKind.Property, "1", p.TypeName, PropData(ctx, p));
            }
        }

        AddEvents(type);

        AddSubProperties(type, "", depth: 0);
        return items;

        // Font-Bold, HeaderStyle-CssClass and, one level further, HeaderStyle-Font-Bold (ASP.NET accepts any depth).
        void AddSubProperties(ControlType owner, string prefix, int depth)
        {
            foreach (var p in owner.Properties)
            {
                if (!p.IsContent || p.Type is not { } sub)
                {
                    continue;
                }

                foreach (var s in sub.Properties)
                {
                    if (s.Settable && !s.IsTemplate)
                    {
                        Add(prefix + p.Name + "-" + s.Name, CompletionItemKind.Property, depth == 0 ? "3" : "4", s.TypeName, PropData(ctx, s));
                    }
                }

                if (depth == 0)
                {
                    AddSubProperties(sub, prefix + p.Name + "-", depth + 1);
                }
            }
        }

        void AddEvents(ControlType t)
        {
            foreach (var e in t.Events)
            {
                Add("On" + e.Name, CompletionItemKind.Event, "2", null, new CompletionData("event", ctx.FileId, e.DeclaringAssembly, e.DeclaringType, e.Name));
            }
        }
    }

    private static CompletionData PropData(Context ctx, ControlProperty p) => new("prop", ctx.FileId, p.DeclaringAssembly, p.DeclaringType, p.Name);

    private static CompletionItem AttributeItem(Context ctx, string name, int kind, string sort, string? detail, CompletionData? data)
    {
        // Renaming an attribute that already has a value: just the name. Otherwise name="" with the cursor inside
        // (snippet), or name= without snippets (typing the quote triggers value completion).
        bool hasValue = ctx.Cursor.Attribute is { ValueStart: >= 0 };
        string text = hasValue ? name : ctx.Client.Snippets ? name + "=\"$1\"" : name + "=";
        return Item(ctx, name, kind, text, ctx.Client.Snippets && !hasValue ? Protocol.InsertTextFormat.Snippet : null, sort + name, detail, data);
    }

    private static bool IsRunatServer(MarkupCursor cursor) =>
        cursor.Attributes.Any(a => a.Name.Equals("runat", StringComparison.OrdinalIgnoreCase) && a.Value?.Trim().Equals("server", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>What the tag at the cursor is: from the parsed tree when the tag is complete, else from its name and the enclosing element.</summary>
    private static TagTarget Target(Context ctx)
    {
        var a = ctx.Analysis;
        string name = ctx.Cursor.TagName;
        var element = a.Tree.AllElements().FirstOrDefault(e => e.StartTag.Start.Offset == ctx.Cursor.TagStart);
        if (element is not null)
        {
            switch (element.Annotation)
            {
                case ResolvedControl rc:
                    bool html = element.Prefix is null && element.Role == ElementRole.Control;
                    return new TagTarget(rc.Type, rc.IsControl && !html, html, rc.UserControlPath, element);
                case PropertyAnnotation pa:
                    return new TagTarget(pa.IsTemplate ? null : pa.Property?.PropertyType, false, false, null, element);
            }
        }

        var parent = a.ElementContaining(ctx.Cursor.TagStart);
        switch (ChildContext(parent))
        {
            case ChildKind.InnerProperties:
                return new TagTarget(OwnerType(parent!)?.GetProperty(name) is { IsTemplate: false } p ? p.Type : null, false, false, null, null);
            case ChildKind.Items or ChildKind.Controls when name.Contains(':', StringComparison.Ordinal):
                var entry = Catalog(a).FirstOrDefault(e => e.Tag.Equals(name, StringComparison.OrdinalIgnoreCase));
                return entry is null
                    ? new TagTarget(null, false, false, null, null)
                    : new TagTarget(entry.Type, entry.UserControlPath is not null || entry.Type?.IsControl == true, false, entry.UserControlPath, null);
            case ChildKind.Controls:
                var type = ctx.Cursor.Attributes.FirstOrDefault(x => x.Name.Equals("type", StringComparison.OrdinalIgnoreCase))?.Value;
                return new TagTarget(a.Schema.FindHtmlType(name, type), false, true, null, null);
            default:
                return new TagTarget(null, false, false, null, null);
        }
    }

    // ---- values -------------------------------------------------------------------------------------------------

    private List<CompletionItem> ValueItems(Context ctx)
    {
        var attribute = ctx.Cursor.Attribute!;
        string name = attribute.Name;
        if (name.Equals("runat", StringComparison.OrdinalIgnoreCase))
        {
            return [Value(ctx, "server", CompletionItemKind.Keyword)];
        }

        var target = Target(ctx);
        if (Navigator.ControlReferenceAttributes.Contains(name))
        {
            return ControlIds(ctx);
        }

        if (name.Equals("ContentPlaceHolderID", StringComparison.OrdinalIgnoreCase))
        {
            return Placeholders(ctx);
        }

        if (name.Length > 2 && name.StartsWith("On", StringComparison.OrdinalIgnoreCase))
        {
            if (target.Type?.GetEvent(name[2..]) is { } e)
            {
                return Handlers(ctx, e.Name, e.ArgsTypes);
            }

            if (target.UserControlPath is not null)
            {
                return Handlers(ctx, name[2..], []);
            }
        }

        if (target.Type is not { } type)
        {
            return [];
        }

        var property = type.ResolvePropertyPath(name);
        if (property?.Type is not { } valueType)
        {
            return [];
        }

        if (valueType.IsEnum)
        {
            return [.. valueType.EnumValues.Select(v => Value(ctx, v, CompletionItemKind.EnumMember, new CompletionData("enum", ctx.FileId, valueType.AssemblyName, valueType.FullName, v)))];
        }

        return valueType.FullName == "System.Boolean" ? [Value(ctx, "True", CompletionItemKind.Value), Value(ctx, "False", CompletionItemKind.Value)] : [];
    }

    /// <summary>
    /// Subs of the code-behind class that markup can wire to the event: accessible from the derived page class (not
    /// Private or Friend), with two parameters whose second is the event's args type or one of its base types (VB
    /// relaxed delegate conversion). Parameterless Subs only with <see cref="ServerSettings.ZeroArgHandlers"/>.
    /// </summary>
    private List<CompletionItem> Handlers(Context ctx, string eventName, IReadOnlyList<string> argsTypes)
    {
        if (ctx.Analysis.CodeBehindPath is not { } cb || workspace.GetVbFile(cb) is not { } vb)
        {
            return [];
        }

        var accepted = new HashSet<string>(argsTypes.Select(Simple), StringComparer.OrdinalIgnoreCase);
        var id = ctx.Cursor.Attributes.FirstOrDefault(a => a.Name.Equals("ID", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        var conventional = id is null ? null : id + "_" + eventName;
        var items = new List<CompletionItem>();
        foreach (var sub in vb.Subs(ctx.Analysis.ClassName))
        {
            if (!sub.CallableFromMarkup)
            {
                continue;
            }

            // Groups: the event's own args type (any two-parameter Sub for a user control event, whose args type is
            // unknown), then a base type (EventArgs), then parameterless Subs if enabled. Within a group, Subs not
            // already wired with Handles come first (Page_Load Handles Me.Load is rarely meant for markup too).
            char? group = sub.ParameterTypes.Count switch
            {
                2 when accepted.Count == 0 => '1',
                2 when sub.ParameterTypes[1].Equals(Simple(argsTypes[0]), StringComparison.OrdinalIgnoreCase) => '1',
                2 when accepted.Contains(sub.ParameterTypes[1]) => '2',
                0 when Settings.ZeroArgHandlers => '3',
                _ => null,
            };
            if (group is { } g)
            {
                string sort = !sub.HasHandles && sub.Name.Equals(conventional, StringComparison.OrdinalIgnoreCase)
                    ? "00" + sub.Name.ToLowerInvariant()
                    : $"{g}{(sub.HasHandles ? '1' : '0')}{sub.Name.ToLowerInvariant()}"; // clients compare sortText ordinally
                items.Add(Value(ctx, sub.Name, CompletionItemKind.Method, null, sort, sub.Signature));
            }
        }

        return items;

        static string Simple(string fullName) => fullName[(fullName.LastIndexOf('.') + 1)..];
    }

    private static List<CompletionItem> ControlIds(Context ctx)
    {
        var own = ctx.Cursor.Attributes.FirstOrDefault(a => a.Name.Equals("ID", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CompletionItem>();
        foreach (var el in ctx.Analysis.Tree.AllElements())
        {
            var id = el.Id?.Trim();
            if (id is null || !Navigator.IsIdentifier(id) || id.Equals(own, StringComparison.OrdinalIgnoreCase) || !seen.Add(id))
            {
                continue;
            }

            items.Add(Value(ctx, id, CompletionItemKind.Field, null, null, el.Annotation is ResolvedControl { Type: { } t } ? t.Name : el.Name));
        }

        return items;
    }

    private List<CompletionItem> Placeholders(Context ctx)
    {
        var master = ctx.Analysis.MainDirective?.GetAttributeValue("MasterPageFile")?.Trim();
        if (string.IsNullOrEmpty(master) || master.Contains("<%", StringComparison.Ordinal) ||
            workspace.GetDocument(ctx.Analysis.Project.ResolveVirtualPath(master, ctx.Path)) is not { } doc)
        {
            return [];
        }

        return [.. MarkupDocument.ParseDirectivesOnly(doc.Text).Tokens
            .Where(t => t.Kind == TokenKind.StartTag && t.Name.EndsWith("ContentPlaceHolder", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.GetAttributeValue("ID")?.Trim())
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => Value(ctx, id, CompletionItemKind.Field))];
    }

    // ---- directives ---------------------------------------------------------------------------------------------

    private static List<CompletionItem> DirectiveAttributeItems(Context ctx)
    {
        if (!DirectiveAttributes.TryGetValue(ctx.Cursor.TagName, out var names))
        {
            return [];
        }

        var present = new HashSet<string>(ctx.Cursor.Attributes.Where(a => a != ctx.Cursor.Attribute).Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        return [.. names.Where(present.Add).Select((n, i) => AttributeItem(ctx, n, CompletionItemKind.Property, i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), null, null))];
    }

    private List<CompletionItem> DirectiveValueItems(Context ctx)
    {
        string directive = ctx.Cursor.TagName;
        string name = ctx.Cursor.Attribute!.Name;
        var project = ctx.Analysis.Project;

        if (BooleanDirectiveAttributes.Contains(name))
        {
            return [.. Booleans.Select(v => Value(ctx, v, CompletionItemKind.Value))];
        }

        if (DirectiveValues.TryGetValue(name, out var values))
        {
            return [.. values.Select(v => Value(ctx, v, CompletionItemKind.Value))];
        }

        string? extension = (directive.ToUpperInvariant(), name.ToUpperInvariant()) switch
        {
            (_, "MASTERPAGEFILE") => ".master",
            ("MASTERTYPE" or "PREVIOUSPAGETYPE", "VIRTUALPATH") => directive.Equals("MasterType", StringComparison.OrdinalIgnoreCase) ? ".master" : ".aspx",
            ("REGISTER", "SRC") or ("REFERENCE", "CONTROL") => ".ascx",
            ("REFERENCE", "PAGE") => ".aspx",
            _ => null,
        };
        if (extension is not null)
        {
            return [.. ProjectFiles(project.ProjectDirectory, extension)
                .Where(f => !f.Equals(ctx.Path, StringComparison.OrdinalIgnoreCase))
                .Select(f => Value(ctx, VirtualPath(project, f), CompletionItemKind.File))];
        }

        if (name.Equals("CodeBehind", StringComparison.OrdinalIgnoreCase) || name.Equals("CodeFile", StringComparison.OrdinalIgnoreCase))
        {
            var file = System.IO.Path.GetFileName(ctx.Path) + ".vb";
            return File.Exists(ctx.Path + ".vb") ? [Value(ctx, file, CompletionItemKind.File)] : [];
        }

        if (name.Equals("Inherits", StringComparison.OrdinalIgnoreCase) && ctx.Analysis.CodeBehindPath is { } cb && workspace.GetVbFile(cb) is { } vb)
        {
            return [.. vb.QualifiedClassNames().Select(c => Value(ctx, string.IsNullOrEmpty(project.RootNamespace) ? c : project.RootNamespace + "." + c, CompletionItemKind.Class))];
        }

        return [];
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static CompletionItem Simple(Context ctx, string label, int kind) => Item(ctx, label, kind, label);

    /// <summary>A value: replaces the partial value; quoted when nothing (not even the quote) has been typed after <c>=</c>.</summary>
    private static CompletionItem Value(Context ctx, string value, int kind, CompletionData? data = null, string? sort = null, string? detail = null) =>
        Item(ctx, value, kind, ctx.Cursor.NeedsQuotes ? "\"" + value + "\"" : value, null, sort, detail, data,
            filterText: ctx.Cursor.NeedsQuotes ? "\"" + value + "\"" : value);

    private static CompletionItem Item(
        Context ctx, string label, int kind, string newText, int? format = null, string? sort = null, string? detail = null,
        CompletionData? data = null, string? filterText = null) =>
        new()
        {
            Label = label,
            Kind = kind,
            Detail = detail,
            SortText = sort,
            FilterText = filterText is null || filterText == label ? null : filterText,
            InsertTextFormat = format,
            TextEdit = new TextEdit(ctx.Range, newText),
            Ref = data,
        };

    private static string VirtualPath(ProjectContext project, string physical) =>
        "~/" + System.IO.Path.GetRelativePath(project.ProjectDirectory, physical).Replace('\\', '/');

    /// <summary>The code-behind file and class of a user control, from its directive.</summary>
    private static (string Path, string? ClassName)? UserControlCodeBehind(ProjectContext project, string ucPath)
    {
        var main = project.ReadDirectives(ucPath)?.MainDirective;
        var file = main?.GetAttributeValue("CodeBehind") ?? main?.GetAttributeValue("CodeFile");
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        var inherits = main!.GetAttributeValue("Inherits")?.Trim();
        return (System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ucPath)!, file.Trim()), inherits?[(inherits.LastIndexOf('.') + 1)..]);
    }

    /// <summary>Files with an extension under the project, skipping build output and tool directories, in path order.</summary>
    private static List<string> ProjectFiles(string root, string extension)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!ExcludedDirectories.Contains(System.IO.Path.GetFileName(sub)))
                    {
                        pending.Push(sub);
                    }
                }

                result.AddRange(Directory.EnumerateFiles(dir, "*" + extension).Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // skip unreadable directories
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
