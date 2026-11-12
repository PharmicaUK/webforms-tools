using System.Text.Json;

namespace WebForms.LanguageServer;

/// <summary>
/// User settings, from <c>initializationOptions</c> and then <c>workspace/didChangeConfiguration</c>. Both take the
/// same object, either bare or under a <c>webforms</c> key:
/// <code>{ "generateDesignerOnSave": false, "completion": { "zeroArgHandlers": false }, "codeBlocks": { "enabled": true },
///   "diagnostics": { "staleDesigner": true } }</code>
/// </summary>
/// <param name="ZeroArgHandlers">
/// Offer parameterless Subs as event handlers. VB's relaxed delegate conversion makes them valid, but almost any
/// helper Sub qualifies, so they are left out by default.
/// </param>
/// <param name="GenerateDesignerOnSave">
/// Regenerate the designer file when a markup file is saved (<c>textDocument/didSave</c>), as Visual Studio does.
/// </param>
/// <param name="CodeBlocks">
/// VB in markup code blocks (completion, hover, definition, BC errors): compiles the whole project in the server.
/// </param>
/// <param name="StaleDesignerDiagnostic">
/// WF0100 (designer file out of date or missing). The Claude Code plugin turns it off: its hook regenerates the
/// designer right after each edit, but the diagnostic reaches Claude first, as a problem to fix.
/// </param>
public sealed record ServerSettings(bool ZeroArgHandlers = false, bool GenerateDesignerOnSave = false, bool CodeBlocks = true,
    bool StaleDesignerDiagnostic = true)
{
    /// <summary>The settings in <paramref name="json"/> applied over <paramref name="current"/>: absent keys keep their value.</summary>
    public static ServerSettings Read(JsonElement? json, ServerSettings current)
    {
        if (json is not { ValueKind: JsonValueKind.Object } root)
        {
            return current;
        }

        if (root.TryGetProperty("webforms", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            root = nested;
        }

        var settings = current;
        if (Bool(root, "completion", "zeroArgHandlers") is { } zeroArg)
        {
            settings = settings with { ZeroArgHandlers = zeroArg };
        }

        if (Bool(root, "generateDesignerOnSave") is { } onSave)
        {
            settings = settings with { GenerateDesignerOnSave = onSave };
        }

        if (Bool(root, "codeBlocks", "enabled") is { } codeBlocks)
        {
            settings = settings with { CodeBlocks = codeBlocks };
        }

        if (Bool(root, "diagnostics", "staleDesigner") is { } staleDesigner)
        {
            settings = settings with { StaleDesignerDiagnostic = staleDesigner };
        }

        return settings;
    }

    private static bool? Bool(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
