using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.AgentControl.Runtime;

public class AcpProtocolException : Exception
{
    public AcpProtocolException(string message)
        : base(message)
    {
    }

    public AcpProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AcpFrameTooLargeException : AcpProtocolException
{
    public AcpFrameTooLargeException(int maxBytes, int observedBytes)
        : base($"ACP frame exceeded the {maxBytes}-byte limit (observed at least {observedBytes} bytes).")
    {
        MaxBytes = maxBytes;
        ObservedBytes = observedBytes;
    }

    public int MaxBytes { get; }

    public int ObservedBytes { get; }
}

public sealed class AcpRemoteException : AcpProtocolException
{
    public AcpRemoteException(long code, string message, JsonElement? errorData)
        : base($"ACP request failed ({code}): {message}")
    {
        Code = code;
        ErrorData = errorData;
    }

    public long Code { get; }

    public JsonElement? ErrorData { get; }
}

public sealed class AcpSessionClosedException : AcpProtocolException
{
    public AcpSessionClosedException(string message)
        : base(message)
    {
    }

    public AcpSessionClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One newline-delimited JSON-RPC message. Values are cloned out of the parsed
/// document so the envelope is independent of the reader's buffer lifetime.
/// </summary>
public sealed class AcpEnvelope
{
    private AcpEnvelope(
        JsonElement? id,
        string? idKey,
        string? method,
        JsonElement? parameters,
        JsonElement? result,
        JsonElement? error)
    {
        Id = id;
        IdKey = idKey;
        Method = method;
        Params = parameters;
        Result = result;
        Error = error;
    }

    public JsonElement? Id { get; }

    public string? IdKey { get; }

    public string? Method { get; }

    public JsonElement? Params { get; }

    public JsonElement? Result { get; }

    public JsonElement? Error { get; }

    public bool IsRequest => Method is not null && Id is not null;

    public bool IsNotification => Method is not null && Id is null;

    public bool IsResponse => Method is null && Id is not null && (Result is not null || Error is not null);

    public static AcpEnvelope Parse(ReadOnlySpan<byte> frame)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame.ToArray());
        }
        catch (JsonException exception)
        {
            throw new AcpProtocolException("ACP frame was not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AcpProtocolException("ACP frame must be a JSON object.");
            }

            var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : (JsonElement?)null;
            var method = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString()
                : null;
            var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : (JsonElement?)null;
            var result = root.TryGetProperty("result", out var resultElement) ? resultElement.Clone() : (JsonElement?)null;
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement.Clone() : (JsonElement?)null;

            return new AcpEnvelope(id, IdKeyOf(id), method, parameters, result, error);
        }
    }

    internal static string? IdKeyOf(JsonElement? id)
    {
        if (id is not { } value)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => "s:" + value.GetString(),
            JsonValueKind.Number => "n:" + value.GetRawText(),
            _ => "r:" + value.GetRawText(),
        };
    }
}

/// <summary>
/// Newline-delimited JSON framing with a hard frame-size bound. The reader
/// buffers leftover bytes internally so a single stream read may deliver
/// multiple frames without loss.
/// </summary>
public sealed class AcpJsonCodec
{
    public const int DefaultMaxFrameBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _input;
    private readonly int _maxFrameBytes;
    private byte[] _buffer;
    private int _start;
    private int _end;

    public AcpJsonCodec(Stream input, int maxFrameBytes = DefaultMaxFrameBytes)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        if (maxFrameBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
        }

        _maxFrameBytes = maxFrameBytes;
        _buffer = new byte[16 * 1024];
    }

    public int MaxFrameBytes => _maxFrameBytes;

    public async ValueTask<AcpEnvelope?> ReadAsync(CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        return frame is null ? null : AcpEnvelope.Parse(frame);
    }

    public async ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            for (var i = _start; i < _end; i++)
            {
                if (_buffer[i] != (byte)'\n')
                {
                    continue;
                }

                if (i - _start > _maxFrameBytes)
                {
                    throw new AcpFrameTooLargeException(_maxFrameBytes, i - _start);
                }

                var frame = Copy(_start, i - _start);
                _start = i + 1;
                return TrimCarriageReturn(frame);
            }

            var pending = _end - _start;
            if (pending > _maxFrameBytes)
            {
                throw new AcpFrameTooLargeException(_maxFrameBytes, pending);
            }

            if (_start > 0)
            {
                Array.Copy(_buffer, _start, _buffer, 0, pending);
                _start = 0;
                _end = pending;
            }

            if (_end == _buffer.Length)
            {
                var next = Math.Min((long)_buffer.Length * 2, (long)_maxFrameBytes + 1);
                if (next <= _buffer.Length)
                {
                    throw new AcpFrameTooLargeException(_maxFrameBytes, _end);
                }

                Array.Resize(ref _buffer, (int)next);
            }

            var read = await _input.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (_end == _start)
                {
                    return null;
                }

                var frame = Copy(_start, _end - _start);
                _start = _end = 0;
                return TrimCarriageReturn(frame);
            }

            _end += read;
        }
    }

    public static byte[] Encode(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload is IDictionary<string, object?> dictionary)
        {
            var filtered = new Dictionary<string, object?>(dictionary.Count, StringComparer.Ordinal);
            foreach (var (key, value) in dictionary)
            {
                // Omit absent params, but preserve an explicit result:null (a
                // valid JSON-RPC response) and any other null-bearing member.
                if (value is null && string.Equals(key, "params", StringComparison.Ordinal))
                {
                    continue;
                }

                filtered[key] = value;
            }

            payload = filtered;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        var frame = new byte[json.Length + 1];
        Array.Copy(json, frame, json.Length);
        frame[^1] = (byte)'\n';
        return frame;
    }

    private byte[] Copy(int offset, int length)
    {
        var frame = new byte[length];
        Array.Copy(_buffer, offset, frame, 0, length);
        return frame;
    }

    private static byte[] TrimCarriageReturn(byte[] frame)
    {
        if (frame.Length > 0 && frame[^1] == (byte)'\r')
        {
            Array.Resize(ref frame, frame.Length - 1);
        }

        return frame;
    }
}
