using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using WebForms.Designer;

namespace WebForms.LanguageServer.CodeBlocks;

/// <summary>
/// One Roslyn solution per Web Application Project: every Compile item of the .vbproj (designers included), the
/// framework reference assemblies it names, the bin DLLs (read into memory: a mapped file would lock bin), and the
/// projections of the markup files that code-block requests have touched. Built on a background task; after that it
/// is only used from the server's handler loop, and every change forks a new immutable <see cref="Solution"/>, so
/// Roslyn reuses everything that didn't change.
/// </summary>
public sealed class CodeBlockProject
{
    private static readonly Lazy<MefHostServices> Host = new(() => MefHostServices.Create(MefHostServices.DefaultAssemblies));

    private readonly Dictionary<string, (DocumentId Id, object? Stamp)> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DocumentId Id, MarkupAnalysis Analysis, Projection Projection)> _projections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task<Solution> _build;
    private Solution? _solution;
    private ProjectId? _projectId;
    private DateTime _lastDiskCheck;

    private CodeBlockProject(ProjectContext context, VbProjectFile file, Action onReady, Action<string>? log)
    {
        Context = context;
        File = file;
        _build = Task.Run(() =>
        {
            try
            {
                var started = DateTime.UtcNow;
                var solution = Create();
                var compilation = solution.GetProject(_projectId!)!.GetCompilationAsync().GetAwaiter().GetResult()!;
                _ = compilation.GetDeclarationDiagnostics(); // bind every declaration now rather than on the first request
                log?.Invoke($"code blocks: {Path.GetFileName(file.Path)} compiled ({_documents.Count} files) in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
                onReady();
                return solution;
            }
            catch (Exception ex)
            {
                log?.Invoke($"code blocks: {Path.GetFileName(file.Path)} failed to build: {ex}");
                throw;
            }
        });
    }

    /// <summary>The context this was built for: when the workspace replaces it (project file or bin changed), rebuild.</summary>
    public ProjectContext Context { get; }

    public VbProjectFile File { get; }

    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Blocks until the first build has finished (tests, the corpus check).</summary>
    public void Wait()
    {
        try
        {
            _build.Wait();
        }
        catch (AggregateException)
        {
            // see Failure
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the first build (an interactive request made while it runs).</summary>
    public void Wait(TimeSpan timeout)
    {
        try
        {
            _build.Wait(timeout);
        }
        catch (AggregateException)
        {
            // see Failure
        }
    }

    public bool IsReady => _build.IsCompletedSuccessfully;

    public Exception? Failure => _build.Exception?.GetBaseException();

    public static CodeBlockProject Start(ProjectContext context, Action onReady, Action<string>? log) =>
        new(context, VbProjectFile.Read(context.ProjectFile!), onReady, log);

    /// <summary>
    /// The current solution with the open buffers (and changed files on disk) applied and the markup file's
    /// projection in it, or null while the first build is still running.
    /// </summary>
    public (Document Document, Projection Projection)? Get(WebFormsWorkspace workspace, MarkupAnalysis analysis, Projection projection)
    {
        if (!IsReady)
        {
            return null;
        }

        LastUsedUtc = DateTime.UtcNow;
        var solution = _solution ??= _build.Result;
        solution = Sync(workspace, solution);

        if (_projections.TryGetValue(analysis.Path, out var p))
        {
            if (!ReferenceEquals(p.Projection, projection))
            {
                solution = solution.WithDocumentText(p.Id, SourceText.From(projection.Text));
                _projections[analysis.Path] = (p.Id, analysis, projection);
            }
        }
        else
        {
            var id = DocumentId.CreateNewId(_projectId!);
            solution = solution.AddDocument(id, Path.GetFileName(analysis.Path) + ".__projection.vb", SourceText.From(projection.Text),
                filePath: analysis.Path + ".__projection.vb");
            _projections[analysis.Path] = (id, analysis, projection);
        }

        _solution = solution;
        return (solution.GetDocument(_projections[analysis.Path].Id)!, projection);
    }

    /// <summary>The current solution (null until built), for symbol searches.</summary>
    public Solution? Solution => _solution;

    /// <summary>The markup file of a projection document, or null for a real .vb file.</summary>
    public (string MarkupPath, Projection Projection)? ProjectionOf(DocumentId id)
    {
        foreach (var (path, p) in _projections)
        {
            if (p.Id == id)
            {
                return (path, p.Projection);
            }
        }

        return null;
    }

    /// <summary>Open buffers override the files; files changed on disk are re-read (checked at most once a second).</summary>
    private Solution Sync(WebFormsWorkspace workspace, Solution solution)
    {
        bool checkDisk = DateTime.UtcNow - _lastDiskCheck > TimeSpan.FromSeconds(1);
        if (checkDisk)
        {
            _lastDiskCheck = DateTime.UtcNow;
        }

        foreach (var (path, (id, stamp)) in _documents.ToList())
        {
            object? current;
            string? text = null;
            if (workspace.IsOpen(path) && workspace.GetDocument(path) is { Version: { } version } buffer)
            {
                current = ("buffer", version);
                if (Equals(current, stamp))
                {
                    continue;
                }

                text = buffer.Text;
            }
            else
            {
                if (!checkDisk && stamp is not (string, int))
                {
                    continue;
                }

                current = DiskStamp(path);
                if (Equals(current, stamp))
                {
                    continue;
                }

                text = ReadText(path);
            }

            solution = solution.WithDocumentText(id, SourceText.From(text ?? ""));
            _documents[path] = (id, current);
        }

        return solution;
    }

    private Solution Create()
    {
        var workspace = new AdhocWorkspace(Host.Value);
        _projectId = ProjectId.CreateNewId();
        var parseOptions = new VisualBasicParseOptions(LanguageVersion.Latest, preprocessorSymbols:
        [
            new("_MyType", File.MyType.Length > 0 ? File.MyType : "Empty"),
            .. File.DefineConstants,
        ]);
        var options = new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            rootNamespace: File.RootNamespace,
            globalImports: File.Imports.Select(GlobalImport.Parse),
            optionStrict: File.OptionStrict ? OptionStrict.On : OptionStrict.Off,
            optionInfer: File.OptionInfer,
            optionExplicit: File.OptionExplicit,
            optionCompareText: File.OptionCompareText,
            concurrentBuild: true);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(_projectId, VersionStamp.Create(), File.AssemblyName, File.AssemblyName,
            LanguageNames.VisualBasic, filePath: File.Path, compilationOptions: options, parseOptions: parseOptions, metadataReferences: References()));

        foreach (var path in File.CompileFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var id = DocumentId.CreateNewId(_projectId);
            solution = solution.AddDocument(id, Path.GetFileName(path), SourceText.From(ReadText(path) ?? ""), filePath: path);
            _documents[path] = (id, DiskStamp(path));
        }

        return solution;
    }

    private ImmutableArray<MetadataReference> References()
    {
        var refDir = FrameworkReferences.Locate(File.TargetFrameworkVersion);
        var refs = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in File.FrameworkReferences.Concat(["mscorlib", "System", "System.Core", "Microsoft.VisualBasic", "System.Web"]))
        {
            var path = Path.Combine(refDir, name + ".dll");
            if (System.IO.File.Exists(path))
            {
                refs.TryAdd(name, MetadataReference.CreateFromFile(path)); // reference assemblies never change: mapped is fine
            }
        }

        var facades = Path.Combine(refDir, "Facades");
        if (Directory.Exists(facades))
        {
            foreach (var path in Directory.EnumerateFiles(facades, "*.dll"))
            {
                refs.TryAdd(Path.GetFileNameWithoutExtension(path), MetadataReference.CreateFromFile(path));
            }
        }

        var bin = Path.Combine(File.Directory, "bin");
        var candidates = (Directory.Exists(bin) ? Directory.EnumerateFiles(bin, "*.dll") : []).Concat(File.HintPaths);
        foreach (var path in candidates)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Equals(File.AssemblyName, StringComparison.OrdinalIgnoreCase) || refs.ContainsKey(name) || !System.IO.File.Exists(path))
            {
                continue; // the project's own output; framework assemblies keep their reference version
            }

            var bytes = System.IO.File.ReadAllBytes(path);
            if (IsManagedAssembly(bytes))
            {
                refs[name] = MetadataReference.CreateFromImage(bytes, filePath: path);
            }
        }

        return [.. refs.Values];
    }

    private static bool IsManagedAssembly(byte[] bytes)
    {
        try
        {
            using var pe = new PEReader(ImmutableArray.Create(bytes));
            return pe.HasMetadata && System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe).IsAssembly;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static object? DiskStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
