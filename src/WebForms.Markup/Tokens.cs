namespace WebForms.Markup;

/// <summary>A position in a markup file. Line and column are 1-based; column counts UTF-16 code units.</summary>
public readonly record struct SourcePosition(int Offset, int Line, int Column)
{
    public override string ToString() => $"({Line},{Column})";
}

public sealed class MarkupAttribute
{
    public MarkupAttribute(string name, string? value, SourcePosition namePosition, SourcePosition? valuePosition, char quote)
    {
        Name = name;
        Value = value;
        NamePosition = namePosition;
        ValuePosition = valuePosition;
        Quote = quote;
    }

    public string Name { get; }

    /// <summary>The raw attribute value (without quotes), or null when the attribute has no value.</summary>
    public string? Value { get; }

    public SourcePosition NamePosition { get; }
    public SourcePosition? ValuePosition { get; }

    /// <summary>The quote character used, or '\0' if unquoted.</summary>
    public char Quote { get; }

    public bool IsDataBound => Value is not null && Value.TrimStart().StartsWith("<%", StringComparison.Ordinal);

    public override string ToString() => Value is null ? Name : $"{Name}=\"{Value}\"";
}

public enum TokenKind
{
    Text,
    /// <summary><c>&lt;%@ Name attr="..." %&gt;</c></summary>
    Directive,
    /// <summary><c>&lt;tag ...&gt;</c> or <c>&lt;tag ... /&gt;</c></summary>
    StartTag,
    /// <summary><c>&lt;/tag&gt;</c></summary>
    EndTag,
    /// <summary><c>&lt;% %&gt;</c>, <c>&lt;%= %&gt;</c>, <c>&lt;%: %&gt;</c>, <c>&lt;%# %&gt;</c>, <c>&lt;%$ %&gt;</c></summary>
    CodeBlock,
    /// <summary><c>&lt;%-- --%&gt;</c> — removed entirely by the ASP.NET parser.</summary>
    ServerComment,
    /// <summary>Body of a <c>&lt;script runat="server"&gt;</c> block.</summary>
    ServerScript,
}

public sealed class MarkupToken
{
    private static readonly IReadOnlyList<MarkupAttribute> NoAttributes = [];

    public MarkupToken(TokenKind kind, SourcePosition start, int length)
    {
        Kind = kind;
        Start = start;
        Length = length;
    }

    public TokenKind Kind { get; }
    public SourcePosition Start { get; }
    public int Length { get; }
    public int End => Start.Offset + Length;

    /// <summary>Tag name for Start/End tags, directive name for directives (empty for the implicit main directive, which is never the case in practice).</summary>
    public string Name { get; init; } = "";

    public SourcePosition NamePosition { get; init; }
    public IReadOnlyList<MarkupAttribute> Attributes { get; init; } = NoAttributes;
    public bool SelfClosing { get; init; }

    public MarkupAttribute? GetAttribute(string name)
    {
        foreach (var a in Attributes)
        {
            if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
        }

        return null;
    }

    public string? GetAttributeValue(string name) => GetAttribute(name)?.Value;

    public override string ToString() => $"{Kind} {Name} {Start}";
}
