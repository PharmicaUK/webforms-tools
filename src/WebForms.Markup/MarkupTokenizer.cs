namespace WebForms.Markup;

/// <summary>
/// Splits ASP.NET WebForms markup into tokens, following the lexical rules of
/// System.Web.UI.TemplateParser:
/// <list type="bullet">
/// <item><c>&lt;%-- --%&gt;</c> server comments hide everything inside them, including server tags.</item>
/// <item>HTML comments (<c>&lt;!-- --&gt;</c>) are NOT special: server tags inside them are still parsed.</item>
/// <item>Client <c>&lt;script&gt;</c> blocks are not special either; only <c>&lt;script runat="server"&gt;</c> bodies are opaque.</item>
/// <item>A <c>&lt;</c> that does not start a well-formed tag is literal text.</item>
/// </list>
/// </summary>
public sealed class MarkupTokenizer
{
    private readonly string _text;
    private readonly LineMap _lines;
    private int _pos;

    public MarkupTokenizer(string text)
    {
        _text = text;
        _lines = new LineMap(text);
    }

    public LineMap Lines => _lines;

    public static List<MarkupToken> Tokenize(string text) => new MarkupTokenizer(text).ReadAll();

    public List<MarkupToken> ReadAll()
    {
        var tokens = new List<MarkupToken>();
        int textStart = 0;
        _pos = 0;

        while (_pos < _text.Length)
        {
            int lt = _text.IndexOf('<', _pos);
            if (lt < 0)
            {
                break;
            }

            _pos = lt;
            var token = TryReadConstruct();
            if (token is null)
            {
                _pos = lt + 1; // literal '<'
                continue;
            }

            if (lt > textStart)
            {
                tokens.Add(new MarkupToken(TokenKind.Text, _lines.At(textStart), lt - textStart));
            }

            tokens.Add(token);

            if (token.Kind == TokenKind.StartTag && !token.SelfClosing &&
                token.Name.Equals("script", StringComparison.OrdinalIgnoreCase) &&
                IsRunatServer(token))
            {
                ReadServerScriptBody(tokens);
            }

            textStart = _pos;
        }

        if (textStart < _text.Length)
        {
            tokens.Add(new MarkupToken(TokenKind.Text, _lines.At(textStart), _text.Length - textStart));
        }

        return tokens;
    }

    public static bool IsRunatServer(MarkupToken token) =>
        string.Equals(token.GetAttributeValue("runat")?.Trim(), "server", StringComparison.OrdinalIgnoreCase);

    private void ReadServerScriptBody(List<MarkupToken> tokens)
    {
        int bodyStart = _pos;
        int search = _pos;
        while (true)
        {
            int idx = _text.IndexOf("</", search, StringComparison.Ordinal);
            if (idx < 0)
            {
                tokens.Add(new MarkupToken(TokenKind.ServerScript, _lines.At(bodyStart), _text.Length - bodyStart));
                _pos = _text.Length;
                return;
            }

            _pos = idx;
            var end = TryReadEndTag();
            if (end is not null && end.Name.Equals("script", StringComparison.OrdinalIgnoreCase))
            {
                if (idx > bodyStart)
                {
                    tokens.Add(new MarkupToken(TokenKind.ServerScript, _lines.At(bodyStart), idx - bodyStart));
                }

                tokens.Add(end);
                return;
            }

            search = idx + 2;
        }
    }

    private MarkupToken? TryReadConstruct()
    {
        int start = _pos;
        if (Peek(1) == '%')
        {
            if (Match("<%--"))
            {
                int end = _text.IndexOf("--%>", start + 4, StringComparison.Ordinal);
                int stop = end < 0 ? _text.Length : end + 4;
                _pos = stop;
                return new MarkupToken(TokenKind.ServerComment, _lines.At(start), stop - start);
            }

            if (Peek(2) == '@')
            {
                return ReadDirective();
            }

            int close = _text.IndexOf("%>", start + 2, StringComparison.Ordinal);
            int codeEnd = close < 0 ? _text.Length : close + 2;
            _pos = codeEnd;
            return new MarkupToken(TokenKind.CodeBlock, _lines.At(start), codeEnd - start);
        }

        if (Peek(1) == '/')
        {
            return TryReadEndTag();
        }

        return TryReadStartTag();
    }

    private MarkupToken ReadDirective()
    {
        int start = _pos;
        _pos += 3;
        var attrs = new List<MarkupAttribute>();
        string name = "";
        SourcePosition namePos = _lines.At(_pos);
        bool first = true;

        while (true)
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
            {
                break;
            }

            if (Match("%>"))
            {
                _pos += 2;
                break;
            }

            if (!TryReadAttribute(attrs, allowServerBlocks: false))
            {
                // Garbage inside directive: skip a char so we always make progress.
                _pos++;
                first = false;
                continue;
            }

            if (first)
            {
                var a = attrs[^1];
                if (a.Value is null)
                {
                    name = a.Name;
                    namePos = a.NamePosition;
                    attrs.RemoveAt(attrs.Count - 1);
                }
            }

            first = false;
        }

        return new MarkupToken(TokenKind.Directive, _lines.At(start), _pos - start)
        {
            Name = name,
            NamePosition = namePos,
            Attributes = attrs,
        };
    }

    private MarkupToken? TryReadEndTag()
    {
        int start = _pos;
        int p = start + 2;
        int nameStart = p;
        while (p < _text.Length && IsTagNameChar(_text[p]))
        {
            p++;
        }

        if (p == nameStart)
        {
            return null;
        }

        string name = _text[nameStart..p];
        while (p < _text.Length && char.IsWhiteSpace(_text[p]))
        {
            p++;
        }

        if (p >= _text.Length || _text[p] != '>')
        {
            return null;
        }

        _pos = p + 1;
        return new MarkupToken(TokenKind.EndTag, _lines.At(start), _pos - start)
        {
            Name = name,
            NamePosition = _lines.At(nameStart),
        };
    }

    private MarkupToken? TryReadStartTag()
    {
        int start = _pos;
        int p = start + 1;
        int nameStart = p;
        while (p < _text.Length && IsTagNameChar(_text[p]))
        {
            p++;
        }

        if (p == nameStart)
        {
            return null;
        }

        string name = _text[nameStart..p];
        _pos = p;
        var attrs = new List<MarkupAttribute>();
        bool selfClosing = false;

        while (true)
        {
            int before = _pos;
            SkipWhitespace();
            if (_pos >= _text.Length)
            {
                _pos = start;
                return null;
            }

            char c = _text[_pos];
            if (c == '>')
            {
                _pos++;
                break;
            }

            if (c == '/' && Peek(1) == '>')
            {
                selfClosing = true;
                _pos += 2;
                break;
            }

            if (_pos == before)
            {
                // Attributes must be separated from the name / previous attribute by whitespace.
                _pos = start;
                return null;
            }

            if (!TryReadAttribute(attrs, allowServerBlocks: true))
            {
                _pos = start;
                return null;
            }
        }

        return new MarkupToken(TokenKind.StartTag, _lines.At(start), _pos - start)
        {
            Name = name,
            NamePosition = _lines.At(nameStart),
            Attributes = attrs,
            SelfClosing = selfClosing,
        };
    }

    private bool TryReadAttribute(List<MarkupAttribute> attrs, bool allowServerBlocks)
    {
        int nameStart = _pos;
        if (_pos >= _text.Length || !IsAttrNameStart(_text[_pos]))
        {
            return false;
        }

        _pos++;
        while (_pos < _text.Length && IsAttrNameChar(_text[_pos]))
        {
            _pos++;
        }

        string name = _text[nameStart.._pos];
        var namePos = _lines.At(nameStart);

        int afterName = _pos;
        SkipWhitespace();
        if (_pos >= _text.Length || _text[_pos] != '=')
        {
            _pos = afterName;
            attrs.Add(new MarkupAttribute(name, null, namePos, null, '\0'));
            return true;
        }

        _pos++;
        SkipWhitespace();
        if (_pos >= _text.Length)
        {
            return false;
        }

        char q = _text[_pos];
        if (q is '"' or '\'')
        {
            int valueStart = _pos + 1;
            int p = valueStart;
            while (p < _text.Length && _text[p] != q)
            {
                if (allowServerBlocks && _text[p] == '<' && p + 1 < _text.Length && _text[p + 1] == '%')
                {
                    int close = _text.IndexOf("%>", p + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return false;
                    }

                    p = close + 2;
                    continue;
                }

                p++;
            }

            if (p >= _text.Length)
            {
                return false;
            }

            attrs.Add(new MarkupAttribute(name, _text[valueStart..p], namePos, _lines.At(valueStart), q));
            _pos = p + 1;
            return true;
        }

        int vStart = _pos;
        if (allowServerBlocks && Match("<%"))
        {
            int close = _text.IndexOf("%>", _pos + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            _pos = close + 2;
        }
        else
        {
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (char.IsWhiteSpace(c) || c == '>' || c == '=' || (c == '/' && Peek(1) == '>') || (c == '%' && Peek(1) == '>'))
                {
                    break;
                }

                _pos++;
            }
        }

        attrs.Add(new MarkupAttribute(name, _text[vStart.._pos], namePos, _lines.At(vStart), '\0'));
        return true;
    }

    private static bool IsTagNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '.';

    private static bool IsAttrNameStart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsAttrNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or ':' or '.';

    private void SkipWhitespace()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
        {
            _pos++;
        }
    }

    private char Peek(int offset) => _pos + offset < _text.Length ? _text[_pos + offset] : '\0';

    private bool Match(string s) => string.CompareOrdinal(_text, _pos, s, 0, s.Length) == 0;
}

/// <summary>Maps character offsets to 1-based line/column positions.</summary>
public sealed class LineMap
{
    private readonly List<int> _lineStarts = [0];

    public LineMap(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                _lineStarts.Add(i + 1);
            }
            else if (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    public SourcePosition At(int offset)
    {
        int idx = _lineStarts.BinarySearch(offset);
        if (idx < 0)
        {
            idx = ~idx - 1;
        }

        return new SourcePosition(offset, idx + 1, offset - _lineStarts[idx] + 1);
    }
}
