using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.AgentControl.Worker;

public sealed class WorkerRuntime : IAsyncDisposable
{
    private readonly WorkerStore _store;
    private readonly Stream _acpInput;
    private readonly Stream _acpOutput;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _promptLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _responses = new();
    private readonly ConcurrentDictionary<string, Task<StoredRequest>> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<StoredCancellation>> _cancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<PendingPermission>> _permissionDecisions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (JsonElement Envelope, PendingPermission Pending)> _permissionFrames = new(StringComparer.Ordinal);
    private readonly object _activePromptGate = new();
    private readonly object _submitGate = new();
    private ActivePromptContext? _activePrompt;
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextAcpId;
    private Task? _reader;

    public WorkerRuntime(WorkerStore store, Stream acpInput, Stream acpOutput) { _store = store; _acpInput = acpInput; _acpOutput = acpOutput; }
    public void Start(long? employeePid = null) { _reader = Task.Run(ReadAcpAsync); }

    public Task<StoredRequest> SubmitAsync(long epoch, string connectionNonce, string requestId, JsonElement envelope, string? turnId, CancellationToken connectionToken)
    {
        var prompt = IsPrompt(envelope);
        if (prompt && turnId is null) throw new WorkerProtocolException("Prompt submissions require a host turn id.");
        Task<StoredRequest> durable;
        lock (_submitGate)
        {
            if (_operations.TryGetValue(requestId, out var existingOperation))
            {
                var existing = _store.GetRequest(requestId) ?? throw new WorkerProtocolException("Active request has no durable identity.");
                var hash = Convert.ToHexString(WorkerProtocol.CanonicalPayloadHash(envelope)).ToLowerInvariant();
                if (existing.PayloadHash != hash || existing.TurnId != turnId) throw new WorkerProtocolException("Request id was reused with different authorization data.");
                return existingOperation.WaitAsync(connectionToken);
            }
            var promptOwned = prompt && _promptLock.Wait(0);
            if (prompt && !promptOwned) throw new WorkerProtocolException("Another prompt request is active.");
            try
            {
                var registered = _store.RegisterAndBeginForwardingGated(epoch, connectionNonce, requestId, envelope, turnId);
                if (registered.State != "forwarding")
                {
                    if (promptOwned) _promptLock.Release();
                    return Task.FromResult(registered);
                }
                durable = RunRequestAsync(registered, envelope.Clone(), promptOwned);
                if (!_operations.TryAdd(requestId, durable)) throw new WorkerProtocolException("Request ownership is ambiguous.");
            }
            catch
            {
                if (promptOwned) _promptLock.Release();
                throw;
            }
        }
        return durable.WaitAsync(connectionToken);
    }

    private async Task<StoredRequest> RunRequestAsync(StoredRequest registered, JsonElement envelope, bool promptOwned)
    {
        await Task.Yield();
        var requestId = registered.RequestId;
        var prompt = IsPrompt(envelope);
        long? correlationId = null;
        ActivePromptContext? promptContext = null;
        try
        {
            if (prompt) _store.SetActiveRequest(requestId);
            correlationId = Interlocked.Increment(ref _nextAcpId);
            if (prompt)
            {
                promptContext = new ActivePromptContext(requestId, registered.TurnId!, registered.ProcessGeneration, correlationId.Value, ExtractSessionId(envelope));
                lock (_activePromptGate)
                {
                    if (_activePrompt is not null) throw new WorkerProtocolException("Active prompt ownership is ambiguous.");
                    _activePrompt = promptContext;
                }
            }
            using var normalized = NormalizeEnvelope(envelope, correlationId.Value);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_responses.TryAdd(correlationId.Value, completion)) throw new WorkerProtocolException("ACP correlation allocation failed.");
            await WriteAcpAsync(normalized.RootElement, _lifetime.Token).ConfigureAwait(false);
            _store.MarkForwarded(requestId);
            var result = await completion.Task.ConfigureAwait(false);
            var state = result.TryGetProperty("error", out _) ? "failed" : "completed";
            _store.CompleteRequest(requestId, state, result.GetRawText());
            TryAppendObservation("acp-response", JsonSerializer.Serialize(new { requestId, envelope = result }, WorkerProtocol.JsonOptions));
            return _store.GetRequest(requestId)!;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            var current = _store.GetRequest(requestId);
            if (current?.State is "forwarding" or "forwarded") _store.MarkUncertain(requestId);
            throw new WorkerProtocolException("ACP request completion is uncertain.", exception);
        }
        finally
        {
            if (correlationId is { } id) _responses.TryRemove(id, out _);
            if (promptContext is not null)
            {
                lock (_activePromptGate)
                {
                    if (ReferenceEquals(_activePrompt, promptContext)) _activePrompt = null;
                }
            }
            if (prompt) _store.SetActiveRequest(null);
            if (promptOwned) _promptLock.Release();
            _operations.TryRemove(requestId, out _);
        }
    }

    public Task<StoredCancellation> CancelAsync(long epoch, string connectionNonce, string cancellationId, string targetRequestId, JsonElement envelope, CancellationToken connectionToken)
    {
        ValidateCancellationEnvelope(envelope);
        var registered = _store.RegisterCancellation(epoch, connectionNonce, cancellationId, targetRequestId, envelope);
        if (registered.State != "persisted") return Task.FromResult(registered);
        var durable = _cancellations.GetOrAdd(cancellationId, _ => RunCancellationAsync(registered, envelope.Clone()));
        return durable.WaitAsync(connectionToken);
    }

    private async Task<StoredCancellation> RunCancellationAsync(StoredCancellation registered, JsonElement envelope)
    {
        try
        {
            _store.MarkCancellationForwarding(registered.CancellationId);
            await WriteAcpAsync(envelope, _lifetime.Token).ConfigureAwait(false);
            _store.MarkCancellationForwarded(registered.CancellationId);
            TryAppendObservation("cancel-forwarded", JsonSerializer.Serialize(new { registered.CancellationId, registered.TargetRequestId }, WorkerProtocol.JsonOptions));
            return _store.GetCancellation(registered.CancellationId)!;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            _store.MarkCancellationUncertain(registered.CancellationId);
            throw new WorkerProtocolException("Cancellation delivery is uncertain.", exception);
        }
        finally { _cancellations.TryRemove(registered.CancellationId, out _); }
    }

    public Task<PendingPermission> DecidePermissionAsync(string decisionId, long generation, string requestId, string turnId, string decision, CancellationToken connectionToken)
    {
        var deciding = _store.BeginPermissionDecision(decisionId, generation, requestId, turnId, decision);
        if (deciding.State == "decided") return Task.FromResult(deciding);
        var durable = _permissionDecisions.GetOrAdd(decisionId, _ => RunPermissionDecisionAsync(deciding));
        return durable.WaitAsync(connectionToken);
    }

    private async Task<PendingPermission> RunPermissionDecisionAsync(PendingPermission deciding)
    {
        try
        {
            if (!_permissionFrames.TryGetValue(deciding.DecisionId, out var frame)) { _store.MarkPermissionUncertain(deciding.DecisionId); throw new WorkerProtocolException("Permission response transport requires reconciliation."); }
            var response = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = frame.Envelope.GetProperty("id").Clone(), ["result"] = new { outcome = new { outcome = "selected", optionId = deciding.Decision } } };
            try { await WriteAcpObjectAsync(response, _lifetime.Token).ConfigureAwait(false); }
            catch (Exception exception) { _store.MarkPermissionUncertain(deciding.DecisionId); throw new WorkerProtocolException("Permission response delivery is uncertain.", exception); }
            _store.CompletePermissionDecision(deciding.DecisionId);
            _permissionFrames.TryRemove(deciding.DecisionId, out _);
            var decided = deciding with { State = "decided" };
            TryAppendObservation("permission-decided", JsonSerializer.Serialize(decided, WorkerProtocol.JsonOptions));
            return decided;
        }
        finally { _permissionDecisions.TryRemove(deciding.DecisionId, out _); }
    }

    private void TryAppendObservation(string kind, string payload)
    {
        try { _store.AppendEvent(kind, payload); }
        catch (WorkerProtocolException) when (_store.Status().HoldReason == "replay-overflow") { }
    }

    private async Task ReadAcpAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var document = await WorkerProtocol.ReadFrameAsync(_acpInput, _lifetime.Token).ConfigureAwait(false);
                if (document is null) break;
                using (document)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && !root.TryGetProperty("method", out _) && _responses.TryRemove(id.GetInt64(), out var response)) { response.TrySetResult(root.Clone()); continue; }
                    if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "session/request_permission" && root.TryGetProperty("id", out _))
                        await HandlePermissionRequestAsync(root).ConfigureAwait(false);
                    else TryAppendObservation("acp-event", root.GetRawText());
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { }
        finally
        {
            FailPendingResponses();
            _store.SetProcess("exited"); _store.SetHold(true, "process-exited");
        }
    }

    private void FailPendingResponses()
    {
        var failure = new WorkerProtocolException("ACP transport stopped before request completion.");
        foreach (var pair in _responses.ToArray())
            if (_responses.TryRemove(pair.Key, out var response)) response.TrySetException(failure);
    }

    private async Task HandlePermissionRequestAsync(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
        {
            await RejectUnboundPermissionAsync(root, parameters).ConfigureAwait(false);
            return;
        }
        ActivePromptContext? context;
        lock (_activePromptGate) context = _activePrompt;
        if (context is null || context.ProcessGeneration != _store.ProcessGeneration || !SessionMatches(context.SessionId, parameters))
        {
            await RejectUnboundPermissionAsync(root, parameters).ConfigureAwait(false);
            return;
        }
        var decisionId = CreateDecisionId();
        try
        {
            var pending = _store.AddPermission(context.RequestId, context.TurnId, decisionId, parameters);
            if (!_permissionFrames.TryAdd(decisionId, (root.Clone(), pending))) throw new WorkerProtocolException("Permission correlation is ambiguous.");
            TryAppendObservation("permission-pending", JsonSerializer.Serialize(pending, WorkerProtocol.JsonOptions));
        }
        catch (WorkerProtocolException)
        {
            await RejectUnboundPermissionAsync(root, parameters).ConfigureAwait(false);
        }
    }

    private async Task RejectUnboundPermissionAsync(JsonElement root, JsonElement parameters)
    {
        object result = new { outcome = new { outcome = "cancelled" } };
        if (parameters.ValueKind == JsonValueKind.Object && TrySelectRejectOption(parameters) is { } optionId)
            result = new { outcome = new { outcome = "selected", optionId } };
        var response = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = root.GetProperty("id").Clone(), ["result"] = result };
        try { await WriteAcpObjectAsync(response, _lifetime.Token).ConfigureAwait(false); }
        catch { _store.SetHold(true, "permission-binding-uncertain"); }
    }

    private static string? TrySelectRejectOption(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) return null;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object || !option.TryGetProperty("optionId", out var id) || id.ValueKind != JsonValueKind.String) continue;
            var value = id.GetString();
            if (value is "reject_once") return value;
        }
        return null;
    }

    private static string CreateDecisionId() => "perm:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static bool SessionMatches(string? expected, JsonElement parameters)
    {
        if (expected is null) return true;
        return parameters.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String && string.Equals(expected, session.GetString(), StringComparison.Ordinal);
    }

    private static string? ExtractSessionId(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String) return null;
        var value = session.GetString();
        if (value is null) return null;
        WorkerProtocol.ValidateIdentifier(value, WorkerProtocol.MaxIdentifierLength, "session id");
        return value;
    }

    private static bool IsPrompt(JsonElement envelope) => envelope.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "session/prompt";

    private static void ValidateCancellationEnvelope(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || method.GetString() != "session/cancel")
            throw new WorkerProtocolException("Cancellation envelope must use session/cancel.");
        foreach (var property in envelope.EnumerateObject()) if (property.Name is not ("method" or "params")) throw new WorkerProtocolException("Cancellation envelope contains unsupported fields.");
    }

    private static JsonDocument NormalizeEnvelope(JsonElement envelope, long id)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) throw new WorkerProtocolException("ACP envelope requires a method.");
        foreach (var property in envelope.EnumerateObject()) if (property.Name is not ("method" or "params")) throw new WorkerProtocolException("ACP envelope contains unsupported fields.");
        var value = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method.GetString() };
        if (envelope.TryGetProperty("params", out var parameters)) value["params"] = parameters.Clone();
        return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, WorkerProtocol.JsonOptions));
    }
    private Task WriteAcpAsync(JsonElement value, CancellationToken token) => WriteAcpObjectAsync(value, token);
    private async Task WriteAcpObjectAsync(object value, CancellationToken token) { await _writeLock.WaitAsync(token).ConfigureAwait(false); try { await WorkerProtocol.WriteFrameAsync(_acpOutput, value, token).ConfigureAwait(false); } finally { _writeLock.Release(); } }
    private sealed record ActivePromptContext(string RequestId, string TurnId, long ProcessGeneration, long CorrelationId, string? SessionId);
    public async ValueTask DisposeAsync() { _lifetime.Cancel(); _acpInput.Dispose(); _acpOutput.Dispose(); if (_reader is not null) try { await _reader.ConfigureAwait(false); } catch { } var operations = _operations.Values.Cast<Task>().Concat(_cancellations.Values).Concat(_permissionDecisions.Values).ToArray(); if (operations.Length > 0) try { await Task.WhenAll(operations).ConfigureAwait(false); } catch { } _writeLock.Dispose(); _promptLock.Dispose(); _lifetime.Dispose(); }
    public static FileStream OpenInheritedFd(int fd, FileAccess access) => new(new SafeFileHandle((IntPtr)fd, ownsHandle: false), access, 4096, isAsync: false);
}

public sealed class WorkerBridge : IAsyncDisposable
{
    private readonly WorkerOptions _options; private readonly WorkerStore _store; private readonly WorkerRuntime _runtime; private readonly byte[] _key; private readonly IWorkerClock _clock;
    private readonly Dictionary<string, long> _nonces = new(StringComparer.Ordinal); private readonly object _nonceLock = new(); private Socket? _listener;
    public WorkerBridge(WorkerOptions options, WorkerStore store, WorkerRuntime runtime, byte[] key, IWorkerClock? clock = null) { _options = options; _store = store; _runtime = runtime; _key = key; _clock = clock ?? new SystemWorkerClock(); }

    public async Task RunAsync(CancellationToken token)
    {
        SecureStaleSocket();
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); _listener.Bind(new UnixDomainSocketEndPoint(_options.SocketPath));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_options.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(16);
        while (!token.IsCancellationRequested)
        {
            Socket socket; try { socket = await _listener.AcceptAsync(token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            _ = Task.Run(() => HandleAsync(socket, token), CancellationToken.None);
        }
    }

    private async Task HandleAsync(Socket socket, CancellationToken bridgeToken)
    {
        using (socket) using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            try
            {
                VerifyPeer(socket);
                using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(bridgeToken); authTimeout.CancelAfter(_options.ChallengeLifetime);
                using var hello = await WorkerProtocol.ReadFrameAsync(stream, authTimeout.Token).ConfigureAwait(false) ?? throw new WorkerProtocolException("Authentication hello is required.");
                var h = hello.RootElement; RequireExactFields(h, "type", "version", "role", "controllerId", "workerId", "keyId", "clientNonce");
                if (Required(h, "type") != "hello") throw new WorkerProtocolException("Authentication hello is invalid.");
                var version = Required(h, "version"); var role = Required(h, "role"); var controller = Required(h, "controllerId"); var worker = Required(h, "workerId"); var keyId = Required(h, "keyId"); var clientNonce = Required(h, "clientNonce");
                ValidateAuthIdentity(controller, worker, keyId, clientNonce);
                if (version != WorkerProtocol.Version || role != "controller" || controller != _options.ControllerId || worker != _options.WorkerId || keyId != WorkerProtocol.KeyId(_key)) throw new WorkerProtocolException("Authentication identity mismatch.");
                UseNonce(clientNonce);
                var serverNonceBytes = RandomNumberGenerator.GetBytes(WorkerProtocol.NonceBytes);
                try
                {
                    var serverNonce = Convert.ToBase64String(serverNonceBytes); var issued = _clock.UtcNow.ToUnixTimeMilliseconds();
                    var serverMac = WorkerProtocol.ComputeMac(_key, "server-proof", role, controller, worker, keyId, clientNonce, serverNonce, issued);
                    await WorkerProtocol.WriteFrameAsync(stream, new { type = "challenge", version, role, controllerId = controller, workerId = worker, keyId, clientNonce, serverNonce, issuedUnixMilliseconds = issued, mac = serverMac }, authTimeout.Token);
                    using var proof = await WorkerProtocol.ReadFrameAsync(stream, authTimeout.Token).ConfigureAwait(false) ?? throw new WorkerProtocolException("Client proof is required.");
                    RequireExactFields(proof.RootElement, "type", "mac"); if (Required(proof.RootElement, "type") != "proof") throw new WorkerProtocolException("Client proof is invalid.");
                    var expected = WorkerProtocol.ComputeMac(_key, "client-proof", role, controller, worker, keyId, clientNonce, serverNonce, issued);
                    if (!IsProofFresh(_clock.UtcNow.ToUnixTimeMilliseconds(), issued, _options.ChallengeLifetime) || !WorkerProtocol.VerifyMac(expected, Required(proof.RootElement, "mac"))) throw new WorkerProtocolException("Client proof rejected.");
                }
                finally { CryptographicOperations.ZeroMemory(serverNonceBytes); }
                var connectionNonceBytes = RandomNumberGenerator.GetBytes(WorkerProtocol.NonceBytes);
                Lease lease;
                try { lease = _store.AcquireLease(controller, Convert.ToBase64String(connectionNonceBytes)); }
                finally { CryptographicOperations.ZeroMemory(connectionNonceBytes); }
                await WorkerProtocol.WriteFrameAsync(stream, new { type = "authenticated", lease }, bridgeToken);
                while (true) { using var frame = await WorkerProtocol.ReadFrameAsync(stream, bridgeToken).ConfigureAwait(false); if (frame is null) break; await DispatchAsync(stream, frame.RootElement, lease, bridgeToken).ConfigureAwait(false); }
            }
            catch (OperationCanceledException) { }
            catch (WorkerProtocolException) { try { await WorkerProtocol.WriteFrameAsync(stream, new { type = "error", error = "worker-request-rejected" }, CancellationToken.None); } catch { } }
            catch (Exception) { try { await WorkerProtocol.WriteFrameAsync(stream, new { type = "error", error = "worker-operation-failed" }, CancellationToken.None); } catch { } }
        }
    }

    internal async Task DispatchAsync(Stream stream, JsonElement message, Lease socketLease, CancellationToken connectionToken)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new WorkerProtocolException("Control message must be an object.");
        var operation = Required(message, "operation");
        _store.RequireLease(socketLease.Epoch, socketLease.ConnectionNonce);
        var mutation = operation is "heartbeat" or "hold" or "ack-events" or "submit" or "cancel" or "permission";
        if (mutation)
        {
            var epoch = RequiredInt64(message, "epoch", 1);
            var nonce = Required(message, "connectionNonce");
            if (epoch != socketLease.Epoch || nonce != socketLease.ConnectionNonce) throw new WorkerProtocolException("Operation lease does not match its authenticated socket.");
            _store.RequireLease(epoch, nonce);
        }
        object result = operation switch
        {
            "status" => _store.Status(),
            "heartbeat" => Run(() => _store.Heartbeat(socketLease.Epoch, socketLease.ConnectionNonce)),
            "hold" => Run(() => _store.SetHold(RequiredBoolean(message, "held"), OptionalString(message, "reason", 128))),
            "replay" => _store.Replay(RequiredInt64(message, "workerGeneration", 0), RequiredInt64(message, "afterSequence", 0)),
            "ack-events" => Run(() => _store.Acknowledge(RequiredInt64(message, "workerGeneration", 0), RequiredInt64(message, "sequence", 0))),
            "reconcile" => Reconcile(message),
            "submit" => await _runtime.SubmitAsync(socketLease.Epoch, socketLease.ConnectionNonce, RequiredBounded(message, "requestId"), RequiredElement(message, "envelope", JsonValueKind.Object), OptionalString(message, "turnId", WorkerProtocol.MaxIdentifierLength), connectionToken).ConfigureAwait(false),
            "cancel" => await _runtime.CancelAsync(socketLease.Epoch, socketLease.ConnectionNonce, RequiredBounded(message, "cancellationId"), RequiredBounded(message, "targetRequestId"), RequiredElement(message, "envelope", JsonValueKind.Object), connectionToken).ConfigureAwait(false),
            "permission" => await _runtime.DecidePermissionAsync(RequiredBounded(message, "decisionId"), RequiredInt64(message, "processGeneration", 0), RequiredBounded(message, "requestId"), RequiredBounded(message, "turnId"), RequiredBounded(message, "decision"), connectionToken).ConfigureAwait(false),
            _ => throw new WorkerProtocolException("Unsupported operation.")
        };
        await WorkerProtocol.WriteFrameAsync(stream, new { type = "result", operation, result }, connectionToken);
    }

    private object Reconcile(JsonElement message)
    {
        var kind = OptionalString(message, "kind", 32) ?? "request";
        var id = RequiredBounded(message, kind == "cancellation" ? "cancellationId" : "requestId");
        return kind switch
        {
            "request" => _store.GetRequest(id) ?? throw new WorkerProtocolException("Request is unknown."),
            "cancellation" => _store.GetCancellation(id) ?? throw new WorkerProtocolException("Cancellation is unknown."),
            _ => throw new WorkerProtocolException("Unsupported reconciliation kind."),
        };
    }

    internal static bool IsProofFresh(long nowUnixMilliseconds, long issuedUnixMilliseconds, TimeSpan lifetime)
    {
        var elapsed = nowUnixMilliseconds - issuedUnixMilliseconds;
        return elapsed >= 0 && elapsed <= lifetime.TotalMilliseconds;
    }

    private void ValidateAuthIdentity(string controller, string worker, string keyId, string clientNonce) { WorkerProtocol.ValidateIdentifier(controller, WorkerProtocol.MaxIdentifierLength, "controller id"); WorkerProtocol.ValidateIdentifier(worker, WorkerProtocol.MaxIdentifierLength, "worker id"); WorkerProtocol.ValidateIdentifier(keyId, 71, "key id"); using var nonce = new ZeroingBuffer(WorkerProtocol.ParseNonce(clientNonce, "client nonce")); }
    private void UseNonce(string nonce)
    {
        var now = _clock.UtcNow.ToUnixTimeMilliseconds(); lock (_nonceLock)
        {
            foreach (var old in _nonces.Where(x => now - x.Value > _options.ChallengeLifetime.TotalMilliseconds).Select(x => x.Key).ToArray()) _nonces.Remove(old);
            if (_nonces.ContainsKey(nonce)) throw new WorkerProtocolException("Authentication nonce was reused.");
            if (_nonces.Count >= _options.NonceCacheLimit) throw new WorkerProtocolException("Authentication nonce capacity is temporarily exhausted.");
            _nonces[nonce] = now;
        }
    }
    private void SecureStaleSocket()
    {
        if (!File.Exists(_options.SocketPath)) return;
        if (!OperatingSystem.IsLinux()) throw new WorkerProtocolException("An existing bridge socket requires operator reconciliation.");
        if (lstat(_options.SocketPath, out var stat) != 0 || (stat.Mode & 0xF000) != 0xC000 || stat.LinkCount != 1 || (_options.ExpectedBridgeUid >= 0 && stat.Uid != (uint)_options.ExpectedBridgeUid)) throw new WorkerProtocolException("The existing bridge socket is not a stale owned socket.");
        File.Delete(_options.SocketPath);
    }
    private static object Run(Action action) { action(); return new { ok = true }; }
    private static async Task<object> RunAsync(Func<Task> action) { await action().ConfigureAwait(false); return new { ok = true }; }
    private static string Required(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()! : throw new WorkerProtocolException($"Missing {name}.");
    private static string RequiredBounded(JsonElement element, string name) { var value = Required(element, name); WorkerProtocol.ValidateIdentifier(value, WorkerProtocol.MaxIdentifierLength, name); return value; }
    private static long RequiredInt64(JsonElement element, string name, long minimum) { if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value) || value < minimum) throw new WorkerProtocolException($"Invalid {name}."); return value; }
    private static bool RequiredBoolean(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : throw new WorkerProtocolException($"Invalid {name}.");
    private static string? OptionalString(JsonElement element, string name, int maximum) { if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null; if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } value || value.Length > maximum) throw new WorkerProtocolException($"Invalid {name}."); return value; }
    private static JsonElement RequiredElement(JsonElement element, string name, JsonValueKind kind) => element.TryGetProperty(name, out var property) && property.ValueKind == kind ? property : throw new WorkerProtocolException($"Invalid {name}.");
    private static void RequireExactFields(JsonElement element, params string[] fields) { if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal), StringComparer.Ordinal) is false) throw new WorkerProtocolException("Authentication message fields are invalid."); }
    private static void VerifyPeer(Socket socket) { if (!OperatingSystem.IsLinux()) return; var size = Marshal.SizeOf<UCred>(); var buffer = new byte[size]; socket.GetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)17, buffer); var credential = MemoryMarshal.Read<UCred>(buffer); if (credential.Uid != (uint)geteuid()) throw new WorkerProtocolException("Local peer identity is invalid."); }
    [StructLayout(LayoutKind.Sequential)] private struct UCred { public int Pid; public uint Uid; public uint Gid; }
    [StructLayout(LayoutKind.Sequential)] private struct StatBuffer { public ulong Device, Inode, LinkCount; public uint Mode, Uid, Gid, Pad; public ulong Rdev; public long Size, BlockSize, Blocks, Atime, AtimeNs, Mtime, MtimeNs, Ctime, CtimeNs; private long R0, R1, R2; }
    private sealed class ZeroingBuffer(byte[] value) : IDisposable { public void Dispose() => CryptographicOperations.ZeroMemory(value); }
    [DllImport("libc")] private static extern int geteuid();
    [DllImport("libc", SetLastError = true)] private static extern int lstat(string path, out StatBuffer stat);
    public async ValueTask DisposeAsync() { _listener?.Dispose(); CryptographicOperations.ZeroMemory(_key); await _runtime.DisposeAsync(); _store.Dispose(); try { SecureStaleSocket(); } catch { } }
}
