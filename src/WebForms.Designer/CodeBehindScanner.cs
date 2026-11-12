using System.Text;

namespace WebForms.Designer;

public sealed record CodeBehindInfo(bool ClassFound, string? Namespace, HashSet<string> Members)
{
    public static CodeBehindInfo Empty => new(false, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Reads the code-behind class. Visual Studio does not emit a designer field for a control whose ID matches a
/// member declared in the code-behind ("To modify move field declaration from designer file to code-behind
/// file"), and the designer class must be in the same namespace as the code-behind class.
/// <para>
/// This is a deliberately small VB scanner (block structure + class-level declarations) instead of Roslyn:
/// loading Roslyn costs 50-150 ms per process, and this runs on every save. It is checked against Roslyn over
/// the whole corpus by <c>tools/CorpusReport --verify-scanner</c>.
/// </para>
/// </summary>
public static class CodeBehindScanner
{
    private enum FrameKind
    {
        Namespace,
        Type,
        Interface,
        Method,
        Property,
        Event,
        Accessor,
    }

    private sealed record Frame(FrameKind Kind, string Keyword, string? Name);

    private static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public", "Private", "Protected", "Friend", "Shared", "Shadows", "Overridable", "Overrides",
        "NotOverridable", "MustOverride", "Overloads", "ReadOnly", "WriteOnly", "WithEvents", "Dim", "Const",
        "Static", "Partial", "Default", "Async", "Iterator", "Widening", "Narrowing", "Custom", "MustInherit",
        "NotInheritable",
    };

    private static readonly HashSet<string> NonFieldKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sub", "Function", "Property", "Event", "Class", "Module", "Structure", "Interface", "Enum", "Delegate",
        "Declare", "Operator", "Namespace", "Imports", "Inherits", "Implements", "Option", "End", "Get", "Set",
        "AddHandler", "RemoveHandler", "RaiseEvent",
    };

    public static CodeBehindInfo Scan(string codeBehindPath, string className)
    {
        if (!File.Exists(codeBehindPath))
        {
            return CodeBehindInfo.Empty;
        }

        return ScanText(File.ReadAllText(codeBehindPath), className);
    }

    public static CodeBehindInfo ScanText(string source, string className)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<Frame>();
        bool found = false;
        string? ns = null;
        bool pendingPropertyBody = false;
        bool pendingEventBody = false;

        foreach (var statement in LogicalLines(source))
        {
            var tokens = Tokenize(statement);
            if (tokens.Count == 0)
            {
                continue;
            }

            string first = tokens[0];
            var top = stack.Count > 0 ? stack[^1] : null;

            if (pendingPropertyBody || pendingEventBody)
            {
                // An expanded property starts with an accessor (Get/Set), possibly with an access modifier.
                int a = 0;
                while (a < tokens.Count - 1 && Modifiers.Contains(tokens[a]))
                {
                    a++;
                }

                bool accessor = tokens[a].Equals("Get", StringComparison.OrdinalIgnoreCase) || tokens[a].Equals("Set", StringComparison.OrdinalIgnoreCase);
                if (pendingPropertyBody && accessor)
                {
                    stack.Add(new Frame(FrameKind.Property, "Property", null));
                    top = stack[^1];
                }

                pendingPropertyBody = false;
                pendingEventBody = false;
            }

            if (first.Equals("End", StringComparison.OrdinalIgnoreCase))
            {
                if (tokens.Count > 1)
                {
                    PopTo(stack, tokens[1]);
                }

                continue;
            }

            bool inMethodLike = stack.Count > 0 && stack[^1].Kind is FrameKind.Method or FrameKind.Accessor;
            if (inMethodLike || top?.Kind is FrameKind.Property or FrameKind.Event)
            {
                if (top?.Kind is FrameKind.Property or FrameKind.Event &&
                    first is var f && (f.Equals("Get", StringComparison.OrdinalIgnoreCase) || f.Equals("Set", StringComparison.OrdinalIgnoreCase) ||
                                       f.Equals("AddHandler", StringComparison.OrdinalIgnoreCase) || f.Equals("RemoveHandler", StringComparison.OrdinalIgnoreCase) ||
                                       f.Equals("RaiseEvent", StringComparison.OrdinalIgnoreCase) || Modifiers.Contains(f)))
                {
                    stack.Add(new Frame(FrameKind.Accessor, AccessorKeyword(tokens), null));
                    continue;
                }

                // Inside a method body: only multi-line lambdas open blocks that end with End Sub / End Function.
                foreach (var kw in MultiLineLambdas(statement))
                {
                    stack.Add(new Frame(FrameKind.Method, kw, null));
                }

                continue;
            }

            // Statement at namespace / type level.
            int i = 0;
            bool hadModifier = false;
            while (i < tokens.Count && Modifiers.Contains(tokens[i]))
            {
                hadModifier = true;
                i++;
            }

            if (i >= tokens.Count)
            {
                continue;
            }

            string kwd = tokens[i];
            string? name = i + 1 < tokens.Count ? tokens[i + 1] : null;
            bool inTargetClass = top is { Kind: FrameKind.Type, Keyword: "Class" } && string.Equals(top.Name, className, StringComparison.OrdinalIgnoreCase);
            bool inInterface = top?.Kind == FrameKind.Interface;

            switch (kwd.ToLowerInvariant())
            {
                case "namespace":
                    stack.Add(new Frame(FrameKind.Namespace, "Namespace", statement.Trim()[(statement.Trim().IndexOf(' ') + 1)..].Trim()));
                    break;
                case "class" or "module" or "structure":
                    // A generic class is a different type from a non-generic one with the same name.
                    if (i + 3 < tokens.Count && tokens[i + 2] == "(" && tokens[i + 3].Equals("Of", StringComparison.OrdinalIgnoreCase))
                    {
                        name += "`";
                    }

                    stack.Add(new Frame(FrameKind.Type, Canon(kwd), Unbracket(name)));
                    if (!found && Canon(kwd) == "Class" && string.Equals(Unbracket(name), className, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        var parts = stack.Where(s => s.Kind == FrameKind.Namespace).Select(s => s.Name!).ToList();
                        ns = parts.Count == 0 ? null : string.Join(".", parts);
                    }

                    break;
                case "interface":
                    stack.Add(new Frame(FrameKind.Interface, "Interface", Unbracket(name)));
                    break;
                case "enum":
                    stack.Add(new Frame(FrameKind.Type, "Enum", Unbracket(name)));
                    break;
                case "sub" or "function" or "operator":
                    if (!inInterface && !ContainsWord(tokens, "MustOverride"))
                    {
                        stack.Add(new Frame(FrameKind.Method, Canon(kwd), null));
                    }

                    break;
                case "property":
                    if (inTargetClass && name is not null)
                    {
                        members.Add(Unbracket(name)!);
                    }

                    if (!inInterface && !ContainsWord(tokens, "MustOverride"))
                    {
                        pendingPropertyBody = true;
                    }

                    break;
                case "event":
                    if (inTargetClass && name is not null)
                    {
                        members.Add(Unbracket(name)!);
                    }

                    if (ContainsWord(tokens, "Custom"))
                    {
                        stack.Add(new Frame(FrameKind.Event, "Event", null));
                    }

                    break;
                case "declare" or "delegate" or "imports" or "inherits" or "implements" or "option":
                    break;
                default:
                    if (hadModifier && inTargetClass && !NonFieldKeywords.Contains(kwd))
                    {
                        foreach (var declared in FieldNames(statement, tokens, i))
                        {
                            members.Add(declared);
                        }
                    }

                    break;
            }
        }

        return new CodeBehindInfo(found, ns, members);
    }

    private static string AccessorKeyword(List<string> tokens)
    {
        foreach (var t in tokens)
        {
            if (!Modifiers.Contains(t))
            {
                return Canon(t);
            }
        }

        return "Get";
    }

    private static void PopTo(List<Frame> stack, string endKeyword)
    {
        string k = Canon(endKeyword);
        if (k is not ("Namespace" or "Class" or "Module" or "Structure" or "Interface" or "Enum" or "Sub" or "Function" or "Operator" or
            "Property" or "Event" or "Get" or "Set" or "AddHandler" or "RemoveHandler" or "RaiseEvent"))
        {
            return; // End If / End Using / End While ...
        }

        for (int j = stack.Count - 1; j >= 0; j--)
        {
            if (stack[j].Keyword.Equals(k, StringComparison.OrdinalIgnoreCase))
            {
                stack.RemoveRange(j, stack.Count - j);
                return;
            }
        }
    }

    private static string Canon(string kw) => kw.Length == 0 ? kw : char.ToUpperInvariant(kw[0]) + kw[1..].ToLowerInvariant() switch
    {
        "ddhandler" => "ddHandler",
        "emovehandler" => "emoveHandler",
        "aiseevent" => "aiseEvent",
        var x => x,
    };

    private static bool ContainsWord(List<string> tokens, string word) => tokens.Any(t => t.Equals(word, StringComparison.OrdinalIgnoreCase));

    private static string? Unbracket(string? name) => name?.Trim('[', ']');

    /// <summary>Names declared by a field statement: "Protected WithEvents a As X, b, c As New Y(1, 2)".</summary>
    private static IEnumerable<string> FieldNames(string statement, List<string> tokens, int firstNameToken)
    {
        // Rebuild from the statement text after the modifiers, then split on top-level commas.
        var text = statement.TrimStart();
        if (text.StartsWith('<'))
        {
            text = SkipAttributes(text);
        }

        int modifiersSeen = 0;
        int pos = 0;
        while (modifiersSeen < firstNameToken && pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }

            while (pos < text.Length && !char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }

            modifiersSeen++;
        }

        foreach (var part in SplitTopLevel(text[pos..], ','))
        {
            var p = part.Trim();
            int end = 0;
            if (p.StartsWith('['))
            {
                end = p.IndexOf(']');
                if (end > 0)
                {
                    yield return p[1..end];
                }

                continue;
            }

            while (end < p.Length && (char.IsLetterOrDigit(p[end]) || p[end] == '_'))
            {
                end++;
            }

            if (end > 0)
            {
                yield return p[..end];
            }
        }
    }

    private static IEnumerable<string> SplitTopLevel(string s, char sep)
    {
        int depth = 0, start = 0;
        bool inString = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"')
            {
                inString = !inString;
            }
            else if (!inString)
            {
                if (c is '(' or '{')
                {
                    depth++;
                }
                else if (c is ')' or '}')
                {
                    depth--;
                }
                else if (c == sep && depth == 0)
                {
                    yield return s[start..i];
                    start = i + 1;
                }
            }
        }

        yield return s[start..];
    }

    private static string SkipAttributes(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '<')
            {
                depth++;
            }
            else if (text[i] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    var rest = text[(i + 1)..].TrimStart();
                    return rest.StartsWith('<') ? SkipAttributes(rest) : rest;
                }
            }
        }

        return text;
    }

    /// <summary>Words of a statement (identifiers / keywords only), skipping leading attributes.</summary>
    private static List<string> Tokenize(string statement)
    {
        var s = statement.TrimStart();
        if (s.StartsWith('<'))
        {
            s = SkipAttributes(s);
        }

        var tokens = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsLetter(c) || c == '_' || c == '[')
            {
                int start = i;
                if (c == '[')
                {
                    int close = s.IndexOf(']', i);
                    i = close < 0 ? s.Length : close + 1;
                }
                else
                {
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                    {
                        i++;
                    }
                }

                tokens.Add(s[start..i]);
            }
            else if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    i++;
                }

                i++;
                tokens.Add("\"");
            }
            else if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else
            {
                tokens.Add(c.ToString());
                i++;
            }
        }

        return tokens;
    }

    /// <summary>Multi-line lambda headers in a statement: "Sub(x)" / "Function(x) As T" with nothing after them on the line.</summary>
    private static IEnumerable<string> MultiLineLambdas(string statement)
    {
        var s = statement;
        foreach (var kw in new[] { "Sub", "Function" })
        {
            int idx = 0;
            while ((idx = IndexOfWord(s, kw, idx)) >= 0)
            {
                int after = idx + kw.Length;
                int p = after;
                while (p < s.Length && char.IsWhiteSpace(s[p]))
                {
                    p++;
                }

                string before = s[..idx].TrimEnd();
                bool isExitOrEnd = before.EndsWith("Exit", StringComparison.OrdinalIgnoreCase) || before.EndsWith("End", StringComparison.OrdinalIgnoreCase);
                if (p < s.Length && s[p] == '(' && !isExitOrEnd)
                {
                    int close = MatchParen(s, p);
                    if (close > 0)
                    {
                        // Single-line lambdas cannot have an As clause, so "As ..." after the parameters means multi-line.
                        var rest = s[(close + 1)..].Trim();
                        if (rest.Length == 0 || rest.StartsWith("As ", StringComparison.OrdinalIgnoreCase))
                        {
                            yield return kw;
                        }
                    }
                }

                idx = after;
            }
        }
    }

    private static int MatchParen(string s, int open)
    {
        int depth = 0;
        bool inString = false;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"')
            {
                inString = !inString;
            }
            else if (!inString && c == '(')
            {
                depth++;
            }
            else if (!inString && c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int IndexOfWord(string s, string word, int start)
    {
        while (true)
        {
            int idx = s.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return -1;
            }

            bool okBefore = idx == 0 || !(char.IsLetterOrDigit(s[idx - 1]) || s[idx - 1] == '_' || s[idx - 1] == '.');
            int end = idx + word.Length;
            bool okAfter = end >= s.Length || !(char.IsLetterOrDigit(s[end]) || s[end] == '_');
            if (okBefore && okAfter)
            {
                return idx;
            }

            start = idx + 1;
        }
    }

    /// <summary>
    /// Splits source into logical statements: comments and string contents removed (strings become ""),
    /// explicit and common implicit line continuations joined, ':' statement separators split, directives dropped.
    /// </summary>
    internal static IEnumerable<string> LogicalLines(string source)
    {
        var sb = new StringBuilder();
        foreach (var line in StripCommentsAndStrings(source).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(trimmed);

            if (ContinuesOnNextLine(trimmed))
            {
                if (trimmed.EndsWith(" _", StringComparison.Ordinal) || trimmed == "_")
                {
                    sb.Length -= 1;
                }

                continue;
            }

            var statement = sb.ToString();
            sb.Clear();
            foreach (var part in SplitTopLevel(statement, ':'))
            {
                var p = part.Trim();
                if (p.Length > 0)
                {
                    yield return p;
                }
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static bool ContinuesOnNextLine(string t)
    {
        if (t.Length == 0)
        {
            return false;
        }

        if (t.EndsWith(" _", StringComparison.Ordinal) || t == "_")
        {
            return true;
        }

        char c = t[^1];
        return c is ',' or '(' or '{' or '=' or '&' or '+' or '.';
    }

    /// <summary>
    /// Removes comments and string contents from the whole file (strings may span lines since VB 14); string
    /// literals become <c>""</c> so statements keep their shape. Line breaks outside strings are kept.
    /// </summary>
    private enum Lex
    {
        String,
        InterpolatedString,
        Hole,
    }

    private static string StripCommentsAndStrings(string source)
    {
        var sb = new StringBuilder(source.Length);
        // Nesting of strings / interpolated strings / interpolation holes ({...} inside $"..."). Empty = code.
        var stack = new Stack<(Lex Kind, int Depth)>();
        bool inComment = false;

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\r')
            {
                continue;
            }

            if (inComment)
            {
                if (c == '\n')
                {
                    inComment = false;
                    sb.Append('\n');
                }

                continue;
            }

            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (stack.Count > 0 && stack.Peek().Kind is Lex.String or Lex.InterpolatedString)
            {
                var kind = stack.Peek().Kind;
                if (c == '"')
                {
                    if (next == '"')
                    {
                        i++; // escaped quote
                        continue;
                    }

                    stack.Pop();
                    if (stack.Count == 0)
                    {
                        sb.Append('"');
                    }
                }
                else if (kind == Lex.InterpolatedString && c == '{')
                {
                    if (next == '{')
                    {
                        i++; // escaped brace
                    }
                    else
                    {
                        stack.Push((Lex.Hole, 1));
                    }
                }

                continue;
            }

            // Code (top level, or inside an interpolation hole).
            bool topLevel = stack.Count == 0;
            if (c == '"')
            {
                bool interpolated = i > 0 && source[i - 1] == '$';
                stack.Push((interpolated ? Lex.InterpolatedString : Lex.String, 0));
                if (topLevel)
                {
                    sb.Append('"');
                }

                continue;
            }

            if (!topLevel)
            {
                // Inside a hole: track braces to find its end.
                var (kind, depth) = stack.Pop();
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                }

                if (depth > 0)
                {
                    stack.Push((kind, depth));
                }

                continue;
            }

            if (c is '\'' or '‘' or '’')
            {
                inComment = true;
                continue;
            }

            if ((c is 'R' or 'r') && (i == 0 || !(char.IsLetterOrDigit(source[i - 1]) || source[i - 1] == '_')) &&
                string.Compare(source, i, "REM", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 &&
                (i + 3 >= source.Length || char.IsWhiteSpace(source[i + 3])))
            {
                inComment = true;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
