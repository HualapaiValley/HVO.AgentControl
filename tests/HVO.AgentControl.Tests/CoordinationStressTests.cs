using HVO.AgentControl.Core;
using HVO.AgentControl.GitHub;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CoordinationStressTests
{
    [Fact]
    public async Task BatchedFanOutDispatchesEveryTargetExactlyOnce()
    {
        await using var app = new TestApp();
        var (coordinator, workers) = await Seed(app.Store, 8);
        var ids = workers.Select(x => x.Id).ToArray();
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id,
            "Fan out exactly one prompt to every worker.", ids));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Fan out",
            workers.Select(x => new CoordinatorAction("send_prompt", x.Id, "Report memory usage.")).ToArray()));
        await app.Store.CoordinationTick();
        var dispatched = (await app.Store.Snapshot()).Commands.Where(x => x.Origin == "coordinator:" + run.Id).ToArray();
        Assert.Equal(workers.Length, dispatched.Length);
        Assert.Equal(workers.Length, dispatched.Select(x => x.WorkerId).Distinct().Count());
        Assert.Equal(ids.OrderBy(x => x), dispatched.Select(x => x.WorkerId).OrderBy(x => x));
        Assert.All(dispatched, x => Assert.Equal(Delivery.Queued, x.State));
        var latest = (await app.Store.Coordinations()).Single();
        Assert.Equal("Waiting", latest.State);
        Assert.NotNull(latest.DecisionJson);
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id && x.WorkerId == workers[0].Id);
    }

    [Fact]
    public async Task DuplicateRequestIdsReturnTheSameRecordAndConflictsAreRejected()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var promptId = Guid.NewGuid().ToString();
        var first = new PromptInput(promptId, "duplicate-safe instruction", 0, RiskLevel: TaskRiskLevels.Low);
        var results = await Task.WhenAll(app.Store.Prompt(worker.Id, first), app.Store.Prompt(worker.Id, first));
        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Single((await app.Store.Snapshot()).Commands);
        Assert.Equal(results[0].Id, (await app.Store.Prompt(worker.Id, first)).Id);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.Prompt(worker.Id, first with { Text = "conflicting instruction" }));
        var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile());
        var createId = Guid.NewGuid().ToString();
        var create = new CreateWorkerInput(createId, runtime.Id, "Duplicate", "Project", "/home/agent/workspaces/duplicate-x",
            "provider", "model");
        Assert.Equal((await app.Store.CreateWorker(create)).Id, (await app.Store.CreateWorker(create)).Id);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.CreateWorker(create with { Name = "Different" }));
    }

    [Fact]
    public async Task PromptReplayNormalizesOnlyTheNewAbsentOptionalMergeScope()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var id = Guid.NewGuid().ToString();
        var input = new PromptInput(id, "legacy durable instruction", 0, RiskLevel: TaskRiskLevels.Low);
        var legacyPayload = Json.Write(new
        {
            input.Id,
            input.Text,
            input.ExpectedRevision,
            input.ProviderId,
            input.ModelId,
            input.StatusInquiry,
            input.Agent,
            input.Variant,
            input.IncludeGuidance,
            input.ProgressMinutes,
            input.RiskLevel
        });
        await app.Store.Write(db =>
        {
            db.Commands.Add(new CommandRecord
            {
                Id = id,
                RuntimeId = worker.RuntimeId,
                WorkerId = worker.Id,
                Kind = "Prompt",
                Payload = legacyPayload
            });
            return Task.FromResult(true);
        });

        Assert.Equal(id, (await app.Store.Prompt(worker.Id, input)).Id);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.Prompt(worker.Id,
            input with
            {
                GitHubMergeScope = GitHubMergeTaskAuthority.Scope(GitHubMergeTaskKinds.Reviewer,
                "Owner/Repo", 12, new string('a', 40))
            }));
    }

    [Fact]
    public async Task CoordinatorDispatchRetainsTypedGitHubMergeScopeWithoutElevatingOrdinaryPrompts()
    {
        await using var app = new TestApp();
        var (coordinator, workers) = await Seed(app.Store, 1);
        var scope = GitHubMergeTaskAuthority.Scope(GitHubMergeTaskKinds.Reviewer, "Owner/Repo", 12, new string('a', 40));
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id,
            "Assign the exact review task.", [workers[0].Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Review",
            [new CoordinatorAction("send_prompt", workers[0].Id, "Review the exact head.", GitHubMergeScope: scope)]));

        await app.Store.CoordinationTick();

        var commandSummary = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + run.Id);
        var command = await app.Store.Command(commandSummary.Id);
        Assert.Equal(scope, Json.Read<PromptInput>(command.Payload).GitHubMergeScope);
        Assert.Contains("AgentControl retained GitHub merge task scope", command.ExecutionPayload);
    }

    [Fact]
    public async Task BusyWorkerWithQueuedWorkExcludesTargetWithoutPartialDispatch()
    {
        await using var app = new TestApp();
        var (coordinator, workers) = await Seed(app.Store, 2);
        var a = workers[0];
        var busy = await app.Store.Prompt(a.Id,
            new(Guid.NewGuid().ToString(), "already busy", 0, RiskLevel: TaskRiskLevels.Low));
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id,
            "Ask both workers for memory usage.", workers.Select(x => x.Id).ToArray()));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Ask", new[]
        {
            new CoordinatorAction("send_prompt", workers[0].Id, "Memory usage?"),
            new CoordinatorAction("send_prompt", workers[1].Id, "Memory usage?")
        }));
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        await app.Store.CoordinationTick();
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Id == busy.Id);
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Unknown")]
    public async Task NonIdleOrStaleWorkerExcludesTargetWithoutDispatch(string activity)
    {
        await using var app = new TestApp();
        var (coordinator, workers) = await Seed(app.Store, 2);
        await app.Store.Write(async db =>
        {
            var target = (await db.Workers.FindAsync(workers[0].Id))!;
            target.Activity = activity;
            target.Stale = activity != "Active";
            return true;
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id,
            "Ask both workers.", workers.Select(x => x.Id).ToArray()));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Ask", new[]
        {
            new CoordinatorAction("send_prompt", workers[0].Id, "Memory usage?"),
            new CoordinatorAction("send_prompt", workers[1].Id, "Memory usage?")
        }));
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    [Fact]
    public async Task DurableStateAndRequestIdempotencySurviveRestartAndRecovery()
    {
        string data, secrets, runId, workerId, directPromptId, shippedId;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var worker = await PersistenceTests.SeedWorker(app.Store);
            workerId = worker.Id;
            directPromptId = Guid.NewGuid().ToString();
            await app.Store.Prompt(workerId, new PromptInput(directPromptId, "at-most-once durable instruction", 0,
                RiskLevel: TaskRiskLevels.Low));
            var (coordinator, participants) = await Seed(app.Store, 1);
            var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id,
                "Persist across restart.", [participants[0].Id]));
            runId = run.Id;
            await app.Store.CoordinationTick();
            await FinishDecision(app.Store, runId, new("Ask", [new("send_prompt", participants[0].Id, "What time is it?")]));
            await app.Store.CoordinationTick();
            shippedId = (await app.Store.Snapshot()).Commands.Single(x => x.Origin == "coordinator:" + runId).Id;
            await app.Store.Write(async db =>
            {
                (await db.Commands.FindAsync(shippedId))!.State = Delivery.Dispatching;
                return true;
            });
        }
        await using (var restarted = new TestApp(data, secrets))
        {
            var persisted = (await restarted.Store.Coordinations()).Single(x => x.Id == runId);
            Assert.Equal("Waiting", persisted.State);
            Assert.Single((await restarted.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + runId);
            await restarted.Store.Recover();
            var snapshot = await restarted.Store.Snapshot();
            Assert.Equal(Delivery.Unknown, snapshot.Commands.Single(x => x.Id == shippedId).State);
            Assert.True(snapshot.Workers.Single(x => x.Id == workerId).Stale);
            Assert.Equal(directPromptId, (await restarted.Store.Prompt(workerId,
                new PromptInput(directPromptId, "at-most-once durable instruction", 0, RiskLevel: TaskRiskLevels.Low))).Id);
            await Assert.ThrowsAsync<ControlException>(() => restarted.Store.Prompt(workerId,
                new PromptInput(directPromptId, "different content", 0, RiskLevel: TaskRiskLevels.Low)));
        }
    }

    [Fact]
    public async Task StaleObservationRejectsWholeBatchWithoutPartialFanOut()
    {
        await using var app = new TestApp();
        var (coordinator, workers) = await Seed(app.Store, 2);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id,
            "Ask both workers for memory usage.", workers.Select(x => x.Id).ToArray()));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Ask", new[]
        {
            new CoordinatorAction("send_prompt", workers[0].Id, "Memory usage?"),
            new CoordinatorAction("send_prompt", workers[1].Id, "Memory usage?")
        }));
        await app.Store.Write(async db => { (await db.Workers.FindAsync(workers[1].Id))!.Revision++; return true; });
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        await app.Store.CoordinationTick();
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
    }

    private static async Task<(WorkerRecord, WorkerRecord[])> Seed(ControlStore store, int workerCount)
    {
        var coordinator = await PersistenceTests.SeedWorker(store);
        await store.Write(async db =>
        {
            (await db.Workers.FindAsync(coordinator.Id))!.Role = SessionRoles.Coordinator;
            for (var i = 0; i < workerCount; i++)
                db.Workers.Add(new WorkerRecord
                {
                    Name = "Stress" + i,
                    RuntimeId = coordinator.RuntimeId,
                    NativeSessionId = "ses_stress_" + i,
                    Directory = "/home/agent/workspaces/stress/" + i,
                    Activity = "Idle"
                });
            return true;
        });
        await ObserveIdle(store);
        var workers = (await store.Read(db => db.Workers.Where(x => x.Id != coordinator.Id).ToArrayAsync()))
            .OrderBy(x => x.Name).ToArray();
        return (coordinator, workers);
    }

    private static async Task ObserveIdle(ControlStore store) => await store.Write(async db =>
    {
        foreach (var worker in await db.Workers.ToListAsync())
        {
            worker.Activity = "Idle";
            worker.Stale = false;
            worker.LastObservedAt = ControlStore.Now;
        }
        return true;
    });

    private static async Task FinishDecision(ControlStore store, string id, CoordinatorDecision decision)
    {
        var run = (await store.Coordinations()).Single(x => x.Id == id);
        await store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(run.DecisionCommandId!))!;
            command.State = Delivery.Finished;
            command.ResultJson = Json.Write(new { messages = new[] { new { parts = new[] { new { type = "text", text = Json.Write(decision) } } } } });
            return true;
        });
    }
}
