using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.AgentControl.Worker;

/// <summary>Shared, storage-independent worker bridge framing, canonical hashing and authentication primitives.</summary>
public static class WorkerProtocol
{
    public const string Version = "hvo-worker-acp/2";
    public const int MaxControlFrameBytes = 1024 * 1024;
    public const int MaxAcpFrameBytes = 8 * 1024 * 1024;
    public const int MaxCanonicalPayloadBytes = 1024 * 1024;
    public const int MaxControlJsonDepth = 64;
    public const int MaxAcpJsonDepth = 128;
    public const int MaxJsonDepth = MaxControlJsonDepth;
    public const int NonceBytes = 32;
    public const int MaxIdentifierLength = 128;
    public const string ControllerRole = "controller";
    public const string ViewerRole = "viewer";
    public const int MaxViewerInputBytes = 16 * 1024;
    public const int MaxViewerOutputBytes = 64 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, MaxDepth = MaxControlJsonDepth };

    /// <summary>
    /// The only option IDs the host will ever select to reject a permission, in
    /// selection-priority order.
    /// </summary>
    /// <remarks>
    /// Pinned OpenCode 1.18.30 can emit generic IDs (<c>once</c>, <c>always</c>,
    /// <c>reject</c>) instead of the older suffixed IDs. An allow and a reject
    /// option can therefore share the same word bound, so matching substrings is
    /// unsafe. This is the exact, ordinal reject-semantic allowlist; nothing else
    /// is ever selected.
    /// </remarks>
    public static readonly IReadOnlyList<string> RejectPermissionOptionIds = ["reject_once", "reject", "reject_always"];

    private static readonly IReadOnlyList<string> OneShotRejectPermissionOptionIds = ["reject_once", "reject"];

    /// <summary>
    /// Selects a reject option from the exact offered option IDs by ordinal
    /// equality, preferring one-shot <c>reject_once</c>, then generic
    /// <c>reject</c>, then <c>reject_always</c>. Returns <see langword="false"/>
    /// and a null selection when no offered ID is on the allowlist; <c>once</c>,
    /// <c>always</c>, <c>allow</c> and any unknown or malicious name are never
    /// selected.
    /// </summary>
    public static bool TrySelectRejectOption(IEnumerable<string>? optionIds, out string? optionId) =>
        TrySelectOption(optionIds, RejectPermissionOptionIds, out optionId);

    /// <summary>
    /// Selects a rejection from complete ACP permission option objects. Only the
    /// fixed reject IDs can be returned. A missing kind preserves ID-only
    /// compatibility; a present kind must be the exact compatible reject kind.
    /// Allow, unknown, malformed, and contradictory kinds make an option
    /// ineligible. The one-shot selector excludes both persistent IDs and
    /// persistent kinds.
    /// </summary>
    public static string? SelectRejectOptionFromPermissionFrame(JsonElement parameters, bool oneShotOnly) =>
        SelectSafeRejectOptionsFromPermissionFrame(parameters, oneShotOnly).FirstOrDefault();

    /// <summary>
    /// Returns every eligible fixed reject ID in canonical selection-priority
    /// order. The returned values are safe to persist as authorization evidence;
    /// arbitrary vendor IDs and raw option objects are never retained.
    /// </summary>
    /// <remarks>
    /// Options are grouped by exact ordinal <c>optionId</c>. A repeated ID is a
    /// protocol violation and vetoes that ID entirely, even when every occurrence
    /// was individually eligible: only IDs offered exactly once and eligible are
    /// returned. A duplicate can therefore never widen the reject allowlist, and
    /// the result is empty rather than partially trusting a contradictory frame.
    /// </remarks>
    public static IReadOnlyList<string> SelectSafeRejectOptionsFromPermissionFrame(JsonElement parameters, bool oneShotOnly)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object || !TryReadBoundedOptionId(option, out var optionId)) continue;
            seen[optionId!] = seen.TryGetValue(optionId!, out var count) ? count + 1 : 1;
            var kind = ReadPermissionOptionKind(option);
            if (IsSafeRejectOption(optionId!, kind, oneShotOnly)) eligible.Add(optionId!);
        }

        var candidates = oneShotOnly ? OneShotRejectPermissionOptionIds : RejectPermissionOptionIds;
        return candidates.Where(id => eligible.Contains(id) && seen[id] == 1).ToArray();
    }

    private static bool IsSafeRejectOption(string optionId, PermissionOptionKind kind, bool oneShotOnly)
    {
        if (oneShotOnly && (optionId == "reject_always" || kind == PermissionOptionKind.RejectAlways)) return false;
        return optionId switch
        {
            "reject_once" => kind is PermissionOptionKind.Missing or PermissionOptionKind.RejectOnce,
            "reject" => kind is PermissionOptionKind.Missing or PermissionOptionKind.RejectOnce or PermissionOptionKind.RejectAlways,
            "reject_always" => !oneShotOnly && kind is PermissionOptionKind.Missing or PermissionOptionKind.RejectAlways,
            _ => false,
        };
    }

    private static bool TryReadBoundedOptionId(JsonElement option, out string? optionId)
    {
        optionId = null;
        if (!option.TryGetProperty("optionId", out var id)
            || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { Length: > 0 and <= 512 } value
            || value.Any(char.IsControl))
        {
            return false;
        }

        optionId = value;
        return true;
    }

    private static PermissionOptionKind ReadPermissionOptionKind(JsonElement option)
    {
        if (!option.TryGetProperty("kind", out var kind)) return PermissionOptionKind.Missing;
        if (kind.ValueKind != JsonValueKind.String) return PermissionOptionKind.Ineligible;
        return kind.GetString() switch
        {
            "reject_once" => PermissionOptionKind.RejectOnce,
            "reject_always" => PermissionOptionKind.RejectAlways,
            _ => PermissionOptionKind.Ineligible,
        };
    }

    private enum PermissionOptionKind
    {
        Missing,
        RejectOnce,
        RejectAlways,
        Ineligible,
    }

    private static bool TrySelectOption(IEnumerable<string>? optionIds, IReadOnlyList<string> candidates, out string? optionId)
    {
        optionId = null;
        if (optionIds is null) return false;
        var offered = optionIds as ICollection<string> ?? optionIds.ToList();
        foreach (var candidate in candidates)
        {
            if (offered.Contains(candidate, StringComparer.Ordinal))
            {
                optionId = candidate;
                return true;
            }
        }

        return false;
    }

    public static byte[] CanonicalPayloadHash(JsonElement payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = MaxControlJsonDepth })) WriteCanonical(writer, payload, 0);
        if (buffer.WrittenCount > MaxCanonicalPayloadBytes) throw new WorkerProtocolException("Canonical request payload is too large.");
        return SHA256.HashData(buffer.WrittenSpan);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > MaxControlJsonDepth) throw new WorkerProtocolException("JSON payload nesting is too deep.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value, depth + 1); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array: writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item, depth + 1); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: WriteCanonicalNumber(writer, value); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new WorkerProtocolException("Unsupported JSON value in request payload.");
        }
    }

    private static void WriteCanonicalNumber(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.TryGetInt64(out var signed)) { writer.WriteNumberValue(signed); return; }
        if (value.TryGetUInt64(out var unsigned)) { writer.WriteNumberValue(unsigned); return; }
        if (value.TryGetDecimal(out var decimalValue)) { writer.WriteRawValue(decimalValue.ToString("G29", CultureInfo.InvariantCulture)); return; }
        if (!value.TryGetDouble(out var doubleValue) || !double.IsFinite(doubleValue)) throw new WorkerProtocolException("JSON number is outside the supported canonical range.");
        if (doubleValue == 0) doubleValue = 0;
        writer.WriteRawValue(doubleValue.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "e", StringComparison.Ordinal).Replace("E", "e", StringComparison.Ordinal));
    }

    public static string KeyId(ReadOnlySpan<byte> key) => "sha256:" + Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
    public static byte[] ParseKey(string input)
    {
        ArgumentNullException.ThrowIfNull(input); byte[] key;
        try { var value = input.Trim(); key = value.Length == 64 ? Convert.FromHexString(value) : Convert.FromBase64String(value); }
        catch (FormatException exception) { throw new WorkerProtocolException("The bootstrap key encoding is invalid.", exception); }
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key); throw new WorkerProtocolException("The bootstrap key must decode to exactly 32 bytes.");
    }

    public static byte[] ParseNonce(string value, string name)
    {
        ValidateIdentifier(value, 64, name, true); byte[] bytes;
        try { bytes = Convert.FromBase64String(value); } catch (FormatException exception) { throw new WorkerProtocolException($"Invalid {name}.", exception); }
        if (bytes.Length == NonceBytes && Convert.ToBase64String(bytes) == value) return bytes;
        CryptographicOperations.ZeroMemory(bytes); throw new WorkerProtocolException($"Invalid {name}.");
    }

    public static void ValidateIdentifier(string value, int maximumLength, string name, bool allowBase64Punctuation = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) throw new WorkerProtocolException($"Invalid {name}.");
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

    public static string ComputeViewerMac(ReadOnlySpan<byte> key, string label, string controllerId, string workerId, string keyId, string clientNonce, string serverNonce, long issuedUnixMilliseconds, long ownershipEpoch, string connectionNonce, string sessionId)
    {
        var message = string.Join('\n', Version, label, ViewerRole, controllerId, workerId, keyId, clientNonce, serverNonce, issuedUnixMilliseconds.ToString(CultureInfo.InvariantCulture), ownershipEpoch.ToString(CultureInfo.InvariantCulture), connectionNonce, sessionId);
        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));
    }

    public static bool VerifyMac(string expectedBase64, string suppliedBase64)
    {
        byte[]? expected = null; byte[]? supplied = null;
        try { expected = Convert.FromBase64String(expectedBase64); supplied = Convert.FromBase64String(suppliedBase64); return expected.Length == 32 && supplied.Length == 32 && CryptographicOperations.FixedTimeEquals(expected, supplied); }
        catch (FormatException) { return false; }
        finally { if (expected is not null) CryptographicOperations.ZeroMemory(expected); if (supplied is not null) CryptographicOperations.ZeroMemory(supplied); }
    }

    public static ValueTask<JsonDocument?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken) => new NdjsonFrameReader(stream, MaxControlFrameBytes, MaxControlJsonDepth, false, 1).ReadAsync(cancellationToken);
    public static NdjsonFrameReader CreateControlReader(Stream stream) => new(stream, MaxControlFrameBytes, MaxControlJsonDepth, false, 16 * 1024);
    public static NdjsonFrameReader CreateAcpReader(Stream stream) => new(stream, MaxAcpFrameBytes, MaxAcpJsonDepth, true, 64 * 1024);
    public static ValueTask WriteFrameAsync(Stream stream, object value, CancellationToken cancellationToken) => WriteFrameCoreAsync(stream, value, MaxControlFrameBytes, JsonOptions, cancellationToken);
    public static ValueTask WriteAcpFrameAsync(Stream stream, object value, CancellationToken cancellationToken) => WriteFrameCoreAsync(stream, value, MaxAcpFrameBytes, new JsonSerializerOptions(JsonOptions) { MaxDepth = MaxAcpJsonDepth }, cancellationToken);

    private static async ValueTask WriteFrameCoreAsync(Stream stream, object value, int maximumBytes, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, options); if (json.Length >= maximumBytes) throw new WorkerProtocolException("Outgoing frame is too large.");
        var frame = GC.AllocateUninitializedArray<byte>(json.Length + 1); json.CopyTo(frame, 0); frame[^1] = (byte)'\n';
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false); await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class NdjsonFrameReader
{
    private readonly Stream _stream; private readonly int _maximumBytes; private readonly int _maximumDepth; private readonly bool _acp; private byte[] _buffer; private int _start; private int _end;
    public NdjsonFrameReader(Stream stream, int maximumBytes, int maximumDepth, bool acp, int readBufferBytes) { _stream = stream; _maximumBytes = maximumBytes; _maximumDepth = maximumDepth; _acp = acp; _buffer = new byte[Math.Min(maximumBytes + 1, readBufferBytes)]; }
    public async ValueTask<JsonDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)'\n');
            if (newline >= 0) { var length = newline; if (length == 0) throw Failure("Empty frames are not allowed."); try { var document = JsonDocument.Parse(_buffer.AsMemory(_start, length), new JsonDocumentOptions { MaxDepth = _maximumDepth }); _start += length + 1; return document; } catch (JsonException exception) { throw Failure("Malformed JSON frame.", exception); } }
            if (_end - _start >= _maximumBytes) throw Failure("Frame is too large."); CompactOrGrow(); var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0) { if (_end == _start) return null; throw Failure("The peer closed a partial frame."); }
            _end += read;
        }
    }
    private void CompactOrGrow() { if (_start > 0) { _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer); _end -= _start; _start = 0; } if (_end < _buffer.Length) return; var next = Math.Min(_maximumBytes + 1, checked(_buffer.Length * 2)); if (next <= _buffer.Length) throw Failure("Frame is too large."); Array.Resize(ref _buffer, next); }
    private Exception Failure(string message, Exception? inner = null) => _acp ? new AcpProtocolException(message, inner) : new WorkerProtocolException(message, inner);
}

public class WorkerProtocolException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class WorkerOperationUncertainException(string message, Exception? inner = null) : WorkerProtocolException(message, inner);
public sealed class WorkerReplayLossException(string message, Exception? inner = null) : WorkerProtocolException(message, inner);
public sealed class AcpProtocolException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class WorkerStoreException(string message, Exception? inner = null) : Exception(message, inner);
