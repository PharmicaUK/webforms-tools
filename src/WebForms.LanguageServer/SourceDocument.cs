using WebForms.LanguageServer.Protocol;
using WebForms.Markup;

namespace WebForms.LanguageServer;

/// <summary>The text of a file (an open buffer or the file on disk) with LSP position mapping.</summary>
public sealed class SourceDocument
{
    private int[]? _lineStarts;

    public SourceDocument(string path, string text, int? version)
    {
        Path = path;
        Text = text;
        Version = version;
    }

    public string Path { get; }
    public string Text { get; }

    /// <summary>The editor's version for an open document; null for a file read from disk.</summary>
    public int? Version { get; }

    private int[] LineStarts => _lineStarts ??= ComputeLineStarts(Text);

    /// <summary>The offset of an LSP position, clamped to the line and the text.</summary>
    public int ToOffset(LspPosition position)
    {
        var starts = LineStarts;
        if (position.Line < 0)
        {
            return 0;
        }

        if (position.Line >= starts.Length)
        {
            return Text.Length;
        }

        int start = starts[position.Line];
        int end = position.Line + 1 < starts.Length ? starts[position.Line + 1] : Text.Length;
        return Math.Clamp(start + Math.Max(0, position.Character), start, end);
    }

    public LspPosition ToPosition(int offset)
    {
        var starts = LineStarts;
        offset = Math.Clamp(offset, 0, Text.Length);
        int line = Array.BinarySearch(starts, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return new LspPosition(line, offset - starts[line]);
    }

    public LspRange ToRange(int offset, int length) => new(ToPosition(offset), ToPosition(offset + length));

    public static LspPosition ToPosition(SourcePosition position) => new(position.Line - 1, position.Column - 1);

    /// <summary>Line starts after \n, \r\n and a lone \r, as the LSP specification defines lines.</summary>
    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }
}

/// <summary>Conversion between file paths and <c>file:</c> URIs.</summary>
/// <remarks>
/// URIs the client sent are given back exactly as sent: clients match documents by URI string, and the path is not
/// a faithful round trip. On Windows, GetFullPath expands 8.3 names (a GitHub runner's temp folder
/// <c>C:\Users\RUNNER~1\...</c> becomes <c>C:\Users\runneradmin\...</c>), so diagnostics published under the expanded
/// path never reached the document VS Code had open (found by the extension's CI run).
/// </remarks>
public static class DocumentUri
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ClientUris = new(StringComparer.OrdinalIgnoreCase);

    public static string? ToPath(string uri)
    {
        if (ToPathCore(uri) is not { } path)
        {
            return null;
        }

        ClientUris[path] = uri;
        return path;
    }

    /// <summary>The URI the client used for this path, else one built from the path.</summary>
    public static string FromPath(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        return ClientUris.TryGetValue(full, out var uri) ? uri : new Uri(full).AbsoluteUri;
    }

    private static string? ToPathCore(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || !u.IsFile)
        {
            return null;
        }

        // VS Code escapes the drive colon (file:///c%3A/...): Uri then leaves "/c:/..." in LocalPath, which
        // GetFullPath would resolve against the current drive (C:\c:\...).
        string local = u.LocalPath;
        if (local.Length >= 3 && local[0] is '/' or '\\' && char.IsAsciiLetter(local[1]) && local[2] == ':')
        {
            local = local[1..];
        }

        return System.IO.Path.GetFullPath(local);
    }

}
