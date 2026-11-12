using System.Reflection;
using System.Text;

namespace WebForms.Cli;

/// <summary><c>webforms lsp</c>: the ASPX language server over stdin/stdout.</summary>
internal static class LspCommand
{
    public static int Run(string[] args)
    {
        string? logPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--stdio":
                    break; // the only transport; accepted because many clients pass it
                case "--log":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("webforms: --log requires a file.");
                        return ExitCodes.Usage;
                    }

                    logPath = args[i];
                    break;
                default:
                    Console.Error.WriteLine($"webforms: unknown lsp option '{args[i]}'.");
                    return ExitCodes.Usage;
            }
        }

        // stdout carries only protocol messages. Without --log nothing is logged: editors (Neovim) report any stderr
        // output from a server as an error. Failed requests still reach the client as error responses.
        using var log = logPath is null ? null : SharedLogWriter.TryOpen(logPath);
        var version = typeof(LspCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        log?.WriteLine($"webforms lsp {version} started");
        log?.Flush();

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var server = new LanguageServer.WebFormsLanguageServer(stdin, stdout, log, version);
        return server.RunAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// A log file several server processes can share (an editor starts one server per project, all with the same
/// <c>--log</c>): opened with <see cref="FileShare.ReadWrite"/>, every line prefixed with the process id and appended at
/// the current end of the file on <see cref="Flush"/>.
/// </summary>
public sealed class SharedLogWriter : TextWriter
{
    private readonly FileStream _stream;
    private readonly StringBuilder _buffer = new();
    private readonly string _prefix = $"[{Environment.ProcessId}] ";
    private bool _atLineStart = true;

    private SharedLogWriter(FileStream stream) => _stream = stream;

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>Opens the log, or returns null (with one line on stderr) if it can't be opened: logging never stops the server.</summary>
    public static SharedLogWriter? TryOpen(string path)
    {
        try
        {
            return new SharedLogWriter(new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"webforms: cannot open log file '{path}': {ex.Message}");
            return null;
        }
    }

    public override void Write(char value)
    {
        if (_atLineStart)
        {
            _buffer.Append(_prefix);
            _atLineStart = false;
        }

        _buffer.Append(value);
        if (value == '\n')
        {
            _atLineStart = true;
        }
    }

    public override void Write(string? value)
    {
        foreach (char c in value ?? "")
        {
            Write(c);
        }
    }

    public override void Flush()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(_buffer.ToString());
        _buffer.Clear();
        try
        {
            _stream.Seek(0, SeekOrigin.End);
            _stream.Write(bytes);
            _stream.Flush();
        }
        catch (IOException)
        {
            // a full disk or a vanished file must not take the server down
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }
}
