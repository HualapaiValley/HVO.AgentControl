using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CoordinationRestartRegressionTests
{
    [Fact]
    public async Task WebHostRestartPreservesActiveParticipantIdentityWithoutRedispatchOrOwnerResume()
    {
        string data, secrets, runId, commandId, sessionId, nativeMessageId;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            await WaitForBackendStart(app.Store);
            var (coordinator, participant) = await Seed(app.Store);
            sessionId = participant.NativeSessionId;
            var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id,
                "Keep the existing native turn active.", [participant.Id]));
            runId = run.Id;
            Assert.True(await app.Store.CoordinationTick());
            await ObserveIdle(app.Store);
            await FinishDecision(app.Store, runId, new("Dispatch once", [new("send_prompt", participant.Id, "Continue the native work.")]));
            Assert.True(await app.Store.CoordinationTick());
            var commands = (await app.Store.Snapshot()).Commands;
            Assert.Contains(commands, x => x.Origin == "coordinator:" + runId);
            var dispatch = commands.Single(x => x.Origin == "coordinator:" + runId);
            commandId = dispatch.Id;
            nativeMessageId = "msg_restart_active";
            await app.Store.Write(async db =>
            {
                var command = (await db.Commands.FindAsync(commandId))!;
                command.State = Delivery.Running; command.NativeMessageId = nativeMessageId; command.Attempts = 1;
                var worker = (await db.Workers.FindAsync(participant.Id))!;
                worker.Activity = "Active"; worker.Stale = false; worker.LastObservedAt = ControlStore.Now;
                return true;
            });
        }

        await using var restarted = new TestApp(data, secrets);
        // A host restart loses monitoring, not native activity. Recovery makes the observation stale
        // while retaining the active command/session identity for later native reconciliation.
        await restarted.Store.Recover();
        var saved = (await restarted.Store.Coordinations()).Single(x => x.Id == runId);
        var detail = await restarted.Store.Detail((await restarted.Store.Snapshot()).Workers.Single(x => x.NativeSessionId == sessionId).Id);
        var active = detail.Commands.Single(x => x.Id == commandId);
        Assert.Equal("Waiting", saved.State);
        Assert.Equal(sessionId, detail.Worker.NativeSessionId);
        Assert.Equal("Active", detail.Worker.Activity);
        Assert.True(detail.Worker.Stale);
        Assert.Equal(Delivery.Running, active.State);
        Assert.Equal(nativeMessageId, active.NativeMessageId);
        Assert.Equal(1, active.Attempts);

        Assert.False(await restarted.Store.CoordinationTick());
        var snapshot = await restarted.Store.Snapshot();
        Assert.Single(snapshot.Commands, x => x.Origin == "coordinator:" + runId);
        Assert.Single(snapshot.Commands, x => x.Origin == "coordinator-decision:" + runId);
    }

    // Requires tests/Fixtures/start.sh and HVO_SSH_FIXTURES=1. The test above remains the
    // fixture-independent durable restart regression; this one proves the live SSH/native boundary.
    [SshFact]
    public async Task ActiveNativeCoordinationSurvivesWebHostRestartWithoutSecondSubmission()
    {
        var data = Path.Combine(Path.GetTempPath(), "hvo-coordination-restart-" + Guid.NewGuid().ToString("N"));
        var app = new TestApp(data, SshIntegrationTests.FixtureSecrets);
        var runtime = SshIntegrationTests.Profile("a", "Coordination restart", Random.Shared.Next(30001, 40000));
        try
        {
            var store = app.Store;
            runtime = await store.SaveRuntime(runtime);
            await store.RuntimeCommand(runtime.Id, "EnsureServer", Guid.NewGuid().ToString());
            await TestApp.Wait(async () => (await store.Snapshot()).Runtimes.Single().Health == "Healthy", "Fixture runtime did not connect", 60);
            var coordinator = await SshIntegrationTests.Create(store, runtime, "restart-coordinator", "/home/agent/workspaces/a");
            var participant = await SshIntegrationTests.Create(store, runtime, "restart-participant", "/home/agent/workspaces/b");
            await store.Write(async db => { (await db.Workers.FindAsync(coordinator.Id))!.Role = SessionRoles.Coordinator; return true; });
            await TestApp.Wait(async () => (await store.Snapshot()).Workers.All(x => !x.Stale), "Fixture workers did not reconcile", 60);

            var run = await store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id,
                "Keep the participant's active native command running across restart.", [participant.Id]));
            await store.CoordinationTick();
            await FinishDecision(store, run.Id, new("Dispatch once", [new("send_prompt", participant.Id, "Keep working [hold]")]));
            await store.CoordinationTick();
            var dispatch = (await store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id);
            await TestApp.Wait(async () =>
            {
                var detail = await store.Detail(participant.Id);
                return detail.Worker.Activity == "Active" && detail.Commands.Single(x => x.Id == dispatch.Id).State == Delivery.Running;
            }, "Participant command did not become active", 30);

            var active = (await store.Detail(participant.Id)).Commands.Single(x => x.Id == dispatch.Id);
            var sessionId = participant.NativeSessionId;
            var nativeMessageId = active.NativeMessageId;
            var attempts = active.Attempts;
            var factory = app.Services.GetRequiredService<IRuntimeTransportFactory>();
            await using var probe = await factory.Connect(runtime, CancellationToken.None);
            var submissions = (await probe.Api.Get("/fixture/stats", CancellationToken.None)).GetProperty("submissions").GetInt32();

            await app.DisposeAsync();
            app = new TestApp(data, SshIntegrationTests.FixtureSecrets); store = app.Store;
            await TestApp.Wait(async () =>
            {
                var detail = await store.Detail(participant.Id);
                return !detail.Worker.Stale && detail.Worker.Activity == "Active";
            }, "Restarted host did not reconcile active native work", 60);
            var afterRestart = (await store.Detail(participant.Id)).Commands.Single(x => x.Id == dispatch.Id);
            Assert.Equal(sessionId, (await store.Detail(participant.Id)).Worker.NativeSessionId);
            Assert.Equal(nativeMessageId, afterRestart.NativeMessageId);
            Assert.Equal(attempts, afterRestart.Attempts);
            Assert.Equal(Delivery.Running, afterRestart.State);
            Assert.Equal(submissions, (await probe.Api.Get("/fixture/stats", CancellationToken.None)).GetProperty("submissions").GetInt32());

            Assert.False(await store.CoordinationTick());
            Assert.Single((await store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
            await SshIntegrationTests.Finished(store, dispatch);
        }
        finally
        {
            try
            {
                var factory = app.Services.GetRequiredService<IRuntimeTransportFactory>();
                await using var connection = await factory.Connect(runtime, CancellationToken.None);
                await connection.StopOwnedServer(CancellationToken.None);
            }
            catch (Exception) { }
            await app.DisposeAsync();
        }
    }

    private static async Task<(WorkerRecord Coordinator, WorkerRecord Participant)> Seed(ControlStore store)
    {
        var coordinator = await PersistenceTests.SeedWorker(store);
        var participant = new WorkerRecord
        {
            Name = "Restart participant",
            RuntimeId = coordinator.RuntimeId,
            ManagedServerId = coordinator.ManagedServerId,
            NativeSessionId = "ses_restart_participant",
            Directory = "/home/agent/workspaces/restart-participant",
            Activity = "Idle"
        };
        await store.Write(async db =>
        {
            (await db.Workers.FindAsync(coordinator.Id))!.Role = SessionRoles.Coordinator;
            db.Workers.Add(participant);
            foreach (var worker in await db.Workers.ToListAsync())
            {
                worker.Activity = "Idle"; worker.Stale = false; worker.LastObservedAt = ControlStore.Now;
            }
            return true;
        });
        return (coordinator, participant);
    }

    private static Task WaitForBackendStart(ControlStore store) => TestApp.Wait(() =>
        store.Read(db => db.Events.AnyAsync(x => x.Type == "BackendStarted")), "Runtime supervisor did not start");

    private static Task<bool> ObserveIdle(ControlStore store) => store.Write(async db =>
    {
        foreach (var worker in await db.Workers.ToListAsync())
        {
            worker.Activity = "Idle"; worker.Stale = false; worker.LastObservedAt = ControlStore.Now;
        }
        return true;
    });

    private static async Task FinishDecision(ControlStore store, string runId, CoordinatorDecision decision)
    {
        var run = (await store.Coordinations()).Single(x => x.Id == runId);
        await store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(run.DecisionCommandId!))!;
            command.State = Delivery.Finished;
            command.ResultJson = Json.Write(new { messages = new[] { new { parts = new[] { new { type = "text", text = Json.Write(decision) } } } } });
            return true;
        });
    }
}
