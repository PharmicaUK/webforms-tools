using System.Text;

namespace WebForms.Designer;

public enum StyleMode
{
    /// <summary>Keep the existing file's BOM / trailing-space / blank-line style; Visual Studio style for new files.</summary>
    Auto,
    VisualStudio,
    Rider,
}

/// <summary>
/// When to write the typed <c>Public Shadows ReadOnly Property Master()</c>. Visual Studio writes it only for a
/// <c>&lt;%@ MasterType %&gt;</c> directive, never for a MasterPageFile alone (verified with VS 2022). Rider writes it
/// for any MasterPageFile, which is wrong when code-behind switches master at runtime (InvalidCastException).
/// </summary>
public enum MasterPropertyMode
{
    /// <summary>Only for a MasterType directive; regenerating removes a property Rider added (default, as Visual Studio).</summary>
    Directive,

    /// <summary>For a MasterType directive, and wherever the existing file already has it.</summary>
    Keep,

    /// <summary>For any page with a MasterPageFile (Rider behaviour).</summary>
    Always,
}

public sealed record OutputOptions(StyleMode Style = StyleMode.Auto, MasterPropertyMode Master = MasterPropertyMode.Directive);

/// <summary>Turns a generation result into the exact bytes to write, taking the existing file into account.</summary>
public static class DesignerOutput
{
    public static byte[] Build(DesignerModel model, byte[]? existing, OutputOptions options)
    {
        var style = options.Style switch
        {
            StyleMode.VisualStudio => DesignerStyle.VisualStudio,
            StyleMode.Rider => DesignerStyle.Rider,
            _ => existing is null ? DesignerStyle.VisualStudio : DesignerStyle.Detect(existing),
        };

        bool master = model.MasterTypeDeclared || options.Master switch
        {
            MasterPropertyMode.Always => true,
            MasterPropertyMode.Keep => existing is not null && ExistingDesigner.Parse(Decode(existing)).MasterTypeName is not null,
            _ => false,
        };

        if (!master && model.MasterTypeName is not null)
        {
            model = model with { MasterTypeName = null };
        }

        return DesignerWriter.Encode(DesignerWriter.Render(model, style), style);
    }

    /// <summary>Warning WF0101 (message only) when a new designer file is not a Compile item of the project; else null.</summary>
    public static string? MissingFromProject(ProjectContext project, string designerPath)
    {
        if (project.ProjectFile is null)
        {
            return null;
        }

        var rel = Path.GetRelativePath(project.ProjectDirectory, designerPath);
        var text = File.ReadAllText(project.ProjectFile);
        return text.Contains($"Include=\"{rel}\"", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"New designer file is not included in {Path.GetFileName(project.ProjectFile)}; add <Compile Include=\"{rel}\"><DependentUpon>{Path.GetFileName(designerPath[..^".designer.vb".Length])}</DependentUpon></Compile>.";
    }

    public static string Decode(byte[] bytes)
    {
        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
    }
}
