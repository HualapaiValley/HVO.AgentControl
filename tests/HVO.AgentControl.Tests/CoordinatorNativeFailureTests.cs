using System.Reflection;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed partial class CoordinationTests
{
    [Theory]
    [InlineData("APIError", 400, "", "InvalidRequest")]
    [InlineData("APIError", 401, "", "AuthenticationRequired")]
    [InlineData("APIError", 429, "", "Throttled")]
    [InlineData("APIError", 503, "", "Unavailable")]
    [InlineData("APIError", 429, "insufficient_quota", "Exhausted")]
    [InlineData("ProviderAuthError", 0, "", "AuthenticationRequired")]
    [InlineData("MessageAbortedError", 0, "", "Cancelled")]
    [InlineData("ContextOverflowError", 0, "", "ContextLimit")]
    [InlineData("MessageOutputLengthError", 0, "", "OutputLimit")]
    [InlineData("UnknownError", 0, "", "NativeError")]
    public async Task NativeDecisionFailureIsHeldBeforeFormatRepairWithDeliveryAndWorkerProgressPreserved(
        string errorName, int status, string code, string category)
    {
        await using var app = new TestApp();
        var (run, coordinator, worker) = await NativeDecisionRun(app.Store);
        var independent = await app.Store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), "Independent work", worker.Revision));
        await app.Store.Write(async db => { (await db.Commands.FindAsync(independent.Id))!.State = Delivery.Running; return true; });
        var command = await ReconcileDecision(app, run, coordinator, NativeError(errorName, status, code));
        Assert.Equal(Delivery.Finished, command.State);
        Assert.Equal("Failed", await app.Store.Read(async db => (await db.Assignments.FindAsync(command.Id))!.Outcome));
        await app.Store.CoordinationTick();

        var held = (await app.Store.Coordinations()).Single();
        var failure = Json.Read<CoordinatorContext>(held.InputJson).NativeFailure!;
        Assert.Equal("Waiting", held.State);
        Assert.True(failure.Held);
        Assert.Equal(category, failure.Category);
        Assert.Equal(command.Id, failure.CommandId);
        Assert.Equal(command.NativeMessageId, failure.CallerId);
        Assert.Equal(coordinator.NativeSessionId, failure.SessionId);
        Assert.Equal("msg_failure_" + command.Id, failure.AssistantId);
        Assert.Null(Json.Read<CoordinatorContext>(held.InputJson).Repair);
        Assert.Null(Json.Read<CoordinatorContext>(held.InputJson).Recovery);
        Assert.DoesNotContain("secret-provider-body", held.InputJson + held.Detail);
        if (category == "Exhausted") Assert.Null(failure.RetryAt);
        for (var tick = 0; tick < 5; tick++) Assert.False(await app.Store.CoordinationTick());
        await app.Store.CoordinationSupervisionTick(ControlStore.Now + 60000);
        Assert.True((await app.Store.Coordinations()).Single().LastSupervisorAt > 0);
        Assert.Equal(Delivery.Running, await app.Store.Read(async db => (await db.Commands.FindAsync(independent.Id))!.State));
        Assert.Equal(Delivery.Finished, await app.Store.Read(async db => (await db.Commands.FindAsync(command.Id))!.State));
        Assert.Equal(command.ResultJson, await app.Store.Read(async db => (await db.Commands.FindAsync(command.Id))!.ResultJson));
        Assert.Equal(1, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "CoordinatorNativeFailureHeld")));
        Assert.Equal(0, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "CoordinatorCorrectionRequested" || x.Type == "CoordinatorDecisionApplied")));
    }

    [Fact]
    public async Task GenuineMalformedNativeAnswerStillGetsFormatCorrection()
    {
        await using var app = new TestApp();
        var (run, coordinator, _) = await NativeDecisionRun(app.Store);
        await ReconcileDecision(app, run, coordinator, null, "APIError is a word in my non-JSON answer");
        await app.Store.CoordinationTick();
        Assert.Equal(1, Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).Repair!.Attempt);
        await app.Store.CoordinationTick();
        Assert.Equal(1, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "CoordinatorCorrectionRequested")));
        Assert.Equal(0, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "CoordinatorNativeFailureHeld")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorWithValidDecisionTextOrToolEvidenceCannotApplyActions(bool tools)
    {
        await using var app = new TestApp();
        var (run, coordinator, worker) = await NativeDecisionRun(app.Store);
        var text = Json.Write(new CoordinatorDecision("Looks successful", [new("send_prompt", worker.Id, "Must never dispatch")]));
        await ReconcileDecision(app, run, coordinator, NativeError("APIError", 400), text, tools);
        await app.Store.CoordinationTick();
        var checkpoint = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).NativeFailure!;
        Assert.True(checkpoint.HasText);
        Assert.Equal(tools, checkpoint.HasTools);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        if (tools)
        {
            await ChangeNativeDecisionRoute(app.Store, coordinator.Id);
            Assert.False(await app.Store.CoordinationTick());
            Assert.Contains("Tool evidence requires review", (await app.Store.Coordinations()).Single().Detail);
        }
    }

    [Fact]
    public async Task HoldSurvivesRestartAndOwnerPauseAndLinksFreshAttemptsOnlyAfterEffectiveRouteChanges()
    {
        string data, secrets, runId, coordinatorId, originalId, result;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (run, coordinator, _) = await NativeDecisionRun(app.Store); runId = run.Id; coordinatorId = coordinator.Id;
            var original = await ReconcileDecision(app, run, coordinator, NativeError("APIError", 400)); originalId = original.Id; result = original.ResultJson;
            await app.Store.CoordinationTick();
            await app.Store.Write(async db => { (await db.CoordinationRuns.FindAsync(runId))!.LastDecisionAt = 1; await db.Events.ExecuteDeleteAsync(); return true; });
        }
        await using var restarted = new TestApp(data, secrets);
        await ObserveIdle(restarted.Store);
        for (var i = 0; i < 5; i++) Assert.False(await restarted.Store.CoordinationTick());
        var held = (await restarted.Store.Coordinations()).Single();
        Assert.True(Json.Read<CoordinatorContext>(held.InputJson).NativeFailure!.Held);
        await restarted.Store.PromptCoordination(runId, new(Guid.NewGuid().ToString(), held.Revision, "Please continue"));
        Assert.False(await restarted.Store.CoordinationTick()); // A text follow-up does not fix the rejected request.
        held = (await restarted.Store.Coordinations()).Single();
        await restarted.Store.ControlCoordination(runId, new(held.Revision, "pause"));
        await ChangeNativeDecisionRoute(restarted.Store, coordinatorId);
        Assert.False(await restarted.Store.CoordinationTick());
        await restarted.Store.CoordinationSupervisionTick(ControlStore.Now + 60000);
        var paused = (await restarted.Store.Coordinations()).Single();
        Assert.Equal("Paused", paused.State);
        await restarted.Store.ControlCoordination(runId, new(paused.Revision, "resume"));
        Assert.True(await restarted.Store.CoordinationTick()); // Release only; no stale command replay.
        Assert.True(await restarted.Store.CoordinationTick());
        var fresh = (await restarted.Store.Coordinations()).Single();
        Assert.NotEqual(originalId, fresh.DecisionCommandId);
        var next = await restarted.Store.Read(async db => (await db.Commands.FindAsync(fresh.DecisionCommandId))!);
        Assert.Equal("good-model", Json.Read<PromptInput>(next.ExecutionPayload).ModelId);
        Assert.Contains(originalId, Json.Read<PromptInput>(next.ExecutionPayload).Text);
        Assert.Equal(result, await restarted.Store.Read(async db => (await db.Commands.FindAsync(originalId))!.ResultJson));
        var coordinatorAfterRestart = (await restarted.Store.Detail(coordinatorId)).Worker;
        await ReconcileDecision(restarted, fresh, coordinatorAfterRestart, NativeError("APIError", 400));
        await restarted.Store.CoordinationTick();
        var again = Json.Read<CoordinatorContext>((await restarted.Store.Coordinations()).Single().InputJson).NativeFailure!;
        Assert.Equal(originalId, again.PreviousCommandId);
        Assert.Equal(next.Id, again.CommandId);
        for (var i = 0; i < 5; i++) Assert.False(await restarted.Store.CoordinationTick());
        Assert.Equal(2, (await restarted.Store.Snapshot()).Commands.Count(x => x.Origin == "coordinator-decision:" + runId));
    }

    [Fact]
    public async Task FailureUsesFrozenEffectiveRequestEvenWhenFutureSettingsAlreadyChanged()
    {
        await using var app = new TestApp();
        var (run, coordinator, _) = await NativeDecisionRun(app.Store);
        await ChangeNativeDecisionRoute(app.Store, coordinator.Id);
        await ReconcileDecision(app, run, coordinator, NativeError("APIError", 400));
        await app.Store.PromptCoordination(run.Id, new(Guid.NewGuid().ToString(), run.Revision, "Fresh owner follow-up"));
        await app.Store.CoordinationTick();
        Assert.Equal("bad-model", Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).NativeFailure!.ModelId);
        Assert.True(await app.Store.CoordinationTick());
        await app.Store.CoordinationTick();
        var commandId = (await app.Store.Coordinations()).Single().DecisionCommandId;
        Assert.Equal("good-model", await app.Store.Read(async db => Json.Read<PromptInput>((await db.Commands.FindAsync(commandId))!.ExecutionPayload).ModelId));
    }

    [Fact]
    public async Task ChangedRouteMustBeDiscoveredAndItsPoolAvailableBeforeRelease()
    {
        await using var app = new TestApp();
        var (run, coordinator, _) = await NativeDecisionRun(app.Store);
        await ReconcileDecision(app, run, coordinator, NativeError("APIError", 400));
        await app.Store.CoordinationTick();
        await app.Store.Write(async db => { (await db.Workers.FindAsync(coordinator.Id))!.ModelId = "not-discovered"; return true; });
        Assert.False(await app.Store.CoordinationTick());
        await ChangeNativeDecisionRoute(app.Store, coordinator.Id);
        await app.Store.Write(db => { db.Add(new ProviderPool { Id = "provider:fixture", ProviderId = "fixture", State = "Exhausted" }); return Task.FromResult(true); });
        Assert.False(await app.Store.CoordinationTick());
        await app.Store.ResumePool("provider:fixture", new(0, true));
        Assert.True(await app.Store.CoordinationTick());
    }

    [Fact]
    public async Task UnrelatedNativeErrorDoesNotContaminateSuccessfulCallerDecision()
    {
        await using var app = new TestApp();
        var (run, coordinator, _) = await NativeDecisionRun(app.Store);
        await ReconcileDecision(app, run, coordinator, null, Json.Write(new CoordinatorDecision("Observed successfully", [])), unrelatedError: true);
        await app.Store.CoordinationTick();
        var observed = (await app.Store.Coordinations()).Single();
        Assert.Null(Json.Read<CoordinatorContext>(observed.InputJson).NativeFailure);
        Assert.Equal("Observed successfully", observed.Detail);
        Assert.Equal(1, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "CoordinatorDecisionApplied")));
    }

    [Fact]
    public async Task CooldownAloneDoesNotReleaseProviderHoldButVerifiedRecoveryDoes()
    {
        await using var app = new TestApp();
        var (run, coordinator, _) = await NativeDecisionRun(app.Store);
        await ReconcileDecision(app, run, coordinator, NativeError("APIError", 429));
        await app.Store.CoordinationTick();
        await app.Store.Write(async db => { (await db.Set<ProviderPool>().FindAsync("provider:fixture"))!.RetryAt = 0; return true; });
        Assert.False(await app.Store.CoordinationTick());
        var pool = await app.Store.Read(async db => (await db.Set<ProviderPool>().FindAsync("provider:fixture"))!);
        await app.Store.ResumePool(pool.Id, new(pool.Revision, true));
        Assert.True(await app.Store.CoordinationTick());
        await app.Store.CoordinationTick();
        Assert.NotEqual(run.DecisionCommandId, (await app.Store.Coordinations()).Single().DecisionCommandId);
    }

    private static async Task<(CoordinationRun, WorkerRecord, WorkerRecord)> NativeDecisionRun(ControlStore store)
    {
        var (coordinator, worker, _) = await Seed(store);
        await store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(coordinator.Id))!;
            saved.ProviderId = "fixture"; saved.ModelId = "bad-model";
            saved.ModelsJson = Json.Write(new[] { new ModelChoice("fixture", "bad-model", "Bad"), new ModelChoice("fixture", "good-model", "Good") });
            return true;
        });
        var run = await store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Coordinate", [worker.Id], ContinuousSupervision: true));
        await store.CoordinationTick();
        return ((await store.Coordinations()).Single(x => x.Id == run.Id), (await store.Detail(coordinator.Id)).Worker, worker);
    }

    private static async Task ChangeNativeDecisionRoute(ControlStore store, string workerId)
    {
        var worker = (await store.Detail(workerId)).Worker;
        await store.UpdateWorker(workerId, new(Guid.NewGuid().ToString(), worker.SettingsRevision, worker.Name,
            worker.Project, worker.Description, "fixture", "good-model", worker.Agent, worker.Variant));
    }

    private static JsonElement NativeError(string name, int status, string code = "") => JsonSerializer.SerializeToElement(new
    {
        name,
        data = new { statusCode = status, code, responseBody = "secret-provider-body", message = "secret-provider-body" }
    });

    private static async Task<CommandRecord> ReconcileDecision(TestApp app, CoordinationRun run, WorkerRecord worker,
        JsonElement? error, string? text = null, bool tools = false, bool unrelatedError = false)
    {
        var command = await app.Store.Write(async db =>
        {
            var value = (await db.Commands.FindAsync(run.DecisionCommandId))!;
            value.NativeMessageId = "msg_caller_" + value.Id; value.State = Delivery.Accepted;
            value.ProviderPoolId = "provider:fixture"; return value;
        });
        var parts = new List<object>();
        if (text is not null) parts.Add(new { type = "text", text });
        if (tools) parts.Add(new { type = "tool", tool = "bash", state = new { status = "completed", input = new { }, output = "Retained tool evidence" } });
        var snapshot = new NativeSnapshot(JsonSerializer.SerializeToElement(new { }),
        [
            JsonSerializer.SerializeToElement(new { info = new { id = command.NativeMessageId, role = "user", sessionID = worker.NativeSessionId, time = new { created = 1L } }, parts = Array.Empty<object>() }),
            JsonSerializer.SerializeToElement(new { info = new { id = "msg_failure_" + command.Id, role = "assistant", parentID = command.NativeMessageId, sessionID = worker.NativeSessionId, time = new { created = 2L, completed = 3L }, finish = error is null ? "stop" : (string?)null, error }, parts })
        ], "idle", JsonSerializer.SerializeToElement(new { }), [], []);
        if (unrelatedError)
            snapshot = snapshot with
            {
                Messages = snapshot.Messages.Append(JsonSerializer.SerializeToElement(new
                {
                    info = new { id = "msg_unrelated", role = "assistant", parentID = "msg_another_caller", sessionID = "ses_another", time = new { created = 4L, completed = 5L }, error = NativeError("APIError", 400) },
                    parts = Array.Empty<object>()
                })).ToArray()
            };
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        await (Task<bool>)typeof(RuntimeSupervisor).GetMethod("Reconcile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(supervisor, [worker.Id, snapshot])!;
        return await app.Store.Read(async db => (await db.Commands.FindAsync(command.Id))!);
    }
}
