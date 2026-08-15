#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Surtr.Compiler.Diagnostics;
using Surtr.LanguageServer.Protocol;
using Surtr.LanguageServer.Workspace;
using Range = Surtr.LanguageServer.Protocol.Range;

namespace Surtr.LanguageServer
{
    /// <summary>
    /// The LSP message loop: reads one JSON-RPC message at a time, answers requests, and rebuilds
    /// the workspace on every change. Single-threaded, exactly as the protocol expects a server to
    /// be — notifications are handled in the order they arrive, so a <c>didOpen</c> always precedes
    /// the first <c>hover</c> against its text.
    /// </summary>
    public sealed class LspServer : IDisposable
    {
        private readonly JsonRpcConnection _connection;
        private Workspace.Workspace? _workspace;
        private bool _shutdownRequested;

        public LspServer(Stream input, Stream output)
        {
            _connection = new JsonRpcConnection(input, output);
        }

        public void Run()
        {
            while (!_shutdownRequested)
            {
                RpcMessage? message = _connection.Read();
                if (message is null)
                    break;

                if (message.IsResponse)
                    continue;

                // One request must never take the server down with it. A handler that throws would
                // otherwise unwind out of the loop and kill the process, which the editor sees only
                // as a server that vanished — it restarts it, the next request kills it again, and
                // the actual fault is never reported anywhere the user can see it.
                try
                {
                    Handle(message);
                }
                catch (Exception ex)
                {
                    Log(ex);

                    if (message.IsRequest)
                        Fail(message, RpcErrorCodes.InternalError, ex.Message);
                }
            }
        }

        private void Handle(RpcMessage message)
        {
            switch (message.Method)
            {
                case "initialize":
                    OnInitialize(message);
                    break;

                case "initialized":
                    break;

                case "shutdown":
                    Reply(message, null);
                    break;

                case "exit":
                    _shutdownRequested = true;
                    break;

                case "textDocument/didOpen":
                    OnDidOpen(message);
                    break;

                case "textDocument/didChange":
                    OnDidChange(message);
                    break;

                case "textDocument/didSave":
                    OnDidSave(message);
                    break;

                case "textDocument/didClose":
                    OnDidClose(message);
                    break;

                case "textDocument/hover":
                    OnHover(message);
                    break;

                case "textDocument/definition":
                    OnDefinition(message);
                    break;

                case "textDocument/completion":
                    OnCompletion(message);
                    break;

                case "textDocument/signatureHelp":
                    OnSignatureHelp(message);
                    break;

                default:
                    if (message.IsRequest)
                        Fail(message, RpcErrorCodes.MethodNotFound, "Method not implemented: " + message.Method);
                    break;
            }
        }

        private void OnInitialize(RpcMessage message)
        {
            var parameters = Params<InitializeParams>(message);
            string root = FirstNonNull(parameters?.RootUri, parameters?.RootPath);
            if (root.Length > 0)
            {
                root = Workspace.Workspace.PathFromUri(root);
                _workspace?.Dispose();
                _workspace = new Workspace.Workspace(root);
                PublishAll(_workspace.Rebuild());
            }
            else
            {
                // No folder in this session — a single file, opened without a workspace. The
                // workspace is created from the first document that opens, whose directory then
                // names its module.
                _workspace?.Dispose();
                _workspace = null;
            }

            var result = new InitializeResult
            {
                Capabilities = new ServerCapabilities
                {
                    TextDocumentSync = new TextDocumentSyncOptions(),
                    CompletionProvider = new CompletionOptions(),
                    SignatureHelpProvider = new SignatureHelpOptions(),
                },
                ServerInfo = new ServerInfo(),
            };

            Reply(message, result);
        }

        private void OnDidOpen(RpcMessage message)
        {
            var parameters = Params<DidOpenTextDocumentParams>(message);
            if (parameters?.TextDocument is null)
                return;

            if (_workspace is null)
            {
                // A document is the first thing the session knows: its directory becomes the
                // workspace root, and that directory's name the root module, so the file itself
                // belongs to a module rather than to none (§2.1).
                string path = Workspace.Workspace.PathFromUri(parameters.TextDocument.Uri);
                string root = Path.GetDirectoryName(path) ?? path;
                string rootModule = Path.GetFileName(root);
                _workspace = new Workspace.Workspace(root, rootModule);
            }

            _workspace.SetOpenDocument(parameters.TextDocument.Uri, parameters.TextDocument.Text);
            PublishAll(_workspace.Rebuild());
        }

        private void OnDidChange(RpcMessage message)
        {
            var parameters = Params<DidChangeTextDocumentParams>(message);
            if (parameters?.TextDocument is null || parameters.ContentChanges.Count == 0)
                return;

            if (_workspace is null)
                return;

            // Full synchronization: the single change carries the whole document.
            string text = parameters.ContentChanges[parameters.ContentChanges.Count - 1].Text;
            _workspace.SetOpenDocument(parameters.TextDocument.Uri, text);
            PublishAll(_workspace.Rebuild());
        }

        private void OnDidSave(RpcMessage message)
        {
            if (_workspace is null)
                return;

            // The open buffer already carries the saved text; rebuilding picks up any file the save
            // touched on disk that is not open.
            PublishAll(_workspace.Rebuild());
        }

        private void OnDidClose(RpcMessage message)
        {
            var parameters = Params<DidCloseTextDocumentParams>(message);
            if (parameters?.TextDocument is null)
                return;

            if (_workspace is null)
                return;

            _workspace.CloseDocument(parameters.TextDocument.Uri);
            PublishAll(_workspace.Rebuild());
        }

        private void OnHover(RpcMessage message)
        {
            var parameters = Params<TextDocumentPositionParams>(message);
            if (parameters?.TextDocument is null || _workspace is null)
            {
                Reply(message, null);
                return;
            }

            string path = Workspace.Workspace.PathFromUri(parameters.TextDocument.Uri);
            string text = _workspace.CurrentText(path);
            var lines = TextLines.Index(text);
            int position = lines.OffsetAt(parameters.Position.Line, parameters.Position.Character);

            ResolvedHit? hit = SymbolResolver.Resolve(_workspace.Snapshot, path, text, position);
            if (hit is null)
            {
                Reply(message, null);
                return;
            }

            var start = lines.PositionAt(hit.AnchorStart);
            var end = lines.PositionAt(hit.AnchorStart + hit.AnchorLength);
            var result = new HoverResult
            {
                Contents = new MarkupContent { Value = hit.Markdown ?? string.Empty },
                Range = new Range(new Position(start.Line, start.Character), new Position(end.Line, end.Character)),
            };

            Reply(message, result);
        }

        private void OnDefinition(RpcMessage message)
        {
            var parameters = Params<TextDocumentPositionParams>(message);
            ResolvedHit? hit = Resolve(parameters);
            if (hit is null || !hit.HasDefinition)
            {
                Reply(message, null);
                return;
            }

            string file = hit.DefinitionFile!;
            string text = _workspace!.CurrentText(file);
            var lines = TextLines.Index(text);
            var range = new Range(
                new Position(lines.PositionAt(hit.DefinitionStart).Line, lines.PositionAt(hit.DefinitionStart).Character),
                new Position(lines.PositionAt(hit.DefinitionStart + hit.DefinitionLength).Line, lines.PositionAt(hit.DefinitionStart + hit.DefinitionLength).Character));

            Reply(message, new Location(Workspace.Workspace.UriFromPath(file), range));
        }

        private ResolvedHit? Resolve(TextDocumentPositionParams? parameters)
        {
            if (parameters?.TextDocument is null || _workspace is null)
                return null;

            string path = Workspace.Workspace.PathFromUri(parameters.TextDocument.Uri);
            string text = _workspace.CurrentText(path);
            var lines = TextLines.Index(text);
            int position = lines.OffsetAt(parameters.Position.Line, parameters.Position.Character);

            return SymbolResolver.Resolve(_workspace.Snapshot, path, text, position);
        }

        private void OnCompletion(RpcMessage message)
        {
            var parameters = Params<TextDocumentPositionParams>(message);
            if (parameters?.TextDocument is null || _workspace is null)
            {
                Reply(message, null);
                return;
            }

            string path = Workspace.Workspace.PathFromUri(parameters.TextDocument.Uri);
            string text = _workspace.CurrentText(path);
            var lines = TextLines.Index(text);
            int position = lines.OffsetAt(parameters.Position.Line, parameters.Position.Character);

            Reply(message, CompletionProvider.Complete(_workspace.Snapshot, path, text, position));
        }

        private void OnSignatureHelp(RpcMessage message)
        {
            var parameters = Params<TextDocumentPositionParams>(message);
            if (parameters?.TextDocument is null || _workspace is null)
            {
                Reply(message, null);
                return;
            }

            string path = Workspace.Workspace.PathFromUri(parameters.TextDocument.Uri);
            string text = _workspace.CurrentText(path);
            var lines = TextLines.Index(text);
            int position = lines.OffsetAt(parameters.Position.Line, parameters.Position.Character);

            Reply(message, CompletionProvider.SignatureHelp(_workspace.Snapshot, path, text, position));
        }

        /// <summary>Publishes diagnostics for every file with some, and clears the ones that went quiet.</summary>
        private void PublishAll(IReadOnlyDictionary<string, IReadOnlyList<SurtrDiagnostic>> grouped)
        {
            if (_workspace is null)
                return;

            // Every rebuild publishes for every source file — an empty list is the way a fix is
            // announced, and it is also what first appears in a workspace that compiles clean.
            var touched = new HashSet<string>(PathComparer);
            foreach (string file in _workspace.FindSourceFiles())
                touched.Add(file);

            foreach (string file in touched)
            {
                string text = _workspace.CurrentText(file);
                var lines = TextLines.Index(text);
                var diagnostics = new List<LspDiagnostic>();

                if (grouped.TryGetValue(file, out var sourceDiagnostics))
                {
                    foreach (SurtrDiagnostic source in sourceDiagnostics)
                        diagnostics.Add(ToLsp(source, lines));
                }

                _connection.Write(RpcMessage.Notification("textDocument/publishDiagnostics",
                    new PublishDiagnosticsParams
                    {
                        Uri = Workspace.Workspace.UriFromPath(file),
                        Diagnostics = diagnostics,
                    }));
            }

            // A diagnostic that names something which is not a source file belongs to the workspace
            // as a whole — Rebuild attributes a failed compilation to the root directory — so no
            // document publishes it and it would be lost. That is the case where every feature goes
            // quiet at once, which is exactly when the reason has to reach the user.
            var workspaceFailures = new List<string>();
            foreach (var pair in grouped)
            {
                if (touched.Contains(pair.Key))
                    continue;

                foreach (SurtrDiagnostic diagnostic in pair.Value)
                    workspaceFailures.Add(diagnostic.Message);
            }

            if (workspaceFailures.Count > 0)
            {
                _connection.Write(RpcMessage.Notification("window/showMessage",
                    new ShowMessageParams
                    {
                        Type = 1,
                        Message = "Surtr: " + string.Join(" | ", workspaceFailures),
                    }));
            }
        }

        private static LspDiagnostic ToLsp(SurtrDiagnostic source, TextLines lines)
        {
            int severity = source.Severity switch
            {
                SurtrDiagnosticSeverity.Warning => 2,
                SurtrDiagnosticSeverity.Error => 1,
                _ => 3,
            };

            var range = lines.RangeOf(source.Span);
            return new LspDiagnostic(
                new Range(new Position(range.Start.Line, range.Start.Character), new Position(range.End.Line, range.End.Character)),
                source.Message,
                severity,
                "surtr",
                source.Id);
        }

        private static T? Params<T>(RpcMessage message)
            => message.Parameters.HasValue
                ? message.Parameters.Value.Deserialize<T>(RpcJson.Options)
                : default;

        private static string FirstNonNull(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrEmpty(value))
                    return value;
            }

            return string.Empty;
        }

        /// <summary>
        /// Records a fault that a request survived. Nothing may go to the console — that is the
        /// protocol's own channel — so the one place left is a file beside the server's crash log.
        /// </summary>
        private static void Log(Exception exception)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "surtr-lsp.log"),
                    exception + Environment.NewLine);
            }
            catch (IOException)
            {
                // Nowhere to write and nothing to report it on; the request already failed cleanly.
            }
        }

        private void Reply(RpcMessage request, object? result)
            => _connection.Write(RpcMessage.Response(request.Id!.Value, result));

        private void Fail(RpcMessage request, int code, string message)
            => _connection.Write(RpcMessage.Response(request.Id!.Value, null, new JsonRpcError(code, message)));

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public void Dispose() => _workspace?.Dispose();
    }
}
