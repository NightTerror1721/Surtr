#nullable enable

using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Surtr.LanguageServer.Protocol
{
    /// <summary>
    /// A single JSON-RPC message as it travels the wire: a request, a response, or a notification.
    /// Kept untyped here — routing and serialization happen once, in <see cref="JsonRpcConnection"/>.
    /// </summary>
    public sealed class RpcMessage
    {
        private RpcMessage(JsonElement? id, string method, JsonElement? parameters, JsonElement? result, JsonRpcError? error)
        {
            Id = id;
            Method = method;
            Parameters = parameters;
            Result = result;
            Error = error;
        }

        /// <summary>The request's id, present on a request and on the response that answers it.</summary>
        public JsonElement? Id { get; }

        /// <summary>The method name, present on a request or notification.</summary>
        public string Method { get; }

        /// <summary>The request's or notification's parameters.</summary>
        public JsonElement? Parameters { get; }

        /// <summary>A response's result, when the request succeeded.</summary>
        public JsonElement? Result { get; }

        /// <summary>A response's error, when the request failed.</summary>
        public JsonRpcError? Error { get; }

        /// <summary>Whether this message has an id, and is therefore a request or a response rather than a notification.</summary>
        public bool IsRequest => Id.HasValue && Method.Length > 0;

        /// <summary>Whether this message answers a request.</summary>
        public bool IsResponse => Id.HasValue && Method.Length == 0;

        /// <summary>Parses one message from JSON.</summary>
        public static RpcMessage Parse(JsonElement element)
        {
            // Every element kept is cloned: the caller may hand the message on after the JsonDocument
            // it came from is disposed, and a JsonElement would otherwise dangle into freed memory.
            JsonElement? id = element.TryGetProperty("id", out var idValue) ? idValue.Clone() : null;
            string method = element.TryGetProperty("method", out var methodValue)
                ? methodValue.ValueKind == JsonValueKind.String ? methodValue.GetString() ?? string.Empty : string.Empty
                : string.Empty;

            JsonElement? parameters = element.TryGetProperty("params", out var paramValue) ? paramValue.Clone() : null;
            JsonElement? result = element.TryGetProperty("result", out var resultValue) ? resultValue.Clone() : null;

            JsonRpcError? error = null;
            if (element.TryGetProperty("error", out var errorValue) && errorValue.ValueKind == JsonValueKind.Object)
            {
                int code = 0;
                string message = string.Empty;
                if (errorValue.TryGetProperty("code", out var codeValue))
                    code = codeValue.ValueKind == JsonValueKind.Number ? codeValue.GetInt32() : 0;
                if (errorValue.TryGetProperty("message", out var messageValue))
                    message = messageValue.ValueKind == JsonValueKind.String ? messageValue.GetString() ?? string.Empty : string.Empty;
                error = new JsonRpcError(code, message);
            }

            return new RpcMessage(id, method, parameters, result, error);
        }

        /// <summary>Builds a request.</summary>
        public static RpcMessage Request(JsonElement id, string method, object? parameters)
            => new RpcMessage(id, method, JsonValue(parameters), null, null);

        /// <summary>Builds a notification.</summary>
        public static RpcMessage Notification(string method, object? parameters)
            => new RpcMessage(null, method, JsonValue(parameters), null, null);

        /// <summary>Builds a response, either to a request that succeeded or one that failed.</summary>
        public static RpcMessage Response(JsonElement id, object? result, JsonRpcError? error = null)
            => new RpcMessage(id, string.Empty, null, error is null ? JsonValue(result) : null, error);

        private static JsonElement? JsonValue(object? value)
        {
            if (value is null)
                return null;

            using var document = JsonSerializer.SerializeToDocument(value, RpcJson.Options);
            return document.RootElement.Clone();
        }
    }

    /// <summary>A JSON-RPC error, as defined by the protocol.</summary>
    public sealed class JsonRpcError
    {
        public JsonRpcError(int code, string message)
        {
            Code = code;
            Message = message;
        }

        public int Code { get; }

        public string Message { get; }
    }

    /// <summary>
    /// Reads and writes JSON-RPC messages framed with the LSP's <c>Content-Length</c> header over a
    /// byte stream. Reading is a blocking loop on the standard input; writing goes to standard
    /// output. One thread does both, in order, which is exactly the single-threaded shape a language
    /// server is supposed to have.
    /// </summary>
    public sealed class JsonRpcConnection
    {
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly JsonSerializerOptions _options;

        /// <summary>
        /// Scratch space for one header block. It grows rather than overflowing: a header block is
        /// normally under a hundred bytes, but a fixed buffer turns any unexpectedly long one into a
        /// fatal error, and one malformed block would then take the whole server down with it.
        /// </summary>
        private byte[] _headerBuffer = new byte[256];

        public JsonRpcConnection(Stream input, Stream output)
        {
            _input = input;
            _output = output;
            _options = RpcJson.Options;
        }

        /// <summary>Reads the next message, or <see langword="null"/> when the stream ends.</summary>
        public RpcMessage? Read()
        {
            string? headers = ReadHeaders();
            if (headers is null)
                return null;

            int contentLength = -1;
            foreach (string line in headers.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    string value = trimmed.Substring("Content-Length:".Length).Trim();

                    // A failed parse must leave the length negative rather than zero: reading a
                    // zero-length body would consume none of a real one, and every message after it
                    // would then be read starting in the middle of this one's JSON.
                    if (!int.TryParse(value, out contentLength))
                        contentLength = -1;
                }
            }

            if (contentLength < 0)
                return null;

            byte[] body = new byte[contentLength];
            ReadExact(body, 0, contentLength);

            using var document = JsonDocument.Parse(body);
            return RpcMessage.Parse(document.RootElement);
        }

        /// <summary>Writes a message to the output stream.</summary>
        public void Write(RpcMessage message)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new MessageShape(message), _options);

            // SerializeToUtf8Bytes produces UTF-8; the content length is the byte count.
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            _output.Write(header, 0, header.Length);
            _output.Write(body, 0, body.Length);
            _output.Flush();
        }

        private string? ReadHeaders()
        {
            int index = 0;
            while (true)
            {
                int read = _input.ReadByte();
                if (read < 0)
                    return index == 0 ? null : throw new EndOfStreamException("Stream ended mid-header.");

                _headerBuffer[index++] = (byte)read;

                if (index >= 4
                    && _headerBuffer[index - 4] == (byte)'\r'
                    && _headerBuffer[index - 3] == (byte)'\n'
                    && _headerBuffer[index - 2] == (byte)'\r'
                    && _headerBuffer[index - 1] == (byte)'\n')
                {
                    return Encoding.ASCII.GetString(_headerBuffer, 0, index);
                }

                if (index == _headerBuffer.Length)
                    Array.Resize(ref _headerBuffer, _headerBuffer.Length * 2);
            }
        }

        private void ReadExact(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = _input.Read(buffer, offset, count);
                if (read <= 0)
                    throw new EndOfStreamException("Stream ended mid-message.");
                offset += read;
                count -= read;
            }
        }

        /// <summary>The shape messages serialize to: only the fields that are present are written.</summary>
        private sealed class MessageShape
        {
            public MessageShape(RpcMessage message)
            {
                if (message.Id.HasValue)
                    Id = message.Id.Value;

                if (message.Method.Length > 0)
                    Method = message.Method;

                if (message.Result.HasValue)
                    Result = message.Result.Value;

                if (message.Error is not null)
                    Error = message.Error;

                if (message.Parameters.HasValue)
                    Params = message.Parameters.Value;

                // A successful response must carry a result, even when it is null; only a response
                // with an error is allowed to omit it. JSON-RPC's `result: null` is a value.
                if (message.Error is null && message.Id.HasValue && message.Method.Length == 0 && !message.Result.HasValue)
                    Result = JsonValueNull;
            }

            private static readonly JsonElement JsonValueNull = JsonSerializer.SerializeToElement((object?)null);

            public string? Jsonrpc { get; } = "2.0";

            public JsonElement? Id { get; }

            public string? Method { get; }

            public JsonElement? Params { get; }

            public JsonElement? Result { get; }

            public JsonRpcError? Error { get; }
        }
    }
}
