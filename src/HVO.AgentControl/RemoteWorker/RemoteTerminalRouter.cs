using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Terminal;
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
        catch (Exception exception) when (exception is OrganizationStoreException or InvalidOperationException) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            var input = ForwardInputAsync(browser, remote, store, viewer.Id, lease, target.WorkerId, linked.Token);
            var output = ForwardOutputAsync(browser, remote, store, viewer.Id, lease, target.WorkerId, linked.Token);
            await Task.WhenAny(input, output).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(input).ConfigureAwait(false);
            await ObserveAsync(output).ConfigureAwait(false);
            var current = store.ListRemoteTerminalViewers(target.WorkerId).Single(x => x.Id == viewer.Id);
            if (current.State == "connected") viewer = store.TransitionRemoteTerminalViewer(current.Id, current.Revision, "connected", "detached");
        }
        catch
        {
            var current = store.ListRemoteTerminalViewers(target.WorkerId).Single(x => x.Id == viewer.Id);
            if (current.State is "requested" or "connected") store.TransitionRemoteTerminalViewer(current.Id, current.Revision, current.State, "failed");
            throw;
        }
        finally
        {
            if (remote is not null) await remote.DisposeAsync().ConfigureAwait(false);
            browser?.Dispose();
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

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static void RequireCurrentLease(OrganizationStore store, WorkerConnectionLease lease, string workerId)
    {
        var cursor = store.GetWorkerCursor(workerId);
        if (cursor is null || cursor.ConnectionState != "authenticated" || cursor.ObservedOwnershipEpoch != lease.Ownership.Epoch || cursor.ObservedProcessGeneration != lease.Status.ProcessGeneration) throw new OrganizationConcurrencyException("Remote terminal owner lease is stale.");
    }
}
