using System.Security.Cryptography;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Worker;

namespace HVO.AgentControl.RemoteWorker;

public sealed class RemoteTerminalSession : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly string _sessionId;
    private RemoteTerminalSession(Stream stream, string sessionId) { _stream = stream; _sessionId = sessionId; }

    public static async Task<RemoteTerminalSession> ConnectAsync(IRemoteTerminalConnector connector, WorkerEnrollmentRecord enrollment, WorkerConnectionLease owner, string sessionId, TimeSpan freshness, CancellationToken token)
    {
        var stream = await connector.ConnectViewerAsync(enrollment.WorkerId, token).ConfigureAwait(false);
        var key = ControllerPrivateFile.ReadExact(enrollment.KeyFilePath, ControllerPrivateFile.EffectiveUid, ControllerFileModes.Private0600, 32);
        try
        {
            var clientBytes = RandomNumberGenerator.GetBytes(WorkerProtocol.NonceBytes); string clientNonce;
            try { clientNonce = Convert.ToBase64String(clientBytes); } finally { CryptographicOperations.ZeroMemory(clientBytes); }
            var keyId = WorkerProtocol.KeyId(key);
            await WorkerProtocol.WriteFrameAsync(stream, new { type = "hello", version = WorkerProtocol.Version, role = WorkerProtocol.ViewerRole, controllerId = enrollment.ControllerId, workerId = enrollment.WorkerId, keyId, clientNonce, ownershipEpoch = owner.Ownership.Epoch, connectionNonce = owner.Ownership.ConnectionNonce, sessionId }, token).ConfigureAwait(false);
            using var challenge = await WorkerProtocol.ReadFrameAsync(stream, token).ConfigureAwait(false) ?? throw new WorkerProtocolException("Viewer authentication closed.");
            var root = challenge.RootElement;
            RequireExactFields(root, "type", "version", "role", "controllerId", "workerId", "keyId", "clientNonce", "serverNonce", "issuedUnixMilliseconds", "mac");
            var serverNonce = Required(root, "serverNonce"); using var parsedNonce = new Zero(WorkerProtocol.ParseNonce(serverNonce, "server nonce"));
            if (!root.TryGetProperty("issuedUnixMilliseconds", out var issuedElement) || !issuedElement.TryGetInt64(out var issued)) throw new WorkerProtocolException("Viewer challenge time is invalid.");
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - issued;
            if (Required(root, "type") != "challenge" || Required(root, "version") != WorkerProtocol.Version || Required(root, "role") != WorkerProtocol.ViewerRole || Required(root, "controllerId") != enrollment.ControllerId || Required(root, "workerId") != enrollment.WorkerId || Required(root, "keyId") != keyId || Required(root, "clientNonce") != clientNonce || elapsed < 0 || elapsed > freshness.TotalMilliseconds) throw new WorkerProtocolException("Viewer challenge is invalid or stale.");
            var expected = WorkerProtocol.ComputeViewerMac(key, "server-proof", enrollment.ControllerId, enrollment.WorkerId, keyId, clientNonce, serverNonce, issued, owner.Ownership.Epoch, owner.Ownership.ConnectionNonce, sessionId);
            if (!WorkerProtocol.VerifyMac(expected, Required(root, "mac"))) throw new WorkerProtocolException("Viewer server proof is invalid.");
            await WorkerProtocol.WriteFrameAsync(stream, new { type = "proof", mac = WorkerProtocol.ComputeViewerMac(key, "client-proof", enrollment.ControllerId, enrollment.WorkerId, keyId, clientNonce, serverNonce, issued, owner.Ownership.Epoch, owner.Ownership.ConnectionNonce, sessionId) }, token).ConfigureAwait(false);
            using var authenticated = await WorkerProtocol.ReadFrameAsync(stream, token).ConfigureAwait(false) ?? throw new WorkerProtocolException("Viewer authentication did not complete.");
            RequireExactFields(authenticated.RootElement, "type", "role", "sessionId");
            if (Required(authenticated.RootElement, "type") != "authenticated" || Required(authenticated.RootElement, "role") != WorkerProtocol.ViewerRole || Required(authenticated.RootElement, "sessionId") != sessionId) throw new WorkerProtocolException("Viewer authentication result is invalid.");
            return new RemoteTerminalSession(stream, sessionId);
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public Task SendInputAsync(ReadOnlyMemory<byte> utf8, CancellationToken token)
    {
        if (utf8.Length > WorkerProtocol.MaxViewerInputBytes) throw new WorkerProtocolException("Viewer input exceeds the fixed limit.");
        return WorkerProtocol.WriteFrameAsync(_stream, new { type = "input", sessionId = _sessionId, data = Convert.ToBase64String(utf8.Span) }, token).AsTask();
    }
    public Task ResizeAsync(int rows, int columns, CancellationToken token)
    {
        if (rows is < 1 or > 500 || columns is < 1 or > 500) throw new WorkerProtocolException("Viewer dimensions are invalid.");
        return WorkerProtocol.WriteFrameAsync(_stream, new { type = "resize", sessionId = _sessionId, rows, columns }, token).AsTask();
    }
    public async Task<byte[]?> ReadOutputAsync(CancellationToken token)
    {
        using var frame = await WorkerProtocol.ReadFrameAsync(_stream, token).ConfigureAwait(false); if (frame is null) return null;
        if (Required(frame.RootElement, "type") != "output" || Required(frame.RootElement, "sessionId") != _sessionId) throw new WorkerProtocolException("Viewer output frame is invalid.");
        var bytes = Convert.FromBase64String(Required(frame.RootElement, "data")); if (bytes.Length > WorkerProtocol.MaxViewerOutputBytes) { CryptographicOperations.ZeroMemory(bytes); throw new WorkerProtocolException("Viewer output exceeds the fixed limit."); }
        return bytes;
    }
    public Task DetachAsync(CancellationToken token) => WorkerProtocol.WriteFrameAsync(_stream, new { type = "detach", sessionId = _sessionId }, token).AsTask();
    private static string Required(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : throw new WorkerProtocolException($"Viewer field {name} is invalid.");
    private static void RequireExactFields(JsonElement element, params string[] fields) { if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new WorkerProtocolException("Viewer authentication fields are invalid."); }
    private sealed class Zero(byte[] bytes) : IDisposable { public void Dispose() => CryptographicOperations.ZeroMemory(bytes); }
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
