using System.Xml.Linq;
using WebForms.LanguageServer.Protocol;

namespace WebForms.LanguageServer.Completion;

/// <summary>
/// Completion inside ASP.NET expression builders, <c>&lt;%$ Prefix: arguments %&gt;</c> (not VB: the builder parses
/// the arguments at runtime):
/// <list type="bullet">
/// <item>the prefixes: <c>AppSettings</c>, <c>ConnectionStrings</c>, <c>Resources</c>, <c>RouteUrl</c>,
/// <c>RouteValue</c> and the <c>&lt;expressionBuilders&gt;</c> registered in web.config;</item>
/// <item><c>AppSettings:</c> the keys of <c>&lt;appSettings&gt;</c> (including its <c>file</c> / <c>configSource</c>);</item>
/// <item><c>ConnectionStrings:</c> the names of <c>&lt;connectionStrings&gt;</c>, then <c>.ConnectionString</c> /
/// <c>.ProviderName</c>;</item>
/// <item><c>Resources:</c> the classes in <c>App_GlobalResources</c>, then after the comma their keys.</item>
/// </list>
/// The web.config files from the project root down to the page's directory apply, as at runtime.
/// </summary>
public static class ExpressionBuilderCompletion
{
    private static readonly string[] BuiltIn = ["AppSettings", "ConnectionStrings", "Resources", "RouteUrl", "RouteValue"];

    private static readonly string[] ConnectionStringMembers = ["ConnectionString", "ProviderName"];

    /// <summary>Null when the offset is not inside a <c>&lt;%$ %&gt;</c> block.</summary>
    public static CompletionList? Complete(MarkupAnalysis analysis, int offset, ClientOptions client)
    {
        var text = analysis.Document.Text;
        int open = text.LastIndexOf("<%$", Math.Max(0, Math.Min(offset, text.Length) - 1), StringComparison.Ordinal);
        if (open < 0 || offset < open + 3 || text.IndexOf("%>", open, offset - open, StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        var configs = ConfigFiles(analysis.Project.ProjectDirectory, analysis.Path);
        string content = text[(open + 3)..offset];
        int colon = content.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            int start = open + 3 + (content.Length - content.TrimStart().Length);
            var prefixes = BuiltIn.Concat(configs.SelectMany(c => Elements(c, "system.web", "compilation", "expressionBuilders")
                .Select(e => (string?)e.Attribute("expressionPrefix")).OfType<string>())).Distinct(StringComparer.OrdinalIgnoreCase);
            return List(analysis, start, offset, prefixes.Select(p => (p, p + ":", CompletionItemKind.Module, (string?)"expression builder")), client);
        }

        string prefix = content[..colon].Trim();
        string args = content[(colon + 1)..];
        int argsStart = open + 3 + colon + 1 + (args.Length - args.TrimStart().Length);
        string typed = args.TrimStart();

        if (prefix.Equals("AppSettings", StringComparison.OrdinalIgnoreCase))
        {
            var keys = configs.SelectMany(c => Section(c, "appSettings")).Select(e => (string?)e.Attribute("key")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
            return List(analysis, argsStart, offset, keys.Select(k => (k, k, CompletionItemKind.Value, (string?)"appSettings")), client);
        }

        if (prefix.Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase))
        {
            int dot = typed.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
            {
                return List(analysis, argsStart + dot + 1, offset,
                    ConnectionStringMembers.Select(p => (p, p, CompletionItemKind.Property, (string?)null)), client);
            }

            var names = configs.SelectMany(c => Section(c, "connectionStrings")).Select(e => (string?)e.Attribute("name")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
            return List(analysis, argsStart, offset, names.Select(n => (n, n, CompletionItemKind.Value, (string?)"connectionStrings")), client);
        }

        if (prefix.Equals("Resources", StringComparison.OrdinalIgnoreCase))
        {
            var resources = Path.Combine(analysis.Project.ProjectDirectory, "App_GlobalResources");
            int comma = typed.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                var classes = Directory.Exists(resources)
                    ? Directory.EnumerateFiles(resources, "*.resx").Select(f => Path.GetFileNameWithoutExtension(f).Split('.')[0]).Distinct(StringComparer.OrdinalIgnoreCase)
                    : [];
                return List(analysis, argsStart, offset, classes.Select(c => (c, c + ", ", CompletionItemKind.Class, (string?)"App_GlobalResources")), client);
            }

            string className = typed[..comma].Trim();
            string afterComma = typed[(comma + 1)..];
            int keyStart = argsStart + comma + 1 + (afterComma.Length - afterComma.TrimStart().Length);
            var resx = Path.Combine(resources, className + ".resx");
            var keys = File.Exists(resx) ? Load(resx)?.Root?.Elements("data").Select(e => (string?)e.Attribute("name")).OfType<string>() ?? [] : [];
            return List(analysis, keyStart, offset, keys.Select(k => (k, k, CompletionItemKind.Value, (string?)(className + ".resx"))), client);
        }

        return null; // RouteUrl / RouteValue / custom builders: routes and arguments are defined in code
    }

    private static CompletionList List(MarkupAnalysis analysis, int start, int end, IEnumerable<(string Label, string Insert, int Kind, string? Detail)> items, ClientOptions client)
    {
        var range = analysis.Document.ToRange(start, Math.Max(0, end - start));
        var result = items.Select(i => new CompletionItem
        {
            Label = i.Label,
            Kind = i.Kind,
            Detail = i.Detail,
            TextEdit = client.EditRangeDefault ? null : new TextEdit(range, i.Insert),
            TextEditText = client.EditRangeDefault ? i.Insert : null,
        }).ToArray();
        return new CompletionList(false, result, client.EditRangeDefault ? new CompletionItemDefaults(range) : null);
    }

    /// <summary>The web.config files that apply to a page, root first.</summary>
    private static List<(string Path, XDocument Doc)> ConfigFiles(string projectDirectory, string page)
    {
        var dirs = new List<string>();
        for (var d = new DirectoryInfo(Path.GetDirectoryName(page)!); d is not null; d = d.Parent)
        {
            dirs.Add(d.FullName);
            if (string.Equals(d.FullName.TrimEnd('\\', '/'), projectDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        dirs.Reverse();
        var result = new List<(string, XDocument)>();
        foreach (var dir in dirs)
        {
            var path = Path.Combine(dir, "web.config");
            if (File.Exists(path) && Load(path) is { } doc)
            {
                result.Add((path, doc));
            }
        }

        return result;
    }

    /// <summary>The <c>&lt;add&gt;</c> elements of a configuration section, following <c>configSource</c> and (appSettings) <c>file</c>.</summary>
    private static IEnumerable<XElement> Section((string Path, XDocument Doc) config, string name)
    {
        var section = config.Doc.Root?.Element(name);
        if (section is null)
        {
            yield break;
        }

        foreach (var external in new[] { (string?)section.Attribute("configSource"), (string?)section.Attribute("file") }.OfType<string>())
        {
            var path = Path.Combine(Path.GetDirectoryName(config.Path)!, external);
            if (File.Exists(path) && Load(path)?.Root is { } root)
            {
                foreach (var add in root.Elements("add"))
                {
                    yield return add;
                }
            }
        }

        foreach (var add in section.Elements("add"))
        {
            yield return add;
        }
    }

    private static IEnumerable<XElement> Elements((string Path, XDocument Doc) config, params string[] path)
    {
        XElement? el = config.Doc.Root;
        foreach (var name in path)
        {
            el = el?.Element(name);
        }

        return el?.Elements("add") ?? [];
    }

    private static readonly Dictionary<string, (DateTime Stamp, XDocument? Doc)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An XML file, cached by its write time; null if it can't be read.</summary>
    private static XDocument? Load(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                return cached.Doc;
            }
        }

        XDocument? doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            doc = null;
        }

        lock (Cache)
        {
            Cache[path] = (stamp, doc);
        }

        return doc;
    }
}
