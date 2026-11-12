using WebForms.Designer;
using WebForms.Markup;

namespace WebForms.LanguageServer;

public enum HitPart
{
    /// <summary>The tag or directive name.</summary>
    Name,
    AttributeName,
    AttributeValue,

    /// <summary>Elsewhere inside the token (whitespace between attributes, the closing bracket, …).</summary>
    Other,
}

/// <summary>What is under the cursor: a start tag or directive, and possibly one of its attributes.</summary>
public sealed record MarkupHit(MarkupToken Token, MarkupElement? Element, MarkupAttribute? Attribute, HitPart Part);

/// <summary>
/// A markup file run through the designer pipeline (<see cref="DesignerGenerator"/>), so that elements carry the same
/// resolved control types the designer file is generated from.
/// </summary>
public sealed class MarkupAnalysis
{
    private readonly Dictionary<MarkupToken, MarkupElement> _elements = new(ReferenceEqualityComparer.Instance);
    private readonly List<MarkupElement> _ordered = [];

    private MarkupAnalysis(SourceDocument document, ProjectContext project, GenerationResult result, IReadOnlyList<MarkupToken> tokens)
    {
        Document = document;
        Project = project;
        Result = result;
        Tokens = tokens;
        if (result.Document is { } doc)
        {
            foreach (var el in doc.AllElements())
            {
                _elements[el.StartTag] = el;
                _ordered.Add(el);
            }
        }
    }

    public SourceDocument Document { get; }
    public ProjectContext Project { get; }
    public GenerationResult Result { get; }
    public IReadOnlyList<MarkupToken> Tokens { get; }

    public string Path => Document.Path;

    /// <summary>The code-behind class name (last segment of Inherits), or null.</summary>
    public string? ClassName => Result.Model?.ClassName ?? InheritsClassName();

    /// <summary>The CodeBehind (or CodeFile) file, if the directive names one.</summary>
    public string? CodeBehindPath => Result.CodeBehindPath ?? CodeFilePath();

    public string DesignerPath => Path + ".designer.vb";

    /// <summary>Server elements of the document (empty when the file has no usable directives).</summary>
    public IReadOnlyList<MarkupElement> Elements => _ordered;

    public MarkupToken? MainDirective => Tokens.FirstOrDefault(t => t.Kind == TokenKind.Directive &&
        (t.Name.Equals("Page", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
         t.Name.Equals("Master", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The schema the page is parsed with: page Register directives, then web.config, then the built-in <c>asp</c>
    /// registrations.
    /// </summary>
    public ControlSchema Schema => _schema ??= new ControlSchema(Project, Path, DesignerGenerator.PageRegistrations(Tokens));

    private ControlSchema? _schema;
    private MarkupDocument? _tree;

    /// <summary>
    /// The element tree: the designer pipeline's, or, for a page it skips (CodeFile, no CodeBehind), the same parse
    /// done here so that completion works there too.
    /// </summary>
    public MarkupDocument Tree => _tree ??= Result.Document ?? MarkupDocument.Parse(Document.Text, Schema);

    /// <summary>
    /// The innermost server element whose body contains <paramref name="offset"/>. An element that is never closed
    /// extends to the end of its parent (the user is still typing its body).
    /// </summary>
    public MarkupElement? ElementContaining(int offset)
    {
        MarkupElement? found = null;
        var level = (IReadOnlyList<MarkupElement>)Tree.Roots;
        while (true)
        {
            MarkupElement? inner = null;
            foreach (var el in level)
            {
                if (el.StartTag.End > offset || el.StartTag.Start.Offset >= offset)
                {
                    break; // children are in document order
                }

                bool open = el.EndTag is null && !el.StartTag.SelfClosing && el.Body != BodyMode.Empty;
                if (open || (el.EndTag is { } end && offset <= end.Start.Offset))
                {
                    inner = el; // a later sibling can still contain it if this one was never closed
                }
            }

            if (inner is null)
            {
                return found;
            }

            found = inner;
            level = inner.Children;
        }
    }

    public static MarkupAnalysis Create(SourceDocument document, ProjectContext project, Func<string, string, CodeBehindInfo>? scanCodeBehind = null)
    {
        var result = DesignerGenerator.Generate(document.Path, document.Text, project, scanCodeBehind);
        var tokens = result.Document?.Tokens ?? MarkupDocument.ParseDirectivesOnly(document.Text).Tokens;
        return new MarkupAnalysis(document, project, result, tokens);
    }

    public MarkupElement? ElementFor(MarkupToken startTag) => _elements.GetValueOrDefault(startTag);

    /// <summary>Server elements whose ID is <paramref name="id"/> (case-insensitive, like FindControl).</summary>
    public IEnumerable<MarkupElement> ElementsWithId(string id) =>
        Elements.Where(e => e.Id is { } x && x.Trim().Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The start tag or directive at an offset, and the attribute under it.</summary>
    public MarkupHit? HitTest(int offset)
    {
        var token = FindToken(offset);
        if (token is null)
        {
            return null;
        }

        var element = token.Kind == TokenKind.StartTag ? ElementFor(token) : null;
        foreach (var a in token.Attributes)
        {
            if (Within(offset, a.NamePosition.Offset, a.Name.Length))
            {
                return new MarkupHit(token, element, a, HitPart.AttributeName);
            }

            if (a.ValuePosition is { } vp && a.Value is not null && Within(offset, vp.Offset, a.Value.Length))
            {
                return new MarkupHit(token, element, a, HitPart.AttributeValue);
            }
        }

        if (token.Name.Length > 0 && Within(offset, token.NamePosition.Offset, token.Name.Length))
        {
            return new MarkupHit(token, element, null, HitPart.Name);
        }

        return new MarkupHit(token, element, null, HitPart.Other);
    }

    /// <summary>Inclusive of the end, so a cursor just after the last character still hits.</summary>
    private static bool Within(int offset, int start, int length) => offset >= start && offset <= start + length;

    private MarkupToken? FindToken(int offset)
    {
        int lo = 0, hi = Tokens.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var t = Tokens[mid];
            if (offset < t.Start.Offset)
            {
                hi = mid - 1;
            }
            else if (offset >= t.End)
            {
                lo = mid + 1;
            }
            else
            {
                return t.Kind is TokenKind.StartTag or TokenKind.Directive ? t : null;
            }
        }

        return null;
    }

    private string? InheritsClassName()
    {
        var inherits = MainDirective?.GetAttributeValue("Inherits")?.Trim();
        return string.IsNullOrEmpty(inherits) ? null : inherits[(inherits.LastIndexOf('.') + 1)..];
    }

    private string? CodeFilePath()
    {
        var main = MainDirective;
        var file = main?.GetAttributeValue("CodeBehind") ?? main?.GetAttributeValue("CodeFile");
        return string.IsNullOrWhiteSpace(file) ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, file.Trim());
    }
}
