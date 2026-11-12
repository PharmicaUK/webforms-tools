using WebForms.Markup;

namespace WebForms.LanguageServer.Completion;

public enum CursorKind
{
    /// <summary>Not somewhere this server completes (text, code blocks, client script/style, comments, finished end tags).</summary>
    None,
    TagName,
    /// <summary>After <c>&lt;/</c>: <see cref="MarkupCursor.ReplaceStart"/> is the '/'.</summary>
    EndTagName,
    AttributeName,
    AttributeValue,
    DirectiveName,
    DirectiveAttributeName,
    DirectiveAttributeValue,
}

/// <summary>An attribute of the tag being edited. <paramref name="ValueStart"/> is the first character after the quote (-1 without a value).</summary>
public sealed record CursorAttribute(string Name, int NameStart, string? Value, int ValueStart);

/// <summary>
/// What is being typed at an offset. Unlike <see cref="MarkupTokenizer"/>, which treats an incomplete tag as literal
/// text, this reads a tag up to the cursor however unfinished it is (<c>&lt;asp:Bu</c>, <c>&lt;asp:Button Te</c>,
/// <c>Text="ab</c>), so it works while the user types.
/// </summary>
/// <param name="TagStart">Offset of the <c>&lt;</c> of the tag or directive.</param>
/// <param name="Attributes">Every attribute of the tag, including those after the cursor.</param>
/// <param name="Attribute">The attribute whose name or value is under the cursor (null for a new attribute name).</param>
/// <param name="ReplaceStart">Start of the text a completion replaces: the partial name or value before the cursor.</param>
/// <param name="NeedsQuotes">The cursor is right after <c>=</c> with no quote yet: values are inserted with quotes.</param>
public sealed record MarkupCursor(
    CursorKind Kind,
    int Offset,
    int TagStart,
    string TagName,
    IReadOnlyList<CursorAttribute> Attributes,
    CursorAttribute? Attribute,
    int ReplaceStart,
    bool NeedsQuotes)
{
    private const int MaxTagLength = 8000;

    private static readonly MarkupCursor NoCompletion = new(CursorKind.None, 0, 0, "", [], null, 0, false);

    /// <summary>The partial name or value between <see cref="ReplaceStart"/> and the cursor.</summary>
    public string Prefix(string text) => text[ReplaceStart..Offset];

    public static MarkupCursor At(string text, IReadOnlyList<MarkupToken> tokens, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int index = LastTokenStartingBefore(tokens, offset);
        int tagStart = -1;
        if (index >= 0)
        {
            var t = tokens[index];
            switch (t.Kind)
            {
                case TokenKind.CodeBlock or TokenKind.ServerComment when offset < t.End || !IsTerminated(text, t):
                case TokenKind.ServerScript when offset <= t.End:
                    return NoCompletion;
                case TokenKind.StartTag or TokenKind.Directive when offset < t.End:
                    tagStart = t.Start.Offset;
                    break;
            }
        }

        if (InHtmlComment(text, tokens, index, offset))
        {
            return NoCompletion;
        }

        if (tagStart < 0)
        {
            if (InClientScriptOrStyle(tokens, index, offset))
            {
                return NoCompletion;
            }

            tagStart = FindIncompleteTagStart(text, offset);
            if (tagStart < 0)
            {
                return NoCompletion;
            }
        }

        return text.AsSpan(tagStart).StartsWith("<%@") ? LexDirective(text, tagStart, offset) : LexTag(text, tagStart, offset);
    }

    private static bool IsTerminated(string text, MarkupToken t) =>
        t.Length >= 4 && text.AsSpan(t.Start.Offset, t.Length).EndsWith("%>");

    /// <summary>Inside the body of a client <c>&lt;script&gt;</c> or <c>&lt;style&gt;</c> (the HTML server's territory).</summary>
    private static bool InClientScriptOrStyle(IReadOnlyList<MarkupToken> tokens, int index, int offset)
    {
        for (int i = index; i >= 0; i--)
        {
            var t = tokens[i];
            if (t.Kind is not (TokenKind.StartTag or TokenKind.EndTag) || t.End > offset ||
                !(t.Name.Equals("script", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return t.Kind == TokenKind.StartTag && !t.SelfClosing;
        }

        return false;
    }

    /// <summary>Inside an HTML comment, where completion returns nothing.</summary>
    public static bool IsInHtmlComment(string text, IReadOnlyList<MarkupToken> tokens, int offset) =>
        InHtmlComment(text, tokens, LastTokenStartingBefore(tokens, offset), offset);

    /// <summary>
    /// After a <c>&lt;!--</c> with no <c>--&gt;</c> before the cursor (closed later or not at all). Only text tokens are
    /// searched: <c>&lt;!--</c> in a code block or a <c>&lt;%-- --%&gt;</c> comment is not an HTML comment. Server
    /// controls inside an HTML comment are still parsed by ASP.NET, but nobody writes new ones there.
    /// </summary>
    private static bool InHtmlComment(string text, IReadOnlyList<MarkupToken> tokens, int index, int offset)
    {
        for (int i = index; i >= 0; i--)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Text)
            {
                continue;
            }

            var span = text.AsSpan(t.Start.Offset, Math.Min(t.End, offset) - t.Start.Offset);
            int open = span.LastIndexOf("<!--", StringComparison.Ordinal);
            int close = span.LastIndexOf("-->", StringComparison.Ordinal);
            if (open >= 0 || close >= 0)
            {
                return open > close;
            }
        }

        return false;
    }

    private static int LastTokenStartingBefore(IReadOnlyList<MarkupToken> tokens, int offset)
    {
        int lo = 0, hi = tokens.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (tokens[mid].Start.Offset < offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// Scans back from the cursor for the <c>&lt;</c> of an unfinished tag or directive. A <c>&gt;</c> on the way means
    /// the cursor is in text (a <c>%&gt;</c> skips back over its code block, which may sit in an attribute value).
    /// </summary>
    private static int FindIncompleteTagStart(string text, int offset)
    {
        int limit = Math.Max(0, offset - MaxTagLength);
        for (int i = offset - 1; i >= limit; i--)
        {
            char c = text[i];
            if (c == '>')
            {
                if (i > 0 && text[i - 1] == '%')
                {
                    int open = text.LastIndexOf("<%", i - 1, StringComparison.Ordinal);
                    if (open < 0)
                    {
                        return -1;
                    }

                    if (open + 2 < text.Length && text[open + 2] == '@')
                    {
                        return -1; // the end of a complete directive
                    }

                    i = open;
                    continue;
                }

                return -1;
            }

            if (c != '<')
            {
                continue;
            }

            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (next == '%')
            {
                return i + 2 < text.Length && text[i + 2] == '@' ? i : -1;
            }

            if (i + 1 == offset || IsTagNameStart(next) || next == '/')
            {
                return i;
            }
        }

        return -1;
    }

    private static MarkupCursor LexTag(string text, int start, int offset)
    {
        int p = start + 1;
        if (p < text.Length && text[p] == '/')
        {
            // An unfinished end tag, cursor at the end of the name typed so far (<c>&lt;/</c>, <c>&lt;/asp:Pa</c>): the
            // item replaces from the '/'. A finished one (<c>&gt;</c> follows) is left alone.
            int endNameStart = p + 1;
            int endNameEnd = endNameStart;
            while (endNameEnd < text.Length && IsTagNameChar(text[endNameEnd]))
            {
                endNameEnd++;
            }

            return offset == endNameEnd && (endNameEnd == text.Length || text[endNameEnd] != '>')
                ? new MarkupCursor(CursorKind.EndTagName, offset, start, text[endNameStart..endNameEnd], [], null, p, false)
                : NoCompletion;
        }

        int nameStart = p;
        while (p < text.Length && IsTagNameChar(text[p]))
        {
            p++;
        }

        string name = text[nameStart..p];
        if (offset <= p)
        {
            return new MarkupCursor(CursorKind.TagName, offset, start, name, ReadAttributes(text, p, -1, out _, out _), null, nameStart, false);
        }

        var attributes = ReadAttributes(text, p, offset, out var hit, out var where);
        return where.Kind == CursorKind.None
            ? NoCompletion
            : new MarkupCursor(where.Kind, offset, start, name, attributes, hit, where.ReplaceStart, where.NeedsQuotes);
    }

    private static MarkupCursor LexDirective(string text, int start, int offset)
    {
        int p = start + 3;
        while (p < text.Length && char.IsWhiteSpace(text[p]))
        {
            p++;
        }

        int nameStart = p;
        while (p < text.Length && IsAttrNameChar(text[p]))
        {
            p++;
        }

        // "<%@ Page": the first word is the directive name unless it has a value (a Page directive with the name omitted).
        int q = p;
        while (q < text.Length && char.IsWhiteSpace(text[q]))
        {
            q++;
        }

        bool hasName = p > nameStart && !(q < text.Length && text[q] == '=');
        if (offset <= p && offset >= start + 3 && (hasName || p == nameStart))
        {
            int replace = offset < nameStart ? offset : nameStart;
            return new MarkupCursor(CursorKind.DirectiveName, offset, start, text[nameStart..p], [], null, replace, false);
        }

        string name = hasName ? text[nameStart..p] : "Page";
        var attributes = ReadAttributes(text, hasName ? p : nameStart, offset, out var hit, out var where);
        var kind = where.Kind switch
        {
            CursorKind.AttributeName => CursorKind.DirectiveAttributeName,
            CursorKind.AttributeValue => CursorKind.DirectiveAttributeValue,
            _ => CursorKind.None,
        };
        return kind == CursorKind.None
            ? NoCompletion
            : new MarkupCursor(kind, offset, start, name, attributes, hit, where.ReplaceStart, where.NeedsQuotes);
    }

    private readonly record struct Where(CursorKind Kind, int ReplaceStart, bool NeedsQuotes);

    /// <summary>
    /// Reads attributes from <paramref name="p"/> to the end of the tag (<c>&gt;</c>, <c>/&gt;</c>, <c>%&gt;</c>, the next
    /// tag, or the end of the text) and works out where <paramref name="offset"/> falls (-1: don't care).
    /// </summary>
    private static List<CursorAttribute> ReadAttributes(string text, int p, int offset, out CursorAttribute? hit, out Where where)
    {
        var attributes = new List<CursorAttribute>();
        hit = null;
        where = default;
        bool decided = offset < 0;
        int limit = Math.Min(text.Length, p + MaxTagLength);

        while (p < limit)
        {
            int wsStart = p;
            while (p < limit && char.IsWhiteSpace(text[p]))
            {
                p++;
            }

            if (!decided && (offset < p || (offset == p && (p >= limit || !IsAttrNameStart(text[p])))))
            {
                // In the whitespace between attributes (and not glued to the previous name or value). At the first
                // character of the next name, the cursor is on that name (read below).
                decided = true;
                if (offset > wsStart)
                {
                    where = new Where(CursorKind.AttributeName, offset, false);
                }
            }

            if (p >= limit)
            {
                break;
            }

            char c = text[p];
            char next = p + 1 < text.Length ? text[p + 1] : '\0';
            if (c == '>' || (c is '/' or '%' && next == '>') || (c == '<' && next != '%'))
            {
                break; // end of the tag, or the start of the next one (an unfinished tag)
            }

            if (c == '<')
            {
                int close = text.IndexOf("%>", p + 2, StringComparison.Ordinal);
                if (!decided && (close < 0 || offset < close + 2))
                {
                    decided = true; // inside a code block in attribute position
                }

                p = close < 0 ? limit : close + 2;
                continue;
            }

            if (!IsAttrNameStart(c))
            {
                p++;
                continue;
            }

            int nameStart = p;
            while (p < limit && IsAttrNameChar(text[p]))
            {
                p++;
            }

            string name = text[nameStart..p];
            bool cursorInName = !decided && offset <= p;
            int q = p;
            while (q < limit && char.IsWhiteSpace(text[q]))
            {
                q++;
            }

            if (q >= limit || text[q] != '=')
            {
                var bare = new CursorAttribute(name, nameStart, null, -1);
                attributes.Add(bare);
                if (cursorInName)
                {
                    (decided, hit, where) = (true, bare, new Where(CursorKind.AttributeName, nameStart, false));
                }

                continue;
            }

            int eq = q;
            q++;
            while (q < limit && char.IsWhiteSpace(text[q]) && text[q] is not ('\r' or '\n'))
            {
                q++;
            }

            char quote = q < limit ? text[q] : '\0';
            int valueStart, valueEnd;
            if (quote is '"' or '\'')
            {
                valueStart = q + 1;
                valueEnd = valueStart;
                while (valueEnd < limit && text[valueEnd] != quote)
                {
                    if (text[valueEnd] == '<' && valueEnd + 1 < limit && text[valueEnd + 1] == '%')
                    {
                        int close = text.IndexOf("%>", valueEnd + 2, StringComparison.Ordinal);
                        valueEnd = close < 0 ? limit : close + 2;
                        continue;
                    }

                    valueEnd++;
                }

                p = Math.Min(limit, valueEnd + 1);
            }
            else
            {
                valueStart = q;
                valueEnd = q;
                while (valueEnd < limit && !char.IsWhiteSpace(text[valueEnd]) && text[valueEnd] is not ('>' or '<' or '"' or '\''))
                {
                    valueEnd++;
                }

                p = valueEnd;
            }

            var attribute = new CursorAttribute(name, nameStart, text[valueStart..valueEnd], valueStart);
            attributes.Add(attribute);
            if (decided)
            {
                continue;
            }

            if (cursorInName)
            {
                (decided, hit, where) = (true, attribute, new Where(CursorKind.AttributeName, nameStart, false));
            }
            else if (offset > eq && (offset < q || (offset == q && quote is not ('"' or '\'') && valueEnd == valueStart)))
            {
                (decided, hit, where) = (true, attribute, new Where(CursorKind.AttributeValue, offset, true));
            }
            else if (offset >= valueStart && offset <= valueEnd && offset > eq)
            {
                decided = true;
                if (!text.AsSpan(valueStart, offset - valueStart).Contains("<%", StringComparison.Ordinal))
                {
                    (hit, where) = (attribute, new Where(CursorKind.AttributeValue, valueStart, false));
                }
            }
            else if (offset <= p)
            {
                decided = true; // on the quote or the '='
            }
        }

        return attributes;
    }

    private static bool IsTagNameStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsTagNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '.';

    private static bool IsAttrNameStart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsAttrNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or ':' or '.';
}
