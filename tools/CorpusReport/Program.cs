using System.Diagnostics;
using System.Text;
using WebForms.Designer;

// Corpus report: regenerate every existing designer file in a WebForms solution (read-only) and classify
// the result as exact / semantic / likely-stale / unexplained. See CLAUDE.md.
//
// Usage: dotnet run --project tools/CorpusReport -- <corpus-root> [--out <dir>] [--filter <substring>] [--verify-scanner] [--verify-formatter] [--verify-lsp]

string? corpus = null;
string outDir = Path.GetFullPath("artifacts/corpus-report");
string? filter = null;
bool verifyScanner = false;
bool verifyFormatter = false;
bool verifyLsp = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = Path.GetFullPath(args[++i]); break;
        case "--filter": filter = args[++i]; break;
        case "--verify-scanner": verifyScanner = true; break;
        case "--verify-formatter": verifyFormatter = true; break;
        case "--verify-lsp": verifyLsp = true; break;
        default: corpus = args[i]; break;
    }
}

if (corpus is null)
{
    Console.Error.WriteLine("Usage: CorpusReport <corpus-root> [--out <dir>] [--filter <substring>] [--verify-scanner] [--verify-formatter] [--verify-lsp]");
    return 3;
}

corpus = Path.GetFullPath(corpus);
if (verifyFormatter)
{
    return FormatterVerifier.Run(corpus, outDir, filter);
}

if (verifyLsp)
{
    return LspVerifier.Run(corpus, outDir, filter);
}

if (verifyScanner)
{
    return ScannerVerifier.Run(corpus);
}

Directory.CreateDirectory(outDir);
var generatedDir = Path.Combine(outDir, "generated");
if (Directory.Exists(generatedDir))
{
    Directory.Delete(generatedDir, recursive: true);
}

var git = new Git(corpus);
var lastCommit = git.LastCommitTimes();
var dirty = git.DirtyFiles();

var projectDirs = Directory.EnumerateFiles(corpus, "*.vbproj", SearchOption.AllDirectories)
    .Where(p => !IsExcluded(p))
    .Select(Path.GetDirectoryName)
    .Cast<string>()
    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
    .ToList();

var rows = new List<Row>();
var missingDesigners = new List<string>();
int masterPresent = 0;
var sw = Stopwatch.StartNew();

foreach (var projDir in projectDirs)
{
    var project = ProjectContext.ForDirectory(projDir);
    foreach (var markup in EnumerateMarkup(projDir))
    {
        if (filter is not null && !markup.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var designer = markup + ".designer.vb";
        bool hasDesigner = File.Exists(designer);
        var fileSw = Stopwatch.StartNew();
        GenerationResult result;
        try
        {
            result = DesignerGenerator.Generate(markup, File.ReadAllText(markup), project);
        }
        catch (Exception ex)
        {
            rows.Add(new Row(Rel(markup), Bucket.Error, $"exception: {ex.GetType().Name}: {ex.Message}", [], 0));
            continue;
        }

        double ms = fileSw.Elapsed.TotalMilliseconds;

        if (result.SkipReason is not null)
        {
            if (hasDesigner)
            {
                rows.Add(new Row(Rel(markup), Bucket.Error, $"designer exists but generator skipped: {result.SkipReason}", [], ms));
            }

            continue;
        }

        if (!hasDesigner)
        {
            missingDesigners.Add(Rel(markup));
            continue;
        }

        var diags = result.Diagnostics.Select(d => d.ToString().Replace(corpus + Path.DirectorySeparatorChar, "")).ToList();
        if (result.HasErrors || result.Model is null)
        {
            rows.Add(new Row(Rel(markup), Bucket.Error, "generation failed", diags, ms));
            continue;
        }

        var existingBytes = File.ReadAllBytes(designer);
        var style = DesignerStyle.Detect(existingBytes);
        // The report measures how faithfully existing files are reproduced, so keep the Rider Master property where it
        // exists (the CLI removes it by default; counted separately).
        var bytes = DesignerOutput.Build(result.Model, existingBytes, new OutputOptions(Master: MasterPropertyMode.Keep));
        if (ExistingDesigner.Parse(DesignerOutput.Decode(existingBytes)).MasterTypeName is not null)
        {
            masterPresent++;
        }

        var outPath = Path.Combine(generatedDir, Rel(designer));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllBytes(outPath, bytes);

        if (existingBytes.AsSpan().SequenceEqual(bytes))
        {
            rows.Add(new Row(Rel(markup), Bucket.Exact, StyleName(style), diags, ms));
            continue;
        }

        var existing = ExistingDesigner.Parse(DesignerOutput.Decode(existingBytes));
        var diff = SemanticDiff(result.Model, existing, project.RootNamespace);
        if (diff.Count == 0)
        {
            rows.Add(new Row(Rel(markup), Bucket.Semantic, FormattingDiff(existingBytes, bytes), diags, ms));
            continue;
        }

        // Semantic mismatch: is the existing designer simply stale?
        var relMarkup = Rel(markup);
        var relDesigner = Rel(designer);
        long tMarkup = dirty.Contains(relMarkup) ? long.MaxValue : lastCommit.GetValueOrDefault(relMarkup);
        long tDesigner = dirty.Contains(relDesigner) ? long.MaxValue : lastCommit.GetValueOrDefault(relDesigner);
        string? verified = null;
        if (tMarkup > tDesigner)
        {
            // Regenerate from the markup as it was when the designer was last committed.
            var commit = git.LastCommit(relDesigner);
            var oldMarkup = commit is null ? null : git.Show(commit, relMarkup);
            if (oldMarkup is not null)
            {
                var old = DesignerGenerator.Generate(markup, oldMarkup, project);
                if (old.Model is not null && SemanticDiff(old.Model, existing, project.RootNamespace).Count == 0)
                {
                    verified = $"matches markup at {commit}";
                }
            }

            rows.Add(new Row(relMarkup, Bucket.Stale,
                (verified ?? "markup committed after designer") + $" (markup {Date(tMarkup)}, designer {Date(tDesigner)})",
                [.. diff, .. diags], ms)
            { Verified = verified is not null });
            continue;
        }

        rows.Add(new Row(relMarkup, Bucket.Unexplained, $"markup {Date(tMarkup)}, designer {Date(tDesigner)}", [.. diff, .. diags], ms));
    }
}

var total = sw.Elapsed;
var report = BuildReport(rows, missingDesigners, total, masterPresent);
var reportPath = Path.Combine(outDir, "report.md");
File.WriteAllText(reportPath, report);

Console.WriteLine(Summary(rows, missingDesigners));
Console.WriteLine($"Report: {reportPath}");
Console.WriteLine($"Generated files: {generatedDir}");
return rows.Any(r => r.Bucket is Bucket.Unexplained or Bucket.Error) ? 1 : 0;

// ----------------------------------------------------------------------------------------------------------

static List<string> SemanticDiff(DesignerModel model, ExistingDesigner existing, string rootNs)
{
    var diff = new List<string>();
    if (!string.Equals(model.ClassName, existing.ClassName, StringComparison.Ordinal))
    {
        diff.Add($"class: generated `{model.ClassName}`, existing `{existing.ClassName}`");
    }

    if (!string.Equals(model.Namespace, existing.Namespace, StringComparison.Ordinal))
    {
        diff.Add($"namespace: generated `{model.Namespace ?? "(root)"}`, existing `{existing.Namespace ?? "(root)"}`");
    }

    // A missing Master property is not a difference (Visual Studio never writes it).
    if (existing.MasterTypeName is not null && !string.Equals(model.MasterTypeName, existing.MasterTypeName, StringComparison.Ordinal))
    {
        diff.Add($"Master: generated `{model.MasterTypeName ?? "(none)"}`, existing `{existing.MasterTypeName ?? "(none)"}`");
    }

    var gen = model.Fields.ToDictionary(f => f.Name, f => f.TypeName, StringComparer.Ordinal);
    var ex = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (n, t) in existing.Fields)
    {
        ex.TryAdd(n, t);
    }

    foreach (var (n, t) in gen)
    {
        if (!ex.TryGetValue(n, out var et))
        {
            diff.Add($"+ `{n}` As `{Short(t)}` (generated only)");
        }
        else if (et != t)
        {
            diff.Add($"~ `{n}`: generated `{Short(t)}`, existing `{Short(et)}`");
        }
    }

    foreach (var (n, t) in ex)
    {
        if (!gen.ContainsKey(n))
        {
            diff.Add($"- `{n}` As `{Short(t)}` (existing only)");
        }
    }

    return diff;
}

static string Short(string t) => t.Replace("Global.System.Web.UI.", "").Replace("Global.", "");

static string StyleName(DesignerStyle s) =>
    (s.Bom ? "BOM" : "no BOM") + (s.HeaderTrailingSpace ? ", header space" : ", trimmed") + (s.IndentedBlankLines ? ", indented blanks" : "");

static string FormattingDiff(byte[] existing, byte[] generated)
{
    var a = Encoding.UTF8.GetString(existing).TrimStart('\uFEFF').Split('\n');
    var b = Encoding.UTF8.GetString(generated).TrimStart('\uFEFF').Split('\n');
    int n = Math.Min(a.Length, b.Length);
    for (int i = 0; i < n; i++)
    {
        if (a[i] != b[i])
        {
            return $"first difference at line {i + 1}: existing `{Esc(a[i])}` vs generated `{Esc(b[i])}`";
        }
    }

    return a.Length != b.Length ? $"line count {a.Length} vs {b.Length}" : "byte-level difference (encoding/BOM)";
}

static string Esc(string s) => s.Replace("\r", "\\r").Replace("`", "'");

static string Date(long t) => t == long.MaxValue ? "uncommitted" : t == 0 ? "?" : DateTimeOffset.FromUnixTimeSeconds(t).ToString("yyyy-MM-dd");

string Rel(string p) => Path.GetRelativePath(corpus, p).Replace('\\', '/');

static bool IsExcluded(string path)
{
    var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return parts.Any(p => p is "bin" or "obj" or "node_modules" or ".git" or ".vs" or ".claude" or "packages");
}

static IEnumerable<string> EnumerateMarkup(string dir) =>
    Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
        .Where(f => DesignerGenerator.IsMarkupFile(f) && !IsExcluded(Path.GetRelativePath(dir, f)))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

static string Summary(List<Row> rows, List<string> missing)
{
    var sb = new StringBuilder();
    foreach (var b in Enum.GetValues<Bucket>())
    {
        sb.AppendLine($"{b,-12} {rows.Count(r => r.Bucket == b),5}");
    }

    sb.AppendLine($"{"Total",-12} {rows.Count,5}");
    sb.Append($"(markup with CodeBehind but no designer file: {missing.Count})");
    return sb.ToString();
}

static string BuildReport(List<Row> rows, List<string> missing, TimeSpan total, int masterPresent)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Designer corpus report");
    sb.AppendLine();
    sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. {rows.Count} existing designer files, {total.TotalSeconds:F1} s total, " +
                  $"median {Median(rows):F1} ms/file (in-process, warm).");
    sb.AppendLine();
    sb.AppendLine($"{masterPresent} existing designers have the typed `Master` property that Rider adds. It is kept here (`--master keep`) to compare the rest of the file; the CLI removes it by default.");
    sb.AppendLine();
    sb.AppendLine("| Bucket | Count | Meaning |");
    sb.AppendLine("|---|---:|---|");
    sb.AppendLine($"| Exact | {rows.Count(r => r.Bucket == Bucket.Exact)} | byte-identical (style auto-detected from the existing file) |");
    sb.AppendLine($"| Semantic | {rows.Count(r => r.Bucket == Bucket.Semantic)} | same class/namespace/Master/fields; formatting or field order differs |");
    int stale = rows.Count(r => r.Bucket == Bucket.Stale);
    sb.AppendLine($"| Likely stale | {stale} | markup committed after the designer ({rows.Count(r => r.Bucket == Bucket.Stale && r.Verified)} verified: generating from the markup at the designer's commit reproduces it) |");
    sb.AppendLine($"| Unexplained | {rows.Count(r => r.Bucket == Bucket.Unexplained)} | semantic mismatch, designer not older than markup |");
    sb.AppendLine($"| Error | {rows.Count(r => r.Bucket == Bucket.Error)} | generator failed |");
    sb.AppendLine();

    foreach (var b in new[] { Bucket.Error, Bucket.Unexplained, Bucket.Stale, Bucket.Semantic })
    {
        var list = rows.Where(r => r.Bucket == b).ToList();
        if (list.Count == 0)
        {
            continue;
        }

        sb.AppendLine($"## {b} ({list.Count})");
        sb.AppendLine();
        foreach (var r in list)
        {
            sb.AppendLine($"### `{r.File}`");
            sb.AppendLine();
            sb.AppendLine(r.Note);
            sb.AppendLine();
            foreach (var d in r.Details)
            {
                sb.AppendLine($"- {d}");
            }

            sb.AppendLine();
        }
    }

    sb.AppendLine($"## Markup with CodeBehind but no designer file ({missing.Count})");
    sb.AppendLine();
    foreach (var m in missing)
    {
        sb.AppendLine($"- `{m}`");
    }

    sb.AppendLine();
    sb.AppendLine("## Exact matches by style");
    sb.AppendLine();
    foreach (var g in rows.Where(r => r.Bucket == Bucket.Exact).GroupBy(r => r.Note).OrderByDescending(g => g.Count()))
    {
        sb.AppendLine($"- {g.Key}: {g.Count()}");
    }

    return sb.ToString();
}

static double Median(List<Row> rows)
{
    var t = rows.Select(r => r.Ms).Where(x => x > 0).Order().ToList();
    return t.Count == 0 ? 0 : t[t.Count / 2];
}

enum Bucket
{
    Exact,
    Semantic,
    Stale,
    Unexplained,
    Error,
}

sealed record Row(string File, Bucket Bucket, string Note, List<string> Details, double Ms)
{
    public bool Verified { get; init; }
}

sealed class Git(string root)
{
    public Dictionary<string, long> LastCommitTimes()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long current = 0;
        foreach (var line in Run("log", "--format=@%ct", "--name-only", "--no-renames", "--", "*.aspx", "*.ascx", "*.master", "*.designer.vb").Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0)
            {
                continue;
            }

            if (l[0] == '@')
            {
                current = long.Parse(l[1..]);
            }
            else
            {
                map.TryAdd(l, current);
            }
        }

        return map;
    }

    public HashSet<string> DirtyFiles() =>
        Run("status", "--porcelain", "--untracked-files=all").Split('\n')
            .Where(l => l.Length > 3)
            .Select(l => l[3..].Trim().Trim('"'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string? LastCommit(string rel)
    {
        var h = Run("log", "-1", "--format=%h", "--", rel).Trim();
        return h.Length == 0 ? null : h;
    }

    public string? Show(string commit, string rel)
    {
        var s = Run("show", $"{commit}:{rel}");
        return s.Length == 0 ? null : s;
    }

    private string Run(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? stdout.Result : "";
    }
}
