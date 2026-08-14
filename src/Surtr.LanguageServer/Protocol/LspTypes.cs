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

    /// <summary>Parameters of <c>textDocument/publishDiagnostics</c>.</summary>
    public sealed class PublishDiagnosticsParams
    {
        public string Uri { get; set; } = string.Empty;

        public List<LspDiagnostic> Diagnostics { get; set; } = new List<LspDiagnostic>();
    }
}
