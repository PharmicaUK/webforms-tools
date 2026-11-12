using System.Collections.Concurrent;
using System.Xml.Linq;
using WebForms.Markup;

namespace WebForms.Designer;

/// <summary>A tag prefix registration from a Register directive or a web.config &lt;pages&gt;&lt;controls&gt; entry.</summary>
public sealed record TagRegistration(string TagPrefix, string? Namespace, string? Assembly, string? TagName, string? Src, string Origin)
{
    public bool IsUserControl => TagName is not null && Src is not null;
}

/// <summary>What <see cref="ProjectContext.Refresh"/> found.</summary>
public enum ProjectChange
{
    /// <summary>No file the context depends on has changed.</summary>
    None,

    /// <summary>A web.config or a referenced user control / master page changed; the affected caches were cleared.</summary>
    CachesCleared,

    /// <summary>The project file or a referenced assembly changed: the context is stale and must be recreated.</summary>
    Reload,
}

/// <summary>
/// Everything about a WAP project that designer generation needs. The CLI uses one cached instance per project
/// directory (<see cref="ForFile"/>); the language server creates its own (<see cref="Create"/>) and calls
/// <see cref="Refresh"/> to find out whether the files it was built from have changed.
/// </summary>
public sealed class ProjectContext : IDisposable
{
    private static readonly ConcurrentDictionary<string, ProjectContext> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, IReadOnlyList<TagRegistration>> _webConfigControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _webConfigNamespaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MarkupDocument?> _directiveCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileStamp> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<TypeSystem> _types;

    private ProjectContext(string projectDirectory, string? projectFile)
    {
        ProjectDirectory = projectDirectory;
        ProjectFile = projectFile;
        if (projectFile is not null)
        {
            Track(projectFile);
        }

        Track(Path.Combine(projectDirectory, "bin"), directory: true);
        var references = new List<string>();
        string targetFramework = "v4.8";

        if (projectFile is not null)
        {
            var doc = XDocument.Load(projectFile);
            foreach (var el in doc.Descendants())
            {
                switch (el.Name.LocalName)
                {
                    case "RootNamespace":
                        RootNamespace = el.Value.Trim();
                        break;
                    case "AssemblyName":
                        AssemblyName = el.Value.Trim();
                        break;
                    case "TargetFrameworkVersion":
                        targetFramework = el.Value.Trim();
                        break;
                    case "HintPath":
                        var hintPath = Path.GetFullPath(Path.Combine(projectDirectory, el.Value.Trim()));
                        references.Add(hintPath);
                        Track(hintPath); // so that restoring a missing package is noticed
                        break;
                }
            }
        }

        TargetFrameworkVersion = targetFramework;
        AssemblyName ??= RootNamespace;

        var bin = Path.Combine(projectDirectory, "bin");
        if (Directory.Exists(bin))
        {
            references.AddRange(Directory.EnumerateFiles(bin, "*.dll"));
        }

        References = references;
        _types = new Lazy<TypeSystem>(() => new TypeSystem(FrameworkReferences.Locate(TargetFrameworkVersion), References, path => Track(path)));
    }

    public string ProjectDirectory { get; }
    public string? ProjectFile { get; }
    public string RootNamespace { get; } = "";
    public string? AssemblyName { get; }
    public string TargetFrameworkVersion { get; }
    public IReadOnlyList<string> References { get; }
    public TypeSystem Types => _types.Value;

    /// <summary>Find the project that owns a markup file (nearest ancestor directory containing a .vbproj, else one with a web.config).</summary>
    public static ProjectContext ForFile(string markupPath)
    {
        var (directory, projectFile) = Locate(markupPath);
        return Cache.GetOrAdd(directory, _ => new ProjectContext(directory, projectFile));
    }

    /// <summary>The project directory and file that own a file (see <see cref="ForFile"/>), without loading anything.</summary>
    public static (string Directory, string? ProjectFile) Locate(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string? webConfigDir = null;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var proj = d.EnumerateFiles("*.vbproj").FirstOrDefault();
            if (proj is not null)
            {
                return (d.FullName, proj.FullName);
            }

            if (File.Exists(Path.Combine(d.FullName, "web.config")))
            {
                webConfigDir = d.FullName;
            }
        }

        return (webConfigDir ?? dir, null);
    }

    /// <summary>A new, uncached context (for long-running processes, which recreate it when <see cref="Refresh"/> says so).</summary>
    public static ProjectContext Create(string projectDirectory, string? projectFile) => new(Path.GetFullPath(projectDirectory), projectFile);

    /// <summary>
    /// Checks every file this context has read (project file, bin, loaded assemblies, web.config files, referenced
    /// markup). Clears the caches of changed web.config / markup files; reports <see cref="ProjectChange.Reload"/>
    /// when the project file or an assembly changed, since the type system cannot be updated in place.
    /// </summary>
    public ProjectChange Refresh()
    {
        var result = ProjectChange.None;
        foreach (var (path, stamp) in _stamps)
        {
            var current = FileStamp.Of(path, stamp.IsDirectory);
            if (current == stamp)
            {
                continue;
            }

            string ext = Path.GetExtension(path);
            if (stamp.IsDirectory || path.Equals(ProjectFile, StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return ProjectChange.Reload;
            }

            if (Path.GetFileName(path).Equals("web.config", StringComparison.OrdinalIgnoreCase))
            {
                _webConfigControls.Clear();
                _webConfigNamespaces.Clear();
            }
            else
            {
                _directiveCache.TryRemove(path, out _);
            }

            _stamps[path] = current;
            result = ProjectChange.CachesCleared;
        }

        return result;
    }

    private void Track(string path, bool directory = false) => _stamps.TryAdd(path, FileStamp.Of(path, directory));

    private readonly record struct FileStamp(bool IsDirectory, bool Exists, DateTime LastWriteUtc, long Length)
    {
        public static FileStamp Of(string path, bool directory)
        {
            if (directory)
            {
                var d = new DirectoryInfo(path);
                return new FileStamp(true, d.Exists, d.Exists ? d.LastWriteTimeUtc : default, 0);
            }

            var f = new FileInfo(path);
            return new FileStamp(false, f.Exists, f.Exists ? f.LastWriteTimeUtc : default, f.Exists ? f.Length : 0);
        }
    }

    public static ProjectContext ForDirectory(string projectDirectory)
    {
        var full = Path.GetFullPath(projectDirectory);
        var proj = Directory.EnumerateFiles(full, "*.vbproj").FirstOrDefault();
        return Cache.GetOrAdd(full, _ => new ProjectContext(full, proj));
    }

    /// <summary>Resolve a virtual path (~/x, /x or relative) used in markupFile to a physical path.</summary>
    public string ResolveVirtualPath(string virtualPath, string markupFile)
    {
        var vp = virtualPath.Trim().Replace('/', Path.DirectorySeparatorChar);
        string full;
        if (vp.StartsWith('~'))
        {
            full = Path.Combine(ProjectDirectory, vp.TrimStart('~').TrimStart(Path.DirectorySeparatorChar));
        }
        else if (vp.StartsWith(Path.DirectorySeparatorChar))
        {
            full = Path.Combine(ProjectDirectory, vp.TrimStart(Path.DirectorySeparatorChar));
        }
        else
        {
            full = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(markupFile))!, vp);
        }

        return Path.GetFullPath(full);
    }

    /// <summary>Read only the directives of another markup file (user control / master page). Cached.</summary>
    public MarkupDocument? ReadDirectives(string physicalPath) => _directiveCache.GetOrAdd(physicalPath, p =>
    {
        Track(p);
        if (!File.Exists(p))
        {
            return null;
        }

        return MarkupDocument.ParseDirectivesOnly(File.ReadAllText(p));
    });

    /// <summary>The Inherits type of another markup file, or null.</summary>
    public string? GetInherits(string physicalPath)
    {
        var doc = ReadDirectives(physicalPath);
        var main = doc?.MainDirective;
        return main?.GetAttributeValue("Inherits")?.Trim();
    }

    /// <summary>
    /// web.config &lt;pages&gt;&lt;controls&gt; registrations that apply to a markup file: the project root web.config
    /// and any web.config files in sub-directories down to the file's directory (parents first).
    /// </summary>
    public IReadOnlyList<TagRegistration> GetWebConfigControls(string markupFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(markupFile))!;
        return _webConfigControls.GetOrAdd(dir, d =>
        {
            var dirs = new List<string>();
            for (var x = new DirectoryInfo(d); x is not null; x = x.Parent)
            {
                dirs.Add(x.FullName);
                if (string.Equals(x.FullName.TrimEnd('\\', '/'), ProjectDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            dirs.Reverse();
            var result = new List<TagRegistration>();
            foreach (var x in dirs)
            {
                var cfg = Path.Combine(x, "web.config");
                Track(cfg);
                if (File.Exists(cfg))
                {
                    ReadWebConfigControls(cfg, result);
                }
            }

            return result;
        });
    }

    /// <summary>
    /// web.config &lt;pages&gt;&lt;namespaces&gt; that apply to a markup file (imported into every page, as
    /// <c>&lt;%@ Import %&gt;</c>), from the project root down to the file's directory.
    /// </summary>
    public IReadOnlyList<string> GetWebConfigNamespaces(string markupFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(markupFile))!;
        return _webConfigNamespaces.GetOrAdd(dir, d =>
        {
            var dirs = new List<string>();
            for (var x = new DirectoryInfo(d); x is not null; x = x.Parent)
            {
                dirs.Add(x.FullName);
                if (string.Equals(x.FullName.TrimEnd('\\', '/'), ProjectDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            dirs.Reverse();
            var result = new List<string>();
            foreach (var x in dirs)
            {
                var cfg = Path.Combine(x, "web.config");
                Track(cfg);
                if (!File.Exists(cfg))
                {
                    continue;
                }

                try
                {
                    var namespaces = XDocument.Load(cfg).Root?.Element("system.web")?.Element("pages")?.Element("namespaces");
                    foreach (var el in namespaces?.Elements() ?? [])
                    {
                        if (el.Name.LocalName == "clear")
                        {
                            result.Clear();
                        }
                        else if (el.Name.LocalName == "add" && (string?)el.Attribute("namespace") is { Length: > 0 } ns)
                        {
                            result.Add(ns);
                        }
                        else if (el.Name.LocalName == "remove" && (string?)el.Attribute("namespace") is { } removed)
                        {
                            result.Remove(removed);
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
                {
                    // An unreadable web.config adds nothing.
                }
            }

            return result;
        });
    }

    private static void ReadWebConfigControls(string path, List<TagRegistration> into)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception)
        {
            return;
        }

        var root = doc.Root;
        var systemWeb = root?.Element("system.web");
        var controls = systemWeb?.Element("pages")?.Element("controls");
        if (controls is null)
        {
            return;
        }

        foreach (var el in controls.Elements())
        {
            if (el.Name.LocalName == "clear")
            {
                into.Clear();
                continue;
            }

            if (el.Name.LocalName != "add")
            {
                continue;
            }

            into.Add(new TagRegistration(
                (string?)el.Attribute("tagPrefix") ?? "",
                (string?)el.Attribute("namespace"),
                (string?)el.Attribute("assembly"),
                (string?)el.Attribute("tagName"),
                (string?)el.Attribute("src"),
                path));
        }
    }

    public void Dispose()
    {
        if (_types.IsValueCreated)
        {
            _types.Value.Dispose();
        }
    }
}

/// <summary>Locates .NET Framework reference assemblies.</summary>
public static class FrameworkReferences
{
    public static string Locate(string targetFrameworkVersion)
    {
        var version = targetFrameworkVersion.TrimStart('v', 'V');
        var candidates = new List<string>();

        var env = Environment.GetEnvironmentVariable("WEBFORMS_FRAMEWORK_REFS");
        if (!string.IsNullOrEmpty(env))
        {
            candidates.Add(env);
        }

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf86))
        {
            candidates.Add(Path.Combine(pf86, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v" + version));
        }

        // NuGet reference assembly packages (Microsoft.NETFramework.ReferenceAssemblies.net48 etc.).
        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var pkg = Path.Combine(nuget, "microsoft.netframework.referenceassemblies.net" + version.Replace(".", ""));
        if (Directory.Exists(pkg))
        {
            foreach (var v in Directory.GetDirectories(pkg).OrderDescending())
            {
                candidates.Add(Path.Combine(v, "build", ".NETFramework", "v" + version));
            }
        }

        if (!string.IsNullOrEmpty(pf86))
        {
            // Fall back to the newest installed 4.x reference set.
            var root = Path.Combine(pf86, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");
            if (Directory.Exists(root))
            {
                candidates.AddRange(Directory.GetDirectories(root, "v4.*").OrderDescending());
            }
        }

        foreach (var c in candidates)
        {
            if (File.Exists(Path.Combine(c, "System.Web.dll")))
            {
                return c;
            }
        }

        throw new DesignerException(
            $".NET Framework {targetFrameworkVersion} reference assemblies (System.Web.dll) not found. " +
            "Install the .NET Framework developer pack or set WEBFORMS_FRAMEWORK_REFS to a directory containing System.Web.dll.");
    }
}

public sealed class DesignerException(string message) : Exception(message);
