using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.AgentControl.Worker;

public static class WorkerProtocol
{
    public const string Version = "hvo-worker-acp/1";
    public const int MaxControlFrameBytes = 1024 * 1024;
    public const int MaxCanonicalPayloadBytes = 1024 * 1024;
    public const int MaxJsonDepth = 64;
    public const int NonceBytes = 32;
    public const int MaxIdentifierLength = 128;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = MaxJsonDepth,
    };

    public static byte[] CanonicalPayloadHash(JsonElement payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false, MaxDepth = MaxJsonDepth }))
        {
            WriteCanonical(writer, payload, 0);
        }
        if (buffer.WrittenCount > MaxCanonicalPayloadBytes)
            throw new WorkerProtocolException("Canonical request payload is too large.");
        return SHA256.HashData(buffer.WrittenSpan);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > MaxJsonDepth) throw new WorkerProtocolException("JSON payload nesting is too deep.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                WriteCanonicalNumber(writer, value);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new WorkerProtocolException("Unsupported JSON value in request payload.");
        }
    }

    private static void WriteCanonicalNumber(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.TryGetInt64(out var signed)) { writer.WriteNumberValue(signed); return; }
        if (value.TryGetUInt64(out var unsigned)) { writer.WriteNumberValue(unsigned); return; }
        if (value.TryGetDecimal(out var decimalValue))
        {
            writer.WriteRawValue(decimalValue.ToString("G29", CultureInfo.InvariantCulture), skipInputValidation: false);
            return;
        }
        if (!value.TryGetDouble(out var doubleValue) || !double.IsFinite(doubleValue))
            throw new WorkerProtocolException("JSON number is outside the supported canonical range.");
        if (doubleValue == 0) doubleValue = 0;
        writer.WriteRawValue(doubleValue.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "e", StringComparison.Ordinal).Replace("E", "e", StringComparison.Ordinal), skipInputValidation: false);
    }

    public static string KeyId(ReadOnlySpan<byte> key) => "sha256:" + Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();

    public static byte[] ParseKey(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var value = input.Trim();
        byte[] key;
        try { key = value.Length == 64 ? Convert.FromHexString(value) : Convert.FromBase64String(value); }
        catch (FormatException exception) { throw new WorkerProtocolException("The bootstrap key encoding is invalid.", exception); }
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new WorkerProtocolException("The bootstrap key must decode to exactly 32 bytes.");
    }

    public static byte[] ParseNonce(string value, string name)
    {
        ValidateIdentifier(value, 64, name, allowBase64Punctuation: true);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException exception) { throw new WorkerProtocolException($"Invalid {name}.", exception); }
        if (bytes.Length == NonceBytes && Convert.ToBase64String(bytes) == value) return bytes;
        CryptographicOperations.ZeroMemory(bytes);
        throw new WorkerProtocolException($"Invalid {name}.");
    }

    public static void ValidateIdentifier(string value, int maximumLength, string name, bool allowBase64Punctuation = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new WorkerProtocolException($"Invalid {name}.");
        foreach (var character in value)
        {
            var valid = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or ':';
            if (allowBase64Punctuation) valid |= character is '+' or '/' or '=';
            if (!valid) throw new WorkerProtocolException($"Invalid {name}.");
        }
    }

    public static string ComputeMac(ReadOnlySpan<byte> key, string label, string role, string controllerId, string workerId, string keyId, string clientNonce, string serverNonce, long issuedUnixMilliseconds)
    {
        var message = string.Join('\n', Version, label, role, controllerId, workerId, keyId, clientNonce, serverNonce, issuedUnixMilliseconds.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));
    }

    public static bool VerifyMac(string expectedBase64, string suppliedBase64)
    {
        byte[]? expected = null;
        byte[]? supplied = null;
        try
        {
            expected = Convert.FromBase64String(expectedBase64);
            supplied = Convert.FromBase64String(suppliedBase64);
            return expected.Length == 32 && supplied.Length == 32 && CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
        catch (FormatException) { return false; }
        finally
        {
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            if (supplied is not null) CryptographicOperations.ZeroMemory(supplied);
        }
    }

    public static async ValueTask<JsonDocument?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (writer.WrittenCount == 0) return null;
                throw new WorkerProtocolException("The peer closed a partial frame.");
            }
            if (one[0] == (byte)'\n') break;
            if (writer.WrittenCount >= MaxControlFrameBytes) throw new WorkerProtocolException("Control frame is too large.");
            writer.Write(one);
        }
        if (writer.WrittenCount == 0) throw new WorkerProtocolException("Empty frames are not allowed.");
        try { return JsonDocument.Parse(writer.WrittenMemory, new JsonDocumentOptions { MaxDepth = MaxJsonDepth }); }
        catch (JsonException exception) { throw new WorkerProtocolException("Malformed JSON frame.", exception); }
    }

    public static async ValueTask WriteFrameAsync(Stream stream, object value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (json.Length > MaxControlFrameBytes) throw new WorkerProtocolException("Outgoing control frame is too large.");
        await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class WorkerProtocolException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class WorkerStoreException(string message, Exception? inner = null) : Exception(message, inner);

public interface IWorkerClock { DateTimeOffset UtcNow { get; } long MonotonicMilliseconds { get; } }
public sealed class SystemWorkerClock : IWorkerClock
{
    private readonly long _origin = Environment.TickCount64;
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long MonotonicMilliseconds => Environment.TickCount64 - _origin;
}

public sealed record WorkerOptions(string ControlDirectory, string WorkerId, string ControllerId, string SocketPath,
    TimeSpan ChallengeLifetime, TimeSpan HeartbeatInterval, TimeSpan LeaseLifetime, int EventLimit = 10_000,
    long EventByteLimit = 64L * 1024 * 1024, int NonceCacheLimit = 4096, int ExpectedBridgeUid = -1)
{
    public static WorkerOptions Production(string controlDirectory, string workerId, string controllerId) => new(
        controlDirectory, workerId, controllerId, Path.Combine(controlDirectory, "bridge.sock"), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), ExpectedBridgeUid: 1101);
}

public sealed record Lease(long Epoch, string ControllerId, string ConnectionNonce, DateTimeOffset ObservedUtc);
public sealed record WorkerStatus(long WorkerGeneration, long ProcessGeneration, string ProcessState, string? LifecycleHandle,
    long? ObservedPid, string? SessionId, string? ActiveRequestId, PendingPermission? PendingPermission, long OwnershipEpoch,
    bool LeaseActive, bool DispatchHeld, string? HoldReason, long FirstRetainedSequence, long LastSequence,
    long AcknowledgedWorkerGeneration, long AcknowledgedSequence);
public sealed record PendingPermission(long ProcessGeneration, string RequestId, string TurnId, string DecisionId,
    string PayloadHash, IReadOnlyList<string> OptionIds, string State, string? Decision);
public sealed record StoredRequest(string RequestId, string PayloadHash, string State, string? OutcomeJson,
    long ProcessGeneration, string? TurnId);
public sealed record StoredCancellation(string CancellationId, string TargetRequestId, string PayloadHash,
    string State, long ProcessGeneration);
public sealed record WorkerEvent(long WorkerGeneration, long Sequence, string Kind, string PayloadJson, int ByteCount);
