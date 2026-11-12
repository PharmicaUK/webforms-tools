using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeCleanup;
using Microsoft.CodeAnalysis.CodeCleanup.Providers;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace WebForms.Formatting;

/// <summary>1-based, inclusive line range.</summary>
public readonly record struct LineRange(int Start, int End);

public sealed record FormatDiagnostic(int Line, int Column, string Severity, string Code, string Message)
{
    public string Format(string file) => $"{file}({Line},{Column}): {Severity} {Code}: {Message}";
}

public sealed class FormatResult
{
    /// <summary>The formatted text; null when the input was refused (see <see cref="Diagnostics"/>).</summary>
    public string? Text { get; init; }

    /// <summary>Set when the file was left alone on purpose (generated code); <see cref="Text"/> is the input.</summary>
    public string? SkipReason { get; init; }

    public IReadOnlyList<FormatDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Formats VB the way Visual Studio "pretty lists" it on line commit / Format Document: Roslyn's formatter plus the
/// VB code-cleanup providers (missing <c>Then</c> and <c>()</c>, <c>EndIf</c> to <c>End If</c>, modifier order,
/// <c>&amp;H00FF</c> to <c>&amp;HFF</c>, unnecessary <c>_</c> and <c>:</c>). Identifier case correction is
/// replaced by <see cref="KeywordCasing"/>, which only touches keywords: identifier casing needs the whole
/// project's semantics and guesses wrong on a single file.
/// <para>
/// The output depends only on the input text and the range. Files that still have syntax errors after cleanup
/// (a missing <c>End If</c>, an unterminated string) are refused rather than formatted around the error.
/// </para>
/// </summary>
public static class VbFormatter
{
    private static readonly VisualBasicParseOptions ParseOptions = new(LanguageVersion.Latest);

    public static FormatResult Format(string text, LineRange? lines = null, string? fileName = null)
    {
        return FormatAsync(text, lines, fileName, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static async Task<FormatResult> FormatAsync(string text, LineRange? lines, string? fileName, CancellationToken cancellationToken)
    {
        var source = SourceText.From(text);
        var span = ToSpan(source, lines);
        var tree = VisualBasicSyntaxTree.ParseText(source, ParseOptions, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);

        if (GeneratedCode.Reason(fileName, root) is { } reason)
        {
            return new FormatResult { Text = text, SkipReason = reason };
        }

        // Keyword casing never changes a token's length, so the span stays valid for the cleanup below.
        var cased = KeywordCasing.Apply(source, root, span);

        // Pass 1: the token providers. Pass 2: the formatter, on pass 1's tree (not its text: inserted tokens
        // such as "Then" have no whitespace before them yet). AddMissingTokens leaves a zero-width whitespace
        // trivia after the end-of-line of a statement it gave "()"; the formatter reads that as a line of its
        // own and inserts a blank line when a block statement follows, so those are stripped in between. (VS
        // runs both in one CodeCleaner call and has the same blank-line bug.)
        var newLine = NewLineOf(text);
        var document = CreateDocument(cased, newLine);
        var options = await document.GetCodeCleanupOptionsAsync(cancellationToken).ConfigureAwait(false);
        var providers = CodeCleaner.GetDefaultProviders(document).WhereAsArray(p =>
            p.Name is not (PredefinedCodeCleanupProviderNames.Simplification or PredefinedCodeCleanupProviderNames.CaseCorrection or PredefinedCodeCleanupProviderNames.Format));
        var tokensFixed = await CodeCleaner.CleanupAsync(document, span, options, providers, cancellationToken).ConfigureAwait(false);
        var tokensFixedText = await tokensFixed.GetTextAsync(cancellationToken).ConfigureAwait(false);
        span = MapSpan(span, tokensFixedText.GetTextChanges(cased));

        var tokensFixedRoot = await tokensFixed.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        tokensFixed = tokensFixed.WithSyntaxRoot(WithoutEmptyWhitespace(tokensFixedRoot!));
        // The formatter works on token pairs inside the span, so a range that starts at a line start would never
        // re-indent its first line (the pair is the previous line's last token and that line's first). Start from
        // the end of the previous line instead; at most that line's trailing whitespace changes.
        var formatSpan = span;
        if (lines is not null)
        {
            var fixedLines = tokensFixedText.Lines;
            int startLine = fixedLines.GetLineFromPosition(span.Start).LineNumber;
            if (startLine > 0)
            {
                formatSpan = TextSpan.FromBounds(fixedLines[startLine - 1].End, span.End);
            }
        }

        var cleaned = await Formatter.FormatAsync(tokensFixed, formatSpan, cancellationToken: cancellationToken).ConfigureAwait(false);

        var resultTree = await cleaned.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var remaining = resultTree!.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (remaining.Count > 0)
        {
            // Report against the caller's text: the original errors that cleanup did not fix (cleanup fixes some,
            // such as EndIf). Fall back to the result's errors if the ids don't line up.
            var ids = remaining.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            var original = tree.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error && ids.Contains(d.Id)).ToList();
            return new FormatResult { Diagnostics = (original.Count > 0 ? original : remaining).Select(ToDiagnostic).ToList() };
        }

        var output = (await cleaned.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        return new FormatResult { Text = output };
    }

    private static TextSpan ToSpan(SourceText source, LineRange? lines)
    {
        if (lines is not { } r)
        {
            return new TextSpan(0, source.Length);
        }

        int count = source.Lines.Count;
        if (r.Start < 1 || r.End < r.Start || r.Start > count)
        {
            throw new ArgumentOutOfRangeException(nameof(lines), $"Line range {r.Start}-{r.End} is outside the input (1-{count}).");
        }

        int end = Math.Min(r.End, count);
        return TextSpan.FromBounds(source.Lines[r.Start - 1].Start, source.Lines[end - 1].End);
    }

    /// <summary>Where <paramref name="span"/> ends up after <paramref name="changes"/> (sorted, non-overlapping).</summary>
    private static TextSpan MapSpan(TextSpan span, IReadOnlyList<TextChange> changes)
    {
        return TextSpan.FromBounds(Map(span.Start), Map(span.End));

        int Map(int position)
        {
            int delta = 0;
            foreach (var c in changes)
            {
                if (c.Span.Start >= position)
                {
                    break;
                }

                if (c.Span.End > position)
                {
                    return c.Span.Start + delta + (c.NewText?.Length ?? 0); // inside a change: after its new text
                }

                delta += (c.NewText?.Length ?? 0) - c.Span.Length;
            }

            return position + delta;
        }
    }

    private static SyntaxNode WithoutEmptyWhitespace(SyntaxNode root)
    {
        static bool Empty(SyntaxTrivia t) => t.IsKind(SyntaxKind.WhitespaceTrivia) && t.FullSpan.Length == 0;
        var tokens = root.DescendantTokens().Where(t => t.LeadingTrivia.Any(Empty) || t.TrailingTrivia.Any(Empty));
        return root.ReplaceTokens(tokens, (original, _) => original
            .WithLeadingTrivia(original.LeadingTrivia.Where(t => !Empty(t)))
            .WithTrailingTrivia(original.TrailingTrivia.Where(t => !Empty(t))));
    }

    private static string NewLineOf(string text)
    {
        int lf = text.IndexOf('\n', StringComparison.Ordinal);
        return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : lf >= 0 ? "\n" : "\r\n";
    }

    private static Document CreateDocument(SourceText text, string newLine)
    {
        // A throwaway single-file project with no references: deterministic and fast. The providers that use
        // semantics (adding "()" to a call) then only act on what binds inside the file.
        var workspace = new AdhocWorkspace(MefHostServices.DefaultHost);
        var dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "webforms-format"));
        var projectId = ProjectId.CreateNewId();
        var editorConfig = $"""
            root = true

            [*.vb]
            indent_style = space
            indent_size = 4
            tab_width = 4
            end_of_line = {(newLine == "\n" ? "lf" : "crlf")}
            """;

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "format", "format", LanguageNames.VisualBasic,
                filePath: Path.Combine(dir, "format.vbproj"),
                parseOptions: ParseOptions,
                compilationOptions: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
            .AddAnalyzerConfigDocument(DocumentId.CreateNewId(projectId), ".editorconfig", SourceText.From(editorConfig), filePath: Path.Combine(dir, ".editorconfig"));

        var documentId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(documentId, "input.vb", text, filePath: Path.Combine(dir, "input.vb"));
        return solution.GetDocument(documentId)!;
    }

    private static FormatDiagnostic ToDiagnostic(Diagnostic d)
    {
        var pos = d.Location.GetLineSpan().StartLinePosition;
        return new FormatDiagnostic(pos.Line + 1, pos.Character + 1, "error", d.Id, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ImmutableArray<T> WhereAsArray<T>(this ImmutableArray<T> array, Func<T, bool> predicate) => [.. array.Where(predicate)];
}
