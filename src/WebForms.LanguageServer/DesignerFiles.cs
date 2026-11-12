using WebForms.Designer;

namespace WebForms.LanguageServer;

public enum DesignerOutcome
{
    Written,
    Unchanged,

    /// <summary>The file has no designer (no CodeBehind attribute, CodeFile, no directive).</summary>
    Skipped,

    /// <summary>The markup has errors (see the diagnostics); nothing written.</summary>
    Errors,

    /// <summary>The file could not be written.</summary>
    Failed,
}

public sealed record DesignerResult(DesignerOutcome Outcome, string? DesignerPath, string Message);

/// <summary>
/// "Generate designer" for the language server: the same pipeline and output as <c>webforms designer</c> (style kept
/// from the existing file, Master property as Visual Studio writes it), from the open buffer if there is one, written
/// straight to disk (not as a <c>workspace/applyEdit</c>, which would leave an unsaved buffer in the editor).
/// </summary>
public static class DesignerFiles
{
    private static readonly OutputOptions Options = new();

    /// <summary>The markup file for a markup, code-behind (<c>X.aspx.vb</c>) or designer (<c>X.aspx.designer.vb</c>) path, or null.</summary>
    public static string? MarkupFileFor(string path)
    {
        if (DesignerGenerator.IsMarkupFile(path))
        {
            return path;
        }

        foreach (var suffix in (string[])[".designer.vb", ".vb"])
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && DesignerGenerator.IsMarkupFile(path[..^suffix.Length]))
            {
                return path[..^suffix.Length];
            }
        }

        return null;
    }

    /// <summary>The designer bytes the markup produces, and the existing file's; null when nothing can be generated.</summary>
    public static (byte[] Generated, byte[]? Existing)? Build(MarkupAnalysis analysis)
    {
        var result = analysis.Result;
        if (result.SkipReason is not null || result.HasErrors || result.Model is null || result.DesignerPath is null)
        {
            return null;
        }

        byte[]? existing = File.Exists(result.DesignerPath) ? File.ReadAllBytes(result.DesignerPath) : null;
        return (DesignerOutput.Build(result.Model, existing, Options), existing);
    }

    public static DesignerResult Generate(WebFormsWorkspace workspace, string markupPath)
    {
        string name = Path.GetFileName(markupPath);
        if (workspace.Analyze(markupPath) is not { } analysis)
        {
            return new DesignerResult(DesignerOutcome.Skipped, null, $"{name}: file not found.");
        }

        var result = analysis.Result;
        if (result.SkipReason is not null)
        {
            return new DesignerResult(DesignerOutcome.Skipped, null, $"{name} has no designer file: {result.SkipReason}.");
        }

        var errors = result.Diagnostics.Where(d => d.Severity == Severity.Error).ToList();
        if (errors.Count > 0 || result.Model is null || result.DesignerPath is null)
        {
            string first = errors.Count > 0 ? $" line {errors[0].Line}: {errors[0].Code}: {errors[0].Message}" : "";
            return new DesignerResult(DesignerOutcome.Errors, result.DesignerPath,
                $"{name}: designer not generated, {errors.Count} error(s);{first}");
        }

        var (generated, existing) = Build(analysis)!.Value;
        string designerName = Path.GetFileName(result.DesignerPath);
        if (existing is not null && existing.AsSpan().SequenceEqual(generated))
        {
            return new DesignerResult(DesignerOutcome.Unchanged, result.DesignerPath, $"{designerName} is up to date.");
        }

        try
        {
            File.WriteAllBytes(result.DesignerPath, generated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DesignerResult(DesignerOutcome.Failed, result.DesignerPath, $"{designerName}: {ex.Message}");
        }

        string message = existing is null ? $"{designerName} created." : $"{designerName} written.";
        if (existing is null && DesignerOutput.MissingFromProject(analysis.Project, result.DesignerPath) is { } warning)
        {
            message += " " + warning;
        }

        return new DesignerResult(DesignerOutcome.Written, result.DesignerPath, message);
    }
}
