using System.Diagnostics;
using System.Text;
using WebForms.Formatting;

namespace WebForms.Cli;

/// <summary>
/// <c>webforms format</c>: VB on stdin, formatted VB on stdout (conform.nvim and similar). On any error nothing is
/// written to stdout and the exit code is 2, so an editor keeps the buffer as it was.
/// </summary>
public static class FormatCommand
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static int Run(string[] args, Stream stdin, Stream stdout)
    {
        string? fileName = null;
        LineRange? lines = null;
        bool check = false, verbose = false, time = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--stdin-filename":
                    if (++i >= args.Length)
                    {
                        return UsageError("--stdin-filename requires a path.");
                    }

                    fileName = args[i];
                    break;
                case "--lines":
                    if (++i >= args.Length || ParseLines(args[i]) is not { } range)
                    {
                        return UsageError("--lines must be <start>-<end> (1-based, inclusive), e.g. --lines 10-20.");
                    }

                    lines = range;
                    break;
                case "--check":
                    check = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--time":
                    time = true;
                    break;
                default:
                    return UsageError($"unknown option '{args[i]}' (format reads the source from stdin).");
            }
        }

        var sw = Stopwatch.StartNew();
        var display = fileName ?? "<stdin>";
        var input = ReadAll(stdin);
        bool bom = input.AsSpan().StartsWith(Bom);
        string text;
        try
        {
            text = StrictUtf8.GetString(input, bom ? 3 : 0, input.Length - (bom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            Console.Error.WriteLine($"{display}(1,1): error WF4001: Input is not valid UTF-8.");
            return ExitCodes.Error;
        }

        FormatResult result;
        try
        {
            result = VbFormatter.Format(text, lines, fileName);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "lines")
        {
            return UsageError(ex.Message.Split(" (Parameter", 2)[0]);
        }
#pragma warning disable CA1031 // An unexpected Roslyn failure must still be reported in the diagnostics format.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"{display}(1,1): error WF4002: Formatter failed: {ex.GetType().Name}: {ex.Message}");
            return ExitCodes.Error;
        }

        foreach (var d in result.Diagnostics)
        {
            Console.Error.WriteLine(d.Format(display));
        }

        if (result.Text is null)
        {
            return ExitCodes.Error;
        }

        if (result.SkipReason is not null && verbose)
        {
            Console.Error.WriteLine($"{display}: skipped: {result.SkipReason}.");
        }

        bool changed = !string.Equals(result.Text, text, StringComparison.Ordinal);
        if (time)
        {
            Console.Error.WriteLine($"format: {sw.Elapsed.TotalMilliseconds:F0} ms (process {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms)");
        }

        if (check)
        {
            if (changed)
            {
                Console.Error.WriteLine($"{display}(1,1): warning WF4100: File is not formatted.");
                return ExitCodes.WouldChange;
            }

            return ExitCodes.Ok;
        }

        if (bom)
        {
            stdout.Write(Bom);
        }

        stdout.Write(StrictUtf8.GetBytes(result.Text));
        stdout.Flush();
        return ExitCodes.Ok;
    }

    internal static LineRange? ParseLines(string value)
    {
        var parts = value.Split('-');
        return parts.Length == 2 &&
               int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int a) &&
               int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int b) &&
               a >= 1 && b >= a
            ? new LineRange(a, b)
            : null;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"webforms format: {message}");
        return ExitCodes.Usage;
    }
}
