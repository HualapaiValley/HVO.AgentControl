using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CoordinationContextBudgetTests
{
    [Fact]
    public async Task VerboseConcurrentHistoryFitsWhileOldRunningAssignmentAndRoutingFactsRemain()
    {
        await using var app = new TestApp();
        var coordinator = await PersistenceTests.SeedWorker(app.Store);
        var a = new WorkerRecord { RuntimeId = coordinator.RuntimeId, Directory = "/budget/a", NativeSessionId = "ses_budget_a", Name = "Long worker", Activity = "Active", Stale = false, Revision = 42 };
        var b = new WorkerRecord { RuntimeId = coordinator.RuntimeId, Directory = "/budget/b", NativeSessionId = "ses_budget_b", Name = "Other worker", Activity = "Idle", Stale = false, Revision = 24 };
        var report = "Known capability start " + new string('c', 5000) + " Signing unavailable";
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(coordinator.Id))!;
            saved.Role = SessionRoles.Coordinator; saved.Activity = "Idle"; saved.Stale = false;
            a.CapabilityReport = report; b.CapabilityReport = report;
            a.CapabilitiesJson = "{\"cpu\":2}"; b.CapabilitiesJson = a.CapabilitiesJson;
            db.Workers.AddRange(a, b); return true;
        });
        var instruction = "Preserve owner instruction " + new string('i', 5000);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, instruction, [a.Id, b.Id]));
        var evidence = "CHANGES_REQUESTED: verify result " + new string('r', 5500) + " SHA: abc123";
        var resultJson = Json.Write(new { messages = new[] { new { parts = new[] { new { type = "text", text = evidence } } } } });
        var oldId = Guid.NewGuid().ToString();
        await app.Store.Write(db =>
        {
            for (var i = 0; i < 18; i++)
                db.Commands.Add(new CommandRecord
                {
                    Id = i == 0 ? oldId : Guid.NewGuid().ToString(),
                    RuntimeId = a.RuntimeId,
                    WorkerId = i == 0 ? a.Id : b.Id,
                    Kind = "Prompt",
                    Origin = "coordinator:" + run.Id,
                    State = i == 0 ? Delivery.Running : Delivery.Finished,
                    Payload = Json.Write(new PromptInput(Guid.NewGuid().ToString(), "Task " + new string('p', 5000), 0)),
                    CreatedAt = ControlStore.Now - 1000 + i,
                    UpdatedAt = ControlStore.Now,
                    ResultJson = resultJson,
                    ProgressText = "Still testing",
                    LastProgressAt = ControlStore.Now
                });
            return Task.FromResult(true);
        });
        Assert.True(await app.Store.CoordinationTick());
        var latest = (await app.Store.Coordinations()).Single();
        Assert.Equal("Deciding", latest.State);
        var context = Json.Read<CoordinatorContext>(latest.InputJson);
        Assert.Equal(instruction, context.Instruction);
        Assert.Equal(17, context.Results.Length);
        var old = Assert.Single(context.Results, x => x.Id == oldId);
        Assert.Equal(Delivery.Running, old.State);
        Assert.Contains(context.Dispatch!, x => x.CommandId == oldId && x.State == Delivery.Running);
        Assert.Equal(42, context.Workers.Single(x => x.Id == a.Id).Revision);
        Assert.Equal(a.CapabilitiesJson, context.Workers.Single(x => x.Id == a.Id).CapabilitiesJson);
        Assert.All(context.Results, x =>
        {
            Assert.True(x.ResponseTruncated);
            Assert.Contains("CHANGES_REQUESTED", x.Response);
            Assert.Contains("SHA: abc123", x.Response);
            Assert.Contains("omitted", x.Prompt);
        });
        var snapshot = await app.Store.Snapshot();
        Assert.Equal(report, snapshot.Workers.Single(x => x.Id == a.Id).CapabilityReport);
        Assert.Equal(resultJson, snapshot.Commands.Single(x => x.Id == oldId).ResultJson);
        var prompt = Json.Read<PromptInput>(snapshot.Commands.Single(x => x.Id == latest.DecisionCommandId).Payload).Text;
        Assert.True(prompt.Length <= 64000);
    }

    [Fact]
    public async Task EscapedHistoricalBodiesYieldOmissionReceiptsWithoutLosingActiveWorkOrNewestVerdict()
    {
        await using var app = new TestApp();
        var coordinator = await PersistenceTests.SeedWorker(app.Store);
        var worker = new WorkerRecord { RuntimeId = coordinator.RuntimeId, NativeSessionId = "ses_escaped", Name = "Worker", Activity = "Active", Stale = false };
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(coordinator.Id))!;
            saved.Role = SessionRoles.Coordinator; saved.Activity = "Idle"; saved.Stale = false;
            db.Workers.Add(worker); return true;
        });
        var instruction = "Do not repeat side effects. " + new string('i', 14000);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, instruction, [worker.Id]));
        var ids = Enumerable.Range(0, 18).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var evidence = Json.Write(new { messages = new[] { new { parts = new[] { new { type = "text", text = "CHANGES_REQUESTED " + new string('<', 5500) + " exact-head" } } } } });
        await app.Store.Write(db =>
        {
            for (var i = 0; i < ids.Length; i++)
                db.Commands.Add(new CommandRecord
                {
                    Id = ids[i],
                    RuntimeId = worker.RuntimeId,
                    WorkerId = worker.Id,
                    Kind = "Prompt",
                    Origin = "coordinator:" + run.Id,
                    State = i == 0 ? Delivery.Running : Delivery.Finished,
                    Payload = Json.Write(new PromptInput(ids[i], new string('"', 5000), 0)),
                    CreatedAt = ControlStore.Now - 1000 + i,
                    ResultJson = evidence,
                    ProgressText = "Awaiting reconciliation"
                });
            return Task.FromResult(true);
        });
        await app.Store.CoordinationTick();
        var latest = (await app.Store.Coordinations()).Single();
        Assert.Equal("Deciding", latest.State);
        var context = Json.Read<CoordinatorContext>(latest.InputJson);
        Assert.Equal(instruction, context.Instruction);
        Assert.Equal(17, context.Results.Length);
        Assert.Equal(Delivery.Running, context.Results.Single(x => x.Id == ids[0]).State);
        Assert.DoesNotContain("Historical evidence omitted", context.Results.Single(x => x.Id == ids[0]).Response);
        Assert.Contains("CHANGES_REQUESTED", context.Results.Single(x => x.Id == ids[^1]).Response);
        Assert.Contains(context.Results, x => x.Response.Contains("Historical evidence omitted") && x.ResponseTruncated && x.EarlierTextOmitted);
        Assert.Equal(17, context.Dispatch!.Length);
        var snapshot = await app.Store.Snapshot();
        Assert.All(snapshot.Commands.Where(x => ids.Contains(x.Id)), x => Assert.Equal(evidence, x.ResultJson));
        Assert.DoesNotContain(snapshot.Commands, x => x.Kind == "Prompt" && x.Origin == "coordinator:" + run.Id && !ids.Contains(x.Id));
        Assert.True(Json.Read<PromptInput>(snapshot.Commands.Single(x => x.Id == latest.DecisionCommandId).Payload).Text.Length <= 64000);
    }

    [Fact]
    public async Task OversizedRoutingInventoryStillPausesWithoutDroppingFactsOrDispatching()
    {
        await using var app = new TestApp();
        var coordinator = await PersistenceTests.SeedWorker(app.Store);
        var worker = new WorkerRecord { RuntimeId = coordinator.RuntimeId, NativeSessionId = "ses_budget_w", Name = "Worker", Activity = "Idle", Stale = false, CapabilitiesJson = Json.Write(new { fact = new string('x', 70000) }) };
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(coordinator.Id))!;
            saved.Role = SessionRoles.Coordinator; saved.Activity = "Idle"; saved.Stale = false;
            db.Workers.Add(worker); return true;
        });
        await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Keep all routing facts.", [worker.Id]));
        await app.Store.CoordinationTick();
        Assert.Equal("Paused", (await app.Store.Coordinations()).Single().State);
        Assert.Empty((await app.Store.Snapshot()).Commands);
    }
}
