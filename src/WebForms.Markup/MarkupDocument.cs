namespace WebForms.Markup;

/// <summary>How the body of an element is parsed.</summary>
public enum BodyMode
{
    /// <summary>Children are controls / literal markup (ParseChildren(false), HTML containers, templates).</summary>
    Controls,

    /// <summary>Children are inner properties or collection items (ParseChildren(true), non-control objects).</summary>
    Properties,

    /// <summary>The element never has a body (e.g. <c>&lt;input runat="server"&gt;</c> without a closing tag).</summary>
    Empty,
}

public enum ElementRole
{
    /// <summary>A server control (runat="server", or implicitly created by its parent's builder, e.g. &lt;tr&gt; inside &lt;table runat="server"&gt;).</summary>
    Control,

    /// <summary>An inner property such as &lt;ItemTemplate&gt;, &lt;Columns&gt; or &lt;HeaderStyle&gt;.</summary>
    Property,

    /// <summary>An item inside a collection property / default property (e.g. asp:ListItem, asp:BoundField, ajax:TabPanel).</summary>
    Item,
}

/// <summary>Answers the questions the parser needs that depend on control types.</summary>
public interface IMarkupSchema
{
    /// <summary>Called when a control or item element has been created. Resolve its type and return how its body parses.</summary>
    BodyMode OpenElement(MarkupElement element);

    /// <summary>Called for a non-runat tag inside a control whose body is Controls: does the parent's ControlBuilder turn it into a control (e.g. &lt;tr&gt; under HtmlTable)?</summary>
    bool IsImplicitChildControl(MarkupElement parent, MarkupToken tag);

    /// <summary>
    /// Called for a tag inside an element whose body is Properties. Returns the child's role and an initial annotation.
    /// For Property children the body mode is used as-is; for Item children <see cref="OpenElement"/> is then called
    /// (with the annotation already set, e.g. as a type hint) and decides the body.
    /// </summary>
    PropertyChild ClassifyPropertyChild(MarkupElement parent, MarkupToken tag);
}

/// <param name="Role">Role of the child element.</param>
/// <param name="Body">Body mode, used as-is for Property children.</param>
/// <param name="Annotation">Initial annotation (e.g. a type hint for an Item).</param>
/// <param name="IsLiteral">
/// The tag is not an element at all: the parent's builder drops it as literal content (a collection marked
/// <c>[IgnoreUnknownContent]</c>, such as <c>&lt;tfoot&gt;</c> or <c>&lt;colgroup&gt;</c> inside
/// <c>&lt;table runat="server"&gt;</c>). Its children are still classified against the same parent.
/// </param>
public readonly record struct PropertyChild(ElementRole Role, BodyMode Body, object? Annotation, bool IsLiteral = false)
{
    public static PropertyChild Literal => new(ElementRole.Item, BodyMode.Properties, null, IsLiteral: true);
}

public sealed class MarkupElement
{
    private List<MarkupElement>? _children;

    public MarkupElement(MarkupToken startTag, ElementRole role, MarkupElement? parent)
    {
        StartTag = startTag;
        Role = role;
        Parent = parent;
        int colon = startTag.Name.IndexOf(':');
        Prefix = colon < 0 ? null : startTag.Name[..colon];
        LocalName = colon < 0 ? startTag.Name : startTag.Name[(colon + 1)..];
    }

    public MarkupToken StartTag { get; }
    public MarkupToken? EndTag { get; internal set; }
    public ElementRole Role { get; }
    public MarkupElement? Parent { get; }
    public BodyMode Body { get; internal set; }

    public string Name => StartTag.Name;
    public string? Prefix { get; }
    public string LocalName { get; }
    public SourcePosition Position => StartTag.Start;
    public bool IsRunatServer => MarkupTokenizer.IsRunatServer(StartTag);
    public string? Id => StartTag.GetAttributeValue("id");

    public IReadOnlyList<MarkupElement> Children => (IReadOnlyList<MarkupElement>?)_children ?? [];

    /// <summary>Schema-defined data (e.g. the resolved control type).</summary>
    public object? Annotation { get; set; }

    public string? GetAttributeValue(string name) => StartTag.GetAttributeValue(name);
    public MarkupAttribute? GetAttribute(string name) => StartTag.GetAttribute(name);

    internal void AddChild(MarkupElement child) => (_children ??= []).Add(child);

    public IEnumerable<MarkupElement> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants())
            {
                yield return d;
            }
        }
    }

    public override string ToString() => $"<{Name}> {Role} {Position}";
}

public sealed record MarkupDiagnostic(SourcePosition Position, string Message, bool IsError);

public sealed class MarkupDocument
{
    private MarkupDocument(string text, IReadOnlyList<MarkupToken> tokens, LineMap lines)
    {
        Text = text;
        Tokens = tokens;
        Lines = lines;
    }

    public string Text { get; }
    public IReadOnlyList<MarkupToken> Tokens { get; }
    public LineMap Lines { get; }
    public List<MarkupToken> Directives { get; } = [];
    public List<MarkupElement> Roots { get; } = [];
    public List<MarkupDiagnostic> Diagnostics { get; } = [];

    /// <summary>The Page / Control / Master directive (the first one found).</summary>
    public MarkupToken? MainDirective => Directives.FirstOrDefault(d =>
        d.Name.Equals("Page", StringComparison.OrdinalIgnoreCase) ||
        d.Name.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
        d.Name.Equals("Master", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<MarkupToken> GetDirectives(string name) =>
        Directives.Where(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<MarkupElement> AllElements()
    {
        foreach (var r in Roots)
        {
            yield return r;
            foreach (var d in r.Descendants())
            {
                yield return d;
            }
        }
    }

    /// <summary>Tokenizes only, collecting directives (no element tree). Cheap; used to read Register/Control directives of referenced files.</summary>
    public static MarkupDocument ParseDirectivesOnly(string text)
    {
        var tokenizer = new MarkupTokenizer(text);
        var tokens = tokenizer.ReadAll();
        var doc = new MarkupDocument(text, tokens, tokenizer.Lines);
        doc.Directives.AddRange(tokens.Where(t => t.Kind == TokenKind.Directive));
        return doc;
    }

    /// <summary>
    /// Parses markup into a tree of server elements. Plain HTML tags (no runat="server") are literal text,
    /// exactly as in ASP.NET, so they do not appear in the tree — except where a ControlBuilder interprets them
    /// (inner properties, collection items, implicit table rows/cells).
    /// </summary>
    public static MarkupDocument Parse(string text, IMarkupSchema schema)
    {
        var tokenizer = new MarkupTokenizer(text);
        var tokens = tokenizer.ReadAll();
        var doc = new MarkupDocument(text, tokens, tokenizer.Lines);
        doc.Build(schema);
        return doc;
    }

    /// <summary>Builds the tree from an already tokenized document (from <see cref="ParseDirectivesOnly"/>), without tokenizing again.</summary>
    public static MarkupDocument Parse(MarkupDocument tokenized, IMarkupSchema schema)
    {
        var doc = new MarkupDocument(tokenized.Text, tokenized.Tokens, tokenized.Lines);
        doc.Build(schema);
        return doc;
    }

    private sealed class Frame(MarkupElement? element, BodyMode mode)
    {
        public MarkupElement? Element { get; } = element;
        public BodyMode Mode { get; } = mode;
        public int RepeatCount { get; set; }

        /// <summary>Names of literal tags dropped inside a Properties body, so their end tags are expected.</summary>
        public HashSet<string>? LiteralTags { get; set; }
    }

    private void Build(IMarkupSchema schema)
    {
        var stack = new Stack<Frame>();
        stack.Push(new Frame(null, BodyMode.Controls));

        foreach (var token in Tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Directive:
                    Directives.Add(token);
                    break;

                case TokenKind.StartTag:
                    OnStartTag(token, stack, schema);
                    break;

                case TokenKind.EndTag:
                    OnEndTag(token, stack);
                    break;
            }
        }

        while (stack.Count > 1)
        {
            var f = stack.Pop();
            Diagnostics.Add(new MarkupDiagnostic(f.Element!.Position, $"Server tag <{f.Element.Name}> is not closed.", true));
        }
    }

    private void OnStartTag(MarkupToken token, Stack<Frame> stack, IMarkupSchema schema)
    {
        var frame = stack.Peek();
        var parent = frame.Element;

        MarkupElement element;
        BodyMode body;

        if (frame.Mode == BodyMode.Properties)
        {
            var pc = schema.ClassifyPropertyChild(parent!, token);
            if (pc.IsLiteral)
            {
                if (!token.SelfClosing)
                {
                    if (token.Name.Equals(parent!.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        frame.RepeatCount++;
                    }

                    (frame.LiteralTags ??= new(StringComparer.OrdinalIgnoreCase)).Add(token.Name);
                }

                return;
            }

            element = new MarkupElement(token, pc.Role, parent) { Annotation = pc.Annotation };
            body = pc.Role == ElementRole.Property ? pc.Body : schema.OpenElement(element);
        }
        else
        {
            if (token.Name.Equals("script", StringComparison.OrdinalIgnoreCase) && MarkupTokenizer.IsRunatServer(token))
            {
                return; // server code block, not a control (its body was consumed by the tokenizer)
            }

            bool server = MarkupTokenizer.IsRunatServer(token) ||
                          (parent is not null && schema.IsImplicitChildControl(parent, token));
            if (!server)
            {
                // Literal HTML. ASP.NET counts nested same-name tags so that </div> closes the right server <div>.
                if (parent is not null && !token.SelfClosing &&
                    token.Name.Equals(parent.Name, StringComparison.OrdinalIgnoreCase))
                {
                    frame.RepeatCount++;
                }

                return;
            }

            element = new MarkupElement(token, ElementRole.Control, parent);
            body = schema.OpenElement(element);
        }

        element.Body = body;
        if (parent is null)
        {
            Roots.Add(element);
        }
        else
        {
            parent.AddChild(element);
        }

        if (!token.SelfClosing && body != BodyMode.Empty)
        {
            stack.Push(new Frame(element, body));
        }
    }

    private void OnEndTag(MarkupToken token, Stack<Frame> stack)
    {
        var frame = stack.Peek();
        if (frame.Element is null || !token.Name.Equals(frame.Element.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (frame.Mode == BodyMode.Properties && frame.Element is not null && frame.LiteralTags?.Contains(token.Name) != true)
            {
                Diagnostics.Add(new MarkupDiagnostic(token.Start, $"Unexpected end tag </{token.Name}> inside <{frame.Element.Name}>.", false));
            }

            return; // literal end tag
        }

        if (frame.RepeatCount > 0)
        {
            frame.RepeatCount--;
            return;
        }

        frame.Element.EndTag = token;
        stack.Pop();
    }
}
