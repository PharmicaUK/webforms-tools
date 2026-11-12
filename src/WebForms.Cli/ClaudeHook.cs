using System.Text.Json;
using WebForms.Designer;

namespace WebForms.Cli;

/// <summary>
/// <c>webforms hook claude</c>: a Claude Code hook. Reads the hook JSON from stdin.
/// <list type="bullet">
/// <item>PostToolUse on an .aspx/.ascx/.master (or its .vb code-behind): regenerates the designer file.
/// Errors are reported with exit code 2 so Claude sees them and can fix the markup.</item>
/// <item>PreToolUse on a .designer.vb file: blocks the edit (exit 2) — designer files are generated.</item>
/// </list>
/// Anything else is ignored (exit 0), so the hook can match every Edit/Write.
/// </summary>
public static class ClaudeHook
{
    public static int Run(TextReader stdin, string[] extraArgs)
    {
        string? eventName, filePath;
        try
        {
            using var doc = JsonDocument.Parse(stdin.ReadToEnd());
            var root = doc.RootElement;
            eventName = root.TryGetProperty("hook_event_name", out var e) ? e.GetString() : null;
            filePath = root.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object &&
                       ti.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
        }
        catch (JsonException)
        {
            return ExitCodes.Ok; // not our input; never break the session
        }

        if (string.IsNullOrEmpty(filePath))
        {
            return ExitCodes.Ok;
        }

        if (filePath.EndsWith(".designer.vb", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(eventName, "PreToolUse", StringComparison.Ordinal))
            {
                var markup = filePath[..^".designer.vb".Length];
                Console.Error.WriteLine(
                    $"{Path.GetFileName(filePath)} is generated from {Path.GetFileName(markup)} by `webforms designer` and is regenerated " +
                    "automatically after every markup edit. Edit the markup (or declare the field in the code-behind) instead.");
                return ExitCodes.Error;
            }

            return ExitCodes.Ok;
        }

        if (string.Equals(eventName, "PreToolUse", StringComparison.Ordinal))
        {
            return ExitCodes.Ok;
        }

        string? target = null;
        if (DesignerGenerator.IsMarkupFile(filePath))
        {
            target = filePath;
        }
        else if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
        {
            // Code-behind edits can add/remove members that suppress designer fields.
            var markup = filePath[..^3];
            if (DesignerGenerator.IsMarkupFile(markup) && File.Exists(markup))
            {
                target = markup;
            }
        }

        if (target is null || !File.Exists(target))
        {
            return ExitCodes.Ok;
        }

        int exit = Program.Designer([target, .. extraArgs]);
        return exit == ExitCodes.Error ? ExitCodes.Error : ExitCodes.Ok;
    }
}
