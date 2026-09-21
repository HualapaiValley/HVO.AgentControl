using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace HVO.AgentControl.Worker;

public sealed class WorkerRuntime : IAsyncDisposable
{
    private readonly WorkerStore _store;
    private readonly IWorkerObservationSink _observations;
    private readonly Stream _acpInput;
    private readonly Stream _acpOutput;
    private readonly IWorkerOrientationInstaller _orientationInstaller;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _promptLock = new(1, 1);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _responses = new();
    private readonly ConcurrentDictionary<string, OperationHandle> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<StoredCancellation>> _cancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<PendingPermission>> _permissionDecisions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (JsonElement RequestId, PendingPermission Pending)> _permissionFrames = new(StringComparer.Ordinal);
    private readonly object _activePromptGate = new();
    private readonly object _submitGate = new();
    private readonly object _cancellationGate = new();
    private ActivePromptContext? _activePrompt;
    private TurnTextCapture? _activeComprehensionCapture;
    private TurnTextCapture? _activeTaskReportCapture;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _transportFault = new();
    private readonly NdjsonFrameReader _acpReader;
    private long _nextAcpId;
    private int _transportFailed;
    private int _disposed;
    private Task? _reader;

    public WorkerRuntime(WorkerStore store, Stream acpInput, Stream acpOutput, IWorkerObservationSink? observations = null, IWorkerOrientationInstaller? orientationInstaller = null) { _store = store; _observations = observations ?? store; _acpInput = acpInput; _acpOutput = acpOutput; _orientationInstaller = orientationInstaller ?? new WorkerOrientationInstaller(); _acpReader = WorkerProtocol.CreateAcpReader(acpInput); }
    public void Start(long? employeePid = null) { _reader = Task.Run(ReadAcpAsync); }

    public async Task InitializeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await InvokeFixedAsync("initialize", new
            {
                protocolVersion = 1,
                clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false },
                clientInfo = new { name = "HVO.AgentControl.Worker", version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0" },
            }, timeout ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (result.TryGetProperty("error", out _) || !result.TryGetProperty("result", out var response) || response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value) || value != 1)
                throw new WorkerProtocolException("ACP initialization failed protocol validation.");
            _store.SetAcpInitialized(true);
        }
        catch
        {
            if (_store.Status().ProcessState == "running") _store.SetProcessFailure("protocol-failed", "acp-initialize-failed");
            throw;
        }
    }

    /// <summary>
    /// Creates one ACP session under the exact bridge lease. The ACP protocol call
    /// carries no worker epoch itself, so the durable operation is fenced before
    /// write and again when the returned session identity is committed. Any
    /// completion that cannot be committed under that same lease is uncertain.
    /// </summary>
    public async Task<string> NewSessionAsync(long epoch, string connectionNonce, CancellationToken cancellationToken = default)
    {
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = _store.Status();
            if (status.SessionId is not null) return status.SessionId;
            var requestId = "session-op:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _store.BeginSessionOperation(epoch, connectionNonce, "creating", requestId, null);
            try
            {
                var result = await InvokeFixedAsync("session/new", new { cwd = "/workspace", mcpServers = Array.Empty<object>() }, TimeSpan.FromSeconds(30), cancellationToken, uncertainAfterWrite: true).ConfigureAwait(false);
                if (result.TryGetProperty("error", out _)) { _store.AbortSessionOperation(requestId); throw new WorkerProtocolException("ACP session creation was rejected."); }
                if (!result.TryGetProperty("result", out var response) || response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String || session.GetString() is not { } sessionId)
                { _store.MarkSessionOperationUncertain(requestId); throw new WorkerOperationUncertainException("ACP session creation returned an invalid identity after the effect may have occurred."); }
                try { WorkerProtocol.ValidateIdentifier(sessionId, WorkerProtocol.MaxIdentifierLength, "session id"); }
                catch (WorkerProtocolException exception) { _store.MarkSessionOperationUncertain(requestId); throw new WorkerOperationUncertainException("ACP session creation returned an invalid identity after the effect may have occurred.", exception); }
                try { _store.CompleteSessionOperation(epoch, connectionNonce, requestId, sessionId); }
                catch (Exception exception) { _store.MarkSessionOperationUncertain(requestId); throw new WorkerOperationUncertainException("ACP session creation completed after ownership or process state changed.", exception); }
                return sessionId;
            }
            catch (WorkerOperationUncertainException) { _store.MarkSessionOperationUncertain(requestId); throw; }
            catch { _store.AbortSessionOperation(requestId); throw; }
        }
        finally { _sessionLock.Release(); }
    }

    /// <summary>
    /// Loads the exact retained ACP session under the current bridge lease. A
    /// post-write timeout, transport failure, or failed lease-fenced commit is
    /// retained as uncertain and must never be retried as though no effect occurred.
    /// </summary>
    public async Task<string> LoadSessionAsync(long epoch, string connectionNonce, string sessionId, CancellationToken cancellationToken = default)
    {
        WorkerProtocol.ValidateIdentifier(sessionId, WorkerProtocol.MaxIdentifierLength, "session id");
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = _store.Status();
            if (status.SessionId is not null)
            {
                if (status.SessionId == sessionId) return sessionId;
                throw new WorkerProtocolException("The ACP process is already bound to a different session.");
            }
            var requestId = "session-op:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _store.BeginSessionOperation(epoch, connectionNonce, "loading", requestId, sessionId);
            try
            {
                var result = await InvokeFixedAsync("session/load", new { sessionId, cwd = "/workspace", mcpServers = Array.Empty<object>() }, TimeSpan.FromSeconds(30), cancellationToken, uncertainAfterWrite: true).ConfigureAwait(false);
                if (result.TryGetProperty("error", out _)) { _store.AbortSessionOperation(requestId); throw new WorkerProtocolException("ACP session load was rejected."); }
                try { _store.CompleteSessionOperation(epoch, connectionNonce, requestId, sessionId); }
                catch (Exception exception) { _store.MarkSessionOperationUncertain(requestId); throw new WorkerOperationUncertainException("ACP session load completed after ownership or process state changed.", exception); }
                return sessionId;
            }
            catch (WorkerOperationUncertainException) { _store.MarkSessionOperationUncertain(requestId); throw; }
            catch { _store.AbortSessionOperation(requestId); throw; }
        }
        finally { _sessionLock.Release(); }
    }

    /// <summary>
    /// Installs one orientation artifact through the fixed supervisor write under
    /// the exact bridge lease. The durable intent (<c>installing</c>) is recorded
    /// before any file can exist; a replay of an already-installed identical
    /// artifact is idempotent; a conflicting artifact for the same assignment is
    /// rejected. Any failure after the supervisor request is recorded as
    /// <c>uncertain</c> and never reported as a clean rejection.
    /// </summary>
    internal async Task<OrientationInstallRecord> InstallOrientationAsync(Lease lease, string assignmentId, string orientationVersion, string artifactFileName, string content, string contentHash, CancellationToken cancellationToken)
    {
        var bytes = WorkerProtocol.EncodeOrientationContent(content);
        if (!string.Equals(WorkerProtocol.OrientationContentHash(bytes), contentHash, StringComparison.Ordinal))
            throw new WorkerProtocolException("Orientation content hash does not match the content.");

        var intent = _store.BeginOrientationInstall(lease.Epoch, lease.ConnectionNonce, assignmentId, orientationVersion, artifactFileName, contentHash);
        if (intent.AlreadyInstalled) return intent;
        try
        {
            var receipt = await _orientationInstaller.InstallAsync(assignmentId, orientationVersion, artifactFileName, bytes, contentHash, cancellationToken).ConfigureAwait(false);
            return _store.CompleteOrientationInstall(lease.Epoch, lease.ConnectionNonce, assignmentId, orientationVersion, contentHash, receipt.InstalledPath);
        }
        catch (Exception exception)
        {
            _store.MarkOrientationUncertain(assignmentId, orientationVersion, contentHash);
            if (exception is WorkerOperationUncertainException) throw;
            throw new WorkerOperationUncertainException("Orientation installation completion is uncertain.", exception);
        }
    }

    /// <summary>
    /// Runs one bounded, tool-free `session/prompt` comprehension turn for the
    /// exact installed orientation artifact and returns the validated structured
    /// evidence. The raw response text is captured only in memory, is bounded to
    /// 16 KiB, is never journaled, and only the validated JSON object crosses the
    /// bridge. A replay of an already-comprehended assignment returns the retained
    /// evidence without a second model turn; a running or uncertain operation is
    /// refused so a duplicate remote effect is never issued.
    /// </summary>
    internal async Task<OrientationComprehensionRecord> RunOrientationComprehensionAsync(Lease lease, string assignmentId, string employeeId, string sessionId, string orientationVersion, CancellationToken cancellationToken)
    {
        WorkerProtocol.ValidateIdentifier(assignmentId, WorkerProtocol.MaxIdentifierLength, "orientation assignment id");
        WorkerProtocol.ValidateIdentifier(employeeId, WorkerProtocol.MaxIdentifierLength, "orientation employee id");
        WorkerProtocol.ValidateIdentifier(sessionId, WorkerProtocol.MaxIdentifierLength, "orientation session id");
        WorkerProtocol.ValidateOrientationVersion(orientationVersion);

        // The exact installed artifact must be current for this assignment and
        // version, and the process/session must be the one the caller named.
        var status = _store.Status();
        if (status.OrientationState != "installed"
            || !string.Equals(status.OrientationAssignmentId, assignmentId, StringComparison.Ordinal)
            || !string.Equals(status.OrientationVersion, orientationVersion, StringComparison.Ordinal)
            || status.OrientationArtifactFileName is not { } artifactFileName
            || status.OrientationContentHash is not { } contentHash
            || status.OrientationInstalledPath is not { })
            throw new WorkerProtocolException("The requested orientation assignment is not the exact installed artifact.");
        if (status.ProcessState != "running" || !status.AcpInitialized)
            throw new WorkerProtocolException("A running, ACP-initialized process is required for comprehension.");
        if (!string.Equals(status.SessionId, sessionId, StringComparison.Ordinal))
            throw new WorkerProtocolException("The ACP process is not bound to the requested session.");

        var state = _store.BeginOrientationComprehension(lease.Epoch, lease.ConnectionNonce, assignmentId, employeeId, sessionId, orientationVersion);
        if (state.State == "comprehended" && state.EvidenceJson is { } retained && state.EvidenceHash is { } retainedHash)
            return new OrientationComprehensionRecord(assignmentId, employeeId, sessionId, orientationVersion,
                EvidenceField(retained, "identity"), EvidenceField(retained, "department"), EvidenceField(retained, "reporting"),
                EvidenceList(retained, "duties"), EvidenceList(retained, "restrictions"), EvidenceField(retained, "escalation"),
                "comprehended", retainedHash, true);
        if (state.State is "running" or "uncertain" && !state.NewlyBegun)
            throw new WorkerOperationUncertainException("A remote comprehension turn for this assignment is already recorded as in-flight.");

        // The bridge uid cannot read the 0600 employee file, so the supervisor
        // reads the exact installed artifact and returns its verified bytes.
        var read = await _orientationInstaller.ReadAsync(assignmentId, orientationVersion, artifactFileName, contentHash, cancellationToken).ConfigureAwait(false);
        var artifactContent = System.Text.Encoding.UTF8.GetString(read.Content);
        if (read.Content.AsSpan().IndexOf((byte)0) >= 0) throw new WorkerProtocolException("The installed orientation content is invalid.");

        var prompt = BuildComprehensionPrompt(assignmentId, employeeId, sessionId, orientationVersion, status.OrientationInstalledPath!, artifactContent);
        var capture = new TurnTextCapture(sessionId, WorkerProtocol.MaxOrientationComprehensionBytes);
        JsonElement result;
        try
        {
            // A comprehension turn is not an owner submission: it holds the single
            // prompt slot but has no active submit context, so any permission/tool
            // request is unbound and refused by the existing permission flow.
            if (!await _promptLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _store.FailOrientationComprehension(assignmentId, employeeId, sessionId, orientationVersion);
                throw new WorkerProtocolException("Another prompt request is active.");
            }
            try
            {
                lock (_activePromptGate)
                {
                    if (_activeComprehensionCapture is not null) throw new WorkerProtocolException("A comprehension turn is already running.");
                    _activeComprehensionCapture = capture;
                }
                try
                {
                    result = await InvokeFixedAsync(
                        "session/prompt",
                        new { sessionId, prompt = new object[] { new { type = "text", text = prompt } } },
                        TimeSpan.FromSeconds(120),
                        cancellationToken,
                        uncertainAfterWrite: true,
                        capture.BindRequest).ConfigureAwait(false);
                }
                finally
                {
                    lock (_activePromptGate) if (ReferenceEquals(_activeComprehensionCapture, capture)) _activeComprehensionCapture = null;
                }
            }
            finally { _promptLock.Release(); }
        }
        catch (WorkerOperationUncertainException)
        {
            _store.MarkOrientationComprehensionUncertain(assignmentId, employeeId, sessionId, orientationVersion);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            _store.MarkOrientationComprehensionUncertain(assignmentId, employeeId, sessionId, orientationVersion);
            throw new WorkerOperationUncertainException("ACP comprehension request completion is uncertain.", exception);
        }

        var stopReason = result.TryGetProperty("result", out var resultBody) && resultBody.ValueKind == JsonValueKind.Object && resultBody.TryGetProperty("stopReason", out var stop)
            ? stop.GetString()
            : null;
        if (!string.Equals(stopReason, "end_turn", StringComparison.Ordinal))
        {
            _store.FailOrientationComprehension(assignmentId, employeeId, sessionId, orientationVersion);
            throw new WorkerProtocolException("The comprehension turn did not end normally.");
        }
        var captured = capture.Complete();
        if (captured.Overflowed)
        {
            _store.FailOrientationComprehension(assignmentId, employeeId, sessionId, orientationVersion);
            throw new WorkerProtocolException("The comprehension response exceeds the fixed size limit.");
        }
        if (string.IsNullOrWhiteSpace(captured.Text))
        {
            _store.FailOrientationComprehension(assignmentId, employeeId, sessionId, orientationVersion);
            throw new WorkerProtocolException("The comprehension turn returned no structured evidence.");
        }

        JsonDocument document;
        try { document = JsonDocument.Parse(captured.Text); }
        catch (JsonException)
        {
            _store.FailOrientationComprehension(assignmentId, employeeId, sessionId, orientationVersion);
            throw new WorkerProtocolException("The comprehension response was not valid unfenced JSON.");
        }
        using (document)
        {
            if (!OrientationEvidenceShape.TryValidate(document.RootElement, assignmentId, employeeId, sessionId, orientationVersion, out _))
            {
                _store.FailOrientationComprehension(assignmentId, employeeId, sessionId, orientationVersion);
                throw new WorkerProtocolException("The comprehension response failed structural validation.");
            }
            var canonical = CanonicalizeEvidence(document.RootElement);
            var evidenceHash = WorkerProtocol.OrientationContentHash(System.Text.Encoding.UTF8.GetBytes(canonical));
            _store.CompleteOrientationComprehension(lease.Epoch, lease.ConnectionNonce, assignmentId, employeeId, sessionId, orientationVersion, evidenceHash, canonical);
            TryAppendObservation("orientation-comprehension", JsonSerializer.Serialize(new { assignmentId, employeeId, sessionId, state = "comprehended", evidenceHash }, WorkerProtocol.JsonOptions));
            return new OrientationComprehensionRecord(assignmentId, employeeId, sessionId, orientationVersion,
                EvidenceField(canonical, "identity"), EvidenceField(canonical, "department"), EvidenceField(canonical, "reporting"),
                EvidenceList(canonical, "duties"), EvidenceList(canonical, "restrictions"), EvidenceField(canonical, "escalation"),
                "comprehended", evidenceHash, false);
        }
    }

    private static string BuildComprehensionPrompt(string assignmentId, string employeeId, string sessionId, string orientationVersion, string installedPath, string content)
    {
        // The content is bounded (64 KiB) and carries no secrets; it is embedded so
        // the tool-free turn can comprehend the orientation without any file read.
        return string.Join('\n',
            "Return ONLY one JSON object. Do not use any tools. Do not use markdown fences.",
            "The object must have exactly these fields:",
            "assignmentId, employeeId, sessionId, orientationVersion, identity, department, reporting, duties (array of strings), restrictions (array of strings), escalation.",
            "Use these exact correlation values:",
            $"assignmentId {assignmentId}",
            $"employeeId {employeeId}",
            $"sessionId {sessionId}",
            $"orientationVersion {orientationVersion}",
            $"The orientation artifact is installed at {installedPath}.",
            "Copy identity, department, reporting, duties, restrictions, and escalation verbatim from the artifact's fenced JSON Standing Facts block. Do not infer or paraphrase them from prose.",
            "Standing orientation artifact:",
            content);
    }

    /// <summary>Serializes the validated evidence with the fixed field order for canonical hashing.</summary>
    private static string CanonicalizeEvidence(JsonElement element)
    {
        // Use a stable ordered object so the hash never depends on model key order.
        var ordered = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["assignmentId"] = element.GetProperty("assignmentId").GetString(),
            ["employeeId"] = element.GetProperty("employeeId").GetString(),
            ["sessionId"] = element.GetProperty("sessionId").GetString(),
            ["orientationVersion"] = element.GetProperty("orientationVersion").GetString(),
            ["identity"] = element.GetProperty("identity").GetString(),
            ["department"] = element.GetProperty("department").GetString(),
            ["reporting"] = element.GetProperty("reporting").GetString(),
            ["duties"] = element.GetProperty("duties").EnumerateArray().Select(item => item.GetString()).ToArray(),
            ["restrictions"] = element.GetProperty("restrictions").EnumerateArray().Select(item => item.GetString()).ToArray(),
            ["escalation"] = element.GetProperty("escalation").GetString(),
        };
        return JsonSerializer.Serialize(ordered, WorkerProtocol.JsonOptions);
    }

    private static string EvidenceField(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(name).GetString()!;
    }

    private static IReadOnlyList<string> EvidenceList(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    /// <summary>
    /// Captures the text chunks of exactly one correlated ACP prompt turn. It
    /// recognizes only <c>session/update</c> notifications whose
    /// <c>sessionId</c> matches, whose <c>sessionUpdate</c> is
    /// <c>agent_message_chunk</c>, and whose text is a string; the buffer is
    /// bounded. Orientation capture overflows closed; task reports retain only a
    /// bounded UTF-8 tail. It seals on the response frame whose id matches the
    /// bound request, so late chunks cannot corrupt a completed turn.
    /// </summary>
    private sealed class TurnTextCapture(string sessionId, int maximumBytes, bool retainTail = false)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _buffer = new();
        private int _bytes;
        private long? _requestId;
        private bool _sealed;
        private bool _overflowed;
        private (string Text, bool Overflowed)? _snapshot;

        public void BindRequest(long requestId)
        {
            lock (_gate)
            {
                if (_requestId is not null) throw new WorkerProtocolException("The comprehension capture is already request-bound.");
                _requestId = requestId;
            }
        }

        public void TryAppend(JsonElement frame)
        {
            if (frame.ValueKind != JsonValueKind.Object
                || !frame.TryGetProperty("method", out var method)
                || method.ValueKind != JsonValueKind.String
                || !string.Equals(method.GetString(), "session/update", StringComparison.Ordinal)
                || !frame.TryGetProperty("params", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object
                || !parameters.TryGetProperty("sessionId", out var session)
                || session.ValueKind != JsonValueKind.String
                || !string.Equals(session.GetString(), sessionId, StringComparison.Ordinal)
                || !parameters.TryGetProperty("update", out var update)
                || update.ValueKind != JsonValueKind.Object
                || !update.TryGetProperty("sessionUpdate", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || !string.Equals(kind.GetString(), "agent_message_chunk", StringComparison.Ordinal))
            {
                return;
            }

            string? text = null;
            if (update.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("text", out var nested)
                && nested.ValueKind == JsonValueKind.String)
            {
                text = nested.GetString();
            }
            else if (update.TryGetProperty("text", out var direct) && direct.ValueKind == JsonValueKind.String)
            {
                text = direct.GetString();
            }
            if (text is null) return;

            lock (_gate)
            {
                if (_sealed || _overflowed) return;
                var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
                if (!retainTail)
                {
                    if (_bytes > maximumBytes - bytes) { _overflowed = true; _buffer.Clear(); return; }
                    _buffer.Append(text);
                    _bytes += bytes;
                    return;
                }
                if (_bytes <= maximumBytes - bytes)
                {
                    _buffer.Append(text);
                    _bytes += bytes;
                    return;
                }
                var combined = System.Text.Encoding.UTF8.GetBytes(_buffer.ToString() + text);
                var start = Math.Max(0, combined.Length - maximumBytes);
                while (start < combined.Length && (combined[start] & 0xc0) == 0x80) start++;
                _buffer.Clear().Append(System.Text.Encoding.UTF8.GetString(combined.AsSpan(start)));
                _bytes = combined.Length - start;
            }
        }

        public void TrySeal(JsonElement frame)
        {
            if (frame.ValueKind != JsonValueKind.Object
                || frame.TryGetProperty("method", out _)
                || !frame.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt64(out var responseId))
            {
                return;
            }
            lock (_gate)
            {
                if (_requestId == responseId && !_sealed)
                {
                    _sealed = true;
                    _snapshot = (_buffer.ToString(), _overflowed);
                }
            }
        }

        public (string Text, bool Overflowed) Complete()
        {
            lock (_gate)
            {
                _sealed = true;
                _snapshot ??= (_buffer.ToString(), _overflowed);
                return _snapshot.Value;
            }
        }
    }

    private async Task<JsonElement> InvokeFixedAsync(string method, object parameters, TimeSpan timeout, CancellationToken cancellationToken, bool uncertainAfterWrite = false)
        => await InvokeFixedAsync(method, parameters, timeout, cancellationToken, uncertainAfterWrite, null).ConfigureAwait(false);

    /// <summary>
    /// The fixed-request path with an optional correlation callback. The callback
    /// runs under the write lock immediately before the frame is written, so a
    /// prompt capture can bind the allocated ACP id before any correlated
    /// notification or response can be observed by the reader.
    /// </summary>
    private async Task<JsonElement> InvokeFixedAsync(string method, object parameters, TimeSpan timeout, CancellationToken cancellationToken, bool uncertainAfterWrite, Action<long>? registered)
    {
        if (_reader is null) throw new WorkerProtocolException("ACP reader is not running.");
        var id = Interlocked.Increment(ref _nextAcpId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_responses.TryAdd(id, completion)) throw new WorkerProtocolException("ACP correlation allocation failed.");
        var writeAttempted = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(timeout);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteAcpObjectAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, deadline.Token, () => { writeAttempted = true; registered?.Invoke(id); }).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (uncertainAfterWrite && writeAttempted)
        {
            throw new WorkerOperationUncertainException("ACP fixed request completion is uncertain.", exception);
        }
        catch (Exception exception) when (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new WorkerProtocolException("ACP fixed request timed out.", exception);
        }
        finally { _responses.TryRemove(id, out _); }
    }

    public Task<StoredRequest> BeginSubmit(long epoch, string connectionNonce, string requestId, JsonElement envelope, string? turnId, CancellationToken connectionToken, bool captureTaskReport = false)
    {
        ValidatePromptEnvelope(envelope);
        const bool prompt = true;
        if (turnId is null) throw new WorkerProtocolException("Prompt submissions require a host turn id.");
        OperationHandle handle;
        lock (_submitGate)
        {
            if (_operations.TryGetValue(requestId, out var existingOperation))
            {
                var existing = _store.GetRequest(requestId) ?? throw new WorkerProtocolException("Active request has no durable identity.");
                var hash = Convert.ToHexString(WorkerProtocol.CanonicalPayloadHash(envelope)).ToLowerInvariant();
                if (existing.PayloadHash != hash || existing.TurnId != turnId) throw new WorkerProtocolException("Request id was reused with different authorization data.");
                return existingOperation.Forwarded.Task.WaitAsync(connectionToken);
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
                var forwarded = new TaskCompletionSource<StoredRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
                handle = new OperationHandle(forwarded);
                if (!_operations.TryAdd(requestId, handle)) throw new WorkerProtocolException("Request ownership is ambiguous.");
                handle.Completion = RunRequestAsync(registered, envelope.Clone(), promptOwned, forwarded, captureTaskReport);
                _ = handle.Completion.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch
            {
                if (promptOwned) _promptLock.Release();
                _operations.TryRemove(requestId, out _);
                throw;
            }
        }
        return handle.Forwarded.Task.WaitAsync(connectionToken);
    }

    public Task<StoredRequest> SubmitAsync(long epoch, string connectionNonce, string requestId, JsonElement envelope, string? turnId, CancellationToken connectionToken, bool captureTaskReport = false) =>
        BeginSubmit(epoch, connectionNonce, requestId, envelope, turnId, connectionToken, captureTaskReport);

    private async Task<StoredRequest> RunRequestAsync(StoredRequest registered, JsonElement envelope, bool promptOwned, TaskCompletionSource<StoredRequest> forwarded, bool captureTaskReport)
    {
        await Task.Yield();
        var requestId = registered.RequestId;
        var prompt = IsPrompt(envelope);
        long? correlationId = null;
        ActivePromptContext? promptContext = null;
        TurnTextCapture? reportCapture = null;
        try
        {
            if (prompt) _store.SetActiveRequest(requestId);
            correlationId = Interlocked.Increment(ref _nextAcpId);
            if (prompt)
            {
                promptContext = new ActivePromptContext(requestId, registered.TurnId, registered.ProcessGeneration, registered.OwnershipEpoch, correlationId.Value, registered.SessionId);
                reportCapture = captureTaskReport ? new TurnTextCapture(registered.SessionId, WorkerProtocol.MaxModelTaskReportBytes, retainTail: true) : null;
                reportCapture?.BindRequest(correlationId.Value);
                lock (_activePromptGate)
                {
                    if (_activePrompt is not null || _activeTaskReportCapture is not null) throw new WorkerProtocolException("Active prompt ownership is ambiguous.");
                    _activePrompt = promptContext;
                    _activeTaskReportCapture = reportCapture;
                }
            }
            using var normalized = NormalizeEnvelope(envelope, correlationId.Value);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_responses.TryAdd(correlationId.Value, completion)) throw new WorkerProtocolException("ACP correlation allocation failed.");
            await WriteAcpAsync(normalized.RootElement, _lifetime.Token).ConfigureAwait(false);
            _store.MarkForwarded(requestId);
            forwarded.TrySetResult(_store.GetRequest(requestId)!);
            var result = await completion.Task.ConfigureAwait(false);
            var state = result.TryGetProperty("error", out _) ? "failed" : "completed";
            string outcome;
            var stopReason = result.TryGetProperty("result", out var resultBody) && resultBody.ValueKind == JsonValueKind.Object && resultBody.TryGetProperty("stopReason", out var stop) ? stop.GetString() : null;
            if (captureTaskReport && promptContext?.CancellationRequested == true && !string.Equals(stopReason, "end_turn", StringComparison.Ordinal))
            {
                state = "failed";
                outcome = CancelledOutcome();
            }
            else if (captureTaskReport && state == "completed")
            {
                var captured = reportCapture!.Complete();
                if (!string.Equals(stopReason, "end_turn", StringComparison.Ordinal) || captured.Overflowed || string.IsNullOrWhiteSpace(captured.Text))
                {
                    state = "failed";
                    outcome = TaskReportFailureOutcome();
                }
                else
                {
                    if (!TryCanonicalizeFinalTaskReport(captured.Text, out var canonical))
                    {
                        state = "failed";
                        outcome = TaskReportFailureOutcome();
                    }
                    else outcome = canonical!;
                }
            }
            else outcome = SanitizeOutcome(result, state);
            _store.CompleteRequest(requestId, state, outcome);
            TryAppendObservation("acp-response", SanitizeObservation(result, requestId, correlationId));
            return _store.GetRequest(requestId)!;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            var current = _store.GetRequest(requestId);
            if (current?.State is "forwarding" or "forwarded") _store.MarkUncertain(requestId);
            var uncertain = new WorkerOperationUncertainException("ACP request completion is uncertain.", exception);
            forwarded.TrySetException(uncertain);
            throw uncertain;
        }
        catch (OperationCanceledException exception)
        {
            forwarded.TrySetException(exception);
            throw;
        }
        finally
        {
            if (correlationId is { } id) _responses.TryRemove(id, out _);
            if (promptContext is not null)
            {
                lock (_activePromptGate)
                {
                    if (ReferenceEquals(_activePrompt, promptContext)) _activePrompt = null;
                    if (ReferenceEquals(_activeTaskReportCapture, reportCapture)) _activeTaskReportCapture = null;
                }
            }
            if (prompt) _store.SetActiveRequest(null);
            if (promptOwned) _promptLock.Release();
            _operations.TryRemove(requestId, out _);
        }
    }

    public Task<StoredCancellation> CancelAsync(long epoch, string connectionNonce, string cancellationId, string targetRequestId, JsonElement envelope, CancellationToken connectionToken)
    {
        using var normalized = NormalizeCancellationEnvelope(envelope);
        Task<StoredCancellation> durable;
        lock (_cancellationGate)
        {
            if (_cancellations.TryGetValue(cancellationId, out var active)) return active.WaitAsync(connectionToken);
            var registered = _store.RegisterCancellation(epoch, connectionNonce, cancellationId, targetRequestId, normalized.RootElement);
            if (registered.State != "forwarding") return Task.FromResult(registered);
            durable = RunCancellationAsync(registered, normalized.RootElement.Clone());
            if (!_cancellations.TryAdd(cancellationId, durable)) throw new WorkerProtocolException("Cancellation ownership is ambiguous.");
        }
        return durable.WaitAsync(connectionToken);
    }

    private async Task<StoredCancellation> RunCancellationAsync(StoredCancellation registered, JsonElement envelope)
    {
        await Task.Yield();
        try
        {
            await WriteAcpAsync(envelope, _lifetime.Token).ConfigureAwait(false);
            lock (_activePromptGate)
            {
                if (_activePrompt is { } active && string.Equals(active.RequestId, registered.TargetRequestId, StringComparison.Ordinal))
                    active.CancellationRequested = true;
            }
            _store.MarkCancellationForwarded(registered.CancellationId);
            TryAppendObservation("cancel-forwarded", JsonSerializer.Serialize(new { registered.CancellationId, registered.TargetRequestId }, WorkerProtocol.JsonOptions));
            return _store.GetCancellation(registered.CancellationId)!;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            _store.MarkCancellationUncertain(registered.CancellationId);
            throw new WorkerOperationUncertainException("Cancellation delivery is uncertain.", exception);
        }
        finally { lock (_cancellationGate) _cancellations.TryRemove(registered.CancellationId, out _); }
    }

    public Task<PendingPermission> DecidePermissionAsync(string decisionId, long generation, string requestId, string turnId, string decision, CancellationToken connectionToken) => DecidePermissionAsync(_store.Status().OwnershipEpoch, decisionId, generation, requestId, turnId, decision, connectionToken);
    public Task<PendingPermission> DecidePermissionAsync(long ownershipEpoch, string decisionId, long generation, string requestId, string turnId, string decision, CancellationToken connectionToken)
    {
        var deciding = _store.BeginPermissionDecision(ownershipEpoch, decisionId, generation, requestId, turnId, decision);
        if (deciding.State == "decided") return Task.FromResult(deciding);
        var durable = _permissionDecisions.GetOrAdd(decisionId, _ => RunPermissionDecisionAsync(deciding));
        return durable.WaitAsync(connectionToken);
    }

    private async Task<PendingPermission> RunPermissionDecisionAsync(PendingPermission deciding)
    {
        try
        {
            if (!_permissionFrames.TryGetValue(deciding.DecisionId, out var frame)) { _store.MarkPermissionUncertain(deciding.DecisionId); throw new WorkerOperationUncertainException("Permission response transport requires reconciliation."); }
            var response = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = frame.RequestId, ["result"] = new { outcome = new { outcome = "selected", optionId = deciding.Decision } } };
            try { await WriteAcpObjectAsync(response, _lifetime.Token).ConfigureAwait(false); }
            catch (Exception exception) { _store.MarkPermissionUncertain(deciding.DecisionId); throw new WorkerOperationUncertainException("Permission response delivery is uncertain.", exception); }
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
        try { _observations.AppendEvent(kind, payload); }
        catch (WorkerReplayLossException) { }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
        catch
        {
            try { _observations.SetJournalFailure("observation-append"); }
            catch { _store.FailClosedJournalInMemory(); }
        }
    }

    private async Task ReadAcpAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var document = await _acpReader.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                if (document is null) break;
                using (document)
                {
                    var root = document.RootElement;
                    ObserveTurnCaptures(root);
                    if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && !root.TryGetProperty("method", out _) && _responses.TryRemove(id.GetInt64(), out var response)) { response.TrySetResult(root.Clone()); continue; }
                    if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "session/request_permission" && root.TryGetProperty("id", out _))
                        await HandlePermissionRequestAsync(root).ConfigureAwait(false);
                    else TryAppendObservation("acp-event", SanitizeObservation(root, null, root.TryGetProperty("id", out var observedId) && observedId.TryGetInt64(out var value) ? value : null));
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (AcpProtocolException) { _store.SetProcessFailure("protocol-failed", "acp-protocol-failed"); }
        catch (Exception) { _store.SetProcessFailure("transport-uncertain", "acp-transport-uncertain"); }
        finally
        {
            FailPendingResponses();
            if (_store.Status().ProcessState == "running") _store.SetProcessFailure("exited", "process-exited");
        }
    }

    /// <summary>
    /// Feeds the active comprehension capture from every ACP frame before the
    /// general observation path. Only correlated text chunks are retained in
    /// memory, and the capture seals on the matching response id.
    /// </summary>
    private void ObserveTurnCaptures(JsonElement frame)
    {
        TurnTextCapture? comprehension;
        TurnTextCapture? report;
        lock (_activePromptGate)
        {
            comprehension = _activeComprehensionCapture;
            report = _activeTaskReportCapture;
        }
        comprehension?.TryAppend(frame);
        comprehension?.TrySeal(frame);
        report?.TryAppend(frame);
        report?.TrySeal(frame);
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
        PendingPermission pending;
        try { pending = _store.AddPermission(context.OwnershipEpoch, context.RequestId, context.TurnId, decisionId, parameters); }
        catch (WorkerProtocolException)
        {
            await RejectUnboundPermissionAsync(root, parameters).ConfigureAwait(false);
            return;
        }
        if (!_permissionFrames.TryAdd(decisionId, (root.GetProperty("id").Clone(), pending)))
        {
            _store.InvalidatePermission(decisionId);
            await RejectUnboundPermissionAsync(root, parameters).ConfigureAwait(false);
            return;
        }
        TryAppendObservation("permission-pending", JsonSerializer.Serialize(pending, WorkerProtocol.JsonOptions));
    }

    private async Task RejectUnboundPermissionAsync(JsonElement root, JsonElement parameters)
    {
        object result = new { outcome = new { outcome = "cancelled" } };
        if (parameters.ValueKind == JsonValueKind.Object
            && WorkerProtocol.SelectRejectOptionFromPermissionFrame(parameters, oneShotOnly: true) is { } optionId)
            result = new { outcome = new { outcome = "selected", optionId } };
        var response = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = root.GetProperty("id").Clone(), ["result"] = result };
        try { await WriteAcpObjectAsync(response, _lifetime.Token).ConfigureAwait(false); }
        catch { _store.SetProcessFailure("transport-uncertain", "permission-binding-uncertain"); }
    }

    private static string CreateDecisionId() => "perm:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static bool SessionMatches(string expected, JsonElement parameters) =>
        parameters.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String && string.Equals(expected, session.GetString(), StringComparison.Ordinal);

    private static bool IsPrompt(JsonElement envelope) => envelope.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "session/prompt";

    private static void ValidatePromptEnvelope(JsonElement envelope)
    {
        if (!IsPrompt(envelope)) throw new WorkerProtocolException("Only session/prompt submissions are supported.");
        foreach (var property in envelope.EnumerateObject()) if (property.Name is not ("method" or "params")) throw new WorkerProtocolException("ACP envelope contains unsupported fields.");
        if (!envelope.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String || session.GetString() is not { } sessionId) throw new WorkerProtocolException("session/prompt requires sessionId.");
        WorkerProtocol.ValidateIdentifier(sessionId, WorkerProtocol.MaxIdentifierLength, "session id");
        WorkerProtocol.ValidatePromptContentBlocks(parameters);
    }

    private static JsonDocument NormalizeCancellationEnvelope(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || method.GetString() != "session/cancel")
            throw new WorkerProtocolException("Cancellation envelope must use session/cancel.");
        foreach (var property in envelope.EnumerateObject()) if (property.Name is not ("jsonrpc" or "method" or "params")) throw new WorkerProtocolException("Cancellation envelope contains unsupported fields.");
        if (envelope.TryGetProperty("jsonrpc", out var version) && (version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")) throw new WorkerProtocolException("Cancellation envelope has an invalid JSON-RPC version.");
        if (!envelope.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) throw new WorkerProtocolException("Cancellation params are required.");
        return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = "session/cancel", ["params"] = parameters.Clone() }, WorkerProtocol.JsonOptions));
    }

    private static JsonDocument NormalizeEnvelope(JsonElement envelope, long id)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) throw new WorkerProtocolException("ACP envelope requires a method.");
        foreach (var property in envelope.EnumerateObject()) if (property.Name is not ("method" or "params")) throw new WorkerProtocolException("ACP envelope contains unsupported fields.");
        var value = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method.GetString() };
        if (envelope.TryGetProperty("params", out var parameters)) value["params"] = parameters.Clone();
        return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, WorkerProtocol.JsonOptions));
    }
    private static string TaskReportFailureOutcome() => JsonSerializer.Serialize(new { category = "model-report-invalid" }, WorkerProtocol.JsonOptions);
    private static string CancelledOutcome() => JsonSerializer.Serialize(new { category = "cancelled" }, WorkerProtocol.JsonOptions);

    private static bool TryCanonicalizeFinalTaskReport(string text, out string? canonical)
    {
        canonical = null;
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        if (end == 0) return false;
        if (text.AsSpan(0, end).EndsWith("```", StringComparison.Ordinal))
        {
            var closing = end - 3;
            var opening = text.LastIndexOf("```json", closing - 1, StringComparison.Ordinal);
            if (opening >= 0)
            {
                var jsonStart = opening + "```json".Length;
                while (jsonStart < closing && char.IsWhiteSpace(text[jsonStart])) jsonStart++;
                var jsonEnd = closing;
                while (jsonEnd > jsonStart && char.IsWhiteSpace(text[jsonEnd - 1])) jsonEnd--;
                if (TryCanonicalizeTaskReportCandidate(text.AsMemory(jsonStart, jsonEnd - jsonStart), out canonical)) return true;
            }
        }
        for (var start = text.LastIndexOf('{', end - 1); start >= 0; start = start == 0 ? -1 : text.LastIndexOf('{', start - 1))
        {
            if (HasUnbalancedFence(text.AsSpan(0, start))) continue;
            if (TryCanonicalizeTaskReportCandidate(text.AsMemory(start, end - start), out canonical)) return true;
        }
        canonical = null;
        return false;
    }

    private static bool TryCanonicalizeTaskReportCandidate(ReadOnlyMemory<char> candidate, out string? canonical)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate, new JsonDocumentOptions { MaxDepth = 32 });
            return ModelTaskReportShape.TryCanonicalize(document.RootElement, out canonical, out _);
        }
        catch (JsonException)
        {
            canonical = null;
            return false;
        }
    }

    private static bool HasUnbalancedFence(ReadOnlySpan<char> prefix)
    {
        var count = 0;
        for (var index = prefix.IndexOf("```", StringComparison.Ordinal); index >= 0;)
        {
            count++;
            prefix = prefix[(index + 3)..];
            index = prefix.IndexOf("```", StringComparison.Ordinal);
        }
        return (count & 1) != 0;
    }

    private static string SanitizeOutcome(JsonElement result, string state)
    {
        var raw = result.GetRawText();
        long? category = null;
        if (result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out var numericCode)) category = numericCode;
        return JsonSerializer.Serialize(new { state, errorCategory = category, sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), byteCount = System.Text.Encoding.UTF8.GetByteCount(raw) }, WorkerProtocol.JsonOptions);
    }

    private static string SanitizeObservation(JsonElement frame, string? requestId, long? correlationId)
    {
        var raw = frame.GetRawText();
        var method = frame.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String ? methodElement.GetString() : null;
        return JsonSerializer.Serialize(new { method, requestId, correlationId, sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), byteCount = System.Text.Encoding.UTF8.GetByteCount(raw) }, WorkerProtocol.JsonOptions);
    }

    private Task WriteAcpAsync(JsonElement value, CancellationToken token) => WriteAcpObjectAsync(value, token);
    private async Task WriteAcpObjectAsync(object value, CancellationToken token, Action? beforeWrite = null)
    {
        if (Volatile.Read(ref _transportFailed) != 0) throw new WorkerProtocolException("ACP transport is faulted and cannot accept writes.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _transportFault.Token);
        await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _transportFailed) != 0) throw new WorkerProtocolException("ACP transport is faulted and cannot accept writes.");
            beforeWrite?.Invoke();
            try { await WorkerProtocol.WriteAcpFrameAsync(_acpOutput, value, linked.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException or OperationCanceledException && !_lifetime.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _transportFailed, 1) == 0)
                {
                    _transportFault.Cancel();
                    _store.SetProcessFailure("transport-uncertain", "acp-transport-uncertain");
                    FailPendingResponses();
                }
                throw new WorkerProtocolException("ACP transport write is uncertain.", exception);
            }
        }
        finally { _writeLock.Release(); }
    }
    internal bool IsTransportHealthy => Volatile.Read(ref _transportFailed) == 0;
    private sealed class ActivePromptContext(string requestId, string turnId, long processGeneration, long ownershipEpoch, long correlationId, string sessionId)
    {
        public string RequestId { get; } = requestId;
        public string TurnId { get; } = turnId;
        public long ProcessGeneration { get; } = processGeneration;
        public long OwnershipEpoch { get; } = ownershipEpoch;
        public long CorrelationId { get; } = correlationId;
        public string SessionId { get; } = sessionId;
        public bool CancellationRequested { get; set; }
    }
    private sealed class OperationHandle(TaskCompletionSource<StoredRequest> forwarded)
    {
        public TaskCompletionSource<StoredRequest> Forwarded { get; } = forwarded;
        public Task<StoredRequest> Completion { get; set; } = Task.FromException<StoredRequest>(new InvalidOperationException("Operation completion was not initialized."));
    }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _lifetime.Cancel(); _transportFault.Cancel(); _acpInput.Dispose(); _acpOutput.Dispose(); if (_reader is not null) try { await _reader.ConfigureAwait(false); } catch { } var operations = _operations.Values.Select(x => (Task)x.Completion).Concat(_cancellations.Values).Concat(_permissionDecisions.Values).ToArray(); if (operations.Length > 0) try { await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { } _permissionFrames.Clear(); _writeLock.Dispose(); _promptLock.Dispose(); _sessionLock.Dispose(); _transportFault.Dispose(); _lifetime.Dispose(); }
    public static FileStream OpenInheritedFd(int fd, FileAccess access) => new(new SafeFileHandle((IntPtr)fd, ownsHandle: false), access, 4096, isAsync: false);
}

public sealed class WorkerBridge : IAsyncDisposable
{
    private readonly WorkerOptions _options; private readonly WorkerStore _store; private readonly WorkerRuntime _runtime; private readonly byte[] _key; private readonly IWorkerClock _clock;
    private readonly Dictionary<string, long> _nonces = new(StringComparer.Ordinal); private readonly object _nonceLock = new(); private Socket? _listener;
    private readonly SemaphoreSlim _connections;
    private readonly SemaphoreSlim _authenticating;
    private readonly SemaphoreSlim _viewer = new(1, 1);
    private readonly IWorkerTerminalBackend _terminal;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private long _nextHandler;
    public WorkerBridge(WorkerOptions options, WorkerStore store, WorkerRuntime runtime, byte[] key, IWorkerClock? clock = null, IWorkerTerminalBackend? terminal = null) { _options = options; _store = store; _runtime = runtime; _key = key; _clock = clock ?? new SystemWorkerClock(); _terminal = terminal ?? new UnavailableWorkerTerminalBackend(); _connections = new(options.MaxConnections, options.MaxConnections); _authenticating = new(options.MaxAuthenticatingConnections, options.MaxAuthenticatingConnections); }

    public async Task RunAsync(CancellationToken token)
    {
        SecureStaleSocket();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        var bridgeToken = lifetime.Token;
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); _listener.Bind(new UnixDomainSocketEndPoint(_options.SocketPath));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_options.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(Math.Min(128, _options.MaxConnections));
        var failures = 0;
        while (!bridgeToken.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptAsync(bridgeToken).ConfigureAwait(false); failures = 0; }
            catch (OperationCanceledException) { break; }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.Interrupted or SocketError.TryAgain or SocketError.WouldBlock or SocketError.NoBufferSpaceAvailable or SocketError.TooManyOpenSockets)
            {
                failures = Math.Min(failures + 1, 6);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (1 << failures)), bridgeToken).ConfigureAwait(false);
                continue;
            }
            if (!_connections.Wait(0)) { socket.Dispose(); continue; }
            var id = Interlocked.Increment(ref _nextHandler);
            var handler = Task.Run(async () => { try { await HandleAsync(socket, bridgeToken).ConfigureAwait(false); } finally { _connections.Release(); } }, CancellationToken.None);
            _handlers[id] = handler;
            _ = handler.ContinueWith(_ => { _handlers.TryRemove(id, out Task? ignored); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(Socket socket, CancellationToken bridgeToken)
    {
        using (socket) using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            var authOwned = false;
            try
            {
                if (!_authenticating.Wait(0)) throw new WorkerProtocolException("Authentication capacity is exhausted.");
                authOwned = true;
                VerifyPeer(socket);
                using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(bridgeToken); authTimeout.CancelAfter(_options.ChallengeLifetime);
                using var hello = await WorkerProtocol.ReadFrameAsync(stream, authTimeout.Token).ConfigureAwait(false) ?? throw new WorkerProtocolException("Authentication hello is required.");
                var h = hello.RootElement;
                if (Required(h, "type") != "hello") throw new WorkerProtocolException("Authentication hello is invalid.");
                var version = Required(h, "version"); var role = Required(h, "role"); var controller = Required(h, "controllerId"); var worker = Required(h, "workerId"); var keyId = Required(h, "keyId"); var clientNonce = Required(h, "clientNonce");
                if (role == WorkerProtocol.ControllerRole) RequireExactFields(h, "type", "version", "role", "controllerId", "workerId", "keyId", "clientNonce");
                else if (role == WorkerProtocol.ViewerRole) RequireExactFields(h, "type", "version", "role", "controllerId", "workerId", "keyId", "clientNonce", "ownershipEpoch", "connectionNonce", "sessionId");
                else throw new WorkerProtocolException("Authentication role is invalid.");
                ValidateAuthIdentity(controller, worker, keyId, clientNonce);
                if (version != WorkerProtocol.Version || controller != _options.ControllerId || worker != _options.WorkerId || keyId != WorkerProtocol.KeyId(_key)) throw new WorkerProtocolException("Authentication identity mismatch.");
                UseNonce(clientNonce);
                var serverNonceBytes = RandomNumberGenerator.GetBytes(WorkerProtocol.NonceBytes);
                try
                {
                    var serverNonce = Convert.ToBase64String(serverNonceBytes); var issued = _clock.UtcNow.ToUnixTimeMilliseconds();
                    var serverMac = role == WorkerProtocol.ViewerRole
                        ? WorkerProtocol.ComputeViewerMac(_key, "server-proof", controller, worker, keyId, clientNonce, serverNonce, issued, RequiredInt64(h, "ownershipEpoch", 1), Required(h, "connectionNonce"), RequiredBounded(h, "sessionId"))
                        : WorkerProtocol.ComputeMac(_key, "server-proof", role, controller, worker, keyId, clientNonce, serverNonce, issued);
                    await WorkerProtocol.WriteFrameAsync(stream, new { type = "challenge", version, role, controllerId = controller, workerId = worker, keyId, clientNonce, serverNonce, issuedUnixMilliseconds = issued, mac = serverMac }, authTimeout.Token);
                    using var proof = await WorkerProtocol.ReadFrameAsync(stream, authTimeout.Token).ConfigureAwait(false) ?? throw new WorkerProtocolException("Client proof is required.");
                    RequireExactFields(proof.RootElement, "type", "mac"); if (Required(proof.RootElement, "type") != "proof") throw new WorkerProtocolException("Client proof is invalid.");
                    var expected = role == WorkerProtocol.ViewerRole
                        ? WorkerProtocol.ComputeViewerMac(_key, "client-proof", controller, worker, keyId, clientNonce, serverNonce, issued, RequiredInt64(h, "ownershipEpoch", 1), Required(h, "connectionNonce"), RequiredBounded(h, "sessionId"))
                        : WorkerProtocol.ComputeMac(_key, "client-proof", role, controller, worker, keyId, clientNonce, serverNonce, issued);
                    if (!IsProofFresh(_clock.UtcNow.ToUnixTimeMilliseconds(), issued, _options.ChallengeLifetime) || !WorkerProtocol.VerifyMac(expected, Required(proof.RootElement, "mac"))) throw new WorkerProtocolException("Client proof rejected.");
                }
                finally { CryptographicOperations.ZeroMemory(serverNonceBytes); }
                if (role == WorkerProtocol.ViewerRole)
                {
                    var epoch = RequiredInt64(h, "ownershipEpoch", 1); var connectionNonce = Required(h, "connectionNonce"); var sessionId = RequiredBounded(h, "sessionId");
                    _store.RequireViewerLease(epoch, connectionNonce, sessionId);
                    if (!_terminal.Available) throw new WorkerProtocolException("Worker terminal backend is unavailable.");
                    if (!_viewer.Wait(0)) throw new WorkerProtocolException("A viewer is already attached.");
                    _authenticating.Release(); authOwned = false;
                    try { await WorkerProtocol.WriteFrameAsync(stream, new { type = "authenticated", role = WorkerProtocol.ViewerRole, sessionId }, bridgeToken); await RunViewerAsync(stream, epoch, connectionNonce, sessionId, bridgeToken).ConfigureAwait(false); }
                    catch (WorkerTerminalStopUncertainException)
                    {
                        // A viewer may still hold the session. Record it so the controller
                        // sees an unavailable viewer instead of a clean detach; dispatch and
                        // the ACP process are unaffected.
                        _store.SetViewerHold("viewer-stop-uncertain");
                        throw;
                    }
                    finally { _viewer.Release(); }
                }
                else
                {
                    var connectionNonceBytes = RandomNumberGenerator.GetBytes(WorkerProtocol.NonceBytes);
                    Lease lease;
                    try { lease = _store.AcquireLease(controller, Convert.ToBase64String(connectionNonceBytes)); }
                    finally { CryptographicOperations.ZeroMemory(connectionNonceBytes); }
                    await WorkerProtocol.WriteFrameAsync(stream, new { type = "authenticated", lease }, bridgeToken);
                    _authenticating.Release(); authOwned = false;
                    var reader = WorkerProtocol.CreateControlReader(stream);
                    while (true)
                    {
                        // Framing failures remain outside the per-operation handler:
                        // a malformed/partial frame destroys stream synchronization and
                        // therefore closes the connection.
                        using var frame = await reader.ReadAsync(bridgeToken).ConfigureAwait(false);
                        if (frame is null) break;
                        try
                        {
                            await DispatchAsync(stream, frame.RootElement, lease, bridgeToken).ConfigureAwait(false);
                        }
                        catch (WorkerOperationUncertainException)
                        {
                            // The durable mutation may already have reached ACP. Report only
                            // the fixed category, then close so this owner session cannot be
                            // reused as though the operation were a clean rejection.
                            try { await WorkerProtocol.WriteFrameAsync(stream, new { type = "error", error = "worker-operation-uncertain" }, bridgeToken).ConfigureAwait(false); } catch { }
                            break;
                        }
                        catch (WorkerProtocolException)
                        {
                            // Pre-effect validation/replay/hold rejection is recoverable on
                            // the same authenticated stream only while this socket still owns
                            // the current live lease. Never disclose exception detail.
                            await WorkerProtocol.WriteFrameAsync(stream, new { type = "error", error = "worker-request-rejected" }, bridgeToken).ConfigureAwait(false);
                            if (!_store.IsLeaseCurrent(lease.Epoch, lease.ConnectionNonce) || !_runtime.IsTransportHealthy) break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WorkerProtocolException) { try { await WorkerProtocol.WriteFrameAsync(stream, new { type = "error", error = "worker-request-rejected" }, CancellationToken.None); } catch { } }
            catch (Exception) { try { await WorkerProtocol.WriteFrameAsync(stream, new { type = "error", error = "worker-operation-failed" }, CancellationToken.None); } catch { } }
            finally { if (authOwned) _authenticating.Release(); }
        }
    }

    /// <summary>
    /// Runs one attached viewer session.
    /// </summary>
    /// <remarks>
    /// Failure is always announced. Whichever pump fails first cancels the shared
    /// lifetime and the session ends with an explicit typed <c>close</c> frame
    /// carrying a fixed category, so the controller and the browser see a failed
    /// viewer instead of a silently truncated stream that looks like an idle
    /// terminal. Both pumps are observed before returning, so neither can outlive
    /// the session or hide its failure.
    /// </remarks>
    private async Task RunViewerAsync(Stream stream, long epoch, string connectionNonce, string sessionId, CancellationToken token)
    {
        await using var terminal = await _terminal.AttachAsync(sessionId, token).ConfigureAwait(false);
        // The attach succeeded, so no earlier viewer still holds the PTY: the
        // supervisor refuses a second viewer while one is alive.
        _store.ClearViewerHold();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var closure = new ViewerClosure();
        var output = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in terminal.ReadOutputAsync(lifetime.Token).ConfigureAwait(false))
                {
                    _store.RequireViewerLease(epoch, connectionNonce, sessionId);
                    if (chunk.Data.Length > WorkerProtocol.MaxViewerOutputBytes) throw new WorkerProtocolException("Viewer output exceeds the fixed chunk limit.");
                    await WorkerProtocol.WriteFrameAsync(stream, new { type = "output", sessionId, data = Convert.ToBase64String(chunk.Data) }, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
            catch
            {
                // The output pump owns the socket writer, so it records the category and
                // cancels the input side rather than writing a frame concurrently.
                closure.Set("viewer-output-failed");
                await lifetime.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }, CancellationToken.None);
        try
        {
            var reader = WorkerProtocol.CreateControlReader(stream);
            while (true)
            {
                using var frame = await reader.ReadAsync(lifetime.Token).ConfigureAwait(false); if (frame is null) break;
                _store.RequireViewerLease(epoch, connectionNonce, sessionId);
                var root = frame.RootElement; if (Required(root, "sessionId") != sessionId) throw new WorkerProtocolException("Viewer frame session changed.");
                switch (Required(root, "type"))
                {
                    case "input":
                        {
                            var bytes = WorkerViewerProtocol.DecodeInput(Required(root, "data"));
                            try { await terminal.WriteInputAsync(bytes, lifetime.Token).ConfigureAwait(false); }
                            catch (WorkerTerminalWriteUncertainException uncertain)
                            {
                                // Part of the keystrokes may have reached the PTY. Never retry and
                                // never pretend it succeeded: end the session with the exact
                                // category and the byte count, and expose no input content.
                                closure.Set("input-uncertain", uncertain.BytesWritten);
                                throw;
                            }
                            finally { CryptographicOperations.ZeroMemory(bytes); }
                            break;
                        }
                    case "resize": { var rows = checked((int)RequiredInt64(root, "rows", 1)); var columns = checked((int)RequiredInt64(root, "columns", 1)); if (rows > 500 || columns > 500) throw new WorkerProtocolException("Viewer dimensions exceed the fixed limit."); await terminal.ResizeAsync(rows, columns, lifetime.Token).ConfigureAwait(false); break; }
                    case "detach": return;
                    default: throw new WorkerProtocolException("Viewer operation is not permitted.");
                }
            }
        }
        catch (Exception exception)
        {
            closure.Set(exception is WorkerProtocolException ? "viewer-protocol-failed" : "viewer-input-failed");
            throw;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await output.ConfigureAwait(false); } catch (OperationCanceledException) { } catch { }
            await closure.SendAsync(stream, sessionId).ConfigureAwait(false);
        }
    }

    /// <summary>Records the first viewer failure category and emits the single closing frame.</summary>
    private sealed class ViewerClosure
    {
        private string? _category;
        private int? _bytesWritten;
        private int _sent;

        public void Set(string category, int? bytesWritten = null)
        {
            if (Interlocked.CompareExchange(ref _category, category, null) is null) _bytesWritten = bytesWritten;
        }

        public async Task SendAsync(Stream stream, string sessionId)
        {
            if (_category is not { } category || Interlocked.Exchange(ref _sent, 1) != 0) return;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                object frame = _bytesWritten is { } written
                    ? new { type = "close", sessionId, category, bytesWritten = written }
                    : new { type = "close", sessionId, category };
                await WorkerProtocol.WriteFrameAsync(stream, frame, timeout.Token).ConfigureAwait(false);
            }
            catch { /* The peer is already gone; the controller still observes the closed stream. */ }
        }
    }

    internal async Task DispatchAsync(Stream stream, JsonElement message, Lease socketLease, CancellationToken connectionToken)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new WorkerProtocolException("Control message must be an object.");
        var operation = Required(message, "operation");
        _store.RequireLease(socketLease.Epoch, socketLease.ConnectionNonce);
        var mutation = operation is "heartbeat" or "hold" or "ack-events" or "reconcile-replay-loss" or "reconcile-replay-gap" or "reconcile-journal" or "new-session" or "load-session" or "submit" or "cancel" or "permission" or "stop-process" or "install-orientation" or "orientation-comprehension";
        if (mutation)
        {
            var epoch = RequiredInt64(message, "epoch", 1);
            var nonce = Required(message, "connectionNonce");
            if (epoch != socketLease.Epoch || nonce != socketLease.ConnectionNonce) throw new WorkerProtocolException("Operation lease does not match its authenticated socket.");
            _store.RequireLease(epoch, nonce);
        }
        object result = operation switch
        {
            "status" => ViewerStatus(),
            "heartbeat" => Run(() => _store.Heartbeat(socketLease.Epoch, socketLease.ConnectionNonce)),
            "hold" => Run(() => _store.SetHold(RequiredBoolean(message, "held"), OptionalString(message, "reason", 128))),
            "replay" => _store.Replay(RequiredInt64(message, "workerGeneration", 0), RequiredInt64(message, "afterSequence", 0)),
            "ack-events" => Run(() => _store.Acknowledge(RequiredInt64(message, "workerGeneration", 0), RequiredInt64(message, "sequence", 0))),
            "reconcile-replay-loss" => Run(() => _store.ReconcileReplayLoss(RequiredInt64(message, "workerGeneration", 1), RequiredInt64(message, "markerSequence", 1))),
            "reconcile-replay-gap" => Run(() => _store.ReconcileReplayGap(RequiredBounded(message, "gapId"), RequiredInt64(message, "workerGeneration", 0), RequiredInt64(message, "afterSequence", 0), RequiredInt64(message, "firstRetainedSequence", 0), RequiredInt64(message, "lastSequence", 0))),
            "reconcile-journal" => Run(() => _store.ReconcileJournalFailure(RequiredBounded(message, "operationId"), RequiredInt64(message, "workerGeneration", 1))),
            "reconcile" => Reconcile(message),
            "stop-process" => await RunAsync(StopProcessAsync).ConfigureAwait(false),
            "new-session" => await _runtime.NewSessionAsync(socketLease.Epoch, socketLease.ConnectionNonce, connectionToken).ConfigureAwait(false),
            "load-session" => await _runtime.LoadSessionAsync(socketLease.Epoch, socketLease.ConnectionNonce, RequiredBounded(message, "sessionId"), connectionToken).ConfigureAwait(false),
            "submit" => await _runtime.SubmitAsync(socketLease.Epoch, socketLease.ConnectionNonce, RequiredBounded(message, "requestId"), RequiredElement(message, "envelope", JsonValueKind.Object), OptionalString(message, "turnId", WorkerProtocol.MaxIdentifierLength), connectionToken, OptionalBoolean(message, "captureTaskReport")).ConfigureAwait(false),
            "cancel" => await _runtime.CancelAsync(socketLease.Epoch, socketLease.ConnectionNonce, RequiredBounded(message, "cancellationId"), RequiredBounded(message, "targetRequestId"), RequiredElement(message, "envelope", JsonValueKind.Object), connectionToken).ConfigureAwait(false),
            "permission" => await _runtime.DecidePermissionAsync(socketLease.Epoch, RequiredBounded(message, "decisionId"), RequiredInt64(message, "processGeneration", 0), RequiredBounded(message, "requestId"), RequiredBounded(message, "turnId"), RequiredBounded(message, "decision"), connectionToken).ConfigureAwait(false),
            "install-orientation" => await InstallOrientationAsync(socketLease, message, connectionToken).ConfigureAwait(false),
            "orientation-comprehension" => await RunComprehensionAsync(socketLease, message, connectionToken).ConfigureAwait(false),
            _ => throw new WorkerProtocolException("Unsupported operation.")
        };
        await WorkerProtocol.WriteFrameAsync(stream, new { type = "result", operation, result }, connectionToken);
    }

    private async Task<OrientationInstallRecord> InstallOrientationAsync(Lease socketLease, JsonElement message, CancellationToken connectionToken)
    {
        RequireExactControlFields(message, "operation", "epoch", "connectionNonce", "assignmentId", "orientationVersion", "artifactFileName", "contentHash", "content");
        var assignmentId = RequiredBounded(message, "assignmentId");
        var orientationVersion = RequiredOrientationVersion(message, "orientationVersion");
        var artifactFileName = RequiredOrientationFileName(message, "artifactFileName");
        var contentHash = RequiredOrientationContentHash(message, "contentHash");
        var content = RequiredOrientationContent(message, "content");
        return await _runtime.InstallOrientationAsync(socketLease, assignmentId, orientationVersion, artifactFileName, content, contentHash, connectionToken).ConfigureAwait(false);
    }

    private async Task<OrientationComprehensionRecord> RunComprehensionAsync(Lease socketLease, JsonElement message, CancellationToken connectionToken)
    {
        RequireExactControlFields(message, "operation", "epoch", "connectionNonce", "assignmentId", "employeeId", "sessionId", "orientationVersion");
        var assignmentId = RequiredBounded(message, "assignmentId");
        var employeeId = RequiredBounded(message, "employeeId");
        var sessionId = RequiredBounded(message, "sessionId");
        var orientationVersion = RequiredOrientationVersion(message, "orientationVersion");
        return await _runtime.RunOrientationComprehensionAsync(socketLease, assignmentId, employeeId, sessionId, orientationVersion, connectionToken).ConfigureAwait(false);
    }

    private static async Task StopProcessAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint("/run/worker-supervisor.sock"), timeout.Token).ConfigureAwait(false);
        using var stream = new NetworkStream(socket);
        await WorkerProtocol.WriteFrameAsync(stream, new { operation = "stop" }, timeout.Token).ConfigureAwait(false);
        using var response = await WorkerProtocol.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
        if (response is null || !response.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) throw new WorkerProtocolException("The fixed supervisor stop result is uncertain.");
    }

    private WorkerStatus ViewerStatus()
    {
        var status = _store.Status();
        // An unconfirmed previous teardown suppresses viewer availability until a
        // fresh attach proves the slot is free.
        var available = _terminal.Available && _store.ViewerSessionBound() && _store.ViewerHoldReason() is null;
        return status with { ViewerSupported = _terminal.Available, ViewerAvailable = available };
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
        try { WorkerStore.ValidateOwnedSocket(_options.SocketPath, _options.ExpectedBridgeUid); }
        catch (WorkerStoreException exception) { throw new WorkerProtocolException("The existing bridge socket is not a stale owned socket.", exception); }
        File.Delete(_options.SocketPath);
    }
    private static object Run(Action action) { action(); return new { ok = true }; }
    private static async Task<object> RunAsync(Func<Task> action) { await action().ConfigureAwait(false); return new { ok = true }; }
    private static string Required(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()! : throw new WorkerProtocolException($"Missing {name}.");
    private static string RequiredBounded(JsonElement element, string name) { var value = Required(element, name); WorkerProtocol.ValidateIdentifier(value, WorkerProtocol.MaxIdentifierLength, name); return value; }
    private static string RequiredOrientationVersion(JsonElement element, string name) { var value = Required(element, name); WorkerProtocol.ValidateOrientationVersion(value); return value; }
    private static string RequiredOrientationFileName(JsonElement element, string name) { var value = Required(element, name); WorkerProtocol.ValidateOrientationFileName(value); return value; }
    private static string RequiredOrientationContentHash(JsonElement element, string name) { var value = Required(element, name); WorkerProtocol.ValidateOrientationContentHash(value); return value; }
    private static string RequiredOrientationContent(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String || property.GetString() is not { } value)
            throw new WorkerProtocolException($"Invalid {name}.");
        _ = WorkerProtocol.EncodeOrientationContent(value);
        return value;
    }
    private static void RequireExactControlFields(JsonElement element, params string[] fields) { if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new WorkerProtocolException("Control message fields are invalid."); }
    private static long RequiredInt64(JsonElement element, string name, long minimum) { if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value) || value < minimum) throw new WorkerProtocolException($"Invalid {name}."); return value; }
    private static bool RequiredBoolean(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : throw new WorkerProtocolException($"Invalid {name}.");
    private static bool OptionalBoolean(JsonElement element, string name) => !element.TryGetProperty(name, out var property) ? false : property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : throw new WorkerProtocolException($"Invalid {name}.");
    private static string? OptionalString(JsonElement element, string name, int maximum) { if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null; if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } value || value.Length > maximum) throw new WorkerProtocolException($"Invalid {name}."); return value; }
    private static JsonElement RequiredElement(JsonElement element, string name, JsonValueKind kind) => element.TryGetProperty(name, out var property) && property.ValueKind == kind ? property : throw new WorkerProtocolException($"Invalid {name}.");
    private static void RequireExactFields(JsonElement element, params string[] fields) { if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal), StringComparer.Ordinal) is false) throw new WorkerProtocolException("Authentication message fields are invalid."); }
    private static void VerifyPeer(Socket socket) { if (!OperatingSystem.IsLinux()) return; var size = (uint)Marshal.SizeOf<UCred>(); if (getsockopt(socket.Handle.ToInt32(), 1, 17, out var credential, ref size) != 0 || size != Marshal.SizeOf<UCred>() || credential.Uid != (uint)geteuid()) throw new WorkerProtocolException("Local peer identity is invalid."); }
    [StructLayout(LayoutKind.Sequential)] private struct UCred { public int Pid; public uint Uid; public uint Gid; }
    private sealed class ZeroingBuffer(byte[] value) : IDisposable { public void Dispose() => CryptographicOperations.ZeroMemory(value); }
    [DllImport("libc")] private static extern int geteuid();
    [DllImport("libc", SetLastError = true)] private static extern int getsockopt(int socket, int level, int optionName, out UCred value, ref uint length);
    public async ValueTask DisposeAsync() { _shutdown.Cancel(); _listener?.Dispose(); var handlers = _handlers.Values.ToArray(); if (handlers.Length > 0) try { await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { } CryptographicOperations.ZeroMemory(_key); await _runtime.DisposeAsync(); _store.Dispose(); _connections.Dispose(); _authenticating.Dispose(); _viewer.Dispose(); _shutdown.Dispose(); try { SecureStaleSocket(); } catch { } }
}
