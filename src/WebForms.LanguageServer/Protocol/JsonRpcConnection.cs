using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WebForms.LanguageServer.Protocol;

/// <summary>An incoming JSON-RPC message: a request (has an id) or a notification.</summary>
public sealed class IncomingMessage(JsonDocument document) : IDisposable
{
    private JsonElement Root => document.RootElement;

    public string? Method => Root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

    public JsonElement? Id => Root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String ? id : null;

    public JsonElement? Params => Root.TryGetProperty("params", out var p) && p.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? p : null;

    public bool IsRequest => Id is not null && Method is not null;

    /// <summary>A response to a request the server sent (no method).</summary>
    public bool IsResponse => Method is null;

    public void Dispose() => document.Dispose();
}

/// <summary>
/// JSON-RPC 2.0 over a byte stream with LSP base-protocol framing (<c>Content-Length</c> headers). Reads on one
/// thread; writes are serialized with a lock so that responses and notifications never interleave.
/// </summary>
public sealed class JsonRpcConnection(Stream input, Stream output)
{
    private readonly Lock _writeLock = new();
    private readonly byte[] _one = new byte[1];

    /// <summary>Reads the next message, or returns null at end of input.</summary>
    /// <exception cref="InvalidDataException">The framing or the JSON is invalid.</exception>
    public async Task<IncomingMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        int contentLength = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                contentLength = n;
            }
        }

        if (contentLength < 0)
        {
            throw new InvalidDataException("Message without a Content-Length header.");
        }

        var body = new byte[contentLength];
        await input.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        try
        {
            return new IncomingMessage(JsonDocument.Parse(body));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid JSON: " + ex.Message, ex);
        }
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (true)
        {
            int read = await input.ReadAsync(_one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : throw new EndOfStreamException("End of input inside a message header.");
            }

            if (_one[0] == '\n')
            {
                int len = bytes.Count > 0 && bytes[^1] == '\r' ? bytes.Count - 1 : bytes.Count;
                return Encoding.ASCII.GetString([.. bytes.Take(len)]);
            }

            bytes.Add(_one[0]);
        }
    }

    public void SendResult<T>(JsonElement id, T result, JsonTypeInfo<T> typeInfo) => Send(w =>
    {
        w.WritePropertyName("id");
        id.WriteTo(w);
        w.WritePropertyName("result");
        JsonSerializer.Serialize(w, result, typeInfo);
    });

    public void SendNullResult(JsonElement id) => Send(w =>
    {
        w.WritePropertyName("id");
        id.WriteTo(w);
        w.WriteNull("result");
    });

    public void SendError(JsonElement? id, int code, string message) => Send(w =>
    {
        w.WritePropertyName("id");
        if (id is { } i)
        {
            i.WriteTo(w);
        }
        else
        {
            w.WriteNullValue();
        }

        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    public void SendNotification<T>(string method, T parameters, JsonTypeInfo<T> typeInfo) => Send(w =>
    {
        w.WriteString("method", method);
        w.WritePropertyName("params");
        JsonSerializer.Serialize(w, parameters, typeInfo);
    });

    /// <summary>A request to the client (ids <c>webforms-1</c>, <c>webforms-2</c>, …); its response is read and ignored.</summary>
    public void SendRequest<T>(string method, T parameters, JsonTypeInfo<T> typeInfo)
    {
        string id = "webforms-" + Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
        Send(w =>
        {
            w.WriteString("id", id);
            w.WriteString("method", method);
            w.WritePropertyName("params");
            JsonSerializer.Serialize(w, parameters, typeInfo);
        });
    }

    private int _nextRequestId;

    private void Send(Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            writeBody(w);
            w.WriteEndObject();
        }

        var header = Encoding.ASCII.GetBytes($"Content-Length: {buffer.WrittenCount}\r\n\r\n");
        lock (_writeLock)
        {
            output.Write(header);
            output.Write(buffer.WrittenSpan);
            output.Flush();
        }
    }
}
