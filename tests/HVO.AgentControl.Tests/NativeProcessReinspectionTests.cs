using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class NativeProcessReinspectionTests
{
    private const string Incarnation = "boot-a:100";

    [Fact]
    public async Task OwnerApiRequiresCsrfAndRecordsOnlyTheBoundReinspectionRequest()
    {
        await using var app = new TestApp();
        await StopBackgroundSupervisor(app);
        var ready = await Ready(app);
        var route = "/api/v1/runtimes/" + ready.Runtime.Id + "/native-process/reinspect";
        var input = Request(ready.Runtime);
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(route, input)).StatusCode);

        using var missingCsrf = await app.SignIn();
        missingCsrf.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await missingCsrf.PostAsJsonAsync(route, input)).StatusCode);

        using var owner = await app.SignIn();
        var response = await owner.PostAsJsonAsync(route, input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var command = (await response.Content.ReadFromJsonAsync<CommandRecord>())!;
        Assert.Equal("ReinspectNativeProcess", command.Kind);
        Assert.Equal(input.Id, command.Id);
        Assert.Equal(Delivery.Queued, command.State);
        Assert.DoesNotContain("expectedProcessId", command.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", command.ExecutionPayload);
        Assert.DoesNotContain(Incarnation, command.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchingLiveReinspectionRefreshesOldEvidenceWithoutLifecycleMutationAndReplaysDurably()
    {
        await using var app = new TestApp();
        await StopBackgroundSupervisor(app);
        var ready = await Ready(app);
        var input = Request(ready.Runtime);
        var command = await app.Store.ReinspectNativeProcess(ready.Runtime.Id, input);
        Assert.Equal(command.Id, (await app.Store.ReinspectNativeProcess(ready.Runtime.Id, input)).Id);

        var transport = new ProbeTransport(Identity(ready.Runtime));
        await Dispatch(app, (await Claim(app, ready.Runtime.Id))!, ready.Runtime, transport);

        var saved = (await app.Store.Snapshot()).Commands.Single(x => x.Id == command.Id);
        var runtime = (await app.Store.Snapshot()).Runtimes.Single(x => x.Id == ready.Runtime.Id);
        var worker = (await app.Store.Snapshot()).Workers.Single(x => x.Id == ready.Worker.Id);
        var current = (await app.Store.NativeProcessObservations(ready.Runtime.Id)).First();
        Assert.Equal(Delivery.Finished, saved.State);
        var exact = await app.Store.Command(saved.Id);
        Assert.Equal("Confirmed", System.Text.Json.JsonDocument.Parse(exact.ResultJson).RootElement.GetProperty("state").GetString());
        Assert.DoesNotContain(Incarnation, exact.ResultJson, StringComparison.Ordinal);
        Assert.Equal("Fresh", current.Freshness);
        Assert.Equal(Incarnation, current.Incarnation);
        Assert.Equal(1, transport.ProbeCalls);
        Assert.Equal(0, transport.StopCalls);
        Assert.True(runtime.DesiredConnected);
        Assert.Equal("Connected", runtime.Transport);
        Assert.Equal(ready.Runtime.Revision, runtime.Revision);
        Assert.Equal(ready.Worker.NativeSessionId, worker.NativeSessionId);
        Assert.Equal(ready.Worker.Activity, worker.Activity);
        Assert.Equal(ready.Worker.CurrentAction, worker.CurrentAction);

        var replay = await app.Store.ReinspectNativeProcess(ready.Runtime.Id, input);
        Assert.Equal(command.Id, replay.Id);
        Assert.Equal(Delivery.Finished, replay.State);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ReinspectNativeProcess(ready.Runtime.Id,
            input with { ExpectedIncarnation = "boot-a:200" }));
    }

    [Fact]
    public async Task ReinspectionRejectsPidOrIncarnationMismatchWithoutRefreshingAuthority()
    {
        await using var app = new TestApp();
        await StopBackgroundSupervisor(app);
        var ready = await Ready(app);
        var command = await app.Store.ReinspectNativeProcess(ready.Runtime.Id, Request(ready.Runtime));
        var transport = new ProbeTransport(Identity(ready.Runtime) with { ProcessId = 43 });

        await Dispatch(app, (await Claim(app, ready.Runtime.Id))!, ready.Runtime, transport);

        Assert.Equal(Delivery.Failed, (await app.Store.Snapshot()).Commands.Single(x => x.Id == command.Id).State);
        Assert.Single(await app.Store.NativeProcessObservations(ready.Runtime.Id));
        Assert.Equal(0, transport.StopCalls);
    }

    [Fact]
    public async Task RevisionChangeDuringProbeCancelsReinspectionWithoutRecordingTheProbe()
    {
        await using var app = new TestApp();
        await StopBackgroundSupervisor(app);
        var ready = await Ready(app);
        var command = await app.Store.ReinspectNativeProcess(ready.Runtime.Id, Request(ready.Runtime));
        var transport = new ProbeTransport(Identity(ready.Runtime), wait: true);
        var dispatch = Dispatch(app, (await Claim(app, ready.Runtime.Id))!, ready.Runtime, transport);
        await transport.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await app.Store.RuntimeCommand(ready.Runtime.Id, "RefreshState", Guid.NewGuid().ToString());
        transport.ReleaseProbe.TrySetResult();
        await dispatch;

        Assert.Equal(Delivery.Cancelled, (await app.Store.Snapshot()).Commands.Single(x => x.Id == command.Id).State);
        Assert.Single(await app.Store.NativeProcessObservations(ready.Runtime.Id));
    }

    [Fact]
    public async Task UnsupportedProbeFailsWithoutMutatingTmuxOrReplayingAfterRestart()
    {
        string data;
        string secrets;
        ReinspectNativeProcessInput input;
        string commandId;
        await using (var app = new TestApp())
        {
            await StopBackgroundSupervisor(app);
            var ready = await Ready(app);
            input = Request(ready.Runtime);
            commandId = (await app.Store.ReinspectNativeProcess(ready.Runtime.Id, input)).Id;
            var transport = new ProbeTransport(null);
            await Dispatch(app, (await Claim(app, ready.Runtime.Id))!, ready.Runtime, transport);
            Assert.Equal(Delivery.Failed, (await app.Store.Snapshot()).Commands.Single(x => x.Id == commandId).State);
            Assert.Single(await app.Store.NativeProcessObservations(ready.Runtime.Id));
            Assert.Equal(0, transport.StopCalls);
            data = app.DataPath;
            secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var replay = await restarted.Store.ReinspectNativeProcess((await restarted.Store.Snapshot()).Runtimes.Single().Id, input);
        Assert.Equal(commandId, replay.Id);
        Assert.Equal(Delivery.Failed, replay.State);
    }

    [Fact]
    public async Task TimeoutAndBackendRestartDoNotReplayAnUnconfirmedReinspection()
    {
        await using var app = new TestApp();
        await StopBackgroundSupervisor(app);
        var ready = await Ready(app);
        var timeoutInput = Request(ready.Runtime);
        var timeout = await app.Store.ReinspectNativeProcess(ready.Runtime.Id, timeoutInput);
        await Dispatch(app, (await Claim(app, ready.Runtime.Id))!, ready.Runtime,
            new ProbeTransport(Identity(ready.Runtime), error: new OperationCanceledException()));
        Assert.Equal(Delivery.Failed, (await app.Store.Snapshot()).Commands.Single(x => x.Id == timeout.Id).State);
        Assert.Single(await app.Store.NativeProcessObservations(ready.Runtime.Id));

        var restartInput = Request(ready.Runtime);
        var restart = await app.Store.ReinspectNativeProcess(ready.Runtime.Id, restartInput);
        Assert.Equal(restart.Id, (await Claim(app, ready.Runtime.Id))?.Id);
        await app.Store.Recover();
        var retained = await app.Store.ReinspectNativeProcess(ready.Runtime.Id, restartInput);
        Assert.Equal(restart.Id, retained.Id);
        Assert.Equal(Delivery.Unknown, retained.State);
        Assert.Single(await app.Store.NativeProcessObservations(ready.Runtime.Id));
    }

    [Fact]
    public async Task StopCannotConsumeAReinspectionReceiptSupersededBeforeItsEffect()
    {
        await using var app = new TestApp();
        await StopBackgroundSupervisor(app);
        var ready = await Ready(app);
        await app.Store.ObserveNativeProcess(ready.Runtime.Id, new(ready.Runtime.ManagedServerId, NativeProcessObservationState.Observed,
            "Linux", 42, Incarnation, ControlStore.Now - 1000, "SyntheticTest", "first fresh fixture", []));
        var stop = await app.Store.RuntimeCommand(ready.Runtime.Id, "StopManagedServer", Guid.NewGuid().ToString());
        Assert.Equal(stop.Id, (await Claim(app, ready.Runtime.Id, "StopManagedServer"))?.Id);
        await app.Store.ObserveNativeProcess(ready.Runtime.Id, new(ready.Runtime.ManagedServerId, NativeProcessObservationState.Observed,
            "Linux", 42, Incarnation, ControlStore.Now, "SyntheticTest", "newer fresh fixture", []));

        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("StopStillCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var current = await (Task<bool>)method.Invoke(supervisor, [stop.Id, Json.Read<RuntimeLifecycleInput>(stop.Payload)])!;
        Assert.False(current);
        var saved = (await app.Store.Snapshot()).Commands.Single(x => x.Id == stop.Id);
        Assert.Equal(Delivery.Cancelled, saved.State);
        Assert.Contains("no stop was sent", saved.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await Claim(app, ready.Runtime.Id, "StopManagedServer"));
    }

    [Fact]
    public void LiveProbeScriptOnlyReadsTmuxState()
    {
        var script = NativeProcessProbe.LiveScript(new RuntimeRecord { StateDirectory = "/state", ManagedServerId = "managed", ApiPort = 4096 });
        Assert.DoesNotContain("kill-session", script, StringComparison.Ordinal);
        Assert.DoesNotContain("respawn-pane", script, StringComparison.Ordinal);
        Assert.DoesNotContain("set-option", script, StringComparison.Ordinal);
    }

    private static async Task<ReadyRuntime> Ready(TestApp app)
    {
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(async db =>
        {
            var runtime = (await db.Runtimes.FindAsync(worker.RuntimeId))!;
            runtime.DesiredConnected = true;
            runtime.Transport = "Connected";
            var savedWorker = (await db.Workers.FindAsync(worker.Id))!;
            savedWorker.Activity = "Working";
            savedWorker.CurrentAction = "Owner pause remains in effect.";
            return true;
        });
        var runtime = (await app.Store.Snapshot()).Runtimes.Single(x => x.Id == worker.RuntimeId);
        await app.Store.ObserveNativeProcess(runtime.Id, new(runtime.ManagedServerId, NativeProcessObservationState.Observed,
            "Linux", 42, Incarnation, ControlStore.Now - 61000, "SyntheticTest", "old fixture", []));
        return new(runtime, (await app.Store.Snapshot()).Workers.Single(x => x.Id == worker.Id));
    }

    private static ReinspectNativeProcessInput Request(RuntimeRecord runtime) =>
        new(Guid.NewGuid().ToString(), runtime.Revision, runtime.ManagedServerId, Incarnation);

    private static RuntimeProcessIdentity Identity(RuntimeRecord runtime) =>
        new(RuntimeConnections.Ssh, runtime.ManagedServerId, Incarnation, 42, ControlStore.Now);

    private static async Task<CommandRecord?> Claim(TestApp app, string runtimeId, string? kind = null)
    {
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("Claim", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await (Task<CommandRecord?>)method.Invoke(supervisor, [runtimeId, kind])!;
    }

    private static async Task Dispatch(TestApp app, CommandRecord command, RuntimeRecord runtime, IRuntimeTransport transport)
    {
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(supervisor, [command, runtime, transport, CancellationToken.None])!;
    }

    private static async Task StopBackgroundSupervisor(TestApp app) =>
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);

    private sealed record ReadyRuntime(RuntimeRecord Runtime, WorkerRecord Worker);

    private sealed class ProbeTransport(RuntimeProcessIdentity? observation, bool wait = false, Exception? error = null) : IRuntimeTransport
    {
        private readonly bool wait = wait;
        public TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProbeCalls { get; private set; }
        public int StopCalls { get; private set; }
        public OpenCodeClient Api { get; } = new(new HttpClient { BaseAddress = new Uri("http://reinspection.test") });
        public bool Connected => true;
        public string Platform => "Linux";
        public async Task<RuntimeProcessIdentity?> ProbeProcessIdentity(CancellationToken token)
        {
            ProbeCalls++;
            ProbeStarted.TrySetResult();
            if (wait) await ReleaseProbe.Task.WaitAsync(token);
            if (error is not null) throw error;
            return observation is null ? null : observation with { ObservedAt = ControlStore.Now };
        }
        public Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken token) => throw new NotSupportedException();
        public Task StopOwnedServer(CancellationToken token) { StopCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Api.Dispose(); return ValueTask.CompletedTask; }
    }
}
