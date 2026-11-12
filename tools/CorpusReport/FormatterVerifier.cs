using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using WebForms.Formatting;

/// <summary>
/// Runs <see cref="VbFormatter"/> over every .vb file of the corpus (read-only) and checks that it
/// never throws, is idempotent, gives the same result for "the whole file" as for a line range covering it,
/// and only changes tokens in the ways pretty listing is meant to (case, <c>Then</c>, <c>()</c>, <c>:</c>,
/// numeric literal spelling). Formatted copies of changed files go to &lt;out&gt;/formatted for diffing.
/// </summary>
internal static class FormatterVerifier
{
    public static int Run(string corpus, string outDir, string? filter)
    {
        var formattedDir = Path.Combine(outDir, "formatted");
        if (Directory.Exists(formattedDir))
        {
            Directory.Delete(formattedDir, recursive: true);
        }

        int files = 0, unchanged = 0, changed = 0, skipped = 0, refused = 0, problems = 0;
        int lineEdits = 0;
        var report = new StringBuilder();
        var sw = Stopwatch.StartNew();
        foreach (var file in Directory.EnumerateFiles(corpus, "*.vb", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(corpus, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "node_modules" or ".git" or ".claude" or "packages") ||
                (filter is not null && !rel.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            files++;
            var text = ReadText(file);
            try
            {
                var result = VbFormatter.Format(text, fileName: file);
                if (result.SkipReason is not null)
                {
                    skipped++;
                    continue;
                }

                if (result.Text is null)
                {
                    refused++;
                    report.AppendLine(CultureInfo.InvariantCulture, $"refused  {rel}: {string.Join("; ", result.Diagnostics.Select(d => $"({d.Line},{d.Column}) {d.Code} {d.Message}"))}");
                    continue;
                }

                var output = result.Text;
                if (output == text)
                {
                    unchanged++;
                }
                else
                {
                    changed++;
                    var dest = Path.Combine(formattedDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllText(dest, output);

                    // The language server sends the result as line edits: they must rebuild it exactly.
                    var doc = new WebForms.LanguageServer.SourceDocument(file, text, 1);
                    var edits = WebForms.LanguageServer.LineDiff.Edits(doc, output);
                    lineEdits += edits.Length;
                    if (ApplyEdits(doc, edits) != output)
                    {
                        problems++;
                        report.AppendLine(CultureInfo.InvariantCulture, $"PROBLEM  {rel}: the language server's line edits don't reproduce the result");
                    }
                }

                var again = VbFormatter.Format(output, fileName: file).Text;
                if (again != output)
                {
                    problems++;
                    report.AppendLine(CultureInfo.InvariantCulture, $"PROBLEM  {rel}: not idempotent");
                }

                int lineCount = Microsoft.CodeAnalysis.Text.SourceText.From(text).Lines.Count;
                var ranged = VbFormatter.Format(text, new LineRange(1, lineCount), file).Text;
                if (ranged != output)
                {
                    problems++;
                    report.AppendLine(CultureInfo.InvariantCulture, $"PROBLEM  {rel}: --lines 1-{lineCount} differs from the whole-file result");
                }

                // A range in the middle touches only its own lines (and the previous line's trailing whitespace).
                if (lineCount >= 9)
                {
                    int from = lineCount / 3, to = 2 * lineCount / 3;
                    if (VbFormatter.Format(text, new LineRange(from, to), file).Text is { } middle &&
                        OutsideRangeChanged(text, middle, from, to) is { } where)
                    {
                        problems++;
                        report.AppendLine(CultureInfo.InvariantCulture, $"PROBLEM  {rel}: --lines {from}-{to} changed line {where}");
                    }
                }

                if (TokenMismatch(text, output) is { } mismatch)
                {
                    problems++;
                    report.AppendLine(CultureInfo.InvariantCulture, $"PROBLEM  {rel}: token change {mismatch}");
                }
            }
#pragma warning disable CA1031 // report and carry on
            catch (Exception ex)
#pragma warning restore CA1031
            {
                problems++;
                report.AppendLine(CultureInfo.InvariantCulture, $"PROBLEM  {rel}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.Write(report);
        Console.WriteLine($"Formatter over {files} .vb files: {unchanged} unchanged, {changed} changed, {skipped} skipped (generated), {refused} refused (syntax errors), {problems} problem(s); {lineEdits} LSP line edits for the changed files; {sw.Elapsed.TotalSeconds:F1} s.");
        Console.WriteLine($"Changed files: {formattedDir}");
        return problems == 0 ? 0 : 1;
    }

    private static string ApplyEdits(WebForms.LanguageServer.SourceDocument doc, IEnumerable<WebForms.LanguageServer.Protocol.TextEdit> edits)
    {
        var text = doc.Text;
        foreach (var e in edits.OrderByDescending(e => doc.ToOffset(e.Range.Start)))
        {
            int start = doc.ToOffset(e.Range.Start), end = doc.ToOffset(e.Range.End);
            text = string.Concat(text.AsSpan(0, start), e.NewText, text.AsSpan(end));
        }

        return text;
    }

    /// <summary>
    /// The first line outside <paramref name="from"/>..<paramref name="to"/> (1-based) that changed, or null. Lines
    /// after the range must be identical. Lines before it may change in whitespace and line continuations only:
    /// comments directly above the range are leading trivia of its first token and are indented with it, and cleanup
    /// works on whole statements, so a multi-line statement running into the range loses its unnecessary <c>_</c>.
    /// </summary>
    private static int? OutsideRangeChanged(string before, string after, int from, int to)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        static string Loose(string line)
        {
            line = line.Trim();
            return line.EndsWith(" _", StringComparison.Ordinal) ? line[..^2].TrimEnd() : line;
        }

        for (int i = 0; i < from - 1; i++)
        {
            if (i >= b.Length || Loose(a[i]) != Loose(b[i]))
            {
                return i + 1;
            }
        }

        for (int k = 1; k <= a.Length - to; k++)
        {
            if (k > b.Length || a[^k] != b[^k])
            {
                return a.Length - k + 1;
            }
        }

        return null;
    }

    private static string ReadText(string file)
    {
        var bytes = File.ReadAllBytes(file);
        int skip = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
    }

    /// <summary>
    /// Compares the token streams, ignoring trivia, case, statement-separator colons, <c>Then</c>, empty
    /// argument lists and the spelling of numeric literals. Returns the first difference, or null.
    /// </summary>
    private static string? TokenMismatch(string before, string after)
    {
        var a = Normalize(before);
        var b = Normalize(after);
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : ("<end>", 0);
            var y = i < b.Count ? b[i] : ("<end>", 0);
            if (x.Item1 != y.Item1)
            {
                return $"near line {x.Item2}: '{x.Item1}' -> '{y.Item1}'";
            }
        }

        return null;
    }

    private static readonly HashSet<SyntaxKind> Modifiers =
    [
        SyntaxKind.PublicKeyword, SyntaxKind.PrivateKeyword, SyntaxKind.ProtectedKeyword, SyntaxKind.FriendKeyword,
        SyntaxKind.PartialKeyword, SyntaxKind.SharedKeyword, SyntaxKind.ShadowsKeyword, SyntaxKind.OverloadsKeyword,
        SyntaxKind.OverridableKeyword, SyntaxKind.OverridesKeyword, SyntaxKind.NotOverridableKeyword,
        SyntaxKind.MustOverrideKeyword, SyntaxKind.MustInheritKeyword, SyntaxKind.NotInheritableKeyword,
        SyntaxKind.ReadOnlyKeyword, SyntaxKind.WriteOnlyKeyword, SyntaxKind.DefaultKeyword, SyntaxKind.WithEventsKeyword,
        SyntaxKind.WideningKeyword, SyntaxKind.NarrowingKeyword, SyntaxKind.StaticKeyword, SyntaxKind.ConstKeyword,
        SyntaxKind.DimKeyword, SyntaxKind.AsyncKeyword, SyntaxKind.IteratorKeyword,
    ];

    private static List<(string Text, int Line)> Normalize(string text)
    {
        var tokens = VisualBasicSyntaxTree.ParseText(text).GetRoot().DescendantTokens().ToList();
        var result = new List<(string, int)>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var kind = t.Kind();
            if (kind is SyntaxKind.ColonToken or SyntaxKind.ThenKeyword or SyntaxKind.EndOfFileToken ||
                (kind == SyntaxKind.CaseKeyword && t.Parent.IsKind(SyntaxKind.SelectStatement)))
            {
                continue;
            }

            if (Modifiers.Contains(kind))
            {
                // Modifier order is normalized ("Public Partial" -> "Partial Public"): compare runs sorted.
                int end = i;
                while (end + 1 < tokens.Count && Modifiers.Contains(tokens[end + 1].Kind()))
                {
                    end++;
                }

                int line = t.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                result.AddRange(tokens.Skip(i).Take(end - i + 1).Select(m => m.Text.ToUpperInvariant()).Order(StringComparer.Ordinal).Select(m => (m, line)));
                i = end;
                continue;
            }

            if (kind == SyntaxKind.OpenParenToken && i + 1 < tokens.Count && tokens[i + 1].IsKind(SyntaxKind.CloseParenToken))
            {
                i++;
                continue;
            }

            var s = kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatingLiteralToken or SyntaxKind.DecimalLiteralToken
                ? Convert.ToString(t.Value, CultureInfo.InvariantCulture)!
                : t.Text.ToUpperInvariant();
            result.Add((s, t.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        }

        return result;
    }
}
