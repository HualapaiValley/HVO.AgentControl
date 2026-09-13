using System.Reflection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RuntimeLifecycleOwnershipTests
{
    [Fact]
    public async Task SupersededStopCannotReachReplacementTransportAfterEnsureThenDisconnect()
    {
        await using var app = new TestApp();
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = (await app.Store.Snapshot()).Runtimes.Single(x => x.Id == worker.RuntimeId);
        await app.Store.Write(async db => { (await db.Runtimes.FindAsync(runtime.Id))!.DesiredConnected = true; return true; });
        var handler = new BarrierHandler(worker);
        var factory = new CountingFactory(handler);
        using var supervisor = new RuntimeSupervisor(app.Store, factory, Options.Create(new ControlOptions { PollMilliseconds = 20 }), NullLogger<RuntimeSupervisor>.Instance);
        await supervisor.StartAsync(CancellationToken.None);
        try
        {
            await handler.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = await app.Store.RuntimeCommand(runtime.Id, "StopManagedServer", Guid.NewGuid().ToString());
            var ensure = await app.Store.RuntimeCommand(runtime.Id, "EnsureServer", Guid.NewGuid().ToString());
            handler.ReleaseSnapshot.TrySetResult();
            await TestApp.Wait(async () => (await app.Store.Snapshot()).Commands.Single(x => x.Id == ensure.Id).State == Delivery.Finished, "Ensure after bounded snapshot");
            await app.Store.RuntimeCommand(runtime.Id, "DisconnectRuntime", Guid.NewGuid().ToString());
            await TestApp.Wait(async () => (await app.Store.Snapshot()).Runtimes.Single(x => x.Id == runtime.Id).Transport == "Disconnected", "Disconnect after superseded stop");

            Assert.Equal(0, factory.StopCalls);
            Assert.Equal(Delivery.Cancelled, (await app.Store.Snapshot()).Commands.Single(x => x.Id == stop.Id).State);
            Assert.Equal(worker.NativeSessionId, (await app.Store.Snapshot()).Workers.Single(x => x.Id == worker.Id).NativeSessionId);
        }
        finally { await supervisor.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StopDispatchUsesTheExactFreshOwnedProcessReceipt()
    {
        await using var app = new TestApp();
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = (await app.Store.Snapshot()).Runtimes.Single(x => x.Id == worker.RuntimeId);
        await app.Store.Write(async db => { (await db.Runtimes.FindAsync(runtime.Id))!.DesiredConnected = true; return true; });
        var handler = new BarrierHandler(worker);
        var factory = new CountingFactory(handler);
        using var supervisor = new RuntimeSupervisor(app.Store, factory, Options.Create(new ControlOptions { PollMilliseconds = 20 }), NullLogger<RuntimeSupervisor>.Instance);
        await supervisor.StartAsync(CancellationToken.None);
        try
        {
            await handler.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime));
            var stop = await app.Store.RuntimeCommand(runtime.Id, "StopManagedServer", Guid.NewGuid().ToString());
            handler.ReleaseSnapshot.TrySetResult();
            await TestApp.Wait(async () => (await app.Store.Snapshot()).Commands.Single(x => x.Id == stop.Id).State == Delivery.Finished, "Exact owned stop");

            Assert.Equal(1, factory.StopCalls);
            Assert.Equal(runtime.ManagedServerId, factory.ExpectedStop?.ManagedServerId);
            Assert.Equal(42, factory.ExpectedStop?.ProcessId);
            Assert.Equal("boot-a:100", factory.ExpectedStop?.Incarnation);
        }
        finally { await supervisor.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task NewIntentAfterStopClaimPreventsTheRemoteStopAndRetainsAnUnknownReceipt()
    {
        await using var app = new TestApp();
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = (await app.Store.Snapshot()).Runtimes.Single(x => x.Id == worker.RuntimeId);
        await app.Store.Write(async db => { (await db.Runtimes.FindAsync(runtime.Id))!.DesiredConnected = true; return true; });
        var handler = new BarrierHandler(worker);
        var factory = new CountingFactory(handler);
        using var supervisor = new RuntimeSupervisor(app.Store, factory, Options.Create(new ControlOptions { PollMilliseconds = 20 }), NullLogger<RuntimeSupervisor>.Instance);
        string? stopId = null;
        var injected = 0;
        var ensureId = Guid.NewGuid().ToString();
        var injectedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (stopId is null || Interlocked.CompareExchange(ref injected, 0, 0) != 0) return;
            var state = app.Store.Read(async db => (await db.Commands.FindAsync(stopId))?.State).GetAwaiter().GetResult();
            if (state != Delivery.Dispatching || Interlocked.Exchange(ref injected, 1) != 0) return;
            try
            {
                app.Store.RuntimeCommand(runtime.Id, "EnsureServer", ensureId).GetAwaiter().GetResult();
                injectedSignal.TrySetResult();
            }
            catch (Exception ex) { injectedSignal.TrySetException(ex); throw; }
        }
        app.Store.Changed += OnChanged;
        await supervisor.StartAsync(CancellationToken.None);
        try
        {
            await handler.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime));
            stopId = (await app.Store.RuntimeCommand(runtime.Id, "StopManagedServer", Guid.NewGuid().ToString())).Id;
            handler.ReleaseSnapshot.TrySetResult();
            await injectedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await TestApp.Wait(async () => (await app.Store.Snapshot()).Commands.FirstOrDefault(x => x.Id == ensureId)?.State == Delivery.Finished, "Post-claim ensure");

            var snapshot = await app.Store.Snapshot();
            Assert.Equal(1, injected);
            Assert.Equal(0, factory.StopCalls);
            Assert.Equal(Delivery.Unknown, snapshot.Commands.Single(x => x.Id == stopId).State);
            Assert.Contains(await app.Store.Read(db => db.Events.Where(x => x.Type == "RuntimeLifecycleSuperseded" && x.CommandId == stopId).ToListAsync()),
                x => x.Payload.Contains(ensureId, StringComparison.Ordinal));
            Assert.Equal(worker.NativeSessionId, snapshot.Workers.Single(x => x.Id == worker.Id).NativeSessionId);
        }
        finally
        {
            app.Store.Changed -= OnChanged;
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EnsureSupersedesQueuedStopAndOldRequestReplayCannotReverseIt()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = (await app.Store.Snapshot()).Runtimes.Single(x => x.Id == worker.RuntimeId);
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime));
        var stop = await app.Store.RuntimeCommand(runtime.Id, "StopManagedServer", Guid.NewGuid().ToString());
        var ensure = await app.Store.RuntimeCommand(runtime.Id, "EnsureServer", Guid.NewGuid().ToString());

        var snapshot = await app.Store.Snapshot();
        Assert.True(snapshot.Runtimes.Single(x => x.Id == runtime.Id).DesiredConnected);
        Assert.Equal(Delivery.Cancelled, snapshot.Commands.Single(x => x.Id == stop.Id).State);
        Assert.Equal(Delivery.Queued, snapshot.Commands.Single(x => x.Id == ensure.Id).State);
        Assert.Equal(stop.Id, (await app.Store.RuntimeCommand(runtime.Id, "StopManagedServer", stop.Id)).Id);
        Assert.True((await app.Store.Snapshot()).Runtimes.Single(x => x.Id == runtime.Id).DesiredConnected);
    }

    [Fact]
    public async Task UnsupportedOrStaleEvidenceCannotClaimAnOwnedStop()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = (await app.Store.Snapshot()).Runtimes.Single(x => x.Id == worker.RuntimeId);
        await app.Store.ObserveNativeProcess(runtime.Id, NativeProcessObservation.Unsupported(runtime.ManagedServerId, ControlStore.Now, "TransportUnsupported"));
        var unsupported = await app.Store.RuntimeCommand(runtime.Id, "StopManagedServer", Guid.NewGuid().ToString());
        Assert.Null(await Claim(app, runtime.Id, "StopManagedServer"));
        Assert.Equal(Delivery.Failed, (await app.Store.Snapshot()).Commands.Single(x => x.Id == unsupported.Id).State);

        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, ControlStore.Now - 61000));
        var stale = await app.Store.RuntimeCommand(runtime.Id, "StopManagedServer", Guid.NewGuid().ToString());
        Assert.Null(await Claim(app, runtime.Id, "StopManagedServer"));
        Assert.Equal(Delivery.Failed, (await app.Store.Snapshot()).Commands.Single(x => x.Id == stale.Id).State);
    }

    [Fact]
    public async Task OnlyExactFreshOwnedProcessCanBeClaimedAndLifecycleSettlementIsIdempotent()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = (await app.Store.Snapshot()).Runtimes.Single(x => x.Id == worker.RuntimeId);
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime));
        var stop = await app.Store.RuntimeCommand(runtime.Id, "StopManagedServer", Guid.NewGuid().ToString());

        var claimed = await Claim(app, runtime.Id, "StopManagedServer");
        Assert.Equal(stop.Id, claimed?.Id);
        Assert.Equal(Delivery.Dispatching, (await app.Store.Snapshot()).Commands.Single(x => x.Id == stop.Id).State);

        var ensure = await app.Store.RuntimeCommand(runtime.Id, "EnsureServer", Guid.NewGuid().ToString());
        await Finish(app, runtime.Id, "EnsureServer");
        await Finish(app, runtime.Id, "EnsureServer");
        var saved = (await app.Store.Snapshot()).Commands.Single(x => x.Id == ensure.Id);
        Assert.Equal(Delivery.Finished, saved.State);
        Assert.Equal(1, saved.Attempts);
        Assert.Equal(Delivery.Unknown, (await app.Store.Snapshot()).Commands.Single(x => x.Id == stop.Id).State);
    }

    private static NativeProcessObservation Observed(RuntimeRecord runtime, long? observedAt = null) =>
        new(runtime.ManagedServerId, NativeProcessObservationState.Observed, "Linux", 42, "boot-a:100", observedAt ?? ControlStore.Now,
            "SyntheticTest", "fixture", []);

    private static async Task<CommandRecord?> Claim(TestApp app, string runtimeId, string kind)
    {
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("Claim", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await (Task<CommandRecord?>)method.Invoke(supervisor, [runtimeId, kind])!;
    }

    private static async Task Finish(TestApp app, string runtimeId, string kind)
    {
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("FinishRuntimeCommands", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task<bool>)method.Invoke(supervisor, [runtimeId, kind])!;
    }

    private sealed class CountingFactory(BarrierHandler handler) : IRuntimeTransportFactory
    {
        public int StopCalls;
        public OwnedNativeProcess? ExpectedStop;
        public Task<IRuntimeTransport> Connect(RuntimeRecord runtime, CancellationToken cancellationToken) =>
            Task.FromResult<IRuntimeTransport>(new CountingTransport(new OpenCodeClient(new HttpClient(handler) { BaseAddress = new Uri("http://native.test") }), this));
    }

    private sealed class CountingTransport(OpenCodeClient api, CountingFactory factory) : IRuntimeTransport
    {
        public OpenCodeClient Api => api;
        public bool Connected => true;
        public string Platform => "fixture";
        public Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopOwnedServer(CancellationToken cancellationToken) { factory.StopCalls++; return Task.CompletedTask; }
        public Task StopOwnedServer(OwnedNativeProcess expected, CancellationToken cancellationToken)
        {
            factory.ExpectedStop = expected;
            factory.StopCalls++;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { api.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class BarrierHandler(WorkerRecord worker) : HttpMessageHandler
    {
        public TaskCompletionSource SnapshotStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int snapshots;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/global/event")
            {
                var stream = new StreamContent(new IdleEventStream());
                stream.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return new(HttpStatusCode.OK) { Content = stream };
            }
            if (path == "/doc") return Reply(new { paths = new[] { "/global/event", "/session", "/session/{sessionID}/prompt_async", "/session/{sessionID}/message", "/session/{sessionID}/message/{messageID}", "/session/status", "/provider", "/path" }.ToDictionary(x => x, _ => new { }) });
            if (path is "/global/health" or "/session/status") return Reply(new { healthy = true, version = BootstrapScript.Version });
            if (path == "/provider") return Reply(new { connected = Array.Empty<string>(), all = Array.Empty<object>() });
            if (path == "/session/" + worker.NativeSessionId)
            {
                if (Interlocked.Increment(ref snapshots) == 1)
                {
                    SnapshotStarted.TrySetResult();
                    await ReleaseSnapshot.Task.WaitAsync(cancellationToken);
                }
                return Reply(new { directory = worker.Directory, time = new { created = 1L } });
            }
            if (path == "/session/" + worker.NativeSessionId + "/message") return Reply(Array.Empty<object>());
            if (path is "/permission" or "/question") return Reply(Array.Empty<object>());
            throw new InvalidOperationException("Unexpected fixture route " + path);
        }

        private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }

    private sealed class IdleEventStream() : MemoryStream("data: {\"type\":\"server.heartbeat\"}\n\n"u8.ToArray())
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
