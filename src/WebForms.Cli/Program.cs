using System.Diagnostics;
using System.Reflection;
using WebForms.Designer;

namespace WebForms.Cli;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int WouldChange = 1;
    public const int Error = 2;
    public const int Usage = 3;
}

public static class Program
{
    private const string Usage = """
        webforms - tooling for ASP.NET WebForms (VB.NET) Web Application Projects

        Usage:
          webforms designer <file.aspx|ascx|master>... [options]
          webforms designer --all <project-dir> [options]
          webforms format [format options] < input.vb > output.vb
          webforms hook claude          (Claude Code PostToolUse hook; reads hook JSON on stdin)
          webforms lsp [--log <file>]   (ASPX language server over stdin/stdout)
          webforms --version

        Designer options:
          --check          Do not write; exit 1 if any designer file would change.
          --stdout         Print the generated designer to stdout instead of writing it.
          --all <dir>      Process every .aspx/.ascx/.master with a CodeBehind under <dir>.
          --style <s>      auto (default: keep the existing file's BOM/whitespace style),
                           vs (Visual Studio), rider (trailing whitespace trimmed).
          --master <m>     When to write the typed Master property: directive (default, as
                           Visual Studio: only for <%@ MasterType %>; one Rider added for a
                           MasterPageFile alone is removed), keep (also keep an existing
                           one), always (any MasterPageFile, as Rider).
          --verbose        Report skipped and unchanged files on stderr.
          --time           Report timing on stderr.

        Format options (VB source on stdin, formatted source on stdout):
          --stdin-filename <path>  Name used in diagnostics; *.designer.vb is passed through.
          --lines <a>-<b>  Only format lines a..b (1-based, inclusive).
          --check          Print nothing; exit 1 if formatting would change the input.
          --verbose, --time

        Exit codes: 0 unchanged/written/formatted, 1 would change (--check), 2 error, 3 usage.
        Diagnostics go to stderr as: file(line,col): error|warning CODE: message
        """;

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (DesignerException ex)
        {
            Console.Error.WriteLine($"webforms: error: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.Out.Write(Usage);
            return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
        }

        if (args[0] is "--version" or "-v")
        {
            var v = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            Console.Out.WriteLine(v.Split('+')[0]);
            return ExitCodes.Ok;
        }

        if (args[0] == "designer")
        {
            return Designer(args[1..]);
        }

        if (args[0] == "format")
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            return FormatCommand.Run(args[1..], stdin, stdout);
        }

        if (args[0] == "lsp")
        {
            return LspCommand.Run(args[1..]);
        }

        if (args[0] == "hook" && args.Length >= 2 && args[1] == "claude")
        {
            return ClaudeHook.Run(Console.In, args[2..]);
        }

        Console.Error.WriteLine($"webforms: unknown command '{args[0]}'.");
        Console.Error.Write(Usage);
        return ExitCodes.Usage;
    }

    internal static int Designer(string[] args)
    {
        var files = new List<string>();
        string? allDir = null;
        bool check = false, stdout = false, verbose = false, time = false;
        var options = new OutputOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--master":
                    if (++i >= args.Length || args[i] is not ("directive" or "keep" or "always"))
                    {
                        return UsageError("--master must be directive, keep or always.");
                    }

                    options = options with { Master = args[i] switch { "always" => MasterPropertyMode.Always, "keep" => MasterPropertyMode.Keep, _ => MasterPropertyMode.Directive } };
                    break;
                case "--check":
                    check = true;
                    break;
                case "--stdout":
                    stdout = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--time":
                    time = true;
                    break;
                case "--all":
                    if (++i >= args.Length)
                    {
                        return UsageError("--all requires a directory.");
                    }

                    allDir = args[i];
                    break;
                case "--style":
                    if (++i >= args.Length || args[i] is not ("auto" or "vs" or "rider"))
                    {
                        return UsageError("--style must be auto, vs or rider.");
                    }

                    options = options with { Style = args[i] switch { "vs" => StyleMode.VisualStudio, "rider" => StyleMode.Rider, _ => StyleMode.Auto } };
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        return UsageError($"unknown option '{args[i]}'.");
                    }

                    files.Add(args[i]);
                    break;
            }
        }

        if (allDir is not null)
        {
            if (!Directory.Exists(allDir))
            {
                return UsageError($"directory not found: {allDir}");
            }

            files.AddRange(EnumerateMarkup(allDir));
        }
        else if (files.Count == 0)
        {
            return UsageError("no input files.");
        }

        if (stdout && files.Count != 1)
        {
            return UsageError("--stdout needs exactly one file.");
        }

        var sw = Stopwatch.StartNew();
        int exit = ExitCodes.Ok;
        int changed = 0, unchanged = 0, errors = 0, skipped = 0;

        foreach (var file in files)
        {
            var fileSw = Stopwatch.StartNew();
            int r = ProcessFile(file, check, stdout, options, verbose, explicitFile: allDir is null);
            switch (r)
            {
                case ExitCodes.Error:
                    errors++;
                    exit = ExitCodes.Error;
                    break;
                case ExitCodes.WouldChange:
                    changed++;
                    if (exit == ExitCodes.Ok)
                    {
                        exit = ExitCodes.WouldChange;
                    }

                    break;
                case -1:
                    skipped++;
                    break;
                case -2:
                    changed++;
                    break;
                default:
                    unchanged++;
                    break;
            }

            if (time && allDir is null)
            {
                Console.Error.WriteLine($"{file}: {fileSw.Elapsed.TotalMilliseconds:F0} ms");
            }
        }

        if (allDir is not null)
        {
            string verb = check ? "would change" : "written";
            Console.Error.WriteLine($"webforms designer: {files.Count - skipped} designer(s): {unchanged} unchanged, {changed} {verb}, {errors} error(s); {skipped} skipped (no CodeBehind).");
        }

        if (time)
        {
            Console.Error.WriteLine($"total: {sw.Elapsed.TotalMilliseconds:F0} ms (process {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms)");
        }

        return exit;
    }

    /// <returns>ExitCodes, or -1 skipped, -2 written.</returns>
    private static int ProcessFile(string file, bool check, bool stdout, OutputOptions options, bool verbose, bool explicitFile)
    {
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"{file}(1,1): error WF0001: File not found.");
            return ExitCodes.Error;
        }

        if (!DesignerGenerator.IsMarkupFile(file))
        {
            Console.Error.WriteLine($"{file}(1,1): error WF0002: Not an .aspx, .ascx or .master file.");
            return ExitCodes.Error;
        }

        var result = DesignerGenerator.Generate(file);
        foreach (var d in result.Diagnostics)
        {
            Console.Error.WriteLine(d.ToString());
        }

        if (result.SkipReason is not null)
        {
            if (verbose || (explicitFile && stdout))
            {
                Console.Error.WriteLine($"{file}: skipped: {result.SkipReason}.");
            }

            return -1;
        }

        if (result.HasErrors || result.Model is null)
        {
            return ExitCodes.Error;
        }

        byte[]? existing = File.Exists(result.DesignerPath) ? File.ReadAllBytes(result.DesignerPath!) : null;
        var bytes = DesignerOutput.Build(result.Model, existing, options);

        if (stdout)
        {
            using var o = Console.OpenStandardOutput();
            o.Write(bytes);
            return ExitCodes.Ok;
        }

        bool same = existing is not null && existing.AsSpan().SequenceEqual(bytes);
        if (same)
        {
            if (verbose)
            {
                Console.Error.WriteLine($"{result.DesignerPath}: unchanged.");
            }

            return ExitCodes.Ok;
        }

        if (check)
        {
            Console.Error.WriteLine($"{result.DesignerPath}(1,1): warning WF0100: Designer file is out of date.");
            return ExitCodes.WouldChange;
        }

        File.WriteAllBytes(result.DesignerPath!, bytes);
        if (verbose)
        {
            Console.Error.WriteLine($"{result.DesignerPath}: written.");
        }

        if (existing is null)
        {
            WarnIfNotInProject(result.DesignerPath!);
        }

        return -2;
    }

    private static void WarnIfNotInProject(string designerPath)
    {
        if (DesignerOutput.MissingFromProject(ProjectContext.ForFile(designerPath), designerPath) is { } warning)
        {
            Console.Error.WriteLine($"{designerPath}(1,1): warning WF0101: {warning}");
        }
    }

    internal static IEnumerable<string> EnumerateMarkup(string dir)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".claude", "packages" };
        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(dir));
        var results = new List<string>();
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(d))
            {
                if (DesignerGenerator.IsMarkupFile(f))
                {
                    results.Add(f);
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(d))
            {
                if (!skip.Contains(Path.GetFileName(sub)))
                {
                    stack.Push(sub);
                }
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"webforms: {message}");
        return ExitCodes.Usage;
    }
}
