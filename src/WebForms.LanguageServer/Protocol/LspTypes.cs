using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebForms.LanguageServer.Protocol;

// The subset of the LSP 3.17 types the server uses. Positions are 0-based; characters count UTF-16 code units
// (the LSP default encoding, and what WebForms.Markup.SourcePosition counts).

public sealed record LspPosition(int Line, int Character);

public sealed record LspRange(LspPosition Start, LspPosition End);

public sealed record LspLocation(string Uri, LspRange Range);

public sealed record TextDocumentIdentifier(string Uri);

public sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

public sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public sealed record TextDocumentContentChangeEvent(LspRange? Range, string Text);

public sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

public sealed record DidChangeTextDocumentParams(VersionedTextDocumentIdentifier TextDocument, TextDocumentContentChangeEvent[] ContentChanges);

public sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, LspPosition Position);

public sealed record ReferenceContext(bool IncludeDeclaration);

public sealed record ReferenceParams(TextDocumentIdentifier TextDocument, LspPosition Position, ReferenceContext? Context);

public sealed record CancelParams(JsonElement Id);

public sealed record InitializeParams(int? ProcessId, JsonElement? InitializationOptions, JsonElement? Capabilities);

public sealed record ServerInfo(string Name, string? Version);

public sealed record SaveOptions(bool IncludeText);

public sealed record TextDocumentSyncOptions(bool OpenClose, int Change, SaveOptions? Save = null);

public sealed record ServerCapabilities(
    string PositionEncoding,
    TextDocumentSyncOptions TextDocumentSync,
    bool DefinitionProvider,
    bool ReferencesProvider,
    CompletionOptions? CompletionProvider = null,
    bool? DocumentFormattingProvider = null,
    bool? DocumentRangeFormattingProvider = null,
    JsonElement? CodeActionProvider = null,
    ExecuteCommandOptions? ExecuteCommandProvider = null,
    bool? HoverProvider = null);

public sealed record ExecuteCommandOptions(string[] Commands);

public sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo ServerInfo);

public sealed record LogMessageParams(int Type, string Message);

public sealed record CompletionOptions(string[] TriggerCharacters, bool ResolveProvider);

public sealed record CompletionContext(int TriggerKind, string? TriggerCharacter);

public sealed record CompletionParams(TextDocumentIdentifier TextDocument, LspPosition Position, CompletionContext? Context);

public sealed record TextEdit(LspRange Range, string NewText);

public sealed record MarkupContent(string Kind, string Value);

/// <summary>What completionItem/resolve needs to find an item again. Kept by the server; items carry only its id.</summary>
/// <param name="K">Kind: type, prop, event.</param>
/// <param name="F">Id of the markup file (selects the project; see CompletionProvider).</param>
/// <param name="A">Assembly simple name.</param>
/// <param name="T">Type full name (the declaring type for members).</param>
/// <param name="M">Member name.</param>
public sealed record CompletionData(string K, int F, string A, string T, string? M = null);

public sealed record CompletionItem
{
    public required string Label { get; init; }
    public int? Kind { get; init; }
    public string? Detail { get; init; }
    public MarkupContent? Documentation { get; init; }
    public string? SortText { get; init; }
    public string? FilterText { get; init; }
    public int? InsertTextFormat { get; init; }
    public TextEdit? TextEdit { get; init; }
    /// <summary>Id of the server-side <see cref="CompletionData"/> (one number: a GridView has over 300 attributes).</summary>
    public int? Data { get; init; }

    /// <summary>With <see cref="CompletionList.ItemDefaults"/>: the new text for the shared edit range (LSP 3.17).</summary>
    public string? TextEditText { get; init; }

    [JsonIgnore]
    public CompletionData? Ref { get; init; }
}

public sealed record CompletionItemDefaults(LspRange? EditRange);

public sealed record CompletionList(bool IsIncomplete, CompletionItem[] Items, CompletionItemDefaults? ItemDefaults = null);

public sealed record LspDiagnostic(LspRange Range, int Severity, string Code, string Source, string Message);

public sealed record PublishDiagnosticsParams(string Uri, LspDiagnostic[] Diagnostics, int? Version = null);

public sealed record DidSaveTextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record LspCommand(string Title, string Command, JsonElement[]? Arguments = null);

public sealed record CodeActionContext(LspDiagnostic[]? Diagnostics, string[]? Only);

public sealed record CodeActionParams(TextDocumentIdentifier TextDocument, LspRange Range, CodeActionContext? Context);

public sealed record CodeAction(string Title, string? Kind, LspDiagnostic[]? Diagnostics, bool? IsPreferred, LspCommand Command);

public sealed record ExecuteCommandParams(string Command, JsonElement[]? Arguments);

public sealed record FormattingOptions(int TabSize, bool InsertSpaces);

public sealed record DocumentFormattingParams(TextDocumentIdentifier TextDocument, FormattingOptions? Options);

public sealed record DocumentRangeFormattingParams(TextDocumentIdentifier TextDocument, LspRange Range, FormattingOptions? Options);

public sealed record DocumentFilter(string Pattern);

public sealed record TextDocumentRegistrationOptions(DocumentFilter[] DocumentSelector);

public sealed record LspFileSystemWatcher(string GlobPattern);

public sealed record DidChangeWatchedFilesRegistrationOptions(LspFileSystemWatcher[] Watchers);

public sealed record Registration(string Id, string Method, JsonElement? RegisterOptions);

public sealed record RegistrationParams(Registration[] Registrations);

public sealed record ShowMessageParams(int Type, string Message);

public sealed record LspHover(MarkupContent Contents, LspRange? Range);

public static class DiagnosticSeverity
{
    public const int Error = 1;
    public const int Warning = 2;
    public const int Information = 3;
    public const int Hint = 4;
}

public static class MessageType
{
    public const int Error = 1;
    public const int Warning = 2;
    public const int Info = 3;
    public const int Log = 4;
}

public static class CompletionItemKind
{
    public const int Method = 2;
    public const int Variable = 6;
    public const int Field = 5;
    public const int Class = 7;
    public const int Interface = 8;
    public const int Module = 9;
    public const int Property = 10;
    public const int Value = 12;
    public const int Enum = 13;
    public const int Keyword = 14;
    public const int Snippet = 15;
    public const int File = 17;
    public const int EnumMember = 20;
    public const int Constant = 21;
    public const int Struct = 22;
    public const int Event = 23;
    public const int Operator = 24;
    public const int TypeParameter = 25;
}

public static class InsertTextFormat
{
    public const int PlainText = 1;
    public const int Snippet = 2;
}

public static class TextDocumentSyncKind
{
    public const int Full = 1;
}

public static class ErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ServerNotInitialized = -32002;
    public const int RequestCancelled = -32800;
    public const int RequestFailed = -32803;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(ReferenceParams))]
[JsonSerializable(typeof(CancelParams))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(LspLocation[]))]
[JsonSerializable(typeof(LogMessageParams))]
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(DidSaveTextDocumentParams))]
[JsonSerializable(typeof(CodeActionParams))]
[JsonSerializable(typeof(CodeAction[]))]
[JsonSerializable(typeof(LspCommand[]))]
[JsonSerializable(typeof(ExecuteCommandParams))]
[JsonSerializable(typeof(DocumentFormattingParams))]
[JsonSerializable(typeof(DocumentRangeFormattingParams))]
[JsonSerializable(typeof(TextEdit[]))]
[JsonSerializable(typeof(TextDocumentRegistrationOptions))]
[JsonSerializable(typeof(DidChangeWatchedFilesRegistrationOptions))]
[JsonSerializable(typeof(RegistrationParams))]
[JsonSerializable(typeof(ShowMessageParams))]
[JsonSerializable(typeof(LspHover))]
[JsonSerializable(typeof(string))]
public sealed partial class LspJsonContext : JsonSerializerContext;
