using WebForms.Designer;

namespace WebForms.LanguageServer;

/// <summary>
/// The server's warm state: open documents (their text overrides the file on disk), one <see cref="ProjectContext"/>
/// per project, and caches of analyses keyed by document version / file stamp. Not thread-safe: the server handles
/// requests one at a time.
/// <para>
/// Staleness is checked on use rather than relying on file-change notifications: every lookup of a project calls
/// <see cref="ProjectContext.Refresh"/>, which stats the files the context was built from (a few dozen at most).
/// </para>
/// </summary>
public sealed class WebFormsWorkspace : IDisposable
{
    private readonly Dictionary<string, SourceDocument> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectContext> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (object Key, object? CodeBehindKey, MarkupAnalysis Value)> _analyses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Path, string ClassName), (object Key, CodeBehindInfo Value)> _scans = [];
    private readonly Dictionary<string, (object Key, VbFile Value)> _vbFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Diagnostic log (the server's --log file); null to discard.</summary>
    public Action<string>? Log { get; set; }

    public void Open(string path, string text, int version) => _open[path] = new SourceDocument(path, text, version);

    public void Change(string path, string text, int version) => _open[path] = new SourceDocument(path, text, version);

    public void Close(string path)
    {
        _open.Remove(path);
        _analyses.Remove(path);
        _vbFiles.Remove(path);
        foreach (var k in _scans.Keys.Where(k => k.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _scans.Remove(k);
        }
    }

    public bool IsOpen(string path) => _open.ContainsKey(path);

    /// <summary>Paths of the open documents.</summary>
    public IEnumerable<string> OpenPaths => _open.Keys;

    /// <summary>The open buffer, else the file on disk, else null.</summary>
    public SourceDocument? GetDocument(string path)
    {
        if (_open.TryGetValue(path, out var doc))
        {
            return doc;
        }

        try
        {
            return File.Exists(path) ? new SourceDocument(path, File.ReadAllText(path), null) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The project that owns a file, refreshed (or recreated) if the files it was built from changed.</summary>
    public ProjectContext GetProject(string path)
    {
        var (directory, projectFile) = ProjectContext.Locate(path);
        if (_projects.TryGetValue(directory, out var project))
        {
            switch (project.Refresh())
            {
                case ProjectChange.None:
                    return project;
                case ProjectChange.CachesCleared:
                    Log?.Invoke($"project {directory}: web.config or referenced markup changed; caches cleared");
                    _analyses.Clear();
                    return project;
                case ProjectChange.Reload:
                    Log?.Invoke($"project {directory}: project file or assemblies changed; reloading");
                    project.Dispose();
                    _analyses.Clear();
                    break;
            }
        }

        project = ProjectContext.Create(directory, projectFile);
        _projects[directory] = project;
        return project;
    }

    /// <summary>
    /// The markup analysis of an .aspx/.ascx/.master file, cached until its text, its code-behind (buffer or file) or
    /// the project changes.
    /// </summary>
    public MarkupAnalysis? Analyze(string path)
    {
        var project = GetProject(path);
        var key = StampKey(path, project);
        if (key is not null && _analyses.TryGetValue(path, out var cached) && cached.Key.Equals(key) &&
            Equals(cached.CodeBehindKey, cached.Value.CodeBehindPath is { } cbPath ? StampKey(cbPath, null) : null))
        {
            return cached.Value;
        }

        var doc = GetDocument(path);
        if (doc is null)
        {
            return null;
        }

        var analysis = MarkupAnalysis.Create(doc, project, ScanCodeBehind);
        if (key is not null)
        {
            _analyses[path] = (key, analysis.CodeBehindPath is { } cb ? StampKey(cb, null) : null, analysis);
        }

        return analysis;
    }

    /// <summary>The members of a code-behind class (for the designer pipeline), from the open buffer or the file; cached by version / stamp.</summary>
    public CodeBehindInfo ScanCodeBehind(string path, string className)
    {
        var key = StampKey(path, null);
        if (key is null)
        {
            return CodeBehindInfo.Empty;
        }

        if (_scans.TryGetValue((path, className), out var cached) && cached.Key.Equals(key))
        {
            return cached.Value;
        }

        var info = GetDocument(path) is { } doc ? CodeBehindScanner.ScanText(doc.Text, className) : CodeBehindInfo.Empty;
        _scans[(path, className)] = (key, info);
        return info;
    }

    /// <summary>A parsed VB file (code-behind or designer), or null if it does not exist.</summary>
    public VbFile? GetVbFile(string path)
    {
        var key = StampKey(path, null);
        if (key is not null && _vbFiles.TryGetValue(path, out var cached) && cached.Key.Equals(key))
        {
            return cached.Value;
        }

        var doc = GetDocument(path);
        if (doc is null)
        {
            return null;
        }

        var file = VbFile.Parse(path, doc.Text);
        if (key is not null)
        {
            _vbFiles[path] = (key, file);
        }

        return file;
    }

    /// <summary>Identifies a version of a file's content: the buffer version when open, else the disk stamp.</summary>
    private object? StampKey(string path, ProjectContext? project)
    {
        if (_open.TryGetValue(path, out var doc))
        {
            return (doc.Version, (object?)project);
        }

        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length, (object?)project) : null;
    }

    public void Dispose()
    {
        foreach (var p in _projects.Values)
        {
            p.Dispose();
        }

        _projects.Clear();
    }
}
