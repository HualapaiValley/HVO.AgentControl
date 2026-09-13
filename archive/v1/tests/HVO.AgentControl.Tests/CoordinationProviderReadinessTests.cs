using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed partial class CoordinationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("luna")]
    [InlineData("sol")]
    public async Task HeldProviderRejectsEverySamePoolModelAndFreshDifferentProviderSucceeds(string? model)
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await SeedProviderRoutes(app.Store);
        await ThreeNativeRateLimits(app.Store, a);
        var originals = await app.Store.Read(db => db.Commands.Where(x => x.ProviderPoolId == "provider:openai").ToArrayAsync());
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Assign approved work", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        var observation = (await app.Store.Coordinations()).Single();
        var context = Json.Read<CoordinatorContext>(observation.InputJson);
        var pool = Assert.Single(context.ProviderEvidence!.Pools);
        Assert.Equal("RecoveryRequired", pool.State);
        Assert.Equal("Throttled", pool.LastFailure!.Category);
        Assert.Equal(429, pool.LastFailure.Status);
        Assert.Equal(a.Id, pool.LastFailure.WorkerId);
        Assert.True(pool.EarlierFailuresOmitted);
        Assert.Contains(context.ProviderEvidence.Catalogs.Single(x => x.WorkerId == a.Id).Models,
            x => x.ProviderId == "opencode" && x.ModelId == "nemotron-3-ultra-free");
        Assert.DoesNotContain("secret-native-body", observation.InputJson);

        await FinishDecision(app.Store, run.Id, new("Both tasks", [new("send_prompt", b.Id, "Valid independent task"),
            new("send_prompt", a.Id, "Authorized continuation", ProviderId: model is null ? null : "openai", ModelId: model)]));
        await app.Store.CoordinationTick();
        var rejected = (await app.Store.Coordinations()).Single();
        Assert.Equal("Recovering", rejected.State);
        var reason = Json.Read<CoordinatorContext>(rejected.InputJson).Recovery!.Reason;
        Assert.Contains("actions[1].providerId", reason);
        Assert.Contains("provider:openai", reason);
        Assert.Contains("Changing only modelId", reason);
        Assert.Contains("both providerId and modelId", reason);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Equal("RecoveryRequired", (await app.Store.ProviderPools()).Single().State);

        await RecoveryDue(app.Store, run.Id);
        await app.Store.CoordinationTick();
        var fresh = (await app.Store.Coordinations()).Single();
        Assert.NotEqual(observation.DecisionCommandId, fresh.DecisionCommandId);
        Assert.Equal(reason, Json.Read<CoordinatorContext>(fresh.InputJson).Recovery!.Reason);
        await FinishDecision(app.Store, run.Id, new("Use the approved observed alternative", [
            new("send_prompt", a.Id, "Authorized new instruction", ProviderId: "opencode", ModelId: "nemotron-3-ultra-free")]));
        await app.Store.CoordinationTick();
        var accepted = Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Equal("opencode", Json.Read<PromptInput>(accepted.Payload).ProviderId);
        Assert.Equal(Delivery.Queued, accepted.State);
        Assert.Equal("openai", (await app.Store.Detail(a.Id)).Worker.ProviderId);
        foreach (var original in originals)
        {
            var retained = await app.Store.Read(db => db.Commands.SingleAsync(x => x.Id == original.Id));
            Assert.Equal(original.State, retained.State);
            Assert.Equal(original.ResultJson, retained.ResultJson);
        }
    }

    [Fact]
    public async Task ProviderHoldAfterObservationRejectsEntireOtherwiseValidBatch()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await SeedProviderRoutes(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Assign both", [a.Id, b.Id]));
        await app.Store.CoordinationTick();
        Assert.Empty(Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).ProviderEvidence!.Pools);
        await FinishDecision(app.Store, run.Id, new("Assign both", [new("send_prompt", b.Id, "First valid task"), new("send_prompt", a.Id, "Second task")]));
        await ThreeNativeRateLimits(app.Store, a);
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Empty(await app.Store.Read(db => db.Assignments.Where(x => x.WorkerId == b.Id).ToArrayAsync()));
    }

    [Theory]
    [InlineData("instance", "Unknown")]
    [InlineData("opencode-go", "RefreshRequired")]
    [InlineData("opencode-go", null)]
    public async Task RuntimeProviderReadinessIsCheckedFreshWithoutLeasingOrClearingPools(string provider, string? state)
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await SeedProviderRoutes(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Use configured route", [a.Id]));
        await app.Store.CoordinationTick();
        if (state is not null) await app.Store.Write(db =>
        {
            db.Set<ProviderReadinessReceipt>().Add(new()
            {
                Id = provider + ":" + a.RuntimeId,
                RuntimeId = a.RuntimeId,
                ProviderId = provider,
                State = state,
                Detail = "secret-readiness-detail"
            });
            return Task.FromResult(true);
        });
        await FinishDecision(app.Store, run.Id, new("Route", [new("send_prompt", a.Id, "Task",
            ProviderId: provider == "instance" ? "opencode" : provider, ModelId: provider == "instance" ? "nemotron-3-ultra-free" : "luna")]));
        await app.Store.CoordinationTick();
        var rejected = (await app.Store.Coordinations()).Single();
        Assert.Equal("Recovering", rejected.State);
        Assert.DoesNotContain("secret-readiness-detail", rejected.InputJson);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Empty(await app.Store.ProviderPools());
    }

    [Fact]
    public async Task PoolResumeChangesPlanningEvidenceAndRequestsFreshReassessment()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await SeedProviderRoutes(app.Store);
        await ThreeNativeRateLimits(app.Store, a);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Wait for authorized work", [a.Id], ContinuousSupervision: true));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("No authorized work now", []));
        await app.Store.CoordinationTick();
        await app.Store.CoordinationTick(); // one existing bounded idle planning review
        await FinishDecision(app.Store, run.Id, new("Still no authorized work", []));
        await app.Store.CoordinationTick();
        Assert.False(await app.Store.CoordinationTick());
        var before = (await app.Store.Coordinations()).Single();
        var pool = Assert.Single(await app.Store.ProviderPools());
        await app.Store.ResumePool(pool.Id, new(pool.Revision, true));
        Assert.True(await app.Store.CoordinationTick());
        var after = (await app.Store.Coordinations()).Single();
        var context = Json.Read<CoordinatorContext>(after.InputJson);
        Assert.Equal("Deciding", after.State);
        Assert.NotEqual(Json.Read<CoordinatorContext>(before.InputJson).PlanningObservationKey, context.PlanningObservationKey);
        Assert.Contains("Provider readiness", context.ReassessmentReason);
        Assert.Equal("Available", Assert.Single(context.ProviderEvidence!.Pools).State);
        Assert.NotNull(Assert.Single(context.ProviderEvidence.Pools).LastFailure);
    }

    [Fact]
    public async Task LegacyContextAndOwnerQueueKeepTheirExistingSemantics()
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await SeedProviderRoutes(app.Store);
        await ThreeNativeRateLimits(app.Store, a);
        var owner = await app.Store.Prompt(a.Id, new(Guid.NewGuid().ToString(), "Owner intentionally queued", a.Revision));
        Assert.Equal(Delivery.Queued, owner.State);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Independent task", [b.Id]));
        await app.Store.CoordinationTick();
        await app.Store.Write(async db =>
        {
            var saved = (await db.CoordinationRuns.FindAsync(run.Id))!;
            var legacy = JsonNode.Parse(saved.InputJson)!.AsObject();
            Assert.True(legacy.Remove("providerEvidence"));
            Assert.True(legacy.Remove("providerObservationKey"));
            saved.InputJson = legacy.ToJsonString();
            Assert.Null(Json.Read<CoordinatorContext>(saved.InputJson).ProviderEvidence);
            return true;
        });
        await FinishDecision(app.Store, run.Id, new("Use legacy context safely", [new("send_prompt", b.Id, "An independent task")]));
        await app.Store.CoordinationTick();
        Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Equal("RecoveryRequired", Assert.Single(await app.Store.ProviderPools()).State);
    }

    [Theory]
    [InlineData("Throttled")]
    [InlineData("Unavailable")]
    public async Task ElapsedTransientCooldownAdmitsNewCoordinatorWorkWithOnlyOneDispatchRecoveryLease(string state)
    {
        await using var app = new TestApp();
        var (coordinator, a, b) = await SeedProviderRoutes(app.Store);
        await app.Store.Write(db =>
        {
            db.Set<ProviderPool>().Add(new()
            {
                Id = "provider:openai",
                ProviderId = "openai",
                State = state,
                RetryAt = ControlStore.Now - 1,
                ConsecutiveFailures = 1
            });
            return Task.FromResult(true);
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "One authorized recovery task", [a.Id]));
        await app.Store.CoordinationTick();
        Assert.Equal("RecoveryEligible", Assert.Single(Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).ProviderEvidence!.Pools).AdmissionState);
        await FinishDecision(app.Store, run.Id, new("Try one eligible task", [new("send_prompt", a.Id, "An authorized new task")]));
        await app.Store.CoordinationTick();
        var command = Assert.Single((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Equal(state, Assert.Single(await app.Store.ProviderPools()).State); // validation did not acquire a lease
        await app.Store.Write(async db =>
        {
            Assert.True(await ControlStore.ProviderDispatchAllowed(db, a, command));
            var competitor = new CommandRecord
            {
                Id = Guid.NewGuid().ToString(),
                WorkerId = b.Id,
                Payload = Json.Write(new PromptInput(Guid.NewGuid().ToString(), "Other task", b.Revision, "openai", "sol"))
            };
            Assert.False(await ControlStore.ProviderDispatchAllowed(db, b, competitor));
            Assert.Equal(command.Id, (await db.Set<ProviderPool>().FindAsync("provider:openai"))!.RecoveryCommandId);
            return true;
        });
    }

    [Fact]
    public async Task CooldownExpiryChangesEvidenceWithoutChangingPoolRevision()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await SeedProviderRoutes(app.Store);
        var retryAt = ControlStore.Now + 6000;
        await app.Store.Write(db =>
        {
            db.Set<ProviderPool>().Add(new() { Id = "provider:openai", ProviderId = "openai", State = "Throttled", RetryAt = retryAt, ConsecutiveFailures = 1 });
            return Task.FromResult(true);
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Wait for work", [a.Id], ContinuousSupervision: true));
        await app.Store.CoordinationTick();
        var old = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.Equal("Held", Assert.Single(old.ProviderEvidence!.Pools).AdmissionState);
        await FinishDecision(app.Store, run.Id, new("No authorized task now", []));
        await app.Store.CoordinationTick();
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, retryAt - ControlStore.Now + 50)));
        Assert.True(await app.Store.CoordinationTick());
        var current = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.NotEqual(old.ProviderObservationKey, current.ProviderObservationKey);
        Assert.Equal(Assert.Single(old.ProviderEvidence.Pools).Revision, Assert.Single(current.ProviderEvidence!.Pools).Revision);
        Assert.Equal("RecoveryEligible", Assert.Single(current.ProviderEvidence.Pools).AdmissionState);
        Assert.Contains("Provider readiness", current.ReassessmentReason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task VerifiedAvailablePoolCannotBypassUnknownOrReservedRecoveryOwnership(bool unknown, bool reserved)
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await SeedProviderRoutes(app.Store);
        await app.Store.Write(db =>
        {
            db.Set<ProviderPool>().Add(new()
            {
                Id = "provider:openai",
                ProviderId = "openai",
                State = "Available",
                RecoveryOwnershipUnknown = unknown,
                RecoveryCommandId = reserved ? "existing-recovery" : ""
            });
            return Task.FromResult(true);
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Task", [a.Id]));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Try", [new("send_prompt", a.Id, "Task")]));
        await app.Store.CoordinationTick();
        Assert.Equal("Recovering", (await app.Store.Coordinations()).Single().State);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Equal(unknown, Assert.Single(await app.Store.ProviderPools()).RecoveryOwnershipUnknown);
        Assert.Equal(reserved ? "existing-recovery" : "", Assert.Single(await app.Store.ProviderPools()).RecoveryCommandId);
    }

    [Fact]
    public async Task BoundedCatalogRetainsAvailableProviderBesideLargeHeldCatalog()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await SeedProviderRoutes(app.Store);
        await ThreeNativeRateLimits(app.Store, a);
        await app.Store.Write(async db =>
        {
            var worker = (await db.Workers.FindAsync(a.Id))!;
            worker.ModelsJson = Json.Write(Enumerable.Range(0, 100).Select(i => new ModelChoice("openai", "model-" + i, "Observed"))
                .Concat([new("openai", "luna", "Default"), new("opencode", "nemotron-3-ultra-free", "Approved alternative")]));
            return true;
        });
        await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Use approved models only", [a.Id]));
        await app.Store.CoordinationTick();
        var catalog = Assert.Single(Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).ProviderEvidence!.Catalogs);
        Assert.Equal(32, catalog.Models.Length);
        Assert.Equal(70, catalog.Omitted);
        Assert.Contains(catalog.Models, x => x.ProviderId == "opencode" && x.ModelId == "nemotron-3-ultra-free");
        Assert.Equal("luna", catalog.Models[0].ModelId);
    }

    [Fact]
    public async Task TruncatedPoolEvidenceRetainsDefaultAndObservesOmittedPoolChanges()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await SeedProviderRoutes(app.Store);
        await app.Store.Write(async db =>
        {
            var providers = Enumerable.Range(0, 34).Select(i => "a-held-" + i.ToString("D2")).Append("openai").ToArray();
            (await db.Workers.FindAsync(a.Id))!.ModelsJson = Json.Write(providers.Select(p => new ModelChoice(p, p == "openai" ? "luna" : "model", "Observed")));
            foreach (var provider in providers)
                db.Set<ProviderPool>().Add(new() { Id = "provider:" + provider, ProviderId = provider, State = "Exhausted", ConsecutiveFailures = 1 });
            return true;
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Wait for an approved available route", [a.Id], ContinuousSupervision: true));
        await app.Store.CoordinationTick();
        var first = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.True(first.ProviderEvidence!.PoolsTruncated);
        Assert.Contains(first.ProviderEvidence.Pools, x => x.Id == "provider:openai");
        Assert.DoesNotContain(first.ProviderEvidence.Pools, x => x.Id == "provider:a-held-33");
        await FinishDecision(app.Store, run.Id, new("No work available", []));
        await app.Store.CoordinationTick();
        await app.Store.CoordinationTick(); // one normal idle planning review
        await FinishDecision(app.Store, run.Id, new("Still blocked", []));
        await app.Store.CoordinationTick();
        Assert.False(await app.Store.CoordinationTick());
        var before = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        await app.Store.Write(async db =>
        {
            var command = new CommandRecord
            {
                Id = Guid.NewGuid().ToString(),
                WorkerId = a.Id,
                RuntimeId = a.RuntimeId,
                Kind = "Prompt",
                State = Delivery.Failed,
                ProviderPoolId = "provider:a-held-33"
            };
            db.Commands.Add(command);
            await ControlStore.ObserveProviderFailure(db, a, command, "native-auth-error", new("AuthenticationRequired", 401, null));
            return true;
        });
        Assert.True(await app.Store.CoordinationTick());
        var changed = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.Equal(Json.Write(before.ProviderEvidence), Json.Write(changed.ProviderEvidence));
        Assert.NotEqual(before.ProviderObservationKey, changed.ProviderObservationKey);
        Assert.Contains("Provider readiness", changed.ReassessmentReason);
        await FinishDecision(app.Store, run.Id, new("Still no authorized route", []));
        await app.Store.CoordinationTick();
        var pool = (await app.Store.ProviderPools()).Single(x => x.Id == "provider:openai");
        await app.Store.ResumePool(pool.Id, new(pool.Revision, true));
        Assert.True(await app.Store.CoordinationTick());
        var resumed = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.Equal("Available", resumed.ProviderEvidence!.Pools.Single(x => x.Id == "provider:openai").AdmissionState);
        Assert.NotEqual(changed.ProviderObservationKey, resumed.ProviderObservationKey);
    }

    [Fact]
    public async Task OmittedCatalogChoiceChangesFullProviderObservationWithoutChangingVisibleModels()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await SeedProviderRoutes(app.Store);
        await app.Store.Write(async db =>
        {
            (await db.Workers.FindAsync(a.Id))!.ModelsJson = Json.Write(Enumerable.Range(0, 40)
                .Select(i => new ModelChoice("openai", "model-" + i.ToString("D2"), "Observed")));
            return true;
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Wait for approved work", [a.Id], ContinuousSupervision: true));
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("No work", []));
        await app.Store.CoordinationTick();
        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, new("Still no work", []));
        await app.Store.CoordinationTick();
        Assert.False(await app.Store.CoordinationTick());
        var before = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        await app.Store.Write(async db =>
        {
            var worker = (await db.Workers.FindAsync(a.Id))!;
            worker.ModelsJson = worker.ModelsJson.Replace("model-39", "model-zz", StringComparison.Ordinal);
            return true;
        });
        Assert.True(await app.Store.CoordinationTick());
        var after = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson);
        Assert.Equal(Json.Write(before.ProviderEvidence), Json.Write(after.ProviderEvidence));
        Assert.NotEqual(before.ProviderObservationKey, after.ProviderObservationKey);
        Assert.Contains("Provider readiness", after.ReassessmentReason);
    }

    [Fact]
    public async Task CompactedProviderCatalogDoesNotTriggerAnUnchangedPlanningLoop()
    {
        await using var app = new TestApp();
        var (coordinator, a, _) = await SeedProviderRoutes(app.Store);
        await app.Store.Write(async db =>
        {
            var worker = (await db.Workers.FindAsync(a.Id))!;
            worker.ModelsJson = Json.Write(Enumerable.Range(0, 80).Select(i => new ModelChoice("openai", "model-" + i, "Observed",
                Enumerable.Range(0, 8).Select(v => "variant-" + v + new string('x', 300)).ToArray())));
            return true;
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Check authorized scope " + new string('i', 10000), [a.Id], ContinuousSupervision: true));
        await app.Store.CoordinationTick();
        var first = (await app.Store.Coordinations()).Single();
        Assert.Equal("Deciding", first.State);
        var context = Json.Read<CoordinatorContext>(first.InputJson);
        Assert.NotNull(context.ProviderObservationKey);
        var catalog = Assert.Single(context.ProviderEvidence!.Catalogs);
        Assert.True(catalog.Models.Length < 32);
        Assert.Equal(80, catalog.Models.Length + catalog.Omitted);
        await FinishDecision(app.Store, run.Id, new("No authorized task", []));
        await app.Store.CoordinationTick();
        await app.Store.CoordinationTick(); // exactly one normal idle planning review
        await FinishDecision(app.Store, run.Id, new("Scope still blocked", []));
        await app.Store.CoordinationTick();
        Assert.False(await app.Store.CoordinationTick());
        Assert.Equal(context.ProviderObservationKey, Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single().InputJson).ProviderObservationKey);
    }

    private static async Task<(WorkerRecord Coordinator, WorkerRecord A, WorkerRecord B)> SeedProviderRoutes(ControlStore store)
    {
        var (coordinator, a, b) = await Seed(store);
        await store.Write(async db =>
        {
            a = (await db.Workers.FindAsync(a.Id))!; b = (await db.Workers.FindAsync(b.Id))!;
            a.ProviderId = "openai"; a.ModelId = "luna";
            b.ProviderId = "opencode"; b.ModelId = "nemotron-3-ultra-free";
            a.ModelsJson = b.ModelsJson = Json.Write(new ModelChoice[] { new("openai", "luna", "Luna"), new("openai", "sol", "Sol"),
                new("opencode", "nemotron-3-ultra-free", "Observed free model"), new("opencode-go", "luna", "Luna") });
            return true;
        });
        return (coordinator, a, b);
    }

    private static Task<bool> ThreeNativeRateLimits(ControlStore store, WorkerRecord worker) => store.Write(async db =>
    {
        for (var i = 0; i < 3; i++)
        {
            var command = new CommandRecord
            {
                Id = Guid.NewGuid().ToString(),
                WorkerId = worker.Id,
                RuntimeId = worker.RuntimeId,
                Kind = "Prompt",
                State = Delivery.Failed,
                ProviderPoolId = "provider:openai",
                Payload = Json.Write(new PromptInput(Guid.NewGuid().ToString(), "Original attempt", worker.Revision)),
                ResultJson = "{}",
                CreatedAt = ControlStore.Now - 10000,
                UpdatedAt = ControlStore.Now - 10000
            };
            db.Commands.Add(command);
            var error = JsonSerializer.SerializeToElement(new { name = "APIError", data = new { statusCode = 429, message = "secret-native-body" } });
            await ControlStore.ObserveProviderFailure(db, worker, command, "native-" + i, ProviderFailure.Parse(error, ControlStore.Now)!);
        }
        return true;
    });
}
