using WebForms.LanguageServer.Protocol;

namespace WebForms.LanguageServer;

/// <summary>
/// The smallest set of whole-line <see cref="TextEdit"/>s that turns a document into new text (Myers' diff over lines,
/// line terminators included). Formatting results are sent this way rather than as one whole-document replacement,
/// which in Neovim drops marks, extmarks and folds and moves the cursor.
/// </summary>
public static class LineDiff
{
    /// <summary>Above this many changed lines one edit covers everything between the common prefix and suffix.</summary>
    private const int MaxDifferences = 4000;

    public static TextEdit[] Edits(SourceDocument document, string newText)
    {
        var (a, aStarts) = Split(document.Text);
        var (b, _) = Split(newText);

        int prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            suffix++;
        }

        var hunks = new List<(int OldStart, int OldEnd, int NewStart, int NewEnd)>();
        int n = a.Count - prefix - suffix, m = b.Count - prefix - suffix;
        if (n > 0 || m > 0)
        {
            var matches = Matches(a, b, prefix, n, m);
            if (matches is null)
            {
                hunks.Add((prefix, prefix + n, prefix, prefix + m));
            }
            else
            {
                int pi = prefix - 1, pj = prefix - 1;
                foreach (var (i, j) in matches.Append((prefix + n, prefix + m)))
                {
                    if (i > pi + 1 || j > pj + 1)
                    {
                        hunks.Add((pi + 1, i, pj + 1, j));
                    }

                    (pi, pj) = (i, j);
                }
            }
        }

        int Offset(int line) => line < aStarts.Count ? aStarts[line] : document.Text.Length;
        return [.. hunks.Select(h => new TextEdit(
            new LspRange(document.ToPosition(Offset(h.OldStart)), document.ToPosition(Offset(h.OldEnd))),
            string.Concat(b.Skip(h.NewStart).Take(h.NewEnd - h.NewStart))))];
    }

    /// <summary>Matching line pairs (absolute indices, ascending) of a[start..start+n) and b[start..start+m); null if too different.</summary>
    private static List<(int, int)>? Matches(List<string> a, List<string> b, int start, int n, int m)
    {
        int max = n + m;
        var v = new int[2 * max + 2];
        int off = max + 1;
        var trace = new List<int[]>();
        int found = -1;
        for (int d = 0; d <= Math.Min(max, MaxDifferences) && found < 0; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[start + x] == b[start + y])
                {
                    x++;
                    y++;
                }

                v[off + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                }
            }

            trace.Add(v[(off - d)..(off + d + 1)]);
        }

        if (found < 0)
        {
            return null;
        }

        var matches = new List<(int, int)>();
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            var prev = trace[d - 1]; // index k + (d - 1)
            int k = cx - cy;
            int prevK = k == -d || (k != d && prev[k - 1 + d - 1] < prev[k + 1 + d - 1]) ? k + 1 : k - 1;
            int prevX = prev[prevK + d - 1];
            int prevY = prevX - prevK;

            // The edit (a line inserted from b when coming from diagonal k + 1, else one deleted from a), then the
            // snake of equal lines up to (cx, cy).
            int snakeX = prevK == k + 1 ? prevX : prevX + 1;
            while (cx > snakeX)
            {
                cx--;
                cy--;
                matches.Add((start + cx, start + cy));
            }

            (cx, cy) = (prevX, prevY);
        }

        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            matches.Add((start + cx, start + cy));
        }

        matches.Reverse();
        return matches;
    }

    /// <summary>Lines with their terminators (<c>\r\n</c>, <c>\n</c> or <c>\r</c>), and the offset of each.</summary>
    private static (List<string> Lines, List<int> Starts) Split(string text)
    {
        var lines = new List<string>();
        var starts = new List<int>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                starts.Add(start);
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            starts.Add(start);
            lines.Add(text[start..]);
        }

        return (lines, starts);
    }
}
