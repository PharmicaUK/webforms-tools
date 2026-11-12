using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using WebForms.Designer;
using WebForms.LanguageServer.CodeBlocks;
using WebForms.Formatting;
using WebForms.LanguageServer.Completion;
using WebForms.LanguageServer.Protocol;

namespace WebForms.LanguageServer;

/// <summary>
/// The ASPX language server. One task reads messages (and handles <c>$/cancelRequest</c> immediately); the main
/// loop handles requests, notifications and the debounced diagnostics refresh one at a time, in order, so the
/// workspace needs no locking.
/// <para>
/// What kind of file a request is about is decided by the URI's extension, never by <c>languageId</c> (Neovim sends
/// its filetype there: <c>aspx</c> for .ascx and .master too, <c>vbnet</c> for .vb).
/// </para>
/// </summary>
public sealed class WebFormsLanguageServer : IDisposable
{
    public const string GenerateDesignerCommand = "webforms.generateDesigner";

    private static readonly TimeSpan DefaultDiagnosticsDelay = TimeSpan.FromMilliseconds(300);

    private static readonly string[] WatchedExtensions = ["vb", "aspx", "ascx", "master", "config", "vbproj"];

    private readonly JsonRpcConnection _rpc;
    private readonly WebFormsWorkspace _workspace = new();
    private readonly Navigator _navigator;
    private readonly CompletionProvider _completion;
    private readonly MarkupDiagnostics _diagnostics;
    private readonly CodeBlockService _codeBlocks;
    private readonly Timer _idleTimer;
    private readonly Dictionary<string, IReadOnlyList<LspDiagnostic>> _published = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Timer _diagnosticsTimer;
    private readonly TimeSpan _diagnosticsDelay;
    private readonly TextWriter? _log;
    private readonly string? _version;
    private readonly ConcurrentDictionary<string, byte> _cancelled = new();
    private ClientOptions _client = new();
    private ClientFeatures _features = new();
    private bool _initialized;
    private bool _shutdown;

    /// <param name="diagnosticsDelay">
    /// How long after the last edit diagnostics are refreshed (default 300 ms); <see cref="TimeSpan.Zero"/> refreshes
    /// right after each change (tests).
    /// </param>
    public WebFormsLanguageServer(Stream input, Stream output, TextWriter? log = null, string? version = null, TimeSpan? diagnosticsDelay = null)
    {
        _rpc = new JsonRpcConnection(input, output);
        _log = log;
        _version = version;
        _navigator = new Navigator(_workspace);
        _completion = new CompletionProvider(_workspace);
        _diagnostics = new MarkupDiagnostics(_workspace);
        _codeBlocks = new CodeBlockService(_workspace) { Log = Log, ProjectReady = () => _queue.Writer.TryWrite(DiagnosticsTick.Instance) };
        _idleTimer = new Timer(_ => _queue.Writer.TryWrite(IdleTick.Instance), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _diagnosticsDelay = diagnosticsDelay ?? DefaultDiagnosticsDelay;
        _diagnosticsTimer = new Timer(_ => _queue.Writer.TryWrite(DiagnosticsTick.Instance));
        _workspace.Log = Log;
    }

    public WebFormsWorkspace Workspace => _workspace;

    private ServerSettings Settings
    {
        get => _completion.Settings;
        set
        {
            _completion.Settings = value;
            _codeBlocks.Enabled = value.CodeBlocks;
            _diagnostics.StaleDesigner = value.StaleDesignerDiagnostic;
        }
    }

    /// <summary>Runs until <c>exit</c> or end of input. Returns the process exit code: 0 after a shutdown request, else 1 (LSP specification).</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var reader = Task.Run(() => ReadLoopAsync(cancellationToken), cancellationToken);

        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item is IncomingMessage message)
                {
                    using (message)
                    {
                        if (Handle(message) is { } exitCode)
                        {
                            return exitCode;
                        }
                    }
                }
                else if (item is IdleTick)
                {
                    _codeBlocks.DropIdle();
                }
                else if (_initialized && !_shutdown)
                {
                    PublishDiagnostics();
                }
            }
        }
        finally
        {
            _diagnosticsTimer.Change(Timeout.Infinite, Timeout.Infinite);
            await reader.ConfigureAwait(false);
        }

        Log("end of input");
        return _shutdown ? 0 : 1;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var writer = _queue.Writer;
        try
        {
            while (true)
            {
                IncomingMessage? message;
                try
                {
                    message = await _rpc.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    Log("bad message: " + ex.Message);
                    _rpc.SendError(null, ErrorCodes.ParseError, ex.Message);
                    continue;
                }
                catch (EndOfStreamException)
                {
                    return;
                }

                if (message is null)
                {
                    return;
                }

                if (message.Method == "$/cancelRequest")
                {
                    if (message.Params is { } p && p.TryGetProperty("id", out var id))
                    {
                        _cancelled.TryAdd(id.GetRawText(), 0);
                    }

                    message.Dispose();
                    continue;
                }

                await writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);

                if (message.Method == "exit")
                {
                    return; // stop reading; the client may close the pipe
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <returns>An exit code when the server should stop.</returns>
    private int? Handle(IncomingMessage message)
    {
        if (message.IsResponse)
        {
            if (message.Id is { } responseId && message.Params is null)
            {
                Log($"response to {responseId.GetRawText()}");
            }

            return null; // responses to client/registerCapability: nothing to do either way
        }

        string method = message.Method!;
        if (!message.IsRequest)
        {
            if (method == "exit")
            {
                Log("exit");
                return _shutdown ? 0 : 1;
            }

            try
            {
                HandleNotification(method, message.Params);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log($"{method} failed: {ex}");
            }

            return null;
        }

        var id = message.Id!.Value;
        if (_cancelled.TryRemove(id.GetRawText(), out _))
        {
            _rpc.SendError(id, ErrorCodes.RequestCancelled, "Request cancelled.");
            return null;
        }

        if (!_initialized && method != "initialize")
        {
            _rpc.SendError(id, ErrorCodes.ServerNotInitialized, "The server has not been initialized.");
            return null;
        }

        if (_shutdown)
        {
            _rpc.SendError(id, ErrorCodes.InvalidRequest, "The server is shutting down.");
            return null;
        }

        var started = DateTime.UtcNow;
        try
        {
            HandleRequest(method, id, message.Params);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            Log($"{method}: invalid params: {ex.Message}");
            _rpc.SendError(id, ErrorCodes.InvalidParams, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"{method} failed: {ex}");
            _rpc.SendError(id, ErrorCodes.InternalError, ex is DesignerException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
        }

        Log($"{method} {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
        return null;
    }

    private void HandleRequest(string method, JsonElement id, JsonElement? parameters)
    {
        switch (method)
        {
            case "initialize":
                {
                    _initialized = true;
                    _client = ReadClientOptions(parameters);
                    _features = ReadClientFeatures(parameters);
                    if (parameters is { ValueKind: JsonValueKind.Object } init && init.TryGetProperty("initializationOptions", out var options))
                    {
                        Settings = ServerSettings.Read(options, Settings);
                    }

                    // Formatting is for .vb files only. With dynamic registration it is registered for **/*.vb after
                    // `initialized`, so the client doesn't pick this server to format markup; otherwise it is
                    // advertised here and markup requests get null.
                    var capabilities = new ServerCapabilities(
                        PositionEncoding: "utf-16",
                        TextDocumentSync: new TextDocumentSyncOptions(OpenClose: true, Change: TextDocumentSyncKind.Full, Save: new SaveOptions(IncludeText: false)),
                        DefinitionProvider: true,
                        ReferencesProvider: true,
                        CompletionProvider: new CompletionOptions(CompletionProvider.TriggerCharacters, ResolveProvider: true),
                        DocumentFormattingProvider: _features.DynamicFormatting ? null : true,
                        DocumentRangeFormattingProvider: _features.DynamicRangeFormatting ? null : true,
                        CodeActionProvider: _features.CodeActionLiterals
                            ? JsonDocument.Parse("""{"codeActionKinds":["quickfix","source"]}""").RootElement.Clone()
                            : JsonDocument.Parse("true").RootElement.Clone(),
                        ExecuteCommandProvider: new ExecuteCommandOptions([GenerateDesignerCommand]),
                        HoverProvider: true);
                    _rpc.SendResult(id, new InitializeResult(capabilities, new ServerInfo("webforms", _version)), LspJsonContext.Default.InitializeResult);
                    break;
                }

            case "shutdown":
                _shutdown = true;
                _rpc.SendNullResult(id);
                break;

            case "textDocument/definition":
                {
                    var p = Params(parameters, LspJsonContext.Default.TextDocumentPositionParams);
                    var locations = MarkupPath(p.TextDocument.Uri) is { } path
                        ? _codeBlocks.Definition(path, p.Position) ?? _navigator.Definition(path, p.Position)
                        : [];
                    _rpc.SendResult(id, locations.ToArray(), LspJsonContext.Default.LspLocationArray);
                    break;
                }

            case "textDocument/references":
                {
                    var p = Params(parameters, LspJsonContext.Default.ReferenceParams);
                    bool declaration = p.Context?.IncludeDeclaration ?? true;
                    var locations = MarkupPath(p.TextDocument.Uri) is { } path
                        ? _codeBlocks.References(path, p.Position, declaration) ?? _navigator.References(path, p.Position, declaration)
                        : [];
                    _rpc.SendResult(id, locations.ToArray(), LspJsonContext.Default.LspLocationArray);
                    break;
                }

            case "textDocument/hover":
                {
                    var p = Params(parameters, LspJsonContext.Default.TextDocumentPositionParams);
                    if (MarkupPath(p.TextDocument.Uri) is { } path && _codeBlocks.Hover(path, p.Position) is var (range, text))
                    {
                        var contents = _client.Markdown ? new MarkupContent("markdown", "```vb\n" + text + "\n```") : new MarkupContent("plaintext", text);
                        _rpc.SendResult(id, new LspHover(contents, range), LspJsonContext.Default.LspHover);
                    }
                    else
                    {
                        _rpc.SendNullResult(id);
                    }

                    break;
                }

            case "textDocument/completion":
                {
                    var p = Params(parameters, LspJsonContext.Default.CompletionParams);
                    if (MarkupPath(p.TextDocument.Uri) is { } path && Complete(path, p) is { } list)
                    {
                        _rpc.SendResult(id, list, LspJsonContext.Default.CompletionList);
                    }
                    else
                    {
                        _rpc.SendNullResult(id);
                    }

                    break;
                }

            case "completionItem/resolve":
                {
                    var item = Params(parameters, LspJsonContext.Default.CompletionItem);
                    var resolved = item.Data is < 0 ? _codeBlocks.Resolve(item, _client) : _completion.Resolve(item, _client);
                    _rpc.SendResult(id, resolved, LspJsonContext.Default.CompletionItem);
                    break;
                }

            case "textDocument/codeAction":
                {
                    var p = Params(parameters, LspJsonContext.Default.CodeActionParams);
                    CodeActions(id, p);
                    break;
                }

            case "workspace/executeCommand":
                {
                    var p = Params(parameters, LspJsonContext.Default.ExecuteCommandParams);
                    ExecuteCommand(id, p);
                    break;
                }

            case "textDocument/formatting":
                {
                    var p = Params(parameters, LspJsonContext.Default.DocumentFormattingParams);
                    Format(id, p.TextDocument.Uri, null);
                    break;
                }

            case "textDocument/rangeFormatting":
                {
                    var p = Params(parameters, LspJsonContext.Default.DocumentRangeFormattingParams);
                    Format(id, p.TextDocument.Uri, p.Range);
                    break;
                }

            default:
                _rpc.SendError(id, ErrorCodes.MethodNotFound, $"Method not found: {method}");
                break;
        }
    }

    private void HandleNotification(string method, JsonElement? parameters)
    {
        switch (method)
        {
            case "initialized":
                RegisterCapabilities();
                break;

            case "textDocument/didOpen":
                {
                    var p = Params(parameters, LspJsonContext.Default.DidOpenTextDocumentParams);
                    if (DocumentUri.ToPath(p.TextDocument.Uri) is { } path)
                    {
                        _workspace.Open(path, p.TextDocument.Text, p.TextDocument.Version);
                        if (DesignerGenerator.IsMarkupFile(path))
                        {
                            Publish(path);
                        }
                        else
                        {
                            ScheduleDiagnostics(); // a code-behind buffer can differ from its file
                        }
                    }

                    break;
                }

            case "textDocument/didChange":
                {
                    var p = Params(parameters, LspJsonContext.Default.DidChangeTextDocumentParams);
                    if (DocumentUri.ToPath(p.TextDocument.Uri) is { } path && _workspace.GetDocument(path) is { } current)
                    {
                        _workspace.Change(path, ApplyChanges(current, p.ContentChanges), p.TextDocument.Version);
                        ScheduleDiagnostics();
                    }

                    break;
                }

            case "textDocument/didSave":
                {
                    var p = Params(parameters, LspJsonContext.Default.DidSaveTextDocumentParams);
                    if (DocumentUri.ToPath(p.TextDocument.Uri) is { } path)
                    {
                        if (Settings.GenerateDesignerOnSave && DesignerGenerator.IsMarkupFile(path))
                        {
                            var result = DesignerFiles.Generate(_workspace, path);
                            Log($"generate designer on save: {result.Message}");
                            if (result.Outcome == DesignerOutcome.Failed)
                            {
                                ShowMessage(MessageType.Error, result.Message);
                            }
                        }

                        PublishDiagnostics();
                    }

                    break;
                }

            case "textDocument/didClose":
                {
                    var p = Params(parameters, LspJsonContext.Default.DidCloseTextDocumentParams);
                    if (DocumentUri.ToPath(p.TextDocument.Uri) is { } path)
                    {
                        _workspace.Close(path);
                        _diagnostics.Forget(path);
                        if (_published.Remove(path))
                        {
                            SendDiagnostics(path, [], null);
                        }

                        ScheduleDiagnostics(); // markup whose code-behind buffer was discarded
                    }

                    break;
                }

            case "workspace/didChangeWatchedFiles":
                ScheduleDiagnostics(); // an optimisation only: every request checks file stamps anyway
                break;

            case "workspace/didChangeConfiguration":
                if (parameters is { ValueKind: JsonValueKind.Object } p0 && p0.TryGetProperty("settings", out var settings))
                {
                    Settings = ServerSettings.Read(settings, Settings);
                    Log($"settings: {Settings}");
                }

                break;

            default:
                break; // $/setTrace, ...
        }
    }

    /// <summary>Dynamic registrations, for the clients that support them (sent after <c>initialized</c>).</summary>
    private void RegisterCapabilities()
    {
        var registrations = new List<Registration>();
        var vbOnly = JsonSerializer.SerializeToElement(
            new TextDocumentRegistrationOptions([new DocumentFilter("**/*.vb")]), LspJsonContext.Default.TextDocumentRegistrationOptions);
        if (_features.DynamicFormatting)
        {
            registrations.Add(new Registration("webforms-formatting", "textDocument/formatting", vbOnly));
        }

        if (_features.DynamicRangeFormatting)
        {
            registrations.Add(new Registration("webforms-range-formatting", "textDocument/rangeFormatting", vbOnly));
        }

        if (_features.DynamicWatchedFiles)
        {
            var watchers = new DidChangeWatchedFilesRegistrationOptions(
                [.. WatchedExtensions.Select(e => new LspFileSystemWatcher("**/*." + e))]);
            registrations.Add(new Registration("webforms-watched-files", "workspace/didChangeWatchedFiles",
                JsonSerializer.SerializeToElement(watchers, LspJsonContext.Default.DidChangeWatchedFilesRegistrationOptions)));
        }

        if (registrations.Count > 0)
        {
            _rpc.SendRequest("client/registerCapability", new RegistrationParams([.. registrations]), LspJsonContext.Default.RegistrationParams);
        }
    }

    /// <summary>"Generate designer" for a markup file or its code-behind / designer, as a code action (or a bare command for clients without code action literals).</summary>
    private void CodeActions(JsonElement id, CodeActionParams p)
    {
        if (DocumentUri.ToPath(p.TextDocument.Uri) is not { } path || DesignerFiles.MarkupFileFor(path) is not { } markup ||
            !File.Exists(markup) && !_workspace.IsOpen(markup) || _workspace.Analyze(markup) is not { Result.SkipReason: null })
        {
            SendEmpty();
            return;
        }

        var stale = p.Context?.Diagnostics?.Where(d => d.Source == MarkupDiagnostics.Source && d.Code == MarkupDiagnostics.StaleDesignerCode).ToArray();
        string kind = stale is { Length: > 0 } ? "quickfix" : "source";
        if (p.Context?.Only is { Length: > 0 } only && !only.Any(o => kind == o || kind.StartsWith(o + ".", StringComparison.Ordinal)))
        {
            SendEmpty();
            return;
        }

        var command = new LspCommand("Generate designer", GenerateDesignerCommand,
            [JsonSerializer.SerializeToElement(DocumentUri.FromPath(markup), LspJsonContext.Default.String)]);
        if (_features.CodeActionLiterals)
        {
            var action = new CodeAction("Generate designer", kind, stale is { Length: > 0 } ? stale : null, stale is { Length: > 0 } ? true : null, command);
            _rpc.SendResult(id, [action], LspJsonContext.Default.CodeActionArray);
        }
        else
        {
            _rpc.SendResult(id, [command], LspJsonContext.Default.LspCommandArray);
        }

        void SendEmpty()
        {
            if (_features.CodeActionLiterals)
            {
                _rpc.SendResult(id, [], LspJsonContext.Default.CodeActionArray);
            }
            else
            {
                _rpc.SendResult(id, [], LspJsonContext.Default.LspCommandArray);
            }
        }
    }

    private void ExecuteCommand(JsonElement id, ExecuteCommandParams p)
    {
        if (p.Command != GenerateDesignerCommand)
        {
            _rpc.SendError(id, ErrorCodes.InvalidParams, $"Unknown command: {p.Command}");
            return;
        }

        if (p.Arguments is not [{ ValueKind: JsonValueKind.String } arg, ..] || DocumentUri.ToPath(arg.GetString()!) is not { } path ||
            DesignerFiles.MarkupFileFor(path) is not { } markup)
        {
            _rpc.SendError(id, ErrorCodes.InvalidParams, $"{GenerateDesignerCommand} takes the URI of an .aspx, .ascx or .master file (or its code-behind).");
            return;
        }

        var result = DesignerFiles.Generate(_workspace, markup);
        Log($"generate designer: {result.Message}");
        ShowMessage(result.Outcome switch
        {
            DesignerOutcome.Written or DesignerOutcome.Unchanged => MessageType.Info,
            DesignerOutcome.Failed => MessageType.Error,
            _ => MessageType.Warning,
        }, result.Message);
        _rpc.SendResult(id, result.Message, LspJsonContext.Default.String);
        PublishDiagnostics();
    }

    /// <summary>
    /// VB formatting (the same formatter as <c>webforms format</c>, from the warm process) as minimal line edits. null
    /// for anything but a .vb file; a file with syntax errors gets an error response, not an empty edit list (which
    /// reads as "the formatter did nothing").
    /// </summary>
    private void Format(JsonElement id, string uri, LspRange? range)
    {
        if (DocumentUri.ToPath(uri) is not { } path || !path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ||
            _workspace.GetDocument(path) is not { } doc)
        {
            _rpc.SendNullResult(id);
            return;
        }

        LineRange? lines = null;
        if (range is not null)
        {
            int last = range.End.Line - (range.End.Character == 0 && range.End.Line > range.Start.Line ? 1 : 0);
            int count = doc.ToPosition(doc.Text.Length).Line + 1;
            if (range.Start.Line >= count)
            {
                _rpc.SendResult(id, [], LspJsonContext.Default.TextEditArray);
                return;
            }

            lines = new LineRange(range.Start.Line + 1, Math.Min(last, count - 1) + 1);
        }

        var result = VbFormatter.Format(doc.Text, lines, path);
        if (result.Text is null)
        {
            var first = result.Diagnostics[0];
            string more = result.Diagnostics.Count > 1 ? $" (and {result.Diagnostics.Count - 1} more)" : "";
            _rpc.SendError(id, ErrorCodes.RequestFailed, $"{first.Code} at line {first.Line}: {first.Message}{more}");
            return;
        }

        _rpc.SendResult(id, result.SkipReason is null ? LineDiff.Edits(doc, result.Text) : [], LspJsonContext.Default.TextEditArray);
    }

    /// <summary>
    /// Completion in markup: inside <c>&lt;%$ %&gt;</c> the expression builders, inside a code block VB (Roslyn), else the
    /// markup items. <c>.</c> and <c>,</c> only trigger the first two.
    /// </summary>
    private CompletionList? Complete(string path, CompletionParams p)
    {
        if (_workspace.Analyze(path) is not { } analysis)
        {
            return null;
        }

        if (ExpressionBuilderCompletion.Complete(analysis, analysis.Document.ToOffset(p.Position), _client) is { } builders)
        {
            return builders;
        }

        var list = _codeBlocks.Complete(path, p.Position, p.Context, _client, out bool inBlock);
        if (inBlock)
        {
            return list;
        }

        return p.Context?.TriggerCharacter is "." or "," ? null : _completion.Complete(path, p.Position, _client);
    }

    private void ScheduleDiagnostics()
    {
        if (_diagnosticsDelay <= TimeSpan.Zero)
        {
            PublishDiagnostics();
        }
        else
        {
            _diagnosticsTimer.Change(_diagnosticsDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Recomputes the diagnostics of every open markup file (cached analyses make unchanged ones cheap); sends those that changed.</summary>
    private void PublishDiagnostics()
    {
        _diagnosticsTimer.Change(Timeout.Infinite, Timeout.Infinite);
        foreach (var path in _workspace.OpenPaths.Where(DesignerGenerator.IsMarkupFile).ToList())
        {
            Publish(path);
        }
    }

    private void Publish(string path)
    {
        IReadOnlyList<LspDiagnostic> diagnostics;
        try
        {
            diagnostics = [.. _diagnostics.Compute(path), .. _codeBlocks.Diagnostics(path)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"diagnostics for {path} failed: {ex}");
            return;
        }

        if (_published.TryGetValue(path, out var previous) && previous.SequenceEqual(diagnostics))
        {
            return;
        }

        _published[path] = diagnostics;
        SendDiagnostics(path, diagnostics, _workspace.GetDocument(path)?.Version);
    }

    private void SendDiagnostics(string path, IReadOnlyList<LspDiagnostic> diagnostics, int? version) =>
        _rpc.SendNotification("textDocument/publishDiagnostics",
            new PublishDiagnosticsParams(DocumentUri.FromPath(path), [.. diagnostics], version), LspJsonContext.Default.PublishDiagnosticsParams);

    private void ShowMessage(int type, string message) =>
        _rpc.SendNotification("window/showMessage", new ShowMessageParams(type, message), LspJsonContext.Default.ShowMessageParams);

    /// <summary>Full-text sync is negotiated, but ranged changes are applied too, in order, in case a client sends them.</summary>
    public static string ApplyChanges(SourceDocument document, IEnumerable<TextDocumentContentChangeEvent> changes)
    {
        var current = document;
        foreach (var change in changes)
        {
            if (change.Range is null)
            {
                current = new SourceDocument(current.Path, change.Text, current.Version);
                continue;
            }

            int start = current.ToOffset(change.Range.Start);
            int end = Math.Max(start, current.ToOffset(change.Range.End));
            current = new SourceDocument(current.Path, string.Concat(current.Text.AsSpan(0, start), change.Text, current.Text.AsSpan(end)), current.Version);
        }

        return current.Text;
    }

    /// <summary>What the client's completion UI supports, from <c>capabilities.textDocument.completion</c>.</summary>
    internal static ClientOptions ReadClientOptions(JsonElement? parameters)
    {
        if (parameters is not { } p || !TryGet(p, out var completion, "capabilities", "textDocument", "completion"))
        {
            return new ClientOptions();
        }

        bool snippets = TryGet(completion, out var s, "completionItem", "snippetSupport") && s.ValueKind == JsonValueKind.True;
        bool markdown = TryGet(completion, out var f, "completionItem", "documentationFormat") && Contains(f, "markdown");
        bool editRange = TryGet(completion, out var d, "completionList", "itemDefaults") && Contains(d, "editRange");
        return new ClientOptions(snippets, markdown, editRange);

        static bool Contains(JsonElement array, string value) =>
            array.ValueKind == JsonValueKind.Array && array.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == value);
    }

    /// <summary>Optional protocol features the client supports.</summary>
    internal sealed record ClientFeatures(
        bool DynamicFormatting = false, bool DynamicRangeFormatting = false, bool DynamicWatchedFiles = false, bool CodeActionLiterals = false);

    internal static ClientFeatures ReadClientFeatures(JsonElement? parameters)
    {
        if (parameters is not { } p || !TryGet(p, out var caps, "capabilities"))
        {
            return new ClientFeatures();
        }

        bool True(params string[] path) => TryGet(caps, out var v, path) && v.ValueKind == JsonValueKind.True;
        return new ClientFeatures(
            True("textDocument", "formatting", "dynamicRegistration"),
            True("textDocument", "rangeFormatting", "dynamicRegistration"),
            True("workspace", "didChangeWatchedFiles", "dynamicRegistration"),
            TryGet(caps, out _, "textDocument", "codeAction", "codeActionLiteralSupport"));
    }

    private static bool TryGet(JsonElement element, out JsonElement result, params string[] path)
    {
        result = element;
        foreach (var name in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(name, out result))
            {
                return false;
            }
        }

        return true;
    }

    private static string? MarkupPath(string uri) =>
        DocumentUri.ToPath(uri) is { } path && DesignerGenerator.IsMarkupFile(path) ? path : null;

    private static T Params<T>(JsonElement? parameters, JsonTypeInfo<T> typeInfo) =>
        (parameters is { } p ? p.Deserialize(typeInfo) : default) ?? throw new InvalidDataException("Missing or invalid params.");

    private void Log(string message)
    {
        if (_log is null)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ').Append(message);
        lock (_log)
        {
            _log.WriteLine(sb.ToString());
            _log.Flush();
        }
    }

    public void Dispose()
    {
        _diagnosticsTimer.Dispose();
        _idleTimer.Dispose();
        _workspace.Dispose();
    }

    /// <summary>Queued every minute: drop code-block compilations nobody is using.</summary>
    private sealed class IdleTick
    {
        public static readonly IdleTick Instance = new();
    }

    /// <summary>Queued by the debounce timer: refresh diagnostics on the main loop.</summary>
    private sealed class DiagnosticsTick
    {
        public static readonly DiagnosticsTick Instance = new();
    }
}
