using WebForms.Designer;
using WebForms.LanguageServer.Protocol;
using WebForms.Markup;

namespace WebForms.LanguageServer;

/// <summary>
/// The diagnostics published for a markup file (<c>source = "webforms"</c>):
/// <list type="bullet">
/// <item>the designer pipeline's, with the CLI's codes and severities (WF1xxx directives, WF2001 parse, WF3xxx types);</item>
/// <item>WF5001 (warning): an <c>On&lt;Event&gt;</c> attribute names a Sub the code-behind class doesn't declare (not
/// reported when the class inherits a custom base class, which may declare it);</item>
/// <item>WF5002 (error): the handler is Private or Friend, which the page class compiled from the markup (a derived class
/// in another assembly) can't call (BC30390 at runtime);</item>
/// <item>WF0100 (information): the designer file differs from the one the markup produces, as
/// <c>webforms designer --check</c> reports. Only when the buffer is saved: an edited buffer is always ahead of its
/// designer. The "Generate designer" code action fixes it.</item>
/// </list>
/// </summary>
public sealed class MarkupDiagnostics(WebFormsWorkspace workspace)
{
    public const string Source = "webforms";
    public const string StaleDesignerCode = "WF0100";

    private static readonly HashSet<string> FrameworkBases = new(StringComparer.OrdinalIgnoreCase) { "Page", "UserControl", "MasterPage" };

    private readonly Dictionary<string, (MarkupAnalysis Analysis, object? DesignerStamp, object? MarkupStamp, bool Stale)> _stale =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Report WF0100 (setting <c>diagnostics.staleDesigner</c>).</summary>
    public bool StaleDesigner { get; set; } = true;

    public IReadOnlyList<LspDiagnostic> Compute(string path)
    {
        if (workspace.Analyze(path) is not { } analysis)
        {
            return [];
        }

        var doc = analysis.Document;
        var results = new List<LspDiagnostic>();
        foreach (var d in analysis.Result.Diagnostics)
        {
            int offset = doc.ToOffset(new LspPosition(d.Line - 1, d.Column - 1));
            results.Add(new LspDiagnostic(WordRange(doc, offset), ToLsp(d.Severity), d.Code, Source, d.Message));
        }

        AddHandlerDiagnostics(analysis, results);

        if (StaleDesigner && IsStale(analysis) && analysis.MainDirective is { } main)
        {
            results.Add(new LspDiagnostic(WordRange(doc, main.Start.Offset), DiagnosticSeverity.Information, StaleDesignerCode, Source,
                File.Exists(analysis.DesignerPath)
                    ? "Designer file is out of date."
                    : "Designer file does not exist."));
        }

        return [.. results
            .Distinct()
            .OrderBy(d => d.Range.Start.Line).ThenBy(d => d.Range.Start.Character).ThenBy(d => d.Code, StringComparer.Ordinal)];
    }

    public void Forget(string path) => _stale.Remove(path);

    private void AddHandlerDiagnostics(MarkupAnalysis analysis, List<LspDiagnostic> results)
    {
        if (analysis.Result.SkipReason is not null || analysis.CodeBehindPath is not { } cbPath || analysis.ClassName is not { } cls ||
            workspace.GetVbFile(cbPath) is not { } vb || vb.BaseTypes(cls) is not { } bases)
        {
            return; // no code-behind, or no such class in it (WF1003)
        }

        bool customBase = bases.Any(b => !FrameworkBases.Contains(b[(b.LastIndexOf('.') + 1)..]));
        string cbName = Path.GetFileName(cbPath);
        foreach (var el in analysis.Elements)
        {
            if (el.Annotation is not ResolvedControl { Type: not null, IsUserControl: false })
            {
                continue; // events of user controls and unresolved types aren't known
            }

            foreach (var attr in el.StartTag.Attributes)
            {
                if (!Navigator.IsEventAttribute(el, attr) || attr.ValuePosition is not { } valuePos)
                {
                    continue;
                }

                string name = attr.Value!.Trim();
                var range = analysis.Document.ToRange(valuePos.Offset + (attr.Value.Length - attr.Value.TrimStart().Length), name.Length);
                var access = vb.MethodAccess(cls, name);
                if (access.Count == 0 && !customBase)
                {
                    results.Add(new LspDiagnostic(range, DiagnosticSeverity.Warning, "WF5001", Source,
                        $"No Sub '{name}' in class '{cls}' ({cbName}) to handle {attr.Name}."));
                }
                else if (access.Count > 0 && !access.Any(a => a))
                {
                    results.Add(new LspDiagnostic(range, DiagnosticSeverity.Error, "WF5002", Source,
                        $"'{name}' is Private or Friend: the page class compiled from the markup inherits '{cls}' from another assembly and can't call it. Make it Protected."));
                }
            }
        }
    }

    /// <summary>Whether the saved markup's designer differs from the file on disk; cached by analysis and file stamps.</summary>
    private bool IsStale(MarkupAnalysis analysis)
    {
        string path = analysis.Path;
        object? designerStamp = Stamp(analysis.DesignerPath);
        object? markupStamp = Stamp(path);
        if (_stale.TryGetValue(path, out var cached) && ReferenceEquals(cached.Analysis, analysis) &&
            Equals(cached.DesignerStamp, designerStamp) && Equals(cached.MarkupStamp, markupStamp))
        {
            return cached.Stale;
        }

        bool stale = IsSaved(analysis) && DesignerFiles.Build(analysis) is { } built &&
                     !(built.Existing is { } existing && existing.AsSpan().SequenceEqual(built.Generated));
        _stale[path] = (analysis, designerStamp, markupStamp, stale);
        return stale;
    }

    /// <summary>The analysed text is what is on disk (line endings aside: clients send the buffer's own).</summary>
    private static bool IsSaved(MarkupAnalysis analysis)
    {
        if (analysis.Document.Version is null)
        {
            return true;
        }

        try
        {
            return Normalize(File.ReadAllText(analysis.Path)) == Normalize(analysis.Document.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        static string Normalize(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static object? Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
    }

    /// <summary>The name at <paramref name="offset"/> (a tag, directive or attribute name), skipping a leading <c>&lt;</c> or <c>&lt;%@</c>.</summary>
    internal static LspRange WordRange(SourceDocument doc, int offset)
    {
        var text = doc.Text;
        int start = Math.Clamp(offset, 0, text.Length);
        if (string.CompareOrdinal(text, start, "<%@", 0, 3) == 0)
        {
            start += 3;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }
        }
        else if (start < text.Length && text[start] == '<')
        {
            start++;
        }

        int end = start;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or ':' or '.' or '-'))
        {
            end++;
        }

        if (end == start)
        {
            start = Math.Clamp(offset, 0, text.Length);
            end = Math.Min(text.Length, start + 1);
        }

        return doc.ToRange(start, end - start);
    }

    private static int ToLsp(Severity severity) => severity switch
    {
        Severity.Error => DiagnosticSeverity.Error,
        Severity.Warning => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Information,
    };
}
