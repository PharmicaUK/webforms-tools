using System.Diagnostics;
using System.Globalization;
using System.Text;
using WebForms.Designer;
using WebForms.LanguageServer;
using WebForms.Markup;

/// <summary>
/// Runs the language server's navigation (in process, read-only) over every markup file of the corpus:
/// <list type="bullet">
/// <item>every <c>On&lt;Event&gt;</c> attribute must resolve to a handler in the code-behind (unresolved ones are listed);</item>
/// <item>every control ID that gets a designer field must go to that field first (designers missing it are listed);</item>
/// <item>control-reference attributes (ControlToValidate, …) and directive paths are counted;</item>
/// <item>no request may throw, and no handler list may offer a Sub markup cannot wire (exit 1 otherwise).</item>
/// </list>
/// Writes &lt;out&gt;/lsp.md. Nothing in the corpus is written: the workspace only reads.
/// </summary>
internal static class LspVerifier
{
    public static int Run(string corpus, string outDir, string? filter)
    {
        var files = Directory.EnumerateFiles(corpus, "*.*", SearchOption.AllDirectories)
            .Where(f => DesignerGenerator.IsMarkupFile(f) && !IsExcluded(Path.GetRelativePath(corpus, f)))
            .Where(f => filter is null || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var workspace = new WebFormsWorkspace();
        var navigator = new Navigator(workspace);
        var completion = new CompletionVerifier(workspace);
        var markupDiagnostics = new MarkupDiagnostics(workspace);
        var codeBlocks = new CodeBlockVerifier(workspace);
        var diagnosticCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var handlerDiagnostics = new List<string>();
        var staleDesigners = new List<string>();
        var diagnosticsTime = TimeSpan.Zero;

        int analyzed = 0, events = 0, ids = 0, idsWithUses = 0, refs = 0, refsResolved = 0, paths = 0;
        var unresolvedHandlers = new List<string>();
        var missingFields = new List<string>();
        var missingPaths = new List<string>();
        var problems = new List<string>();
        var slowest = (Ms: 0.0, File: "");
        var sw = Stopwatch.StartNew();

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(corpus, file);
            var fileSw = Stopwatch.StartNew();
            try
            {
                var analysis = workspace.Analyze(file);
                var diagSw = Stopwatch.StartNew();
                var diagnostics = markupDiagnostics.Compute(file);
                diagnosticsTime += diagSw.Elapsed;
                foreach (var d in diagnostics)
                {
                    diagnosticCounts[d.Code] = diagnosticCounts.GetValueOrDefault(d.Code) + 1;
                    string at = $"{rel}({d.Range.Start.Line + 1},{d.Range.Start.Character + 1})";
                    if (d.Range.End.Line < d.Range.Start.Line || (d.Range.End.Line == d.Range.Start.Line && d.Range.End.Character <= d.Range.Start.Character))
                    {
                        problems.Add($"{at}: {d.Code} has an empty range");
                    }

                    if (d.Code is "WF5001" or "WF5002")
                    {
                        handlerDiagnostics.Add($"{at}: {d.Code}: {d.Message}");
                    }
                    else if (d.Code == MarkupDiagnostics.StaleDesignerCode)
                    {
                        staleDesigners.Add(rel);
                    }
                }

                if (analysis?.Result.Document is null)
                {
                    continue; // no CodeBehind (CodeFile / code-less control): only directive links, checked below
                }

                analyzed++;
                codeBlocks.Check(rel, analysis);
                completion.Check(rel, analysis, (el, attr) => navigator.Definition(file, analysis.Document.ToPosition(attr.ValuePosition!.Value.Offset)).Count > 0);
                var fieldNames = new HashSet<string>(analysis.Result.Model?.Fields.Select(f => f.Name) ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var el in analysis.Elements)
                {
                    foreach (var attr in el.StartTag.Attributes)
                    {
                        if (attr.ValuePosition is not { } vp || attr.Value is null || attr.IsDataBound)
                        {
                            continue;
                        }

                        var pos = analysis.Document.ToPosition(vp.Offset);
                        string where = $"{rel}({vp.Line},{vp.Column})";
                        if (Navigator.IsEventAttribute(el, attr))
                        {
                            events++;
                            if (navigator.Definition(file, pos).Count == 0)
                            {
                                unresolvedHandlers.Add($"{where}: {attr.Name}=\"{attr.Value}\"");
                            }
                        }
                        else if (attr.Name.Equals("ID", StringComparison.OrdinalIgnoreCase) && fieldNames.Contains(attr.Value.Trim()))
                        {
                            ids++;
                            var locations = navigator.Definition(file, pos);
                            if (locations.Count == 0 || !locations[0].Uri.EndsWith(".designer.vb", StringComparison.OrdinalIgnoreCase))
                            {
                                missingFields.Add($"{where}: {attr.Value} (no field in the existing designer)");
                            }

                            if (locations.Count > 1)
                            {
                                idsWithUses++;
                            }
                        }
                        else if (Navigator.TargetAt(analysis, vp.Offset) is ControlTarget { IsReference: true })
                        {
                            refs++;
                            if (navigator.Definition(file, pos).Count > 0)
                            {
                                refsResolved++;
                            }
                        }
                    }
                }

                foreach (var directive in analysis.Tokens.Where(t => t.Kind == TokenKind.Directive))
                {
                    foreach (var attr in directive.Attributes)
                    {
                        if (attr.ValuePosition is { } vp && Navigator.TargetAt(analysis, vp.Offset) is FileTarget target)
                        {
                            paths++;
                            if (!File.Exists(target.Path))
                            {
                                missingPaths.Add($"{rel}({vp.Line},{vp.Column}): {attr.Name}=\"{attr.Value}\"");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{rel}: {ex.GetType().Name}: {ex.Message}");
            }

            double ms = fileSw.Elapsed.TotalMilliseconds;
            if (ms > slowest.Ms)
            {
                slowest = (ms, rel);
            }
        }

        var elapsed = sw.Elapsed;
        var summary = new StringBuilder();
        summary.AppendLine(CultureInfo.InvariantCulture, $"LSP check: {files.Count} markup files, {analyzed} with a code-behind, {elapsed.TotalSeconds:F1} s (slowest {slowest.Ms:F0} ms: {slowest.File})");
        summary.AppendLine(CultureInfo.InvariantCulture, $"  event attributes   {events,5}  unresolved {unresolvedHandlers.Count}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"  control IDs        {ids,5}  without designer field {missingFields.Count}; with code-behind uses {idsWithUses}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"  control references {refs,5}  resolved in the same file {refsResolved}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"  directive paths    {paths,5}  missing file {missingPaths.Count}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"  diagnostics        {diagnosticCounts.Values.Sum(),5}  {string.Join(", ", diagnosticCounts.Select(kv => $"{kv.Key} x{kv.Value}"))}; {diagnosticsTime.TotalMilliseconds / Math.Max(1, files.Count):F1} ms per file");
        completion.Summarize(summary);
        codeBlocks.Summarize(summary);
        summary.Append(KeystrokeProbe.Run(files.OrderByDescending(f => new FileInfo(f).Length).Take(3)));
        summary.Append(CultureInfo.InvariantCulture, $"  problems           {problems.Count,5}");

        var report = new StringBuilder();
        report.AppendLine("# Language server check").AppendLine().AppendLine("```").AppendLine(summary.ToString()).AppendLine("```");
        Section(report, "Problems (exceptions)", problems);
        Section(report, "Event attributes without a handler in the code-behind", unresolvedHandlers);
        Section(report, "Control IDs whose designer field is missing (stale designer?)", missingFields);
        Section(report, "Directive paths to missing files", missingPaths);
        Section(report, "Diagnostics: handlers missing (WF5001) or not accessible (WF5002)", handlerDiagnostics);
        Section(report, "Diagnostics: designer differs from the generated one (WF0100, information)", staleDesigners);
        Section(report, "Code blocks: BC errors inside blocks (projection bug or real bug: check each)", codeBlocks.BcErrors);
        Section(report, "Code blocks: members not offered after 'x.'", codeBlocks.MissedMembers);
        Section(report, "Code blocks: members not offered after 'x.' in an unclosed block (rest of the block and its '%>' removed)", codeBlocks.MissedUnclosed);
        Section(report, "Code blocks: Eval field not among the typed DataSource's properties", codeBlocks.MissedEvalFields);
        Section(report, "Completion: tags not offered", completion.MissedTags);
        Section(report, "Completion: end tags whose element is not the innermost open one at '</'", completion.MissedEndTags);
        Section(report, "Completion: attributes (properties / events) not offered", completion.MissedAttributes);
        Section(report, "Completion: existing handlers not offered", completion.MissedHandlers);
        Section(report, "Completion: existing handlers that are parameterless Subs (not offered unless completion.zeroArgHandlers)", completion.ZeroArgHandlers);
        Section(report, "Completion: Private, Friend or parameterless Subs offered as handlers (failure)", completion.BadHandlerOffers);
        Section(report, "Completion: enum / boolean values not offered", completion.MissedValues);
        Section(report, "Expando attributes on server controls (not a property or event: rendered as HTML attributes)",
            [.. completion.ExpandoNames.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} x{kv.Value}")]);

        Directory.CreateDirectory(outDir);
        var reportPath = Path.Combine(outDir, "lsp.md");
        File.WriteAllText(reportPath, report.ToString());
        Console.WriteLine(summary);
        Console.WriteLine($"Report: {reportPath}");
        return problems.Count > 0 || completion.BadHandlerOffers.Count > 0 ? 1 : 0;
    }

    private static void Section(StringBuilder report, string title, List<string> items)
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"## {title} ({items.Count})").AppendLine();
        foreach (var item in items)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"- `{item}`");
        }

        report.AppendLine();
    }

    private static bool IsExcluded(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(p => p is "bin" or "obj" or "node_modules" or ".git" or ".vs" or ".claude" or "packages");
}
