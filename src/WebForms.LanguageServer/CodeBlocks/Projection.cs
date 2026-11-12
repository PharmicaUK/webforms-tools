using System.Text;
using System.Text.RegularExpressions;
using WebForms.Designer;
using WebForms.Markup;

namespace WebForms.LanguageServer.CodeBlocks;

/// <summary>Where the code of one block sits in the markup and in the projection (the text is copied verbatim).</summary>
public readonly record struct BlockSpan(int MarkupStart, int ProjectionStart, int Length);

/// <summary>
/// A markup file's code blocks as a hidden VB document that compiles against the project, the way ASP.NET compiles
/// the page (Razor-style projection):
/// <code>
/// Imports (ASP.NET's default page namespaces, web.config &lt;namespaces&gt;, &lt;%@ Import %&gt;)
/// Namespace Global.ASP
///   Friend NotInheritable Class __Markup_X : Inherits Global.Root.CodeBehindClass
///     Private Sub __Render()                          ' &lt;% %&gt; verbatim, &lt;%= e %&gt; / &lt;%: e %&gt; as __o = (e)
///     Private Sub __DataBind(Container As Control)    ' &lt;%# e %&gt; outside templates
///     Private Sub __Template1(Container As RepeaterItem)   ' one per template with code: its blocks
///       Dim Item As ItemType, BindItem As ItemType    '   when the data control sets ItemType
///     &lt;script runat="server"&gt; bodies as members
/// </code>
/// Positions outside the blocks map nowhere: they belong to the markup features.
/// </summary>
public sealed partial class Projection
{
    /// <summary>The namespaces ASP.NET imports into every page (root web.config of .NET Framework 4.x, plus VB's).</summary>
    private static readonly string[] DefaultImports =
    [
        "System", "System.Collections", "System.Collections.Generic", "System.Collections.Specialized", "System.ComponentModel.DataAnnotations",
        "System.Configuration", "System.Linq", "System.Text", "System.Text.RegularExpressions", "System.Web", "System.Web.Caching",
        "System.Web.DynamicData", "System.Web.SessionState", "System.Web.Security", "System.Web.Profile", "System.Web.UI",
        "System.Web.UI.WebControls", "System.Web.UI.WebControls.WebParts", "System.Web.UI.HtmlControls", "System.Xml.Linq",
        "Microsoft.VisualBasic",
    ];

    private readonly BlockSpan[] _spans;

    private Projection(string text, BlockSpan[] spans, int blockCount, MarkupAnalysis source, int repairAt)
    {
        Text = text;
        _spans = spans;
        BlockCount = blockCount;
        Source = source;
        _repairAt = repairAt;
    }

    private readonly int _repairAt;

    public string Text { get; }
    public int BlockCount { get; }

    /// <summary>The blocks, in offsets of the markup as it is (also when built from a repaired copy).</summary>
    public IReadOnlyList<BlockSpan> Spans => _spans;

    /// <summary>
    /// The analysis the projection was built from: the file's own, or that of a copy with <c>%&gt;</c> inserted after
    /// an unfinished block (<see cref="RepairPoint"/>). Its tree locates templates; map offsets with <see cref="ToSource"/>.
    /// </summary>
    public MarkupAnalysis Source { get; }

    /// <summary>A markup offset as an offset in <see cref="Source"/>.</summary>
    public int ToSource(int markupOffset) => markupOffset > _repairAt ? markupOffset + Repair.Length : markupOffset;

    /// <summary>What <see cref="RepairPoint"/>'s caller inserts.</summary>
    public const string Repair = "%>";

    /// <summary>The projection offset of a markup offset inside (or at either end of) a block's code; null elsewhere.</summary>
    public int? ToProjection(int markupOffset)
    {
        foreach (var s in _spans)
        {
            if (markupOffset >= s.MarkupStart && markupOffset <= s.MarkupStart + s.Length)
            {
                return s.ProjectionStart + (markupOffset - s.MarkupStart);
            }
        }

        return null;
    }

    /// <summary>The markup offset of a projection offset inside (or at either end of) a block's code; null in the scaffolding.</summary>
    public int? ToMarkup(int projectionOffset)
    {
        foreach (var s in _spans)
        {
            if (projectionOffset >= s.ProjectionStart && projectionOffset <= s.ProjectionStart + s.Length)
            {
                return s.MarkupStart + (projectionOffset - s.ProjectionStart);
            }
        }

        return null;
    }

    /// <summary>A projection span that lies within one block, as a markup span; null otherwise.</summary>
    public (int Start, int Length)? ToMarkup(int projectionStart, int length)
    {
        foreach (var s in _spans)
        {
            if (projectionStart >= s.ProjectionStart && projectionStart + length <= s.ProjectionStart + s.Length)
            {
                return (s.MarkupStart + (projectionStart - s.ProjectionStart), length);
            }
        }

        return null;
    }

    /// <summary>The projection of a markup file, or null when it has no code-behind class (CodeFile pages, no directive).</summary>
    /// <param name="baseType">Full name of the class the page compiles from (the code-behind class).</param>
    /// <param name="repairAt">When <paramref name="analysis"/> is of a repaired copy: where <see cref="Repair"/> was
    /// inserted (block offsets are mapped back to the markup as it is).</param>
    public static Projection? Build(MarkupAnalysis analysis, string baseType, IReadOnlyList<string> webConfigNamespaces, int repairAt = int.MaxValue)
    {
        string text = analysis.Document.Text;
        var blocks = Blocks(text, analysis.Tokens);

        var templates = new List<(int Start, int End, string Container, string? ItemType)>();
        foreach (var el in analysis.Tree.AllElements())
        {
            if (el.Annotation is PropertyAnnotation { IsTemplate: true } pa && el.EndTag is { } end)
            {
                templates.Add((el.StartTag.End, end.Start.Offset, pa.Property?.TemplateContainer ?? "System.Web.UI.Control", ItemTypeOf(el)));
            }
        }

        var render = new List<Block>();
        var bind = new List<Block>();
        var members = new List<Block>();
        var inTemplates = new SortedDictionary<int, List<Block>>();
        foreach (var b in blocks)
        {
            if (b.Kind == BlockKind.Script)
            {
                members.Add(b);
                continue;
            }

            int template = -1;
            for (int i = 0; i < templates.Count; i++)
            {
                if (b.Start >= templates[i].Start && b.Start < templates[i].End && (template < 0 || templates[i].Start > templates[template].Start))
                {
                    template = i;
                }
            }

            if (template >= 0)
            {
                if (!inTemplates.TryGetValue(template, out var list))
                {
                    inTemplates[template] = list = [];
                }

                list.Add(b);
            }
            else if (b.Kind == BlockKind.DataBinding)
            {
                bind.Add(b);
            }
            else
            {
                render.Add(b);
            }
        }

        var w = new Writer(text);
        var imports = DefaultImports.Concat(webConfigNamespaces)
            .Concat(analysis.Tokens.Where(t => t.Kind == TokenKind.Directive && t.Name.Equals("Import", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.GetAttributeValue("Namespace")?.Trim()).OfType<string>().Where(n => n.Length > 0))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var import in imports)
        {
            w.Line($"Imports {import}");
        }

        w.Line("Namespace Global.ASP");
        w.Line($"Friend NotInheritable Class __Markup_{ClassSuffix(analysis.Path)}");
        w.Line($"Inherits Global.{baseType}");
        w.Line("Private Function Bind(__expression As String, Optional __format As String = Nothing) As Object");
        w.Line("Return Nothing");
        w.Line("End Function");
        Sub(w, "__Render", null, null, render);
        Sub(w, "__DataBind", "Global.System.Web.UI.Control", null, bind);
        int n = 0;
        foreach (var (index, list) in inTemplates)
        {
            Sub(w, $"__Template{++n}", "Global." + templates[index].Container, templates[index].ItemType, list);
        }

        foreach (var b in members)
        {
            w.Code(b);
            w.Line("");
        }

        w.Line("End Class");
        w.Line("End Namespace");
        // No block's code covers the inserted "%>": each span lies wholly before or after it.
        var spans = w.Spans.Select(s => s.MarkupStart - Repair.Length >= repairAt ? s with { MarkupStart = s.MarkupStart - Repair.Length } : s);
        return new Projection(w.ToString(), [.. spans], blocks.Count, analysis, repairAt);
    }

    private static void Sub(Writer w, string name, string? container, string? itemType, List<Block> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        w.Line(container is null ? $"Private Sub {name}()" : $"Private Sub {name}(Container As {container})");
        w.Line("Dim __o As Object");
        if (itemType is not null)
        {
            w.Line($"Dim Item As Global.{itemType} = Nothing, BindItem As Global.{itemType} = Nothing");
        }

        foreach (var b in blocks)
        {
            if (b.Kind == BlockKind.Statement)
            {
                w.Code(b);
                w.Line("");
            }
            else
            {
                w.Append("__o = (");
                w.Code(b);
                w.Line(")");
            }
        }

        w.Line("End Sub");
    }

    /// <summary>The nearest enclosing element (the template's data control) with an <c>ItemType</c> attribute.</summary>
    private static string? ItemTypeOf(MarkupElement template)
    {
        for (var el = template.Parent; el is not null; el = el.Parent)
        {
            if (el.GetAttribute("ItemType")?.Value?.Trim() is { Length: > 0 } itemType && !itemType.Contains("<%", StringComparison.Ordinal))
            {
                return itemType;
            }
        }

        return null;
    }

    private static string ClassSuffix(string path)
    {
        var sb = new StringBuilder();
        foreach (char c in Path.GetFileName(path))
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return sb.ToString();
    }

    internal enum BlockKind
    {
        Statement,
        Expression,
        DataBinding,
        Script,
    }

    /// <summary>The code of one block: <see cref="Start"/> and <see cref="Length"/> exclude the delimiters.</summary>
    internal readonly record struct Block(BlockKind Kind, int Start, int Length);

    /// <summary>Every code block in document order: <c>&lt;% %&gt;</c> tokens, blocks inside attribute values, script bodies.</summary>
    internal static List<Block> Blocks(string text, IReadOnlyList<MarkupToken> tokens)
    {
        var blocks = new List<Block>();
        foreach (var t in tokens)
        {
            switch (t.Kind)
            {
                case TokenKind.CodeBlock:
                    AddBlock(text, t.Start.Offset, t.End, blocks);
                    break;
                case TokenKind.ServerScript:
                    blocks.Add(new Block(BlockKind.Script, t.Start.Offset, t.Length));
                    break;
                case TokenKind.StartTag:
                    foreach (var a in t.Attributes)
                    {
                        if (a.ValuePosition is { } vp && a.Value is { } value && value.Contains("<%", StringComparison.Ordinal))
                        {
                            foreach (Match m in ServerBlock().Matches(value))
                            {
                                AddBlock(text, vp.Offset + m.Index, vp.Offset + m.Index + m.Length, blocks);
                            }
                        }
                    }

                    break;
            }
        }

        return blocks;
    }

    /// <param name="start">Offset of <c>&lt;%</c>.</param>
    /// <param name="end">Offset after <c>%&gt;</c> (or the end of an unterminated block).</param>
    /// <remarks>
    /// A block still being typed has no <c>%&gt;</c> yet (Neovim doesn't close <c>&lt;%</c>): the tokenizer, like
    /// ASP.NET, runs it on to the next block's <c>%&gt;</c> or the end of the file, taking the markup in between as
    /// code (<c>Container.</c> + <c>&lt;div</c> is a VB XML axis access, and completion offered nothing). Such a block
    /// (one containing <c>&lt;%</c>, or unterminated) ends where <see cref="UnfinishedEnd"/> says, and the block it
    /// swallowed is added back. Finished blocks are left alone: VB XML literals stay possible there. The service
    /// normally builds from a repaired copy (<see cref="RepairPoint"/>), where every block is finished; this covers
    /// a second unfinished block.
    /// </remarks>
    private static void AddBlock(string text, int start, int end, List<Block> blocks)
    {
        int codeStart = start + 2;
        bool closed = end >= codeStart + 2 && text[end - 2] == '%' && text[end - 1] == '>';
        int codeEnd = closed ? end - 2 : end;
        if (UnfinishedEnd(text, start, end) is not { } cut)
        {
            AddCode(text, codeStart, codeEnd, blocks);
            return;
        }

        AddCode(text, codeStart, cut, blocks);
        int nested = text.IndexOf("<%", codeStart, codeEnd - codeStart, StringComparison.Ordinal);
        if (nested >= 0)
        {
            AddBlock(text, nested, end, blocks);
        }
    }

    /// <summary>
    /// Where the first unfinished block's code ends, if the file has one: the place to insert <see cref="Repair"/> so
    /// that the markup after it tokenizes as it will once the block is closed (the run-on block otherwise swallows the
    /// markup up to the next <c>%&gt;</c>: end tags of templates, whole tags around an attribute value).
    /// </summary>
    public static int? RepairPoint(string text, IReadOnlyList<MarkupToken> tokens)
    {
        foreach (var t in tokens)
        {
            if (t.Kind == TokenKind.CodeBlock && UnfinishedEnd(text, t.Start.Offset, t.End) is { } cut)
            {
                return cut;
            }

            if (t.Kind == TokenKind.StartTag)
            {
                foreach (var a in t.Attributes)
                {
                    if (a.ValuePosition is { } vp && a.Value is { } value && value.Contains("<%", StringComparison.Ordinal))
                    {
                        foreach (Match m in ServerBlock().Matches(value))
                        {
                            if (UnfinishedEnd(text, vp.Offset + m.Index, vp.Offset + m.Index + m.Length) is { } c)
                            {
                                return c;
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// For an unfinished block (one containing <c>&lt;%</c>, or unterminated): where its code ends, at the first tag,
    /// or, for a block that starts an attribute value, at that value's closing quote (followed by whitespace, <c>&gt;</c>
    /// or <c>/</c>; a VB string's quote is followed by its text or punctuation). Null for a finished block.
    /// </summary>
    private static int? UnfinishedEnd(string text, int start, int end)
    {
        int codeStart = start + 2;
        bool closed = end >= codeStart + 2 && text[end - 2] == '%' && text[end - 1] == '>';
        int codeEnd = closed ? end - 2 : end;
        int nested = codeStart < codeEnd ? text.IndexOf("<%", codeStart, codeEnd - codeStart, StringComparison.Ordinal) : -1;
        if (closed && nested < 0)
        {
            return null;
        }

        int cut = FirstTag(text, codeStart, nested < 0 ? codeEnd : nested);
        if (start > 0 && text[start - 1] is '\'' or '"' && IsAttributeValueStart(text, start - 1))
        {
            char quote = text[start - 1];
            for (int i = text.IndexOf(quote, codeStart, cut - codeStart); i >= 0; i = text.IndexOf(quote, i + 1, cut - i - 1))
            {
                if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]) || text[i + 1] is '>' or '/')
                {
                    return i;
                }
            }
        }

        return cut;
    }

    /// <summary>Whether the quote opens an attribute value (<c>Text='</c>), not a string in client script (<c>"@id": "</c>).</summary>
    private static bool IsAttributeValueStart(string text, int quote)
    {
        int i = quote - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        return i >= 0 && text[i] == '=';
    }

    /// <summary>The offset of the first <c>&lt;x</c>, <c>&lt;/</c> or <c>&lt;!</c> in the range, else its end.</summary>
    private static int FirstTag(string text, int start, int end)
    {
        for (int i = text.IndexOf('<', start, end - start); i >= 0 && i + 1 < end; i = text.IndexOf('<', i + 1, end - i - 1))
        {
            if (char.IsAsciiLetter(text[i + 1]) || text[i + 1] is '/' or '!')
            {
                return i;
            }
        }

        return end;
    }

    private static void AddCode(string text, int codeStart, int codeEnd, List<Block> blocks)
    {
        if (codeStart > codeEnd)
        {
            return;
        }

        // ASP.NET's expression regex is <%\s*?= : whitespace may come before the '=' (<% =x %>).
        int first = codeStart;
        while (first < codeEnd && char.IsWhiteSpace(text[first]))
        {
            first++;
        }

        if (first < codeEnd && text[first] == '=')
        {
            blocks.Add(new Block(BlockKind.Expression, first + 1, codeEnd - first - 1));
            return;
        }

        var kind = BlockKind.Statement;
        char c = codeStart < codeEnd ? text[codeStart] : ' ';
        switch (c)
        {
            case '=' or ':':
                kind = BlockKind.Expression;
                codeStart++;
                break;
            case '#':
                kind = BlockKind.DataBinding;
                codeStart++;
                if (codeStart < codeEnd && text[codeStart] == ':')
                {
                    codeStart++;
                }

                break;
            case '$' or '@' or '-':
                return; // expression builders, directives, server comments: not VB
        }

        blocks.Add(new Block(kind, codeStart, Math.Max(0, codeEnd - codeStart)));
    }

    [GeneratedRegex("<%.*?(%>|$)", RegexOptions.Singleline)]
    private static partial Regex ServerBlock();

    private sealed class Writer(string markup)
    {
        private readonly StringBuilder _sb = new();

        public List<BlockSpan> Spans { get; } = [];

        public void Line(string s) => _sb.Append(s).Append("\r\n");

        public void Append(string s) => _sb.Append(s);

        public void Code(Block b)
        {
            Spans.Add(new BlockSpan(b.Start, _sb.Length, b.Length));
            _sb.Append(markup, b.Start, b.Length);
        }

        public override string ToString() => _sb.ToString();
    }
}
