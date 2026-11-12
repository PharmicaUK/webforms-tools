using System.Xml.Linq;

namespace WebForms.LanguageServer.CodeBlocks;

/// <summary>
/// What a VB compilation of a Web Application Project needs from its .vbproj: the Compile items, references,
/// project imports and compiler options. Conditional property groups are not evaluated: the first value of each
/// property wins (they hold the Debug configuration first in every corpus project).
/// </summary>
public sealed class VbProjectFile
{
    private VbProjectFile(string path)
    {
        Path = path;
        Directory = System.IO.Path.GetDirectoryName(path)!;
    }

    public string Path { get; }
    public string Directory { get; }
    public List<string> CompileFiles { get; } = [];

    /// <summary>References without a HintPath (framework assemblies), by simple name.</summary>
    public List<string> FrameworkReferences { get; } = [];

    public List<string> HintPaths { get; } = [];
    public List<string> Imports { get; } = [];
    public string RootNamespace { get; private set; } = "";
    public string AssemblyName { get; private set; } = "";
    public string TargetFrameworkVersion { get; private set; } = "v4.8";
    public bool OptionStrict { get; private set; }
    public bool OptionExplicit { get; private set; } = true;
    public bool OptionInfer { get; private set; } = true;
    public bool OptionCompareText { get; private set; }
    public string MyType { get; private set; } = "";
    public List<KeyValuePair<string, object>> DefineConstants { get; } = [];

    public static VbProjectFile Read(string path)
    {
        var project = new VbProjectFile(path);
        var doc = XDocument.Load(path);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in doc.Descendants())
        {
            string name = el.Name.LocalName;
            string value = el.Value.Trim();
            switch (name)
            {
                case "Compile" when el.Attribute("Include")?.Value is { } include:
                    var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(project.Directory, Uri.UnescapeDataString(include)));
                    if (file.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) && !include.Contains('*', StringComparison.Ordinal))
                    {
                        project.CompileFiles.Add(file);
                    }

                    continue;
                case "Reference" when el.Attribute("Include")?.Value is { } reference:
                    var hint = el.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value.Trim();
                    if (hint is { Length: > 0 })
                    {
                        project.HintPaths.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(project.Directory, hint)));
                    }
                    else
                    {
                        project.FrameworkReferences.Add(reference.Split(',')[0].Trim());
                    }

                    continue;
                case "Import" when el.Attribute("Include")?.Value is { } import:
                    project.Imports.Add(import);
                    continue;
            }

            if (el.HasElements || !seen.Add(name))
            {
                continue;
            }

            switch (name)
            {
                case "RootNamespace":
                    project.RootNamespace = value;
                    break;
                case "AssemblyName":
                    project.AssemblyName = value;
                    break;
                case "TargetFrameworkVersion":
                    project.TargetFrameworkVersion = value;
                    break;
                case "OptionStrict":
                    project.OptionStrict = value.Equals("On", StringComparison.OrdinalIgnoreCase);
                    break;
                case "OptionExplicit":
                    project.OptionExplicit = !value.Equals("Off", StringComparison.OrdinalIgnoreCase);
                    break;
                case "OptionInfer":
                    project.OptionInfer = !value.Equals("Off", StringComparison.OrdinalIgnoreCase);
                    break;
                case "OptionCompare":
                    project.OptionCompareText = value.Equals("Text", StringComparison.OrdinalIgnoreCase);
                    break;
                case "MyType":
                    project.MyType = value;
                    break;
                case "DefineConstants":
                    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int eq = part.IndexOf('=', StringComparison.Ordinal);
                        project.DefineConstants.Add(eq < 0
                            ? new(part, true)
                            : new(part[..eq].Trim(), part[(eq + 1)..].Trim().Trim('"')));
                    }

                    break;
            }
        }

        if (project.AssemblyName.Length == 0)
        {
            project.AssemblyName = project.RootNamespace.Length > 0 ? project.RootNamespace : System.IO.Path.GetFileNameWithoutExtension(path);
        }

        return project;
    }
}
