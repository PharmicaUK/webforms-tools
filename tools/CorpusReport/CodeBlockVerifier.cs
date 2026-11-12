using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WebForms.LanguageServer;
using WebForms.LanguageServer.CodeBlocks;
using WebForms.LanguageServer.Completion;
using WebForms.LanguageServer.Protocol;

/// <summary>
/// Code blocks (0.7.0) over the corpus: every markup file with blocks is projected and bound in its project's
/// compilation; every BC error inside a block is listed (each is either a projection bug or a real bug in the page),
/// and completion after every <c>x.Member</c> in a block must offer <c>Member</c> (late-bound receivers, which get no
/// list, are counted apart).
/// </summary>
internal sealed partial class CodeBlockVerifier(WebFormsWorkspace workspace)
{
    private readonly CodeBlockService _service = new(workspace);
    private readonly HashSet<string> _warm = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<double> _times = [];
    private readonly Stopwatch _buildTime = new();
    private int _version = 1_000_000;

    public int Files { get; private set; }
    public int Blocks { get; private set; }
    public int Errors => BcErrors.Count;
    public int Members { get; private set; }
    public int LateBound { get; private set; }
    public List<string> BcErrors { get; } = [];
    public List<string> MissedMembers { get; } = [];

    /// <summary>Member completions repeated with the block unclosed (as typed in Neovim) and the ones that offered nothing.</summary>
    public int UnclosedMembers { get; private set; }

    public List<string> MissedUnclosed { get; } = [];

    public int EvalCalls { get; private set; }

    public int EvalTyped { get; private set; }

    public List<string> MissedEvalFields { get; } = [];

    public SortedDictionary<string, int> EvalTypes { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> ErrorIds { get; } = new(StringComparer.Ordinal);

    public void Check(string rel, MarkupAnalysis analysis)
    {
        if (_service.ProjectionOf(analysis) is not { BlockCount: > 0 } projection || analysis.Project.ProjectFile is null)
        {
            return;
        }

        Files++;
        Blocks += projection.BlockCount;
        if (_warm.Add(analysis.Project.ProjectDirectory))
        {
            _buildTime.Start();
            _service.WaitUntilReady(analysis.Path);
            _buildTime.Stop();
        }

        foreach (var d in _service.Diagnostics(analysis.Path))
        {
            ErrorIds[d.Code] = ErrorIds.GetValueOrDefault(d.Code) + 1;
            var snippet = Snippet(analysis.Document, d.Range);
            BcErrors.Add($"{rel}({d.Range.Start.Line + 1},{d.Range.Start.Character + 1}): {d.Code}: {d.Message} [{snippet}]");
        }

        var text = analysis.Document.Text;
        var dot = new CompletionContext(2, ".");
        var unclosed = new List<(int Offset, int After, string Member, string Access)>();
        foreach (var span in projection.Spans)
        {
            bool unclosedDone = false;
            var code = text.AsSpan(span.MarkupStart, span.Length).ToString();
            foreach (Match m in EvalCall().Matches(code))
            {
                EvalCalls++;
                int fieldStart = span.MarkupStart + m.Groups["field"].Index;
                var list = _service.Complete(analysis.Path, analysis.Document.ToPosition(fieldStart), null, new ClientOptions(), out _);
                if (list is null || list.Items.Length == 0 || list.Items[0].Kind is not (CompletionItemKind.Property or CompletionItemKind.Field) || list.Items.Any(i => i.Label == "ToString"))
                {
                    continue; // untyped source: VB completion inside a string gives nothing or keywords
                }

                EvalTyped++;
                string typeName = list.Items[0].Detail?.Split('.')[0] ?? "?";
                EvalTypes[typeName] = EvalTypes.GetValueOrDefault(typeName) + 1;
                string field = m.Groups["field"].Value;
                if (!list.Items.Any(i => i.Label.Equals(field, StringComparison.OrdinalIgnoreCase)))
                {
                    var pos = analysis.Document.ToPosition(fieldStart);
                    MissedEvalFields.Add($"{rel}({pos.Line + 1},{pos.Character + 1}): Eval(\"{field}\") ({list.Items.Length} fields: {string.Join(", ", list.Items.Take(6).Select(i => i.Label))}…)");
                }
            }

            foreach (Match m in MemberAccess().Matches(code))
            {
                if (InString(code, m.Index))
                {
                    continue;
                }

                int offset = span.MarkupStart + m.Groups["dot"].Index + 1;
                var sw = Stopwatch.StartNew();
                var list = _service.Complete(analysis.Path, analysis.Document.ToPosition(offset), dot, new ClientOptions(), out _);
                _times.Add(sw.Elapsed.TotalMilliseconds);
                string member = m.Groups["member"].Value;
                if (list is null || list.Items.Length == 0 || LateBoundReceiver().IsMatch(m.Groups["receiver"].Value))
                {
                    LateBound++;
                    continue;
                }

                Members++;
                if (!list.Items.Any(i => (i.FilterText ?? i.Label).Equals(member, StringComparison.OrdinalIgnoreCase)))
                {
                    var pos = analysis.Document.ToPosition(offset);
                    MissedMembers.Add($"{rel}({pos.Line + 1},{pos.Character + 1}): {m.Value} ({list.Items.Length} items)");
                }
                else if (!unclosedDone && text.AsSpan(span.MarkupStart + span.Length).StartsWith("%>"))
                {
                    unclosedDone = true;
                    unclosed.Add((offset, span.MarkupStart + span.Length + 2, member, m.Value));
                }
            }
        }

        // The same completion while the block is still being typed: no '%>' yet, so the tokenizer runs it on into the
        // markup below (Neovim doesn't close '<%'). One member per block, with the rest of the block cut from the buffer.
        foreach (var (offset, after, member, access) in unclosed)
        {
            UnclosedMembers++;
            workspace.Open(analysis.Path, text[..offset] + text[after..], ++_version);
            var list = _service.Complete(analysis.Path, analysis.Document.ToPosition(offset), dot, new ClientOptions(), out _);
            if (list is null || !list.Items.Any(i => (i.FilterText ?? i.Label).Equals(member, StringComparison.OrdinalIgnoreCase)))
            {
                var pos = analysis.Document.ToPosition(offset);
                MissedUnclosed.Add($"{rel}({pos.Line + 1},{pos.Character + 1}): {access} ({list?.Items.Length ?? 0} items)");
            }
        }

        if (unclosed.Count > 0)
        {
            workspace.Close(analysis.Path);
        }
    }

    public void Summarize(StringBuilder summary)
    {
        _times.Sort();
        double Pct(double p) => _times.Count == 0 ? 0 : _times[(int)Math.Min(_times.Count - 1, p * _times.Count)];
        summary.AppendLine(CultureInfo.InvariantCulture,
            $"  code blocks        {Blocks,5}  in {Files} files; BC errors in blocks {Errors} ({string.Join(", ", ErrorIds.Select(kv => $"{kv.Key} x{kv.Value}"))}); compiling the projects {_buildTime.Elapsed.TotalSeconds:F1} s");
        summary.AppendLine(CultureInfo.InvariantCulture,
            $"              members {Members,5}  not offered {MissedMembers.Count}; late-bound receivers (Eval, Object) {LateBound}; completion median {Pct(0.5):F1} ms, p99 {Pct(0.99):F1} ms, max {Pct(1):F1} ms");
        summary.AppendLine(CultureInfo.InvariantCulture,
            $"              unclosed {UnclosedMembers,4}  (one member per block, no '%>' yet) not offered {MissedUnclosed.Count}");
        summary.AppendLine(CultureInfo.InvariantCulture,
            $"              Eval(\"…\") {EvalCalls,5}  typed source (fields offered) {EvalTyped}; field not among them {MissedEvalFields.Count}; item types: {string.Join(", ", EvalTypes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"))}");
        summary.AppendLine(CultureInfo.InvariantCulture,
            $"              memory {GC.GetTotalMemory(true) / (1024 * 1024)} MB managed after the check");
    }

    private static string Snippet(SourceDocument doc, LspRange range)
    {
        int start = doc.ToOffset(range.Start);
        int end = Math.Max(start, doc.ToOffset(range.End));
        var s = doc.Text[start..Math.Min(doc.Text.Length, Math.Max(end, start + 1))].ReplaceLineEndings(" ");
        return s.Length > 60 ? s[..60] + "…" : s;
    }

    /// <summary>Whether the index is inside a "string" on its line (an even number of quotes before it).</summary>
    private static bool InString(string code, int index)
    {
        int lineStart = code.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        return code.AsSpan(lineStart, index - lineStart).Count('"') % 2 == 1;
    }

    [GeneratedRegex(@"\bEval\(\s*""(?<field>[^""]*)""")]
    private static partial Regex EvalCall();

    /// <summary>Eval(...) and DataBinder.Eval(...) return Object: members are bound at runtime.</summary>
    [GeneratedRegex(@"^(DataBinder\.)?Eval\(", RegexOptions.IgnoreCase)]
    private static partial Regex LateBoundReceiver();

    [GeneratedRegex(@"(?<![\w.""])(?<receiver>[A-Za-z_]\w*(\([^()]*\))?)(?<dot>\.)(?<member>[A-Za-z_]\w*)")]
    private static partial Regex MemberAccess();
}
