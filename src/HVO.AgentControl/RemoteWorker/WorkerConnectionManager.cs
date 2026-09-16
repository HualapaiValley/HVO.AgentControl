using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Worker;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

public sealed record BridgePendingPermission(long ProcessGeneration, long OwnershipEpoch, string RequestId, string TurnId, string DecisionId, string PayloadHash, IReadOnlyList<string> OptionIds, string State, string? Decision);
public sealed record BridgeReplayLoss(long WorkerGeneration, long MarkerSequence, long DroppedCount, long DroppedBytes);
public sealed record BridgeJournalFailure(string OperationId, long WorkerGeneration, string ErrorCategory);
public sealed record BridgeReplayGap(string Id, string Kind, long WorkerGeneration, long AfterSequence, long FirstRetainedSequence, long LastSequence, long? LossMarkerGeneration, long? LossMarkerSequence);
public sealed record BridgeWorkerStatus(long WorkerGeneration, long ProcessGeneration, string ProcessState, string? LifecycleHandle, long? ObservedPid, string? ActiveRequestId, BridgePendingPermission? PendingPermission, long OwnershipEpoch, bool LeaseActive, bool DispatchHeld, string? HoldReason, IReadOnlyList<string> HoldReasons, long FirstRetainedSequence, long LastSequence, long AcknowledgedWorkerGeneration, long AcknowledgedSequence, BridgeReplayLoss? ReplayLoss, BridgeJournalFailure? JournalFailure, int ReplayGapCount, IReadOnlyList<BridgeReplayGap> ReplayGaps, bool ViewerSupported = false, bool ViewerAvailable = false);
public sealed record BridgeWorkerEvent(long WorkerGeneration, long Sequence, string Kind, string PayloadJson, int ByteCount);
public sealed record BridgeReplayPage(BridgeWorkerEvent?[]? Events, bool HasMore, long NextAfterSequence);
public sealed record BridgeStoredRequest(string RequestId, string PayloadHash, string State, string? OutcomeJson, long ProcessGeneration, long OwnershipEpoch, string TurnId, string SessionId);
public sealed record WorkerSessionResult(string Operation, JsonElement Result, bool WriteAttempted);
public interface IWorkerBridgeSession : IAsyncDisposable
{
    WorkerBridgeLease Lease { get; }
    Task<WorkerSessionResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken);
    object Mutation(string operation, IReadOnlyDictionary<string, object?> fields);
}
public interface IWorkerBridgeSessionFactory { Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken); }
public interface IWorkerDelay { Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken); }
public interface IControllerClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemWorkerDelay : IWorkerDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken); }
public sealed class SystemControllerClock : IControllerClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

internal sealed class WorkerBridgeSessionAdapter(WorkerBridgeClient client) : IWorkerBridgeSession
{
    public WorkerBridgeLease Lease => client.Lease;
    public object Mutation(string operation, IReadOnlyDictionary<string, object?> fields) => client.Mutation(operation, fields);
    public async Task<WorkerSessionResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken)
    {
        var result = await client.InvokeAsync(operation, request, mutation, cancellationToken).ConfigureAwait(false);
        return new(operation, result.Result, mutation);
    }
    public ValueTask DisposeAsync() => client.DisposeAsync();
}

public sealed class WorkerBridgeSessionFactory(IWorkerConnector connector, IOptions<WorkerControlOptions> configured) : IWorkerBridgeSessionFactory
{
    private readonly WorkerControlOptions _options = configured.Value;
    public async Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        var client = await WorkerBridgeClient.ConnectAsync(connector, enrollment.WorkerId, enrollment.ControllerId, enrollment.KeyFilePath, TimeSpan.FromSeconds(_options.AuthenticationTimeoutSeconds), cancellationToken, expectedControllerUid: _options.ExpectedControllerUid, operationTimeout: TimeSpan.FromSeconds(_options.OperationTimeoutSeconds)).ConfigureAwait(false);
        return new WorkerBridgeSessionAdapter(client);
    }
}

public sealed record RemoteDispatchCommand(string EmployeeId, string RuntimeBindingId, string WorkerId, string SessionRecordId, string NativeSessionId, string IdempotencyKey, string Prompt)
{
    public string SessionId => NativeSessionId;
}
public sealed record RemoteCancellationCommand(string RequestId);
public sealed record RemotePermissionRejectCommand(string WorkerId, string DecisionId, int Revision);

/// <summary>The controller-owned, long-lived authenticated session for one worker.</summary>
public sealed class WorkerConnectionLease : IAsyncDisposable
{
    internal WorkerConnectionLease(IWorkerBridgeSession session, BridgeWorkerStatus status, DateTimeOffset contactedAt)
    { Session = session; Status = status; LastContactUtc = contactedAt; }
    internal IWorkerBridgeSession Session { get; }
    public WorkerBridgeLease Ownership => Session.Lease;
    public BridgeWorkerStatus Status { get; internal set; }
    public DateTimeOffset LastContactUtc { get; internal set; }
    public ValueTask DisposeAsync() => Session.DisposeAsync();
}

public sealed class WorkerConnectionManager : IAsyncDisposable
{
    public const int MaxPromptCharacters = 64 * 1024;
    public const int MaxReplayPages = 10_001;
    public const int MaxReplayEvents = 10_000;

    private enum ReplaySequenceResult
    {
        Complete,
        HeldIncomplete,
    }
    private readonly AcpControlHost _control;
    private readonly IWorkerBridgeSessionFactory _sessions;
    private readonly WorkerControlOptions _options;
    private readonly IControllerClock _clock;
    private readonly IWorkerDelay _delay;
    private readonly ConcurrentDictionary<string, WorkerEntry> _workers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private bool _startupReconciled;

    public WorkerConnectionManager(AcpControlHost control, IWorkerBridgeSessionFactory sessions, IOptions<WorkerControlOptions> configured, IControllerClock clock, IWorkerDelay delay)
    { _control = control; _sessions = sessions; _options = configured.Value; _clock = clock; _delay = delay; }

    public async Task<WorkerConnectionLease> ConnectAndSynchronizeAsync(string workerId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        await EnsureStartupReconciledAsync(cancellationToken).ConfigureAwait(false);
        var entry = Entry(workerId);
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await EnsureConnectedAndSynchronizedLockedAsync(workerId, entry, cancellationToken).ConfigureAwait(false); }
        finally { entry.Gate.Release(); }
    }

    public async Task<WorkerCursorRecord> SynchronizeOnceAsync(string workerId, CancellationToken cancellationToken)
    {
        var lease = await ConnectAndSynchronizeAsync(workerId, cancellationToken).ConfigureAwait(false);
        return Store().GetWorkerCursor(workerId) ?? throw new OrganizationStoreException("Worker cursor was not persisted.");
    }

    public async Task<WorkerRequestRecord> DispatchAsync(RemoteDispatchCommand command, CancellationToken cancellationToken)
    {
        RequireEnabled();
        await EnsureStartupReconciledAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(command.Prompt) || command.Prompt.Length > MaxPromptCharacters || command.Prompt.Any(ch => ch == '\0')) throw new OrganizationValidationException("Prompt text is invalid.");
        var entry = Entry(command.WorkerId);
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lease = await EnsureConnectedAndSynchronizedLockedAsync(command.WorkerId, entry, cancellationToken).ConfigureAwait(false);
            await RefreshStatusLockedAsync(command.WorkerId, entry, lease, cancellationToken).ConfigureAwait(false);
            var envelope = new { method = "session/prompt", @params = new { sessionId = command.SessionId, prompt = command.Prompt } };
            var payload = Hash(JsonSerializer.Serialize(envelope, WorkerProtocol.JsonOptions));
            var turnId = "turn-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var store = Store();
            var request = store.BeginWorkerRequest(new(command.EmployeeId, command.RuntimeBindingId, command.WorkerId, command.SessionRecordId, command.NativeSessionId, command.IdempotencyKey, payload, Hash(command.Prompt), lease.Ownership.Epoch, lease.Status.ProcessGeneration, turnId));
            if (request.State != "Intent") return request;
            if (request.OwnershipEpoch != lease.Ownership.Epoch || request.ProcessGeneration != lease.Status.ProcessGeneration)
                return store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Interrupted", "ownership-changed");
            request = store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
            try
            {
                var mutation = lease.Session.Mutation("submit", new Dictionary<string, object?> { ["requestId"] = request.Id, ["envelope"] = envelope, ["turnId"] = request.TurnId });
                var result = await lease.Session.InvokeAsync("submit", mutation, true, cancellationToken).ConfigureAwait(false);
                Touch(lease);
                var remote = JsonSerializer.Deserialize<BridgeStoredRequest>(result.Result.GetRawText(), WorkerProtocol.JsonOptions) ?? throw new WorkerProtocolException("Worker submit result is invalid.");
                return ApplyRemoteRequest(store, request, remote);
            }
            catch (WorkerRemoteException ex) when (ex.Code == "worker-operation-uncertain")
            {
                await LoseSessionLockedAsync(command.WorkerId, entry).ConfigureAwait(false);
                return store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain", ex.Code);
            }
            catch (WorkerRemoteException ex) { return store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Failed", ex.Code); }
            catch (Exception ex) when (IsSessionLoss(ex))
            {
                await LoseSessionLockedAsync(command.WorkerId, entry).ConfigureAwait(false);
                return store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain", "write-uncertain");
            }
        }
        finally { entry.Gate.Release(); }
    }

    public async Task<WorkerCancellationRecord> CancelAsync(RemoteCancellationCommand command, CancellationToken token)
    {
        RequireEnabled();
        await EnsureStartupReconciledAsync(token).ConfigureAwait(false);
        var store = Store();
        var request = store.GetWorkerRequest(command.RequestId) ?? throw new KeyNotFoundException("Request not found.");
        var entry = Entry(request.WorkerId);
        await entry.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var lease = await EnsureConnectedAndSynchronizedLockedAsync(request.WorkerId, entry, token).ConfigureAwait(false);
            await RefreshStatusLockedAsync(request.WorkerId, entry, lease, token).ConfigureAwait(false);
            request = store.GetWorkerRequest(command.RequestId) ?? throw new KeyNotFoundException("Request not found.");
            var cancellation = store.BeginWorkerCancellation(request.Id, Hash("session/cancel:" + request.Id));
            if (cancellation.State != "Intent") return cancellation;
            if (request.OwnershipEpoch != lease.Ownership.Epoch || request.ProcessGeneration != lease.Status.ProcessGeneration)
                return store.TransitionWorkerCancellation(cancellation.Id, cancellation.Revision, "Intent", "Failed");
            var envelope = new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId = request.NativeSessionId } };
            try
            {
                var mutation = lease.Session.Mutation("cancel", new Dictionary<string, object?> { ["cancellationId"] = cancellation.Id, ["targetRequestId"] = request.Id, ["envelope"] = envelope });
                await lease.Session.InvokeAsync("cancel", mutation, true, token).ConfigureAwait(false);
                Touch(lease);
                return store.TransitionWorkerCancellation(cancellation.Id, cancellation.Revision, "Intent", "Forwarded");
            }
            catch (WorkerRemoteException ex) when (ex.Code == "worker-operation-uncertain")
            {
                await LoseSessionLockedAsync(request.WorkerId, entry).ConfigureAwait(false);
                return store.TransitionWorkerCancellation(cancellation.Id, cancellation.Revision, "Intent", "Uncertain");
            }
            catch (Exception ex) when (IsSessionLoss(ex))
            {
                await LoseSessionLockedAsync(request.WorkerId, entry).ConfigureAwait(false);
                return store.TransitionWorkerCancellation(cancellation.Id, cancellation.Revision, "Intent", "Uncertain");
            }
        }
        finally { entry.Gate.Release(); }
    }

    public async Task RejectPermissionAsync(RemotePermissionRejectCommand command, CancellationToken token)
    {
        RequireEnabled();
        await EnsureStartupReconciledAsync(token).ConfigureAwait(false);
        var store = Store();
        var entry = Entry(command.WorkerId);
        await entry.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (entry.Lease is null) throw new OrganizationConcurrencyException("The cached owner lease is unavailable; reconnect would fence the pending permission.");
            var lease = entry.Lease;
            await RefreshStatusLockedAsync(command.WorkerId, entry, lease, token).ConfigureAwait(false);
            var authoritative = store.GetWorkerPendingPermission(command.WorkerId, command.DecisionId) ?? throw new KeyNotFoundException("Pending worker permission not found.");
            if (authoritative.Revision != command.Revision || authoritative.State != "pending") throw new OrganizationConcurrencyException("Pending worker permission changed.");
            var choice = authoritative.OptionIds.Contains("reject_once", StringComparer.Ordinal) ? "reject_once" : authoritative.OptionIds.Contains("reject_always", StringComparer.Ordinal) ? "reject_always" : throw new OrganizationConcurrencyException("No reject permission option is available.");
            if (lease.Ownership.Epoch != authoritative.OwnershipEpoch || lease.Status.OwnershipEpoch != authoritative.OwnershipEpoch || lease.Status.ProcessGeneration != authoritative.ProcessGeneration || lease.Status.PendingPermission?.DecisionId != authoritative.DecisionId)
                throw new OrganizationConcurrencyException("Permission decision does not match the exact cached worker lease and prompt turn.");
            try
            {
                await lease.Session.InvokeAsync("permission", lease.Session.Mutation("permission", new Dictionary<string, object?> { ["decisionId"] = authoritative.DecisionId, ["processGeneration"] = authoritative.ProcessGeneration, ["requestId"] = authoritative.RequestId, ["turnId"] = authoritative.TurnId, ["decision"] = choice }), true, token).ConfigureAwait(false);
                Touch(lease);
                store.TransitionWorkerPendingPermission(authoritative.WorkerId, authoritative.DecisionId, authoritative.Revision, "pending", "decided");
            }
            catch (WorkerRemoteException ex) when (ex.Code == "worker-operation-uncertain")
            {
                store.TransitionWorkerPendingPermission(authoritative.WorkerId, authoritative.DecisionId, authoritative.Revision, "pending", "uncertain");
                await LoseSessionLockedAsync(command.WorkerId, entry).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (IsSessionLoss(ex))
            {
                store.TransitionWorkerPendingPermission(authoritative.WorkerId, authoritative.DecisionId, authoritative.Revision, "pending", "uncertain");
                await LoseSessionLockedAsync(command.WorkerId, entry).ConfigureAwait(false);
                throw;
            }
        }
        finally { entry.Gate.Release(); }
    }

    public async Task HeartbeatAsync(string workerId, TimeSpan timeout, CancellationToken token)
    {
        RequireEnabled();
        await EnsureStartupReconciledAsync(token).ConfigureAwait(false);
        var entry = Entry(workerId);
        await entry.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var lease = await EnsureConnectedAndSynchronizedLockedAsync(workerId, entry, token).ConfigureAwait(false);
            try
            {
                await lease.Session.InvokeAsync("heartbeat", lease.Session.Mutation("heartbeat", new Dictionary<string, object?>()), true, token).ConfigureAwait(false);
                Touch(lease);
                await RefreshStatusLockedAsync(workerId, entry, lease, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSessionLoss(ex))
            {
                var expired = _clock.UtcNow - lease.LastContactUtc >= timeout;
                await LoseSessionLockedAsync(workerId, entry).ConfigureAwait(false);
                Store().RecordWorkerConnectionState(workerId, expired ? "expired" : "disconnected");
                throw;
            }
        }
        finally { entry.Gate.Release(); }
    }

    public async Task RunMaintenanceAsync(CancellationToken token)
    {
        RequireEnabled();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
        var delay = TimeSpan.FromSeconds(Math.Max(1, _options.ConnectTimeoutSeconds));
        while (!linked.IsCancellationRequested)
        {
            foreach (var worker in Store().ListWorkerEnrollments().Where(x => x.Enabled))
            {
                try { await HeartbeatAsync(worker.WorkerId, TimeSpan.FromSeconds(Math.Max(2, _options.ConnectTimeoutSeconds * 2)), linked.Token).ConfigureAwait(false); }
                catch when (!linked.IsCancellationRequested) { }
            }
            await _delay.DelayAsync(delay, linked.Token).ConfigureAwait(false);
        }
    }

    public async Task ReconcileStartupAsync(CancellationToken token)
    {
        RequireEnabled();
        await EnsureStartupReconciledAsync(token).ConfigureAwait(false);
    }

    private async Task EnsureStartupReconciledAsync(CancellationToken token)
    {
        if (_startupReconciled) return;
        await _startupGate.WaitAsync(token).ConfigureAwait(false);
        try { if (!_startupReconciled) { Store().ReconcileControllerStartup(); _startupReconciled = true; } }
        finally { _startupGate.Release(); }
    }

    private async Task<WorkerConnectionLease> EnsureConnectedAndSynchronizedLockedAsync(string workerId, WorkerEntry entry, CancellationToken token)
    {
        if (entry.Lease is not null) return entry.Lease;
        var store = Store();
        var enrollment = store.GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker enrollment not found.");
        store.RecordWorkerConnectionState(workerId, "connecting");
        IWorkerBridgeSession? session = null;
        try
        {
            session = await _sessions.ConnectAsync(enrollment, token).ConfigureAwait(false);
            var initial = await StatusAsync(session, token).ConfigureAwait(false);
            var priorCursor = store.GetWorkerCursor(workerId);
            var ownershipAdvanced = priorCursor is not null && priorCursor.ObservedOwnershipEpoch > 0 && priorCursor.ObservedOwnershipEpoch != session.Lease.Epoch;
            var inheritedActive = ownershipAdvanced && (priorCursor!.ActiveRequestId is not null || priorCursor.PendingPermissionHash is not null);
            if (inheritedActive)
            {
                store.RecordControllerRecovery(workerId, "ownership-changed", $"{priorCursor!.ObservedOwnershipEpoch}:{session.Lease.Epoch}:{priorCursor.ActiveRequestId}:{priorCursor.PendingPermissionHash}");
                initial = initial with { DispatchHeld = true, HoldReasons = [.. initial.HoldReasons, "ownership-changed-active-work"] };
            }
            store.RecordWorkerConnectionState(workerId, "authenticated", session.Lease.Epoch);
            var replay = await ReplayKnownGenerationsAsync(store, enrollment, session, initial, priorCursor, token).ConfigureAwait(false);
            initial = replay.Status;
            store.RecordWorkerStatusAndEvents(workerId, ToController(initial), []);
            if (replay.Result == ReplaySequenceResult.HeldIncomplete)
            {
                entry.Lease = new WorkerConnectionLease(session, initial, _clock.UtcNow);
                session = null;
                return entry.Lease;
            }
            if (!inheritedActive) await ReconcileRequestsAsync(store, enrollment, session, initial, token).ConfigureAwait(false);
            var final = await StatusAsync(session, token).ConfigureAwait(false);
            if (inheritedActive) final = final with { DispatchHeld = true, HoldReasons = [.. final.HoldReasons, "ownership-changed-active-work"] };
            store.RecordWorkerStatusAndEvents(workerId, ToController(final), []);
            entry.Lease = new WorkerConnectionLease(session, final, _clock.UtcNow);
            session = null;
            return entry.Lease;
        }
        catch
        {
            store.RecordWorkerConnectionState(workerId, "held");
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed record ReplayOutcome(BridgeWorkerStatus Status, ReplaySequenceResult Result);

    private static async Task<ReplayOutcome> ReplayKnownGenerationsAsync(OrganizationStore store, WorkerEnrollmentRecord enrollment, IWorkerBridgeSession session, BridgeWorkerStatus status, WorkerCursorRecord? cursor, CancellationToken token)
    {
        // A previous acknowledgment whose delivery was uncertain is retried first.
        // The controller cursor already includes that committed page; after the exact
        // ACK converges, replay resumes from that cursor before any newer generation.
        if (!await RetryPendingAcknowledgmentsAsync(store, enrollment, session, token).ConfigureAwait(false))
        {
            var held = WithReplayAckHold(status);
            store.RecordWorkerStatusAndEvents(enrollment.WorkerId, ToController(held), []);
            return new(held, ReplaySequenceResult.HeldIncomplete);
        }

        var requestedGeneration = cursor?.AcknowledgedWorkerGeneration ?? 0;
        var requestedSequence = cursor?.AcknowledgedSequence ?? 0;
        var generations = new List<(long Generation, long After)>();
        if (requestedGeneration > 0) generations.Add((requestedGeneration, requestedSequence));
        if (status.WorkerGeneration > 0 && status.WorkerGeneration != requestedGeneration) generations.Add((status.WorkerGeneration, 0));
        if (generations.Count == 0 && status.WorkerGeneration > 0) generations.Add((status.WorkerGeneration, 0));
        foreach (var item in generations)
        {
            var replay = await ReplayAsync(store, enrollment, session, status, item.Generation, item.After, token).ConfigureAwait(false);
            status = replay.Status;
            if (replay.Result == ReplaySequenceResult.HeldIncomplete) return replay;
        }
        return new(status, ReplaySequenceResult.Complete);
    }

    /// <summary>
    /// Replays one generation and acknowledges exactly what each page delivered.
    /// </summary>
    /// <remarks>
    /// The acknowledgment carries this page's own generation and maximum sequence,
    /// not the controller's global cursor. A failed acknowledgment stops this page
    /// sequence and every newer generation. The committed controller cursor is then
    /// the resume point after the exact acknowledgment converges on reconnect.
    /// </remarks>
    private static async Task<ReplayOutcome> ReplayAsync(OrganizationStore store, WorkerEnrollmentRecord enrollment, IWorkerBridgeSession session, BridgeWorkerStatus status, long generation, long after, CancellationToken token)
    {
        var cursor = after;
        var pageCount = 0;
        var eventCount = 0;
        var targetSequence = generation == status.WorkerGeneration ? status.LastSequence : (long?)null;
        var maxEventCount = targetSequence is null ? MaxReplayEvents : MaxReplayEvents + 256;
        if (targetSequence is not null)
        {
            if (cursor == targetSequence.Value) return new(status, ReplaySequenceResult.Complete);
            if (cursor > targetSequence.Value)
            {
                var held = RecordReplayProtocolFailure(store, enrollment, status, generation, cursor);
                return new(held, ReplaySequenceResult.HeldIncomplete);
            }
        }
        while (true)
        {
            if (++pageCount > MaxReplayPages)
            {
                RecordReplayProtocolFailure(store, enrollment, status, generation, cursor);
                throw new WorkerProtocolException("Worker replay exceeded the bounded page count.");
            }
            WorkerSessionResult replay;
            try { replay = await session.InvokeAsync("replay", new { operation = "replay", workerGeneration = generation, afterSequence = cursor }, false, token).ConfigureAwait(false); }
            catch (WorkerRemoteException)
            {
                try
                {
                    var authoritative = await StatusAsync(session, token).ConfigureAwait(false);
                    store.RecordWorkerStatusAndEvents(enrollment.WorkerId, ToController(authoritative), []);
                    return new(authoritative, ReplaySequenceResult.HeldIncomplete);
                }
                catch (Exception statusException) when (statusException is WorkerProtocolException or JsonException or IOException or ObjectDisposedException)
                {
                    // The replay rejection proves that recovery is required, but without
                    // the worker's status there is no safe exact tuple to invent. Keep an
                    // operator-only controller obligation with no marker; a later healthy
                    // status will project the worker's exact gap/loss markers.
                    store.RecordControllerRecovery(enrollment.WorkerId, "replay-gap", $"{generation}:{cursor}");
                    var held = status with { DispatchHeld = true, HoldReasons = [.. status.HoldReasons, "replay-gap"] };
                    store.RecordWorkerStatusAndEvents(enrollment.WorkerId, ToController(held), []);
                    return new(held, ReplaySequenceResult.HeldIncomplete);
                }
            }
            BridgeReplayPage page;
            try
            {
                page = JsonSerializer.Deserialize<BridgeReplayPage>(replay.Result.GetRawText(), WorkerProtocol.JsonOptions) ?? throw new WorkerProtocolException("Worker replay page is invalid.");
                if (page.Events is null || page.Events.Any(x => x is null || x.Kind is null || x.PayloadJson is null)) throw new WorkerProtocolException("Worker replay events are invalid.");
                var replayEvents = page.Events.Cast<BridgeWorkerEvent>().ToArray();
                eventCount = checked(eventCount + replayEvents.Length);
                if (replayEvents.Length > 256 || eventCount > maxEventCount || (page.HasMore && replayEvents.Length == 0)) throw new WorkerProtocolException("Worker replay page exceeded the bounded event contract.");
                if (replayEvents.Any(x => x.WorkerGeneration != generation || x.Sequence <= cursor)
                    || replayEvents.Zip(replayEvents.Skip(1), (left, right) => right.Sequence <= left.Sequence).Any(invalid => invalid)
                    || page.NextAfterSequence < cursor
                    || (replayEvents.Length > 0 && page.NextAfterSequence != replayEvents[^1].Sequence)
                    || (replayEvents.Length == 0 && page.NextAfterSequence != cursor))
                    throw new WorkerProtocolException("Worker replay page did not make valid progress.");
                if (replayEvents.Any(x => x.ByteCount < 0
                    || x.ByteCount > 64 * 1024
                    || Encoding.UTF8.GetByteCount(x.PayloadJson) != x.ByteCount
                    || x.Kind.Length is < 1 or > 64))
                    throw new WorkerProtocolException("Worker replay event metadata is invalid.");
            }
            catch (Exception exception) when (exception is WorkerProtocolException or JsonException or OverflowException)
            {
                RecordReplayProtocolFailure(store, enrollment, status, generation, cursor);
                throw;
            }
            var events = page.Events.Cast<BridgeWorkerEvent>().ToArray();
            ControllerWorkerEvent[] sanitized;
            try
            {
                sanitized = events.Select(x => { var json = SanitizeJson(x.PayloadJson); return new ControllerWorkerEvent(x.WorkerGeneration, x.Sequence, SanitizeKind(x.Kind), json, Encoding.UTF8.GetByteCount(json)); }).ToArray();
            }
            catch (JsonException exception)
            {
                RecordReplayProtocolFailure(store, enrollment, status, generation, cursor);
                throw new WorkerProtocolException("Worker replay event payload is invalid.", exception);
            }
            try
            {
                store.RecordWorkerStatusAndEvents(enrollment.WorkerId, ToController(status), sanitized);
            }
            catch (Exception exception) when (exception is OrganizationConcurrencyException or OrganizationValidationException)
            {
                RecordReplayProtocolFailure(store, enrollment, status, generation, cursor);
                throw new WorkerProtocolException("Worker replay conflicted with durable controller metadata.", exception);
            }
            if (sanitized.Length > 0 && !await AcknowledgeAsync(store, enrollment, session, status, generation, page.NextAfterSequence, token).ConfigureAwait(false))
                return new(WithReplayAckHold(status), ReplaySequenceResult.HeldIncomplete);
            if (targetSequence is not null)
            {
                if (page.NextAfterSequence == targetSequence.Value) return new(status, ReplaySequenceResult.Complete);
                if (page.NextAfterSequence > targetSequence.Value)
                {
                    var held = RecordReplayProtocolFailure(store, enrollment, status, generation, page.NextAfterSequence);
                    return new(held, ReplaySequenceResult.HeldIncomplete);
                }
            }
            if (!page.HasMore)
            {
                if (targetSequence is not null)
                {
                    var authoritative = await StatusAsync(session, token).ConfigureAwait(false);
                    if (HasReplayRecovery(authoritative))
                    {
                        store.RecordWorkerStatusAndEvents(enrollment.WorkerId, ToController(authoritative), []);
                        return new(authoritative, ReplaySequenceResult.HeldIncomplete);
                    }
                    if (authoritative.WorkerGeneration != generation || authoritative.LastSequence <= page.NextAfterSequence)
                    {
                        RecordReplayProtocolFailure(store, enrollment, authoritative, generation, page.NextAfterSequence);
                        throw new WorkerProtocolException("Worker status diverged from the replay snapshot target.");
                    }
                    RecordReplayProtocolFailure(store, enrollment, authoritative, generation, page.NextAfterSequence);
                    throw new WorkerProtocolException("Worker replay ended before the status snapshot target.");
                }
                return new(status, ReplaySequenceResult.Complete);
            }
            cursor = page.NextAfterSequence;
        }
    }

    private static bool HasReplayRecovery(BridgeWorkerStatus status) =>
        status.ReplayLoss is not null
        || status.ReplayGaps.Count > 0
        || status.DispatchHeld && ((status.HoldReason?.Contains("replay-gap", StringComparison.Ordinal) ?? false)
            || (status.HoldReason?.Contains("replay-loss", StringComparison.Ordinal) ?? false)
            || status.HoldReasons.Any(reason => reason.Contains("replay-gap", StringComparison.Ordinal) || reason.Contains("replay-loss", StringComparison.Ordinal)));

    private static BridgeWorkerStatus RecordReplayProtocolFailure(OrganizationStore store, WorkerEnrollmentRecord enrollment, BridgeWorkerStatus status, long generation, long cursor)
    {
        store.RecordControllerRecovery(enrollment.WorkerId, "replay-gap", $"protocol:{generation}:{cursor}");
        var reasons = status.HoldReasons.Contains("replay-gap", StringComparer.Ordinal) ? status.HoldReasons : [.. status.HoldReasons, "replay-gap"];
        var held = status with { DispatchHeld = true, HoldReasons = reasons };
        store.RecordWorkerStatusAndEvents(enrollment.WorkerId, ToController(held), []);
        return held;
    }

    private static BridgeWorkerStatus WithReplayAckHold(BridgeWorkerStatus status) =>
        status with { DispatchHeld = true, HoldReasons = status.HoldReasons.Contains("replay-ack-uncertain", StringComparer.Ordinal) ? status.HoldReasons : [.. status.HoldReasons, "replay-ack-uncertain"] };

    private static async Task<bool> AcknowledgeAsync(OrganizationStore store, WorkerEnrollmentRecord enrollment, IWorkerBridgeSession session, BridgeWorkerStatus status, long generation, long sequence, CancellationToken token)
    {
        var marker = AckMarker(generation, sequence);
        try
        {
            await session.InvokeAsync("ack-events", session.Mutation("ack-events", new Dictionary<string, object?> { ["workerGeneration"] = generation, ["sequence"] = sequence }), true, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WorkerWriteUncertainException or WorkerReadUncertainException or WorkerRemoteException or IOException or ObjectDisposedException)
        {
            store.RecordControllerRecovery(enrollment.WorkerId, "replay-ack-uncertain", $"{generation}:{sequence}", marker);
            store.RecordWorkerStatusAndEvents(enrollment.WorkerId, ToController(status) with { DispatchHeld = true, HoldReasons = [.. status.HoldReasons, "replay-ack-uncertain"] }, []);
            return false;
        }
        // The worker accepted the exact marker, so any retained obligation for it is
        // now genuinely discharged.
        var obligation = store.ListWorkerRecoveryObligations(enrollment.WorkerId, activeOnly: true).FirstOrDefault(x => x.Kind == "replay-ack-uncertain" && x.MarkerJson == marker);
        if (obligation is not null) store.ResolveRecoveryObligationById(obligation.Id, obligation.Revision);
        return true;
    }

    private static async Task<bool> RetryPendingAcknowledgmentsAsync(OrganizationStore store, WorkerEnrollmentRecord enrollment, IWorkerBridgeSession session, CancellationToken token)
    {
        foreach (var obligation in store.ListWorkerRecoveryObligations(enrollment.WorkerId, activeOnly: true).Where(x => x.Kind == "replay-ack-uncertain").ToArray())
        {
            if (TryReadAckMarker(obligation.MarkerJson) is not { } marker) return false;
            try { await session.InvokeAsync("ack-events", session.Mutation("ack-events", new Dictionary<string, object?> { ["workerGeneration"] = marker.Generation, ["sequence"] = marker.Sequence }), true, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is WorkerWriteUncertainException or WorkerReadUncertainException or WorkerRemoteException or IOException or ObjectDisposedException) { return false; }
            store.ResolveRecoveryObligationById(obligation.Id, obligation.Revision);
        }
        return true;
    }

    internal static string AckMarker(long generation, long sequence) => JsonSerializer.Serialize(new { kind = "replay-ack-uncertain", workerGeneration = generation, sequence }, WorkerProtocol.JsonOptions);

    internal static (long Generation, long Sequence)? TryReadAckMarker(string? markerJson)
    {
        if (markerJson is null) return null;
        try
        {
            using var document = JsonDocument.Parse(markerJson, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("workerGeneration", out var generation) || !root.TryGetProperty("sequence", out var sequence)) return null;
            if (!generation.TryGetInt64(out var g) || !sequence.TryGetInt64(out var s) || g < 0 || s < 0) return null;
            return (g, s);
        }
        catch (JsonException) { return null; }
    }

    private static async Task ReconcileRequestsAsync(OrganizationStore store, WorkerEnrollmentRecord enrollment, IWorkerBridgeSession session, BridgeWorkerStatus status, CancellationToken token)
    {
        foreach (var request in store.ListWorkerRequests().Where(x => x.WorkerId == enrollment.WorkerId && x.State is "Intent" or "Forwarding" or "Uncertain"))
        {
            if (request.State == "Intent")
            {
                store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Interrupted", "controller-restarted-before-forwarding");
                continue;
            }
            if (request.OwnershipEpoch != session.Lease.Epoch || request.ProcessGeneration != status.ProcessGeneration)
            {
                store.RecordControllerRecovery(enrollment.WorkerId, "request-uncertain", request.Id, RequestMarker(request.Id));
                store.RecordWorkerStatusAndEvents(enrollment.WorkerId, ToController(status) with { DispatchHeld = true, HoldReasons = [.. status.HoldReasons, "request-recovery-required"] }, []);
                continue;
            }
            try
            {
                var result = await session.InvokeAsync("reconcile", new { operation = "reconcile", kind = "request", requestId = request.Id }, false, token).ConfigureAwait(false);
                var remote = JsonSerializer.Deserialize<BridgeStoredRequest>(result.Result.GetRawText(), WorkerProtocol.JsonOptions);
                if (remote is not null) ApplyRemoteRequest(store, request, remote);
            }
            catch (WorkerRemoteException)
            {
                store.RecordControllerRecovery(enrollment.WorkerId, "request-uncertain", request.Id, RequestMarker(request.Id));
                store.RecordWorkerStatusAndEvents(enrollment.WorkerId, ToController(status) with { DispatchHeld = true, HoldReasons = [.. status.HoldReasons, "request-recovery-required"] }, []);
            }
        }
    }

    /// <summary>
    /// Performs the owner-triggered recovery for one exact obligation.
    /// </summary>
    /// <remarks>
    /// The obligation is only cleared after the worker accepted the matching
    /// reconciliation, so clearing the controller row can never be mistaken for a
    /// reconciled worker. Kinds whose real outcome cannot be established remotely
    /// (an uncertain request, a changed ownership epoch) are not auto-resolved
    /// here: they need an operator decision, and the API rejects them rather than
    /// pretending the effect is known.
    /// </remarks>
    public async Task<WorkerRecoveryObligationRecord> RecoverAsync(string workerId, string obligationId, int expectedRevision, CancellationToken token)
    {
        RequireEnabled();
        await EnsureStartupReconciledAsync(token).ConfigureAwait(false);
        var store = Store();
        var obligation = store.GetWorkerRecoveryObligation(obligationId) ?? throw new KeyNotFoundException("Recovery obligation not found.");
        if (obligation.WorkerId != workerId || obligation.Revision != expectedRevision || !obligation.Active) throw new OrganizationConcurrencyException("The recovery obligation changed.");
        if (obligation.MarkerJson is null) throw new WorkerRecoveryRequiredException("This obligation carries no exact worker marker and must be reconciled by an operator.", obligation.Kind);

        var entry = Entry(workerId);
        await entry.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var lease = await EnsureConnectedAndSynchronizedLockedAsync(workerId, entry, token).ConfigureAwait(false);

            // Connecting re-projects the worker's own status, which refreshes
            // worker-sourced obligations and bumps their revision. Re-read the exact
            // row and require that its identity and marker are unchanged, so the
            // operator's decision still applies to the same obligation without
            // depending on a revision the reconnect legitimately moved.
            var current = store.GetWorkerRecoveryObligation(obligation.Id);
            if (current is null || !current.Active || current.Kind != obligation.Kind || current.MarkerHash != obligation.MarkerHash || current.MarkerJson != obligation.MarkerJson)
                throw new OrganizationConcurrencyException("The recovery obligation changed while the worker was contacted.");

            using var marker = JsonDocument.Parse(current.MarkerJson!, new JsonDocumentOptions { MaxDepth = 8 });
            var request = BuildRecoveryRequest(lease, current.Kind, marker.RootElement);
            await lease.Session.InvokeAsync(request.Operation, request.Payload, true, token).ConfigureAwait(false);
            Touch(lease);
            store.ResolveRecoveryObligationById(current.Id, current.Revision);
            await RefreshStatusLockedAsync(workerId, entry, lease, token).ConfigureAwait(false);
            return store.GetWorkerRecoveryObligation(current.Id)!;
        }
        catch (Exception ex) when (IsSessionLoss(ex))
        {
            await LoseSessionLockedAsync(workerId, entry).ConfigureAwait(false);
            throw;
        }
        finally { entry.Gate.Release(); }
    }

    /// <summary>
    /// Acknowledges a marker-less controller obligation after the owner has
    /// externally reconciled the unverifiable effect. This path is store-only and
    /// never contacts or infers state from the worker.
    /// </summary>
    public async Task<WorkerRecoveryObligationRecord> AcknowledgeRecoveryAsync(string workerId, string obligationId, int expectedRevision, string evidenceHash, string disposition, CancellationToken token)
    {
        RequireEnabled();
        await EnsureStartupReconciledAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return Store().AcknowledgeControllerRecovery(workerId, obligationId, expectedRevision, evidenceHash, disposition);
    }

    /// <summary>
    /// Builds the exact worker reconciliation for one retained marker. The marker
    /// field names are the worker's own status shapes (<c>ReplayGap</c>,
    /// <c>ReplayLoss</c>, <c>JournalFailure</c>), so an incomplete marker is
    /// refused rather than reconstructed from assumptions.
    /// </summary>
    private static (string Operation, object Payload) BuildRecoveryRequest(WorkerConnectionLease lease, string kind, JsonElement marker)
    {
        long Number(string name, long minimum) => marker.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) && number >= minimum ? number : throw new WorkerRecoveryRequiredException("The retained recovery marker is incomplete.", kind);
        string Text(string name) => marker.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 128 } text ? text : throw new WorkerRecoveryRequiredException("The retained recovery marker is incomplete.", kind);
        return kind switch
        {
            "replay-ack-uncertain" => ("ack-events", lease.Session.Mutation("ack-events", new Dictionary<string, object?> { ["workerGeneration"] = Number("workerGeneration", 0), ["sequence"] = Number("sequence", 0) })),
            "replay-loss" => ("reconcile-replay-loss", lease.Session.Mutation("reconcile-replay-loss", new Dictionary<string, object?> { ["workerGeneration"] = Number("workerGeneration", 1), ["markerSequence"] = Number("markerSequence", 1) })),
            "replay-gap" => ("reconcile-replay-gap", lease.Session.Mutation("reconcile-replay-gap", new Dictionary<string, object?> { ["gapId"] = Text("id"), ["workerGeneration"] = Number("workerGeneration", 0), ["afterSequence"] = Number("afterSequence", 0), ["firstRetainedSequence"] = Number("firstRetainedSequence", 0), ["lastSequence"] = Number("lastSequence", 0) })),
            "journal-failure" => ("reconcile-journal", lease.Session.Mutation("reconcile-journal", new Dictionary<string, object?> { ["operationId"] = Text("operationId"), ["workerGeneration"] = Number("workerGeneration", 1) })),
            _ => throw new WorkerRecoveryRequiredException("This obligation kind has no automatic worker reconciliation and must be resolved by an operator.", kind),
        };
    }

    internal static string RequestMarker(string requestId) => JsonSerializer.Serialize(new { kind = "request-uncertain", requestId }, WorkerProtocol.JsonOptions);

    internal async Task<WorkerConnectionLease?> GetCachedLeaseAsync(string workerId, CancellationToken token, bool refresh = false)
    {
        RequireEnabled();
        await EnsureStartupReconciledAsync(token).ConfigureAwait(false);
        var entry = Entry(workerId);
        await entry.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (refresh && entry.Lease is not null) await RefreshStatusLockedAsync(workerId, entry, entry.Lease, token).ConfigureAwait(false);
            return entry.Lease;
        }
        finally { entry.Gate.Release(); }
    }

    private async Task RefreshStatusLockedAsync(string workerId, WorkerEntry entry, WorkerConnectionLease lease, CancellationToken token)
    {
        try
        {
            var status = await StatusAsync(lease.Session, token).ConfigureAwait(false);
            Store().RecordWorkerStatusAndEvents(workerId, ToController(status), []);
            lease.Status = status;
            Touch(lease);
        }
        catch (Exception ex) when (IsSessionLoss(ex))
        {
            await LoseSessionLockedAsync(workerId, entry).ConfigureAwait(false);
            throw;
        }
    }

    private async Task LoseSessionLockedAsync(string workerId, WorkerEntry entry)
    {
        var lease = entry.Lease;
        entry.Lease = null;
        if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        Store().RecordWorkerConnectionState(workerId, "disconnected");
    }

    private static WorkerRequestRecord ApplyRemoteRequest(OrganizationStore store, WorkerRequestRecord local, BridgeStoredRequest remote)
    {
        if (remote.RequestId != local.Id || remote.OwnershipEpoch != local.OwnershipEpoch || remote.ProcessGeneration != local.ProcessGeneration || remote.TurnId != local.TurnId || remote.SessionId != local.NativeSessionId) throw new WorkerProtocolException("Worker request correlation does not match the durable controller intent.");
        var to = remote.State switch { "forwarding" or "forwarded" => "Forwarded", "completed" => "Completed", "failed" => "Failed", "uncertain" => "Uncertain", _ => throw new WorkerProtocolException("Worker request state is invalid.") };
        if (local.State == to) return local;
        return store.TransitionWorkerRequest(local.Id, local.Revision, local.State, to, to.ToLowerInvariant(), remote.OutcomeJson);
    }

    private static async Task<BridgeWorkerStatus> StatusAsync(IWorkerBridgeSession session, CancellationToken token)
    {
        var result = await session.InvokeAsync("status", new { operation = "status" }, false, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BridgeWorkerStatus>(result.Result.GetRawText(), WorkerProtocol.JsonOptions) ?? throw new WorkerProtocolException("Worker status is invalid.");
    }
    private static ControllerWorkerStatus ToController(BridgeWorkerStatus s) => new(s.WorkerGeneration, s.ProcessGeneration, s.ProcessState, s.ActiveRequestId, s.PendingPermission?.PayloadHash, s.OwnershipEpoch, s.DispatchHeld, s.HoldReasons, s.AcknowledgedWorkerGeneration, s.AcknowledgedSequence, s.ReplayLoss is null ? null : JsonSerializer.Serialize(s.ReplayLoss, WorkerProtocol.JsonOptions), s.ReplayGaps.Select(x => JsonSerializer.Serialize(x, WorkerProtocol.JsonOptions)).ToArray(), s.JournalFailure is null ? null : JsonSerializer.Serialize(s.JournalFailure, WorkerProtocol.JsonOptions), s.PendingPermission is null ? null : new ControllerPendingPermission(s.PendingPermission.ProcessGeneration, s.PendingPermission.OwnershipEpoch, s.PendingPermission.RequestId, s.PendingPermission.TurnId, s.PendingPermission.DecisionId, s.PendingPermission.PayloadHash, s.PendingPermission.OptionIds, s.PendingPermission.State), s.ViewerSupported, s.ViewerAvailable);
    private static string SanitizeKind(string kind) { var value = new string(kind.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').Take(64).ToArray()); return value.Length > 0 ? value : "event"; }
    private static string SanitizeJson(string json) { using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }); return JsonSerializer.Serialize(document.RootElement, WorkerProtocol.JsonOptions); }
    private static string Hash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    /// <summary>
    /// True when <paramref name="ex"/> means the owner session itself is no longer
    /// trustworthy. A bare <see cref="OperationCanceledException"/> is deliberately
    /// excluded: the bridge client raises it only before any byte was written, so
    /// the caller's cancellation leaves no remote effect and must not tear down a
    /// session that other callers are still using. An internal operation timeout
    /// surfaces as an uncertain write or read instead and is covered here.
    /// </summary>
    private static bool IsSessionLoss(Exception ex) => ex is WorkerWriteUncertainException or WorkerReadUncertainException or IOException or ObjectDisposedException;
    private void Touch(WorkerConnectionLease lease) { lease.LastContactUtc = _clock.UtcNow; }
    private WorkerEntry Entry(string workerId) => _workers.GetOrAdd(workerId, static _ => new WorkerEntry());
    private void RequireEnabled()
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        if (_options.Validate().Count != 0) throw new WorkerControlConfigurationException("Remote worker configuration is invalid.");
    }
    private OrganizationStore Store() => _control.Organization ?? throw new OrganizationStoreException("Organization store unavailable.");

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        foreach (var item in _workers)
        {
            await item.Value.Gate.WaitAsync().ConfigureAwait(false);
            try { if (item.Value.Lease is not null) await item.Value.Lease.DisposeAsync().ConfigureAwait(false); item.Value.Lease = null; }
            finally { item.Value.Gate.Release(); item.Value.Gate.Dispose(); }
        }
        _workers.Clear();
        _startupGate.Dispose();
        _stopping.Dispose();
    }

    private sealed class WorkerEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public WorkerConnectionLease? Lease { get; set; }
    }
}
