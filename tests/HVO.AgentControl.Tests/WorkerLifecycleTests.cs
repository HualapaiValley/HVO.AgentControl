using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WorkerLifecycleTests
{
    [Fact]
    public async Task EditWhileBusyPreservesSessionAndFrozenQueueAndRetries()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(async db =>
        {
            var w = (await db.Workers.FindAsync(worker.Id))!;
            w.ProviderId = "fixture"; w.ModelId = "old"; w.Activity = "Active";
            w.ModelsJson = Json.Write(new[] { new ModelChoice("fixture", "new", "New", ["high"], ["plan"]) });
            return true;
        });
        var queued = await app.Store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), "what time is it?", 0,
            RiskLevel: TaskRiskLevels.Low));
        var input = new UpdateWorkerInput(Guid.NewGuid().ToString(), 0, "Renamed", "Project", "Reviewer", "fixture", "new", "plan", "high");
        var updated = await app.Store.UpdateWorker(worker.Id, input);
        Assert.Equal(worker.NativeSessionId, updated.NativeSessionId);
        Assert.Equal("Active", updated.Activity);
        Assert.Equal(1, (await app.Store.UpdateWorker(worker.Id, input)).SettingsRevision);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.UpdateWorker(worker.Id, input with { Id = Guid.NewGuid().ToString(), Name = "Stale" }));
        var detail = await app.Store.Detail(worker.Id);
        Assert.Equal("old", (await app.Store.CommandPrompt(queued.Id)).ModelId);
        var next = await app.Store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), "review this", detail.Worker.Revision,
            RiskLevel: TaskRiskLevels.Low));
        var captured = Json.Read<PromptInput>(next.ExecutionPayload);
        Assert.Equal("new", captured.ModelId); Assert.Equal("plan", captured.Agent); Assert.Equal("high", captured.Variant);
        Assert.DoesNotContain(detail.Commands, x => x.Kind is "Abort" or "StopManagedServer");
    }

    [Fact]
    public async Task ArchiveRequiresResolvedIdleAndRestoreRetainsIdentityAndClaim()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var input = new WorkerArchiveInput(Guid.NewGuid().ToString(), 0, true);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ArchiveWorker(worker.Id, input));
        await app.Store.Write(async db =>
        {
            var w = (await db.Workers.FindAsync(worker.Id))!;
            w.Activity = "Idle"; w.Stale = false; w.LastObservedAt = ControlStore.Now;
            db.WorkspaceClaims.Add(new WorkspaceClaim { Id = "claim", WorkerId = w.Id, RuntimeId = w.RuntimeId, Directory = w.Directory });
            return true;
        });
        var queued = await app.Store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), "task", 0, RiskLevel: TaskRiskLevels.Low));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ArchiveWorker(worker.Id, input));
        await app.Store.EditQueue(queued.Id, "cancel");
        var archived = await app.Store.ArchiveWorker(worker.Id, input);
        Assert.True(archived.Archived);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.Prompt(worker.Id,
            new(Guid.NewGuid().ToString(), "should fail", archived.Revision, RiskLevel: TaskRiskLevels.Low)));
        var restored = await app.Store.ArchiveWorker(worker.Id, new(Guid.NewGuid().ToString(), archived.SettingsRevision, false));
        Assert.False(restored.Archived); Assert.Equal(worker.NativeSessionId, restored.NativeSessionId);
        Assert.NotNull(await app.Store.Read(db => db.WorkspaceClaims.FindAsync("claim").AsTask()));
    }

    [Fact]
    public void NativeQuestionChoicesEnforceMultiplicityAndCustomAnswers()
    {
        var json = Json.Write(new { questions = new[] { new { question = "Choose", options = new[] { new { label = "A", description = "One" }, new { label = "B", description = "Two" } }, multiple = false, custom = false } } });
        InteractiveRequests.ValidateAnswers(json, [["A"]]);
        Assert.Throws<ControlException>(() => InteractiveRequests.ValidateAnswers(json, [["A", "B"]]));
        Assert.Throws<ControlException>(() => InteractiveRequests.ValidateAnswers(json, [["other"]]));
        Assert.Throws<ControlException>(() => InteractiveRequests.ValidateAnswers(json, []));
        var multiple = json.Replace("\"multiple\":false", "\"multiple\":true");
        InteractiveRequests.ValidateAnswers(multiple, [["A", "B"]]);
        InteractiveRequests.ValidateAnswers(json.Replace("\"custom\":false", "\"custom\":true"), [["custom response"]]);
    }
}
