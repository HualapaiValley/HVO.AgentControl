using System.Security.Cryptography;
using System.Text.Json;
using HVO.AgentControl.Worker;

namespace HVO.AgentControl.RemoteWorker;

public interface IWorkerConnector { Task<Stream> ConnectAsync(string workerId, CancellationToken cancellationToken); }
public interface IRemoteTerminalConnector { Task<Stream> ConnectViewerAsync(string workerId, CancellationToken cancellationToken); }

public sealed record WorkerBridgeLease(long Epoch, string ControllerId, string ConnectionNonce, DateTimeOffset ObservedUtc);
public sealed record WorkerInvocationResult(string Operation, JsonElement Result);

public sealed class WorkerWriteUncertainException(string message, Exception? inner = null) : WorkerProtocolException(message, inner);
public sealed class WorkerReadUncertainException(string message, Exception? inner = null) : WorkerProtocolException(message, inner);
public sealed class WorkerRemoteException(string code) : WorkerProtocolException("The worker rejected the operation with a fixed error category.") { public string Code { get; } = code; }

public sealed class WorkerBridgeClient : IAsyncDisposable
{
    private static readonly HashSet<string> FixedErrors = ["worker-request-rejected", "worker-operation-failed"];
    private readonly Stream _stream;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _operationTimeout;
    private int _faulted;
    public WorkerBridgeLease Lease { get; }

    private WorkerBridgeClient(Stream stream, byte[] key, WorkerBridgeLease lease, TimeSpan operationTimeout) { _stream = stream; _key = key; Lease = lease; _operationTimeout = operationTimeout; }

    public static async Task<WorkerBridgeClient> ConnectAsync(IWorkerConnector connector, string workerId, string controllerId, string keyPath, TimeSpan freshness, CancellationToken cancellationToken, TimeSpan? authenticationTimeout = null, int? expectedControllerUid = null, TimeSpan? operationTimeout = null)
    {
        WorkerProtocol.ValidateIdentifier(workerId, WorkerProtocol.MaxIdentifierLength, "worker id");
        WorkerProtocol.ValidateIdentifier(controllerId, WorkerProtocol.MaxIdentifierLength, "controller id");
        var key = ControllerPrivateFile.ReadExact(keyPath, expectedControllerUid ?? ControllerPrivateFile.EffectiveUid, ControllerFileModes.Private0600, 32);
        Stream? stream = null;
        try
        {
            stream = await connector.ConnectAsync(workerId, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(authenticationTimeout ?? freshness);
            var token = timeout.Token;
            var keyId = WorkerProtocol.KeyId(key);
            var clientBytes = RandomNumberGenerator.GetBytes(WorkerProtocol.NonceBytes);
            string clientNonce;
            try { clientNonce = Convert.ToBase64String(clientBytes); }
            finally { CryptographicOperations.ZeroMemory(clientBytes); }
            await WorkerProtocol.WriteFrameAsync(stream, new { type = "hello", version = WorkerProtocol.Version, role = "controller", controllerId, workerId, keyId, clientNonce }, token).ConfigureAwait(false);

            using var challenge = await WorkerProtocol.ReadFrameAsync(stream, token).ConfigureAwait(false) ?? throw new WorkerProtocolException("Bridge closed during authentication.");
            var root = challenge.RootElement;
            RequireExactFields(root, "type", "version", "role", "controllerId", "workerId", "keyId", "clientNonce", "serverNonce", "issuedUnixMilliseconds", "mac");
            RequireString(root, "type", "challenge"); RequireString(root, "version", WorkerProtocol.Version); RequireString(root, "role", "controller"); RequireString(root, "controllerId", controllerId); RequireString(root, "workerId", workerId); RequireString(root, "keyId", keyId); RequireString(root, "clientNonce", clientNonce);
            var serverNonce = RequiredString(root, "serverNonce"); using var parsed = new Zero(WorkerProtocol.ParseNonce(serverNonce, "server nonce"));
            if (!root.TryGetProperty("issuedUnixMilliseconds", out var issuedProperty) || !issuedProperty.TryGetInt64(out var issued)) throw new WorkerProtocolException("Bridge challenge time is invalid.");
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - issued;
            var suppliedMac = RequiredString(root, "mac");
            var expectedMac = WorkerProtocol.ComputeMac(key, "server-proof", "controller", controllerId, workerId, keyId, clientNonce, serverNonce, issued);
            if (elapsed < 0 || elapsed > freshness.TotalMilliseconds || !WorkerProtocol.VerifyMac(expectedMac, suppliedMac)) throw new WorkerProtocolException("Bridge proof is invalid or stale.");
            await WorkerProtocol.WriteFrameAsync(stream, new { type = "proof", mac = WorkerProtocol.ComputeMac(key, "client-proof", "controller", controllerId, workerId, keyId, clientNonce, serverNonce, issued) }, token).ConfigureAwait(false);

            using var authenticated = await WorkerProtocol.ReadFrameAsync(stream, token).ConfigureAwait(false) ?? throw new WorkerProtocolException("Bridge closed before authentication completed.");
            RequireExactFields(authenticated.RootElement, "type", "lease");
            RequireString(authenticated.RootElement, "type", "authenticated");
            if (!authenticated.RootElement.TryGetProperty("lease", out var leaseElement)) throw new WorkerProtocolException("Authenticated lease is missing.");
            RequireExactFields(leaseElement, "epoch", "controllerId", "connectionNonce", "observedUtc");
            if (!leaseElement.TryGetProperty("epoch", out var epochElement) || !epochElement.TryGetInt64(out var epoch) || epoch < 1) throw new WorkerProtocolException("Lease epoch is invalid.");
            RequireString(leaseElement, "controllerId", controllerId);
            var connectionNonce = RequiredString(leaseElement, "connectionNonce"); using var connectionNonceBytes = new Zero(WorkerProtocol.ParseNonce(connectionNonce, "connection nonce"));
            var observedText = RequiredString(leaseElement, "observedUtc");
            if (!DateTimeOffset.TryParseExact(observedText, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var observed) || observed.Offset != TimeSpan.Zero || Math.Abs((DateTimeOffset.UtcNow - observed).TotalMilliseconds) > freshness.TotalMilliseconds)
                throw new WorkerProtocolException("Lease observation time is invalid or stale.");
            return new WorkerBridgeClient(stream, key, new WorkerBridgeLease(epoch, controllerId, connectionNonce, observed), operationTimeout ?? TimeSpan.FromSeconds(60));
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<WorkerInvocationResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken)
    {
        WorkerProtocol.ValidateIdentifier(operation, 64, "operation");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_faulted != 0) throw new ObjectDisposedException(nameof(WorkerBridgeClient));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_operationTimeout);
            var token = timeout.Token;
            try { await WorkerProtocol.WriteFrameAsync(_stream, request, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException) { Fault(); throw new WorkerWriteUncertainException(mutation ? "Worker mutation delivery is uncertain." : "Worker read request delivery failed.", exception); }
            JsonDocument? response;
            try { response = await WorkerProtocol.ReadFrameAsync(_stream, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or WorkerProtocolException) { Fault(); throw mutation ? new WorkerWriteUncertainException("Worker mutation outcome is uncertain.", exception) : new WorkerReadUncertainException("Worker read outcome is unavailable.", exception); }
            using (response)
            {
                if (response is null) { Fault(); throw mutation ? new WorkerWriteUncertainException("Worker disconnected with a mutation outcome uncertain.") : new WorkerReadUncertainException("Worker disconnected before a read result."); }
                var root = response.RootElement;
                if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "error")
                {
                    RequireExactFields(root, "type", "error");
                    var code = RequiredString(root, "error");
                    if (!FixedErrors.Contains(code)) throw new WorkerProtocolException("Worker returned an unknown error category.");
                    throw new WorkerRemoteException(code);
                }
                RequireExactFields(root, "type", "operation", "result");
                RequireString(root, "type", "result"); RequireString(root, "operation", operation);
                return new WorkerInvocationResult(operation, root.GetProperty("result").Clone());
            }
        }
        finally { _gate.Release(); }
    }

    public object Mutation(string operation, IReadOnlyDictionary<string, object?> fields)
    {
        var request = new Dictionary<string, object?>(fields, StringComparer.Ordinal) { ["operation"] = operation, ["epoch"] = Lease.Epoch, ["connectionNonce"] = Lease.ConnectionNonce };
        return request;
    }

    private void Fault() { if (Interlocked.Exchange(ref _faulted, 1) == 0) _stream.Dispose(); }
    private static string RequiredString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : throw new WorkerProtocolException($"Bridge field {name} is invalid.");
    private static void RequireString(JsonElement root, string name, string expected) { if (!string.Equals(RequiredString(root, name), expected, StringComparison.Ordinal)) throw new WorkerProtocolException("Bridge identity or operation is invalid."); }
    private static void RequireExactFields(JsonElement element, params string[] fields) { if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new WorkerProtocolException("Bridge fields are invalid."); }
    public async ValueTask DisposeAsync() { CryptographicOperations.ZeroMemory(_key); _gate.Dispose(); await _stream.DisposeAsync().ConfigureAwait(false); }
    private sealed class Zero(byte[] bytes) : IDisposable { public void Dispose() => CryptographicOperations.ZeroMemory(bytes); }
}
