using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class TaskRiskFloorTests
{
    [Fact]
    public async Task PromptApiRequiresRiskAndReturnsStructuredFloorRejection()
    {
        await using var app = new TestApp();
        var worker = await Worker(app, "openai", "gpt-5.6-luna");
        using var owner = await app.SignIn();

        var missing = await owner.PostAsJsonAsync($"/api/v1/workers/{worker.Id}/prompts", new
        {
            id = Guid.NewGuid().ToString(),
            text = "Bounded task",
            expectedRevision = worker.Revision
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using (var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()))
            Assert.Equal("invalid_request", body.RootElement.GetProperty("code").GetString());

        var rejected = await owner.PostAsJsonAsync($"/api/v1/workers/{worker.Id}/prompts",
            new PromptInput(Guid.NewGuid().ToString(), "Substantial integration", worker.Revision,
                RiskLevel: TaskRiskLevels.High));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("risk_floor_not_met", error.RootElement.GetProperty("code").GetString());
        Assert.Equal("high", error.RootElement.GetProperty("details").GetProperty("riskLevel").GetString());
        Assert.Equal("low", error.RootElement.GetProperty("details").GetProperty("routeMaximum").GetString());
        Assert.Empty((await app.Store.Detail(worker.Id)).Commands);
    }

    [Fact]
    public async Task ExactStrongRouteIsAcceptedAndRiskDecisionIsFrozenAcrossWorkerEdits()
    {
        await using var app = new TestApp();
        var worker = await Worker(app, "openai", "gpt-5.6-luna");
        var command = await app.Store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), "Integrate the change", worker.Revision,
            "openai", "gpt-5.6-sol", RiskLevel: TaskRiskLevels.High));

        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(worker.Id))!;
            saved.ProviderId = "openai";
            saved.ModelId = "gpt-5.6-luna";
            return true;
        });

        var frozen = Json.Read<PromptInput>(command.ExecutionPayload);
        Assert.Equal(TaskRiskLevels.High, frozen.RiskLevel);
        Assert.Equal("gpt-5.6-sol", frozen.ModelId);
        Assert.Equal("risk-floor-v1", frozen.RiskPolicyVersion);
        Assert.Equal(TaskRiskLevels.High, frozen.RiskRouteMaximum);
    }

    [Fact]
    public async Task SupervisorFailsTamperedBelowFloorCommandBeforeClaimingIt()
    {
        await using var app = new TestApp();
        var worker = await Worker(app, "openai", "gpt-5.6-luna", idle: true);
        var command = await app.Store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), "Routine task", worker.Revision,
            RiskLevel: TaskRiskLevels.Low));
        await app.Store.Write(async db =>
        {
            var saved = (await db.Commands.FindAsync(command.Id))!;
            saved.ExecutionPayload = Json.Write(Json.Read<PromptInput>(saved.ExecutionPayload) with { RiskLevel = TaskRiskLevels.Critical });
            return true;
        });
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var claim = typeof(RuntimeSupervisor).GetMethod("Claim", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var claimed = await (Task<CommandRecord?>)claim.Invoke(supervisor, [worker.RuntimeId, null])!;

        Assert.Null(claimed);
        var saved = await app.Store.Command(command.Id);
        Assert.Equal(Delivery.Failed, saved.State);
        Assert.Equal("risk_floor_not_met", Json.Read<TaskRiskRejection>(saved.ResultJson).Code);
        Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "TaskRiskFloorRejected" && x.CommandId == command.Id).ToListAsync()));
    }

    [Fact]
    public async Task SupervisorFailsPersistedPromptThatOmitsRiskLevel()
    {
        await using var app = new TestApp();
        var worker = await Worker(app, "openai", "gpt-5.6-luna", idle: true);
        var command = await app.Store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), "Routine task", worker.Revision,
            RiskLevel: TaskRiskLevels.Low));
        await app.Store.Write(async db =>
        {
            var saved = (await db.Commands.FindAsync(command.Id))!;
            var payload = JsonNode.Parse(saved.ExecutionPayload)!.AsObject();
            payload.Remove("riskLevel");
            saved.ExecutionPayload = payload.ToJsonString();
            return true;
        });
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var claim = typeof(RuntimeSupervisor).GetMethod("Claim", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var claimed = await (Task<CommandRecord?>)claim.Invoke(supervisor, [worker.RuntimeId, null])!;

        Assert.Null(claimed);
        var saved = await app.Store.Command(command.Id);
        Assert.Equal(Delivery.Failed, saved.State);
        Assert.Equal("risk_level_required", Json.Read<TaskRiskRejection>(saved.ResultJson).Code);
    }

    [Fact]
    public void ConfiguredRouteMaximumControlsAdmission()
    {
        var policy = new TaskRiskFloorOptions();
        var prompt = new PromptInput("id", "Critical task", 0, "openai", "gpt-5.6-luna", RiskLevel: TaskRiskLevels.Critical);
        Assert.Equal("risk_floor_not_met", TaskRiskPolicy.Evaluate(policy, prompt)!.Code);

        policy.RouteMaximums["openai/gpt-5.6-luna"] = TaskRiskLevels.Critical;
        Assert.Null(TaskRiskPolicy.Evaluate(policy, prompt));
        Assert.Null(TaskRiskPolicy.Evaluate(policy,
            new("id", "Critical task", 0, "openai", "gpt-6-astra", RiskLevel: TaskRiskLevels.Critical)));
    }

    [Fact]
    public void StoredAdmissionRequiresCurrentFrozenPolicyProvenance()
    {
        var policy = new TaskRiskFloorOptions();
        var prompt = new PromptInput("id", "Task", 0, "openai", "gpt-5.6-luna", RiskLevel: TaskRiskLevels.Low);

        Assert.False(TaskRiskPolicy.TryReadAndEvaluate(policy, Json.Write(prompt), out _, out var missing));
        Assert.Equal("risk_admission_provenance_invalid", missing!.Code);

        var admitted = TaskRiskPolicy.Admit(policy, prompt);
        policy.Version = "risk-floor-v2";
        Assert.False(TaskRiskPolicy.TryReadAndEvaluate(policy, Json.Write(admitted), out _, out var changed));
        Assert.Equal("risk_admission_provenance_invalid", changed!.Code);
    }

    private static async Task<WorkerRecord> Worker(TestApp app, string provider, string model, bool idle = false)
    {
        var worker = await PersistenceTests.SeedWorker(app.Store);
        return await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(worker.Id))!;
            saved.ProviderId = provider;
            saved.ModelId = model;
            if (idle)
            {
                saved.Stale = false;
                saved.Activity = "Idle";
            }
            return saved;
        });
    }
}

public sealed partial class CoordinationTests
{
    [Fact]
    public async Task BelowFloorCoordinatorActionRejectsWholeBatchWithStructuredReceipt()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Assign integration", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Unsafe batch", [
            new("send_prompt", a.Id, "Routine task", RiskLevel: TaskRiskLevels.Low),
            new("send_prompt", b.Id, "Critical task", RiskLevel: TaskRiskLevels.Critical)]));

        await app.Store.CoordinationTick();

        var rejected = (await app.Store.Coordinations()).Single();
        Assert.Equal("Recovering", rejected.State);
        Assert.Contains("below the configured critical task risk floor", rejected.Detail);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        var receipt = await app.Store.Read(db => db.Events.SingleAsync(x => x.Type == "CoordinatorDecisionRejected"));
        Assert.Contains("risk_floor_not_met", receipt.Payload);
    }

    [Fact]
    public void CoordinatorJsonRequiresExplicitTaskRisk()
    {
        var result = Json.Write(new
        {
            messages = new[] { new { parts = new[] { new { type = "text", text = "{\"summary\":\"Assign\",\"complete\":false,\"actions\":[{\"type\":\"send_prompt\",\"workerId\":\"worker\",\"text\":\"Task\"}]}" } } } }
        });

        var error = Assert.Throws<ControlException>(() => ControlStore.ParseDecision(result));
        Assert.Equal("risk_level_required", error.Code);
    }
}
