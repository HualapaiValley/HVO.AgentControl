using System.Reflection;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ProviderRecoveryLifecycleTests
{
    private const string PoolId = "provider:openai";

    [Theory]
    [InlineData("Exhausted")]
    [InlineData("AuthenticationRequired")]
    public async Task ManualHoldBlocksReservedPreflightUntilAccessResumeButPeersStillWait(string category)
    {
        await using var app = new TestApp();
        var (worker, probe, late, peer) = await Reserve(app);
        await Complete(app, probe.Id, Delivery.Queued);
        await Fail(app, late, category);
        Assert.Null(await Claim(app, worker.RuntimeId));
        var held = Assert.Single(await app.Store.ProviderPools());
        Assert.Equal(category, held.State);
        Assert.Equal(probe.Id, held.RecoveryCommandId);
        Assert.Null(held.RetryAt);
        await app.Store.ResumePool(PoolId, new(held.Revision, true));
        Assert.Equal(probe.Id, (await Claim(app, worker.RuntimeId))!.Id);
        Assert.Null(await Claim(app, peer.RuntimeId));
        Assert.Equal(Delivery.Running, (await app.Store.Read(async db => await db.Commands.FindAsync(late.Id)))!.State);
    }

    [Theory]
    [InlineData("cancel", "Recovering")]
    [InlineData("cancel", "Exhausted")]
    [InlineData("cancel", "AuthenticationRequired")]
    [InlineData("reject", "Recovering")]
    [InlineData("reject", "Exhausted")]
    public async Task DefinitiveUnsentSettlementIsIdempotentAndPreservesAccessAcrossRestart(string action, string access)
    {
        string data, secrets, probeId;
        await using (var app = new TestApp())
        {
            var (_, probe, late, peer) = await Reserve(app);
            if (access != "Recovering") await Fail(app, late, access);
            if (action == "cancel")
            {
                await Complete(app, probe.Id, Delivery.Queued);
                await app.Store.EditQueue(probe.Id, "cancel");
                await Assert.ThrowsAsync<ControlException>(() => app.Store.EditQueue(probe.Id, "cancel"));
            }
            else
            {
                await Complete(app, probe.Id, Delivery.Failed);
                await Complete(app, probe.Id, Delivery.Failed);
            }
            Assert.Null(await Claim(app, peer.RuntimeId));
            data = app.DataPath; secrets = app.SecretPath; probeId = probe.Id;
        }
        await using var restarted = new TestApp(data, secrets);
        var pool = Assert.Single(await restarted.Store.ProviderPools());
        Assert.Empty(pool.RecoveryCommandId);
        Assert.Equal(access == "Recovering" ? "RecoveryRequired" : access, pool.State);
        Assert.Single(await restarted.Store.Read(db => db.Events.Where(x => x.Type == "ProviderRecoveryObserved" && x.CommandId == probeId).ToListAsync()));
    }

    [Fact]
    public async Task UnrelatedCompletionAndMissingOwnerCannotReleaseReservedAuthority()
    {
        await using var app = new TestApp();
        var (_, probe, late, peer) = await Reserve(app);
        await app.Store.Write(async db =>
        {
            await ControlStore.ObserveProviderCompletion(db, late, true);
            Assert.Equal(probe.Id, (await db.Set<ProviderPool>().FindAsync(PoolId))!.RecoveryCommandId);
            // Simulate a broken legacy/corrupted reference, not a native settlement.
            db.Commands.Remove((await db.Commands.FindAsync(probe.Id))!);
            return true;
        });
        var held = Assert.Single(await app.Store.ProviderPools());
        await app.Store.ResumePool(PoolId, new(held.Revision, true));
        Assert.Null(await Claim(app, peer.RuntimeId));
        Assert.Equal(probe.Id, Assert.Single(await app.Store.ProviderPools()).RecoveryCommandId);
    }

    [Theory]
    [InlineData(Delivery.Queued)]
    [InlineData(Delivery.Running)]
    [InlineData(Delivery.Unknown)]
    public async Task AccessResumePreservesReservationAcrossActualStoreRestart(string state)
    {
        string data, secrets, probeId, peerRuntime;
        await using (var app = new TestApp())
        {
            var (_, probe, late, peer) = await Reserve(app);
            await Complete(app, probe.Id, state);
            await Fail(app, late, "Exhausted");
            var held = Assert.Single(await app.Store.ProviderPools());
            await app.Store.ResumePool(PoolId, new(held.Revision, true));
            Assert.Equal(probe.Id, Assert.Single(await app.Store.ProviderPools()).RecoveryCommandId);
            Assert.Null(await Claim(app, peer.RuntimeId));
            data = app.DataPath; secrets = app.SecretPath; probeId = probe.Id; peerRuntime = peer.RuntimeId;
        }
        await using var restarted = new TestApp(data, secrets);
        Assert.Null(await Claim(restarted, peerRuntime));
        Assert.Equal(probeId, Assert.Single(await restarted.Store.ProviderPools()).RecoveryCommandId);
        Assert.Equal(state, (await restarted.Store.Read(async db => await db.Commands.FindAsync(probeId)))!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownAndOwnerAcknowledgementRetainAuthorityUntilNativeTerminalEvidence(bool acknowledge)
    {
        await using var app = new TestApp();
        var (worker, probe, late, peer) = await Reserve(app);
        await Complete(app, probe.Id, Delivery.Unknown);
        if (acknowledge) await app.Store.EditQueue(probe.Id, "resolveUnknown");
        if (acknowledge)
            await Assert.ThrowsAsync<ControlException>(() => app.Store.DeleteWorker(worker.Id, new(Guid.NewGuid().ToString(), worker.SettingsRevision)));
        await Fail(app, late, "Throttled");
        await app.Store.Write(async db => { (await db.Set<ProviderPool>().FindAsync(PoolId))!.RetryAt = ControlStore.Now - 1; return true; });
        Assert.Equal(probe.Id, Assert.Single(await app.Store.ProviderPools()).RecoveryCommandId);
        Assert.Null(await Claim(app, peer.RuntimeId));
        var state = acknowledge ? Delivery.Cancelled : Delivery.Unknown;
        Assert.Equal(state, (await app.Store.Read(async db => await db.Commands.FindAsync(probe.Id)))!.State);
        // Access verification does not settle the request either.
        var held = Assert.Single(await app.Store.ProviderPools());
        await app.Store.ResumePool(PoolId, new(held.Revision, true));
        Assert.Null(await Claim(app, peer.RuntimeId));
        await Reconcile(app, worker, new(JsonSerializer.SerializeToElement(new { }), [], "idle", default, [], []));
        Assert.Equal(probe.Id, Assert.Single(await app.Store.ProviderPools()).RecoveryCommandId);
        await Reconcile(app, worker, Terminal(worker, probe));
        await Reconcile(app, worker, Terminal(worker, probe));
        Assert.Empty(Assert.Single(await app.Store.ProviderPools()).RecoveryCommandId);
        Assert.Equal(acknowledge ? Delivery.Cancelled : Delivery.Finished,
            (await app.Store.Read(async db => await db.Commands.FindAsync(probe.Id)))!.State);
        Assert.NotNull(await Claim(app, peer.RuntimeId));
        Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "ProviderRecoveryObserved" && x.CommandId == probe.Id).ToListAsync()));
        Assert.Equal(Delivery.Running, (await app.Store.Read(async db => await db.Commands.FindAsync(late.Id)))!.State);
    }

    [Theory]
    [InlineData(Delivery.Accepted, "Exhausted")]
    [InlineData(Delivery.Unknown, "AuthenticationRequired")]
    public async Task AcceptedAbortSettlesOwnerOnceAndRetainsFailureInSameSnapshot(string state, string category)
    {
        await using var app = new TestApp();
        var (worker, probe, late, peer) = await Reserve(app);
        await Complete(app, probe.Id, state);
        var abort = await app.Store.Abort(worker.Id, Guid.NewGuid().ToString());
        await Complete(app, abort.Id, Delivery.Accepted);
        var snapshot = Terminal(worker, probe, category);
        await Reconcile(app, worker, snapshot);
        await Reconcile(app, worker, snapshot);
        var pool = Assert.Single(await app.Store.ProviderPools());
        Assert.Equal(category, pool.State);
        Assert.Empty(pool.RecoveryCommandId);
        Assert.Equal(Delivery.Cancelled, (await app.Store.Read(async db => await db.Commands.FindAsync(probe.Id)))!.State);
        Assert.Equal(Delivery.Finished, (await app.Store.Read(async db => await db.Commands.FindAsync(abort.Id)))!.State);
        Assert.Equal(2, await app.Store.Read(db => db.Set<ProviderFailureReceipt>().CountAsync()));
        Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "ProviderRecoveryObserved" && x.CommandId == probe.Id).ToListAsync()));
        Assert.Null(await Claim(app, peer.RuntimeId));
        Assert.Equal(Delivery.Running, (await app.Store.Read(async db => await db.Commands.FindAsync(late.Id)))!.State);
    }

    [Theory]
    [InlineData(Delivery.Queued)]
    [InlineData(Delivery.Dispatching)]
    [InlineData(Delivery.Accepted)]
    [InlineData(Delivery.Running)]
    [InlineData(Delivery.Unknown)]
    public async Task PriorSchemaUpgradePreservesVerifiableLeaseUntilSettlement(string state)
    {
        var data = Path.Combine(Path.GetTempPath(), "hvo-legacy-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var worker = new WorkerRecord { RuntimeId = "legacy-runtime", NativeSessionId = "ses_legacy", ProviderId = "openai", Stale = false, Activity = "Idle" };
        var probe = Prompt(worker); probe.State = state;
        await SeedLegacy(data, worker, probe);
        await using var app = new TestApp(data);
        var pool = Assert.Single(await app.Store.ProviderPools());
        Assert.Equal("Recovering", pool.State);
        Assert.Equal(probe.Id, pool.RecoveryCommandId);
        if (state == Delivery.Queued) await app.Store.EditQueue(probe.Id, "cancel");
        else await Reconcile(app, worker, Terminal(worker, probe));
        pool = Assert.Single(await app.Store.ProviderPools());
        Assert.Empty(pool.RecoveryCommandId);
        Assert.Equal(state == Delivery.Queued ? "RecoveryRequired" : "Available", pool.State);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("terminal")]
    [InlineData("wrong-pool")]
    [InlineData("wrong-runtime")]
    [InlineData("wrong-kind")]
    public async Task PriorSchemaUnverifiableOwnershipCannotBeReleasedByAccessVerification(string defect)
    {
        var data = Path.Combine(Path.GetTempPath(), "hvo-legacy-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var worker = new WorkerRecord { RuntimeId = "legacy-runtime", NativeSessionId = "ses_legacy", ProviderId = "openai", Stale = false, Activity = "Idle" };
        var probe = Prompt(worker); probe.State = Delivery.Running;
        if (defect == "terminal") probe.State = Delivery.Finished;
        if (defect == "wrong-pool") probe.ProviderPoolId = "provider:other";
        if (defect == "wrong-runtime") probe.RuntimeId = "unrelated-runtime";
        if (defect == "wrong-kind") probe.Kind = "Abort";
        await SeedLegacy(data, worker, defect == "missing" ? null : probe);
        await using var app = new TestApp(data);
        var held = Assert.Single(await app.Store.ProviderPools());
        Assert.NotEqual("Available", held.State);
        Assert.True(held.RecoveryOwnershipUnknown);
        Assert.Empty(held.RecoveryCommandId);
        await app.Store.ResumePool(PoolId, new(held.Revision, true));
        await app.Store.Write(async db =>
        {
            Assert.False(await ControlStore.ProviderDispatchAllowed(db, worker, Prompt(worker)));
            return true;
        });
    }

    private static async Task SeedLegacy(string data, WorkerRecord worker, CommandRecord? probe)
    {
        await using var db = new ControlDb(new DbContextOptionsBuilder<ControlDb>().UseSqlite($"Data Source={Path.Combine(data, "agentcontrol.db")}").Options);
        var previous = db.Database.GetMigrations().TakeWhile(x => !x.EndsWith("ProviderRecoveryLeaseOwnership", StringComparison.Ordinal)).Last();
        await db.GetService<IMigrator>().MigrateAsync(previous);
        db.Runtimes.Add(new RuntimeRecord { Id = worker.RuntimeId, DesiredConnected = false });
        db.Workers.Add(worker);
        if (probe is not null) db.Commands.Add(probe);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO ProviderPool (Id, ProviderId, State, RetryAt, ObservedAt, Revision, ConsecutiveFailures, LastCommandId) VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})",
            PoolId, "openai", "Recovering", 0L, ControlStore.Now, 3L, 1, probe?.Id ?? "missing-command");
    }

    private static async Task<(WorkerRecord Worker, CommandRecord Probe, CommandRecord Late, WorkerRecord Peer)> Reserve(TestApp app)
    {
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var lateWorker = await PersistenceTests.SeedWorker(app.Store);
        var peer = await PersistenceTests.SeedWorker(app.Store);
        var original = Prompt(worker); original.State = Delivery.Finished;
        var probe = Prompt(worker);
        var late = Prompt(lateWorker); late.State = Delivery.Running;
        await app.Store.Write(async db =>
        {
            foreach (var id in new[] { worker.Id, lateWorker.Id, peer.Id })
            {
                var saved = (await db.Workers.FindAsync(id))!;
                saved.Stale = false; saved.Activity = "Idle"; saved.ProviderId = "openai";
            }
            db.Commands.AddRange(original, probe, late, Prompt(peer));
            await ControlStore.ObserveProviderFailure(db, worker, original, "first", new("Throttled", 429, null));
            (await db.Set<ProviderPool>().FindAsync(PoolId))!.RetryAt = ControlStore.Now - 1;
            return true;
        });
        Assert.Equal(probe.Id, (await Claim(app, worker.RuntimeId))!.Id);
        return (worker, probe, late, peer);
    }

    private static Task<bool> Fail(TestApp app, CommandRecord command, string category) => app.Store.Write(async db =>
    {
        await ControlStore.ObserveProviderFailure(db, (await db.Workers.FindAsync(command.WorkerId))!, command, "late", new(category, 429, null));
        return true;
    });

    private static CommandRecord Prompt(WorkerRecord worker) => new()
    {
        Id = Guid.NewGuid().ToString(),
        RuntimeId = worker.RuntimeId,
        WorkerId = worker.Id,
        Kind = "Prompt",
        State = Delivery.Queued,
        NativeMessageId = "msg_" + Guid.NewGuid().ToString("N"),
        ProviderPoolId = PoolId,
        Payload = Json.Write(new PromptInput(Guid.NewGuid().ToString(), "Disposable lease regression", 0, "openai", "fixture"))
    };

    private static NativeSnapshot Terminal(WorkerRecord worker, CommandRecord probe, string? category = null) => new(JsonSerializer.SerializeToElement(new { }),
        [JsonSerializer.SerializeToElement(new { info = new { id = probe.NativeMessageId, role = "user", sessionID = worker.NativeSessionId, time = new { created = 1L } }, parts = Array.Empty<object>() }),
         JsonSerializer.SerializeToElement(new
         {
             info = new { id = "msg_final", role = "assistant", sessionID = worker.NativeSessionId, parentID = probe.NativeMessageId, finish = "stop", time = new { created = 2L, completed = 3L },
                 error = category is null ? null : new { name = "APIError", data = new { statusCode = category == "Exhausted" ? 429 : 401, code = category == "Exhausted" ? "insufficient_quota" : "" } } },
             parts = new[] { new { type = "text", text = "Disposable terminal evidence" } }
         })], "idle", default, [], []);

    private static async Task<CommandRecord?> Claim(TestApp app, string runtimeId)
    {
        using var supervisor = Supervisor(app);
        return await (Task<CommandRecord?>)typeof(RuntimeSupervisor).GetMethod("Claim", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(supervisor, [runtimeId, null])!;
    }

    private static async Task Complete(TestApp app, string commandId, string state)
    {
        using var supervisor = Supervisor(app);
        await (Task<bool>)typeof(RuntimeSupervisor).GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(supervisor, [commandId, state, "Disposable preflight/delivery transition"])!;
    }

    private static async Task Reconcile(TestApp app, WorkerRecord worker, NativeSnapshot snapshot)
    {
        using var supervisor = Supervisor(app);
        await (Task<bool>)typeof(RuntimeSupervisor).GetMethod("Reconcile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(supervisor, [worker.Id, snapshot])!;
    }

    private static RuntimeSupervisor Supervisor(TestApp app) => new(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
}
