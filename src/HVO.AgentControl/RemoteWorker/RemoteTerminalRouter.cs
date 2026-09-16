using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Terminal;
using HVO.AgentControl.Worker;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

public interface IRemoteTerminalRouter
{
    bool IsAvailable { get; }
    Task ProxyAsync(HttpContext context, RemoteWorkerSnapshot target, CancellationToken token);
}

/// <summary>Proxies one browser websocket onto a viewer connection using only the cached controller lease.</summary>
public sealed class RemoteTerminalRouter(
    AcpControlHost control,
    WorkerConnectionManager manager,
    IRemoteTerminalConnector connector,
    IOptions<WorkerControlOptions> configured) : IRemoteTerminalRouter
{
    private readonly WorkerControlOptions _options = configured.Value;

    // This is only the controller feature gate. Exact worker capability and current
    // availability come from the persisted status snapshot and are checked below.
    public bool IsAvailable => _options.Enabled;

    /// <summary>Test-visible detail of the most recent unclassified viewer failure.</summary>
    internal Exception? LastFailure { get; private set; }

    public async Task ProxyAsync(HttpContext context, RemoteWorkerSnapshot target, CancellationToken token)
    {
        if (!IsAvailable || !target.ViewerSupported || !target.ViewerAvailable || !context.WebSockets.IsWebSocketRequest || !TerminalProtocol.IsSameOrigin(context.Request.Headers.Origin.ToString(), context.Request.Scheme, context.Request.Host.Value))
        {
            context.Response.StatusCode = IsAvailable ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var store = control.Organization;
        if (store is null) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
        var enrollment = store.GetWorkerEnrollment(target.WorkerId);
        if (enrollment is null) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
        WorkerConnectionLease? lease;
        try { lease = await manager.GetCachedLeaseAsync(target.WorkerId, token, refresh: true).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OrganizationStoreException or RemoteWorkerException or InvalidOperationException)
        {
            // Nothing is upgraded yet, so a status code is still the right answer.
            context.Response.StatusCode = exception is RemoteWorkerUnavailableException { Transport: true } ? StatusCodes.Status502BadGateway : StatusCodes.Status503ServiceUnavailable;
            return;
        }
        if (lease is null || target.SessionRecordId is null || target.NativeSessionId is null || lease.Ownership.Epoch != target.OwnershipEpoch || lease.Status.ProcessGeneration != target.ProcessGeneration)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var viewer = store.BeginRemoteTerminalViewer(target.WorkerId, target.SessionRecordId, lease.Ownership.Epoch);
        RemoteTerminalSession? remote = null;
        WebSocket? browser = null;
        try
        {
            remote = await RemoteTerminalSession.ConnectAsync(connector, enrollment, lease, target.NativeSessionId, TimeSpan.FromSeconds(_options.AuthenticationTimeoutSeconds), token).ConfigureAwait(false);
            viewer = store.TransitionRemoteTerminalViewer(viewer.Id, viewer.Revision, "requested", "connected");
            browser = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Nothing was upgraded yet when the failure came from the connect leg, so
            // a status code is still the correct answer; once the socket is accepted
            // the catch below owns the close handshake instead.
            Fail(store, target.WorkerId, viewer.Id);
            if (remote is not null) await remote.DisposeAsync().ConfigureAwait(false);
            browser?.Dispose();
            if (browser is not null) throw;
            if (exception is OperationCanceledException) throw;
            context.Response.StatusCode = exception is WorkerProtocolException or RemoteWorkerUnavailableException ? StatusCodes.Status502BadGateway : StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // From here the response is an accepted WebSocket. A failure must be closed
        // on the socket with a bounded reason and must not be rethrown into the
        // middleware pipeline, which can no longer write a status code.
        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeReason = "session ended";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var input = ForwardInputAsync(browser, remote, store, viewer.Id, lease, target.WorkerId, linked.Token);
        var output = ForwardOutputAsync(browser, remote, store, viewer.Id, lease, target.WorkerId, linked.Token);
        try
        {
            await Task.WhenAny(input, output).ConfigureAwait(false);

            // Both pumps can fail from one underlying event: the worker announces a
            // category on the output side while the input side merely observes the
            // dead stream. The worker's explicit announcement is the truthful reason,
            // so the output side is inspected first and wins regardless of which task
            // the scheduler happened to complete first. Only an already-finished pump
            // is observed here: the input pump stays blocked on the browser until the
            // close below, so awaiting it now would deadlock the close handshake.
            var failure = await ObserveCompletedAsync(output).ConfigureAwait(false)
                ?? await ObserveCompletedAsync(input).ConfigureAwait(false);
            if (failure is not null) throw failure;
            var current = store.ListRemoteTerminalViewers(target.WorkerId).Single(x => x.Id == viewer.Id);
            if (current.State == "connected") viewer = store.TransitionRemoteTerminalViewer(current.Id, current.Revision, "connected", "detached");
        }
        catch (OperationCanceledException)
        {
            closeStatus = WebSocketCloseStatus.EndpointUnavailable;
            closeReason = "viewer canceled";
            Fail(store, target.WorkerId, viewer.Id);
        }
        catch (RemoteViewerClosedException closed)
        {
            // The worker announced the exact failure category. Pass it through so the
            // browser shows a failed viewer rather than a frozen terminal.
            closeStatus = WebSocketCloseStatus.EndpointUnavailable;
            closeReason = closed.BytesWritten is { } written ? $"{closed.Category}:{written}" : closed.Category;
            Fail(store, target.WorkerId, viewer.Id);
        }
        catch (Exception exception)
        {
            closeStatus = exception is WorkerProtocolException or OrganizationConcurrencyException ? WebSocketCloseStatus.PolicyViolation : WebSocketCloseStatus.InternalServerError;
            closeReason = exception is OrganizationConcurrencyException ? "owner lease changed" : "viewer failed";
            LastFailure = exception;
            Fail(store, target.WorkerId, viewer.Id);
        }
        finally
        {
            // Close before cancelling: cancelling a pending WebSocket receive aborts
            // the connection, which would destroy the close frame the browser needs to
            // learn why the viewer ended. The close itself unblocks the input pump.
            await CloseAsync(browser, closeStatus, closeReason).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            // Both pumps are always observed, so neither can fail silently or outlive
            // the request.
            await ObserveAsync(input).ConfigureAwait(false);
            await ObserveAsync(output).ConfigureAwait(false);
            if (remote is not null) await remote.DisposeAsync().ConfigureAwait(false);
            browser.Dispose();
        }
    }

    private static void Fail(OrganizationStore store, string workerId, string viewerId)
    {
        try
        {
            var current = store.ListRemoteTerminalViewers(workerId).SingleOrDefault(x => x.Id == viewerId);
            if (current is not null && current.State is "requested" or "connected") store.TransitionRemoteTerminalViewer(current.Id, current.Revision, current.State, "failed");
        }
        catch (OrganizationStoreException) { /* The failure is already terminal; store unavailability must not mask it. */ }
    }

    /// <summary>Closes the socket with a reason bounded to the 123-byte protocol limit.</summary>
    private static async Task CloseAsync(WebSocket browser, WebSocketCloseStatus status, string reason)
    {
        if (browser.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        var bounded = reason;
        while (Encoding.UTF8.GetByteCount(bounded) > 123) bounded = bounded[..^1];
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await browser.CloseAsync(status, bounded, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The peer is already gone; the cleanup above has already recorded state.
        }
    }

    private static async Task ForwardInputAsync(WebSocket browser, RemoteTerminalSession remote, OrganizationStore store, string viewerId, WorkerConnectionLease lease, string workerId, CancellationToken token)
    {
        var buffer = new byte[TerminalProtocol.MaxClientMessageBytes];
        while (browser.State == WebSocketState.Open)
        {
            RequireCurrentLease(store, lease, workerId);
            var result = await browser.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) { await remote.DetachAsync(token).ConfigureAwait(false); return; }
            if (!result.EndOfMessage || result.MessageType != WebSocketMessageType.Text || !TerminalProtocol.TryParseClientFrame(Encoding.UTF8.GetString(buffer, 0, result.Count), out var frame)) throw new InvalidOperationException("Remote terminal frame is invalid.");
            if (frame.Kind == TerminalFrameKind.Input)
            {
                var bytes = Encoding.UTF8.GetBytes(frame.Data);
                await remote.SendInputAsync(bytes, token).ConfigureAwait(false);
                store.RecordRemoteTerminalViewerActivity(viewerId, inputBytes: bytes.Length);
            }
            else
            {
                await remote.ResizeAsync(frame.Rows, frame.Columns, token).ConfigureAwait(false);
                store.RecordRemoteTerminalViewerActivity(viewerId, rows: frame.Rows, columns: frame.Columns);
            }
        }
    }

    private static async Task ForwardOutputAsync(WebSocket browser, RemoteTerminalSession remote, OrganizationStore store, string viewerId, WorkerConnectionLease lease, string workerId, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            RequireCurrentLease(store, lease, workerId);
            var bytes = await remote.ReadOutputAsync(token).ConfigureAwait(false);
            if (bytes is null) return;
            var json = JsonSerializer.SerializeToUtf8Bytes(new { type = "output", encoding = "base64", data = Convert.ToBase64String(bytes) });
            await browser.SendAsync(json, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            store.RecordRemoteTerminalViewerActivity(viewerId, outputBytes: bytes.Length);
        }
    }

    /// <summary>Observes one pump and returns its failure, treating cancellation as a clean stop.</summary>
    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); return null; }
        catch (OperationCanceledException) { return null; }
        catch (Exception exception) { return exception; }
    }

    /// <summary>
    /// Returns the failure of a pump that has already finished, without waiting for
    /// one that has not: the input pump stays blocked on the browser until the
    /// socket is closed, so awaiting it here would deadlock the close handshake.
    /// </summary>
    private static Task<Exception?> ObserveCompletedAsync(Task task) =>
        task.IsCompleted ? ObserveAsync(task) : Task.FromResult<Exception?>(null);

    private static void RequireCurrentLease(OrganizationStore store, WorkerConnectionLease lease, string workerId)
    {
        var cursor = store.GetWorkerCursor(workerId);
        if (cursor is null || cursor.ConnectionState != "authenticated" || cursor.ObservedOwnershipEpoch != lease.Ownership.Epoch || cursor.ObservedProcessGeneration != lease.Status.ProcessGeneration) throw new OrganizationConcurrencyException("Remote terminal owner lease is stale.");
    }
}
