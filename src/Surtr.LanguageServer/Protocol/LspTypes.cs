#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Surtr.LanguageServer.Protocol
{
    /// <summary>
    /// The JSON shape every message serializes with. LSP field names are camelCase, absent fields
    /// must be omitted rather than written null, and <see cref="JsonElement"/> members must pass
    /// through verbatim rather than be re-encoded.
    /// </summary>
    public static class RpcJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    /// <summary>Standard JSON-RPC error codes, which the server responds with when it fails a request.</summary>
    public static class RpcErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
    }

    /// <summary>A position in a text document: zero-based line, zero-based UTF-16 character offset.</summary>
    /// <remarks>
    /// A mutable struct with get/set: System.Text.Json deserializes a parameterless struct by
    /// setting its properties, so a read-only struct would come back as (0, 0) no matter what the
    /// client sent — a bug that looks like every request targets the top of the file.
    /// </remarks>
    public struct Position
    {
        public Position(int line, int character)
        {
            Line = line;
            Character = character;
        }

        public int Line { get; set; }

        public int Character { get; set; }
    }

    /// <summary>A half-open range of positions.</summary>
    public struct Range
    {
        public Range(Position start, Position end)
        {
            Start = start;
            End = end;
        }

        public Position Start { get; set; }

        public Position End { get; set; }
    }

    /// <summary>One squiggle under a range of source, as defined by <c>textDocument/publishDiagnostics</c>.</summary>
    public sealed class LspDiagnostic
    {
        public LspDiagnostic(Range range, string message, int severity, string source, string code)
        {
            Range = range;
            Message = message;
            Severity = severity;
            Source = source;
            Code = code;
        }

        public Range Range { get; }

        public int Severity { get; }

        public string Source { get; }

        public string Code { get; }

        public string Message { get; }
    }

    /// <summary>Information the server advertises in its <c>initialize</c> result.</summary>
    public sealed class InitializeResult
    {
        public ServerCapabilities Capabilities { get; set; } = new ServerCapabilities();

        public ServerInfo? ServerInfo { get; set; }
    }

    public sealed class ServerInfo
    {
        public string Name { get; set; } = "surtr-lsp";

        public string Version { get; set; } = "0.1.0";
    }

    /// <summary>The subset of server capabilities the Surtr server implements.</summary>
    public sealed class ServerCapabilities
    {
        public TextDocumentSyncOptions? TextDocumentSync { get; set; }

        public object HoverProvider { get; set; } = true;

        public object DefinitionProvider { get; set; } = true;

        public CompletionOptions? CompletionProvider { get; set; }

        public SignatureHelpOptions? SignatureHelpProvider { get; set; }

        public object? CodeActionProvider { get; set; }

        public SemanticTokensOptions? SemanticTokensProvider { get; set; }

        /// <summary>Offered without a resolve step: a hint's label is final the moment it is sent.</summary>
        public object InlayHintProvider { get; set; } = true;
    }

    /// <summary>What the server offers for <c>textDocument/semanticTokens</c>.</summary>
    public sealed class SemanticTokensOptions
    {
        public SemanticTokensLegend Legend { get; set; } = new SemanticTokensLegend();

        public bool Full { get; set; } = true;
    }

    /// <summary>The fixed vocabulary <c>textDocument/semanticTokens</c> data indexes into.</summary>
    public sealed class SemanticTokensLegend
    {
        public List<string> TokenTypes { get; set; } = new List<string>();

        public List<string> TokenModifiers { get; set; } = new List<string>();
    }

    /// <summary>Parameters of <c>textDocument/semanticTokens/full</c>.</summary>
    public sealed class SemanticTokensParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new TextDocumentIdentifier();
    }

    /// <summary>
    /// The answer to <c>textDocument/semanticTokens/full</c>: tokens encoded five integers at a time
    /// — line delta, start-character delta (from the previous token's start on the same line,
    /// otherwise from column 0), length, token type index, and a token modifier bitmask.
    /// </summary>
    public sealed class SemanticTokens
    {
        public List<int> Data { get; set; } = new List<int>();
    }

    /// <summary>Parameters of <c>textDocument/inlayHint</c>.</summary>
    public sealed class InlayHintParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new TextDocumentIdentifier();

        public Range Range { get; set; }
    }

    /// <summary>One grey inlay hint (LSP 3.17), placed at a character position.</summary>
    public sealed class InlayHint
    {
        public Position Position { get; set; }

        /// <summary>The text to render. A plain string or a list of labelled parts; both serialize fine.</summary>
        public object Label { get; set; } = string.Empty;

        /// <summary>Whether this is a type hint (<see cref="InlayHintKinds.Type"/>) or a parameter hint (<see cref="InlayHintKinds.Parameter"/>).</summary>
        public int? Kind { get; set; }

        public bool? PaddingLeft { get; set; }

        public bool? PaddingRight { get; set; }
    }

    /// <summary>Standard LSP inlay-hint kinds, by their protocol numbers.</summary>
    public static class InlayHintKinds
    {
        public const int Type = 1;

        public const int Parameter = 2;
    }

    /// <summary>What the server offers for <c>textDocument/completion</c>.</summary>
    public sealed class CompletionOptions
    {
        /// <summary>A dot is all that triggers completion while typing; Ctrl+Space asks for it directly.</summary>
        public List<string> TriggerCharacters { get; set; } = new List<string> { "." };
    }

    /// <summary>What the server offers for <c>textDocument/signatureHelp</c>.</summary>
    public sealed class SignatureHelpOptions
    {
        public List<string> TriggerCharacters { get; set; } = new List<string> { "(", "," };

        public List<string> RetriggerCharacters { get; set; } = new List<string> { "," };
    }

    /// <summary>Standard LSP completion item kinds, by their protocol numbers.</summary>
    public static class CompletionItemKinds
    {
        public const int Text = 1;
        public const int Method = 2;
        public const int Function = 3;
        public const int Constructor = 4;
        public const int Field = 5;
        public const int Variable = 6;
        public const int Class = 7;
        public const int Interface = 8;
        public const int Module = 9;
        public const int Property = 10;
        public const int Enum = 13;
        public const int Keyword = 14;
        public const int Snippet = 15;
        public const int TypeParameter = 25;
    }

    /// <summary>The answer to <c>textDocument/completion</c>.</summary>
    public sealed class CompletionList
    {
        public bool IsIncomplete { get; set; }

        public List<CompletionItem> Items { get; set; } = new List<CompletionItem>();
    }

    /// <summary>One suggestion. The label is what gets inserted; the rest describes it.</summary>
    public sealed class CompletionItem
    {
        public string Label { get; set; } = string.Empty;

        public int Kind { get; set; }

        /// <summary>A short one-line description shown beside the label.</summary>
        public string? Detail { get; set; }

        /// <summary>Richer markdown shown once the item is selected.</summary>
        public MarkupContent? Documentation { get; set; }

        /// <summary>Orders the list; sorting by kind alone would put keywords first.</summary>
        public string? SortText { get; set; }
    }

    /// <summary>The answer to <c>textDocument/signatureHelp</c>.</summary>
    public sealed class SignatureHelp
    {
        public List<SignatureInformation> Signatures { get; set; } = new List<SignatureInformation>();

        /// <summary>Which signature the client should show, by index into <see cref="Signatures"/>.</summary>
        public int? ActiveSignature { get; set; }

        /// <summary>Which parameter of that signature the cursor is filling.</summary>
        public int? ActiveParameter { get; set; }
    }

    public sealed class SignatureInformation
    {
        public string Label { get; set; } = string.Empty;

        public MarkupContent? Documentation { get; set; }

        public List<ParameterInformation> Parameters { get; set; } = new List<ParameterInformation>();
    }

    /// <summary>One parameter of a signature; the label is its rendered text.</summary>
    public sealed class ParameterInformation
    {
        public string Label { get; set; } = string.Empty;
    }

    public sealed class TextDocumentSyncOptions
    {
        public bool OpenClose { get; set; } = true;

        public int Change { get; set; } = 1; // Full
    }

    /// <summary>What the server can send for a hover: a markdown string and the range it covers.</summary>
    public sealed class HoverResult
    {
        public MarkupContent Contents { get; set; } = new MarkupContent();

        public Range? Range { get; set; }
    }

    public sealed class MarkupContent
    {
        public string Kind { get; set; } = "markdown";

        public string Value { get; set; } = string.Empty;
    }

    /// <summary>A source location, as a hover or a definition answers with.</summary>
    public sealed class Location
    {
        public Location(string uri, Range range)
        {
            Uri = uri;
            Range = range;
        }

        public string Uri { get; }

        public Range Range { get; }
    }

    /// <summary>Parameters of the <c>initialize</c> request.</summary>
    public sealed class InitializeParams
    {
        public string? RootUri { get; set; }

        public string? RootPath { get; set; }

        /// <summary>
        /// The modern replacement for <see cref="RootUri"/>/<see cref="RootPath"/> — a client that
        /// supports multi-root workspaces sends this instead, and per the spec may leave the two
        /// above unset entirely rather than only pointing at the first folder. This server still
        /// only ever binds to one root (<see cref="Workspace.Workspace"/> is single-root by design),
        /// so on a genuine multi-root workspace only the first folder here is used — a documented
        /// limit, not a silent one.
        /// </summary>
        public List<WorkspaceFolder>? WorkspaceFolders { get; set; }

        /// <summary>Server-specific settings the client passes at startup — see <see cref="SurtrInitializationOptions"/>.</summary>
        public SurtrInitializationOptions? InitializationOptions { get; set; }
    }

    /// <summary>
    /// This server's own settings, carried in <c>initialize</c>'s <c>initializationOptions</c> — the
    /// LSP-standard place for a server to accept configuration a generic client has no opinion about.
    /// </summary>
    public sealed class SurtrInitializationOptions
    {
        /// <summary>
        /// Overrides which folder the workspace treats as its compilation root (§2.1's module-path
        /// derivation point), when it is not the folder the editor opened. A relative path resolves
        /// against the opened workspace folder. Needed for a repo where Surtr sources live under a
        /// nested directory alongside unrelated project folders — deriving module paths from the
        /// repo root would fold every intermediate directory name (a C# project folder like
        /// <c>Surtr.Stdlib</c>, illegal as a Surtr identifier because of the dot) into the path and
        /// reject every file underneath it.
        /// </summary>
        public string? ProjectRoot { get; set; }
    }

    /// <summary>One folder of a (possibly multi-root) workspace, as <c>initialize</c> reports it.</summary>
    public sealed class WorkspaceFolder
    {
        public string Uri { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    /// <summary>Parameters of <c>workspace/didChangeWatchedFiles</c>.</summary>
    public sealed class DidChangeWatchedFilesParams
    {
        public List<FileEvent> Changes { get; set; } = new List<FileEvent>();
    }

    /// <summary>One watched file's change, as reported by the client's own file-system watcher.</summary>
    public sealed class FileEvent
    {
        public string Uri { get; set; } = string.Empty;

        /// <summary>1 = created, 2 = changed, 3 = deleted.</summary>
        public int Type { get; set; }
    }

    /// <summary>Parameters of a request or notification about a text document.</summary>
    public class TextDocumentIdentifier
    {
        public string Uri { get; set; } = string.Empty;
    }

    /// <summary>Parameters of <c>textDocument/hover</c> and <c>textDocument/definition</c>.</summary>
    public sealed class TextDocumentPositionParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new TextDocumentIdentifier();

        public Position Position { get; set; }
    }

    /// <summary>The state a <c>didChange</c> notification carries for full synchronization.</summary>
    public sealed class TextDocumentContentChangeEvent
    {
        public string Text { get; set; } = string.Empty;
    }

    public sealed class DidChangeTextDocumentParams
    {
        public VersionedTextDocumentIdentifier TextDocument { get; set; } = new VersionedTextDocumentIdentifier();

        public List<TextDocumentContentChangeEvent> ContentChanges { get; set; } = new List<TextDocumentContentChangeEvent>();
    }

    public sealed class VersionedTextDocumentIdentifier : TextDocumentIdentifier
    {
        public int Version { get; set; }
    }

    /// <summary>Parameters of <c>textDocument/didOpen</c>: the document and its full text.</summary>
    public sealed class DidOpenTextDocumentParams
    {
        public TextDocumentItem TextDocument { get; set; } = new TextDocumentItem();
    }

    public sealed class TextDocumentItem
    {
        public string Uri { get; set; } = string.Empty;

        public string LanguageId { get; set; } = "surtr";

        public int Version { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    /// <summary>Parameters of <c>textDocument/didSave</c>, which carries nothing the server needs.</summary>
    public sealed class DidSaveTextDocumentParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new TextDocumentIdentifier();
    }

    /// <summary>Parameters of <c>textDocument/didClose</c>.</summary>
    public sealed class DidCloseTextDocumentParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new TextDocumentIdentifier();
    }

    /// <summary>Parameters of <c>window/showMessage</c>: a notice the editor shows to the user.</summary>
    public sealed class ShowMessageParams
    {
        /// <summary>1 error, 2 warning, 3 info, 4 log.</summary>
        public int Type { get; set; } = 1;

        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Parameters of <c>textDocument/publishDiagnostics</c>.</summary>
    public sealed class PublishDiagnosticsParams
    {
        public string Uri { get; set; } = string.Empty;

        public List<LspDiagnostic> Diagnostics { get; set; } = new List<LspDiagnostic>();
    }

    /// <summary>Parameters of <c>textDocument/codeAction</c>.</summary>
    public sealed class CodeActionParams
    {
        public TextDocumentIdentifier TextDocument { get; set; } = new TextDocumentIdentifier();

        public Range Range { get; set; }

        public CodeActionContext Context { get; set; } = new CodeActionContext();
    }

    /// <summary>What the client already knows about the requested range — which diagnostics sit in it.</summary>
    public sealed class CodeActionContext
    {
        public List<LspDiagnostic> Diagnostics { get; set; } = new List<LspDiagnostic>();
    }

    /// <summary>The standard code action kinds a client groups its UI by.</summary>
    public static class CodeActionKinds
    {
        public const string QuickFix = "quickfix";
    }

    /// <summary>One offered fix: a title plus the edit that applies it.</summary>
    public sealed class CodeAction
    {
        public string Title { get; set; } = string.Empty;

        public string Kind { get; set; } = CodeActionKinds.QuickFix;

        public WorkspaceEdit? Edit { get; set; }
    }

    /// <summary>A set of text edits, grouped by the document URI each applies to.</summary>
    public sealed class WorkspaceEdit
    {
        public Dictionary<string, List<TextEdit>> Changes { get; set; } = new Dictionary<string, List<TextEdit>>();
    }

    /// <summary>One replacement: the range it replaces (empty for a pure insertion) and the text.</summary>
    public sealed class TextEdit
    {
        public TextEdit(Range range, string newText)
        {
            Range = range;
            NewText = newText;
        }

        public Range Range { get; }

        public string NewText { get; }
    }
}
