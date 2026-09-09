using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.AgentControl.Tests;

public sealed class ControlServiceTests
{
    [Fact]
    public async Task MigrationKeepsLegacyBindingsCurrentWithGenerationZeroTitle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-control-generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<ControlDb>().UseSqlite($"Data Source={Path.Combine(directory, "control.db")}").Options;
            await using var db = new ControlDb(options);
            var migration = db.Database.GetMigrations().Single(x => x.EndsWith("ControlSessionGenerations", StringComparison.Ordinal));
            var previous = db.Database.GetMigrations().TakeWhile(x => x != migration).Last();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(previous);
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF");
            const string id = "legacy-control-binding";
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO ControlSessions (Id,ControlServiceId,ScopeKind,ScopeId,WorkerId,NativeSessionId,CreationCommandId,State,Detail,Revision) VALUES ({0},{1},{2},{3},{4},{5},{6},{7},{8},{9})",
                id, "legacy-service", "Workgroup", "legacy-scope", "legacy-worker", "ses_legacy", "legacy-command", "Ready", "Retained", 7L);
            await migrator.MigrateAsync();
            var binding = await db.ControlSessions.AsNoTracking().SingleAsync();
            Assert.Equal(0, binding.Generation); Assert.True(binding.IsCurrent); Assert.Null(binding.PredecessorId);
            Assert.Equal("Initial", binding.GenerationReason); Assert.Equal(0, binding.CreatedAt);
            Assert.Equal("agentcontrol-control:" + id, binding.Title); Assert.Equal(7, binding.Revision);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("CreateControlSession")]
    [InlineData("ControlSessionDiscovery")]
    public async Task LegacyCreatePayloadWithoutAgentReplaysExactlyAfterRestart(string receiptKind)
    {
        await using var native = new NativeControlFixture();
        string data, secrets, serviceId, bindingId;
        var input = new CreateControlSessionInput(Guid.NewGuid().ToString(), "Workgroup", "repo-a", "Repo A", "opencode", "big-pickle");
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            using var owner = await app.SignIn();
            var service = await Register(app, owner, native); serviceId = service.Id;
            var binding = await app.Store.CreateControlSession(service.Id, input with { Id = Guid.NewGuid().ToString() }); bindingId = binding.Id;
            var legacyPayload = Json.Write(new { input.Id, input.ScopeKind, input.ScopeId, input.Name, input.ProviderId, input.ModelId, input.Variant });
            await app.Store.Write(db =>
            {
                db.Commands.Add(new CommandRecord
                {
                    Id = input.Id,
                    RuntimeId = service.Id,
                    Kind = receiptKind,
                    State = Delivery.Finished,
                    Payload = legacyPayload,
                    ResultId = binding.Id
                });
                return Task.FromResult(true);
            });
        }
        await using var restarted = new TestApp(data, secrets);
        var replay = await restarted.Store.CreateControlSession(serviceId, input);
        Assert.Equal(bindingId, replay.Id);
        await Assert.ThrowsAsync<ControlException>(() => restarted.Store.CreateControlSession(serviceId, input with { Name = "Changed" }));
        Assert.Single(await restarted.Store.Read(db => db.Commands.Where(x => x.Id == input.Id).ToListAsync()));
    }

    [Fact]
    public async Task SuccessorRenewalSurvivesLostResponseAndCutoverKeepsLineageIsolated()
    {
        await using var native = new NativeControlFixture();
        string data, secrets, serviceId, predecessorId, predecessorWorkerId, predecessorNativeId, successorId, developerId, runId, taskId, requestId;
        RenewControlSessionInput renewal;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            using var owner = await app.SignIn();
            var service = await Register(app, owner, native); serviceId = service.Id;
            var created = await app.Store.CreateControlSession(service.Id,
                new(Guid.NewGuid().ToString(), "Workgroup", "repo-a", "Repo A", "opencode", "big-pickle"));
            await TestApp.Wait(async () => await app.Store.Read(db => db.Workers.AnyAsync(x => x.Id == created.WorkerId && !x.Stale && x.Activity == "Idle")), "Predecessor observed");
            var predecessor = (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == created.Id);
            predecessorId = predecessor.Id; predecessorWorkerId = predecessor.WorkerId; predecessorNativeId = predecessor.NativeSessionId;
            Assert.Equal(0, predecessor.Generation); Assert.True(predecessor.IsCurrent); Assert.Null(predecessor.PredecessorId);
            Assert.Equal("agentcontrol-control:" + predecessor.Id, predecessor.Title);

            var developer = await PersistenceTests.SeedWorker(app.Store); developerId = developer.Id;
            var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), predecessor.WorkerId, "Keep the original run", [developer.Id]));
            run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause")); runId = run.Id;
            taskId = Guid.NewGuid().ToString(); requestId = Guid.NewGuid().ToString();
            await app.Store.Write(db =>
            {
                db.Messages.Add(new TranscriptMessage { WorkerId = predecessor.WorkerId, NativeId = "msg_predecessor", Role = "assistant", Json = "{}", NativeCreatedAt = 1 });
                db.Requests.Add(new PendingRequest { Id = requestId, WorkerId = predecessor.WorkerId, NativeId = "per_predecessor", Kind = "permission", State = "Pending" });
                db.Commands.Add(new CommandRecord { Id = taskId, RuntimeId = developer.RuntimeId, WorkerId = developer.Id, Kind = "Prompt", State = Delivery.Running, Origin = "coordinator:" + run.Id });
                db.Assignments.Add(new AssignmentRecord { Id = taskId, WorkerId = developer.Id, Prompt = "Retained assignment", Outcome = "Running", Evidence = "receipt-kept" });
                return Task.FromResult(true);
            });

            using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
            renewal = new(Guid.NewGuid().ToString(), predecessor.Revision);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.PostAsJsonAsync($"/api/v1/control-services/{service.Id}/sessions/{predecessor.Id}/renew", renewal)).StatusCode);
            using (var missingCsrf = await app.SignIn())
            {
                missingCsrf.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
                Assert.Equal(HttpStatusCode.BadRequest,
                    (await missingCsrf.PostAsJsonAsync($"/api/v1/control-services/{service.Id}/sessions/{predecessor.Id}/renew", renewal)).StatusCode);
            }

            native.LoseCreationResponse = true; native.HideSessions = true;
            var response = await owner.PostAsJsonAsync($"/api/v1/control-services/{service.Id}/sessions/{predecessor.Id}/renew", renewal);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var result = (await response.Content.ReadFromJsonAsync<ControlSessionRenewalResult>())!;
            successorId = result.Successor.Id;
            Assert.Equal(renewal.Id, result.Receipt.Id); Assert.Equal(Delivery.Finished, result.Receipt.State);
            Assert.Equal(successorId, result.Receipt.SuccessorId);
            Assert.Equal(1, result.Successor.Generation); Assert.Equal(predecessor.Id, result.Successor.PredecessorId);
            Assert.False(result.Successor.IsCurrent); Assert.NotEqual(predecessor.WorkerId, result.Successor.WorkerId);
            Assert.Equal("agentcontrol-control:" + result.Successor.Id + ":g1", result.Successor.Title);
            var publicSuccessor = (await owner.GetFromJsonAsync<List<ControlServiceView>>("/api/v1/control-services"))!
                .Single().Sessions.Single(x => x.Id == successorId);
            Assert.Equal(1, publicSuccessor.Generation); Assert.False(publicSuccessor.IsCurrent); Assert.Equal(predecessor.Id, publicSuccessor.PredecessorId);
            await TestApp.Wait(async () => await app.Store.Read(async db => (await db.Commands.FindAsync(result.Successor.CreationCommandId))!.State == Delivery.Unknown), "Successor create response lost");
            Assert.Equal(3, native.CreationPosts); Assert.Equal(3, native.CreateCalls);

            var ordinary = await app.Store.CreateControlSession(service.Id,
                new(Guid.NewGuid().ToString(), "Workgroup", "repo-a", "Repo A", "opencode", "big-pickle"));
            Assert.Equal(predecessor.Id, ordinary.Id);
            var changedReplay = await owner.PostAsJsonAsync($"/api/v1/control-services/{service.Id}/sessions/{predecessor.Id}/renew", renewal with { ExpectedRevision = renewal.ExpectedRevision + 1 });
            Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        }

        await using (var restarted = new TestApp(data, secrets))
        {
            using var owner = await restarted.SignIn();
            await TestApp.Wait(async () =>
            {
                var connection = (await restarted.Store.ControlServices()).Single().Connection;
                return connection.DesiredConnected && connection.Transport == "Connected" && connection.Health == "Healthy";
            }, "Control service reconnected");
            await Task.Delay(400);
            Assert.Equal(3, native.CreationPosts); Assert.Equal(3, native.CreateCalls);
            native.HideSessions = false;
            await TestApp.Wait(async () => (await restarted.Store.ControlServices()).Single().Sessions.Single(x => x.Id == successorId).State == "Ready", "Successor discovered");
            var response = await owner.PostAsJsonAsync($"/api/v1/control-services/{serviceId}/sessions/{predecessorId}/renew", renewal);
            response.EnsureSuccessStatusCode();
            Assert.Equal(successorId, (await response.Content.ReadFromJsonAsync<ControlSessionRenewalResult>())!.Successor.Id);
            Assert.Equal(3, native.CreationPosts); Assert.Equal(3, native.CreateCalls);

            var run = (await restarted.Store.Coordinations()).Single(x => x.Id == runId);
            var successor = (await restarted.Store.ControlServices()).Single().Sessions.Single(x => x.Id == successorId);
            await restarted.Store.Write(async db =>
            {
                var predecessorWorker = (await db.Workers.FindAsync(predecessorWorkerId))!;
                var successorWorker = (await db.Workers.FindAsync(successor.WorkerId))!;
                predecessorWorker.Stale = false; predecessorWorker.Activity = "Idle"; predecessorWorker.LastObservedAt = ControlStore.Now;
                successorWorker.Stale = false; successorWorker.Activity = "Idle"; successorWorker.LastObservedAt = ControlStore.Now;
                var request = (await db.Requests.FindAsync(requestId))!; request.State = "Pending"; request.ReplyCommandId = null;
                return true;
            });
            response = await owner.PostAsJsonAsync($"/api/v1/coordinations/{run.Id}/control-session",
                new MigrateControlSessionInput(Guid.NewGuid().ToString(), run.Revision, successor.Id));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await restarted.Store.Write(async db => { (await db.Requests.FindAsync(requestId))!.State = "Answered"; return true; });
            run = (await restarted.Store.Coordinations()).Single(x => x.Id == runId);
            response = await owner.PostAsJsonAsync($"/api/v1/coordinations/{run.Id}/control-session",
                new MigrateControlSessionInput(Guid.NewGuid().ToString(), run.Revision, successor.Id));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

            var migrated = (await restarted.Store.Coordinations()).Single(x => x.Id == runId);
            Assert.Equal("Paused", migrated.State); Assert.Equal(successor.WorkerId, migrated.CoordinatorWorkerId);
            Assert.Equal(0, migrated.Round); Assert.Equal(Delivery.Running, await restarted.Store.Read(async db => (await db.Commands.FindAsync(taskId))!.State));
            var assignment = await restarted.Store.Read(async db => (await db.Assignments.FindAsync(taskId))!);
            Assert.Equal(developerId, assignment.WorkerId); Assert.Equal("receipt-kept", assignment.Evidence);
            var sessions = (await restarted.Store.ControlServices()).Single().Sessions.Where(x => x.ScopeId == "repo-a").ToArray();
            Assert.Single(sessions, x => x.IsCurrent && x.Id == successor.Id);
            Assert.Single(sessions, x => !x.IsCurrent && x.Id == predecessorId);
            var predecessorWorker = await restarted.Store.Read(async db => (await db.Workers.FindAsync(predecessorWorkerId))!);
            var successorWorker = await restarted.Store.Read(async db => (await db.Workers.FindAsync(successor.WorkerId))!);
            Assert.True(predecessorWorker.Archived); Assert.False(successorWorker.Archived);
            Assert.Equal(predecessorNativeId, predecessorWorker.NativeSessionId);
            Assert.Single(await restarted.Store.Read(db => db.Messages.Where(x => x.WorkerId == predecessorWorkerId).ToListAsync()));
            Assert.Empty(await restarted.Store.Read(db => db.Messages.Where(x => x.WorkerId == successor.WorkerId).ToListAsync()));
            Assert.Single(await restarted.Store.Read(db => db.Requests.Where(x => x.WorkerId == predecessorWorkerId).ToListAsync()));
            Assert.Empty(await restarted.Store.Read(db => db.Requests.Where(x => x.WorkerId == successor.WorkerId).ToListAsync()));
            var ordinary = await restarted.Store.CreateControlSession(serviceId,
                new(Guid.NewGuid().ToString(), "Workgroup", "repo-a", "Repo A", "opencode", "big-pickle"));
            Assert.Equal(successor.Id, ordinary.Id);
            Assert.Single(await restarted.Store.Read(db => db.Events.Where(x => x.Type == "ControlSessionRenewalRequested").ToListAsync()));
            Assert.Single(await restarted.Store.Read(db => db.Events.Where(x => x.Type == "CoordinationControlSessionMigrated").ToListAsync()));
            Assert.Equal(developerId, await restarted.Store.Read(async db => (await db.Commands.FindAsync(taskId))!.WorkerId));
        }
    }

    [Fact]
    public async Task ConcurrentRenewalsCreateOneDerivedSuccessorAndRejectTheOther()
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        var predecessor = await app.Store.CreateControlSession(service.Id,
            new(Guid.NewGuid().ToString(), "Workgroup", "repo-a", "Repo A", "opencode", "big-pickle"));
        await TestApp.Wait(async () => (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == predecessor.Id).State == "Ready", "Predecessor ready");
        predecessor = (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == predecessor.Id);
        var attempts = new[] { new RenewControlSessionInput(Guid.NewGuid().ToString(), predecessor.Revision), new(Guid.NewGuid().ToString(), predecessor.Revision) };
        var outcomes = await Task.WhenAll(attempts.Select(async input =>
        {
            try { return (Result: await app.Store.RenewControlSession(service.Id, predecessor.Id, input), Error: (ControlException?)null); }
            catch (ControlException error) { return (Result: (ControlSessionRenewalResult?)null, Error: error); }
        }));
        Assert.Single(outcomes, x => x.Result is not null); Assert.Single(outcomes, x => x.Error is not null);
        Assert.Single(await app.Store.Read(db => db.ControlSessions.Where(x => x.PredecessorId == predecessor.Id).ToListAsync()));
        Assert.Single(await app.Store.Read(db => db.Commands.Where(x => x.Kind == "RenewControlSession").ToListAsync()));
    }

    [Fact]
    public async Task GenerationCutoverRequiresFreshIdleSessionsAndResolvedDeliveryAndRequests()
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        var predecessor = await app.Store.CreateControlSession(service.Id,
            new(Guid.NewGuid().ToString(), "Workgroup", "repo-a", "Repo A", "opencode", "big-pickle"));
        await TestApp.Wait(async () => await app.Store.Read(db => db.Workers.AnyAsync(x => x.Id == predecessor.WorkerId && !x.Stale && x.Activity == "Idle")), "Predecessor observed");
        predecessor = (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == predecessor.Id);
        var developer = await PersistenceTests.SeedWorker(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), predecessor.WorkerId, "Keep the run paused", [developer.Id]));
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        var renewal = await app.Store.RenewControlSession(service.Id, predecessor.Id, new(Guid.NewGuid().ToString(), predecessor.Revision));
        await TestApp.Wait(async () => await app.Store.Read(db => db.Workers.AnyAsync(x => x.Id == renewal.Successor.WorkerId)), "Successor bound");
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);

        async Task Reset()
        {
            await app.Store.Write(async db =>
            {
                var runtime = (await db.Runtimes.FindAsync(service.Id))!;
                runtime.DesiredConnected = true; runtime.Transport = "Connected"; runtime.Health = "Healthy";
                foreach (var workerId in new[] { predecessor.WorkerId, renewal.Successor.WorkerId })
                {
                    var worker = (await db.Workers.FindAsync(workerId))!;
                    worker.Stale = false; worker.Activity = "Idle"; worker.LastObservedAt = ControlStore.Now;
                }
                foreach (var command in await db.Commands.Where(x => x.WorkerId == renewal.Successor.WorkerId && x.Kind == "Prompt").ToListAsync())
                    command.State = Delivery.Finished;
                foreach (var request in await db.Requests.Where(x => x.WorkerId == renewal.Successor.WorkerId).ToListAsync())
                    request.State = "Answered";
                return true;
            });
        }
        Task Cutover() => app.Store.MigrateControlSession(run.Id,
            new(Guid.NewGuid().ToString(), run.Revision, renewal.Successor.Id));

        await Reset();
        await app.Store.Write(async db => { (await db.Workers.FindAsync(predecessor.WorkerId))!.Stale = true; return true; });
        await Assert.ThrowsAsync<ControlException>(Cutover);
        await Reset();
        await app.Store.Write(async db => { (await db.Workers.FindAsync(renewal.Successor.WorkerId))!.Activity = "Active"; return true; });
        await Assert.ThrowsAsync<ControlException>(Cutover);
        await Reset();
        await app.Store.Write(db =>
        {
            db.Commands.Add(new CommandRecord { Id = Guid.NewGuid().ToString(), RuntimeId = service.Id, WorkerId = renewal.Successor.WorkerId, Kind = "Prompt", State = Delivery.Unknown });
            return Task.FromResult(true);
        });
        await Assert.ThrowsAsync<ControlException>(Cutover);
        await Reset();
        await app.Store.Write(db =>
        {
            db.Requests.Add(new PendingRequest { WorkerId = renewal.Successor.WorkerId, NativeId = "q_successor", Kind = "question", State = "ReplyUnknown" });
            return Task.FromResult(true);
        });
        await Assert.ThrowsAsync<ControlException>(Cutover);
        Assert.Equal(predecessor.WorkerId, (await app.Store.Coordinations()).Single(x => x.Id == run.Id).CoordinatorWorkerId);
        Assert.True((await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == predecessor.Id).IsCurrent);
    }

    [Fact]
    public async Task ExpiredControlDecisionUsesOneDiscoverableSuccessorAndOneLinkedReplacement()
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        var predecessor = await app.Store.CreateControlSession(service.Id,
            new(Guid.NewGuid().ToString(), "Workgroup", "automatic-recovery", "Automatic recovery", "opencode", "big-pickle"));
        await TestApp.Wait(async () => await app.Store.Read(db => db.Workers.AnyAsync(x => x.Id == predecessor.WorkerId && !x.Stale && x.Activity == "Idle")), "Predecessor observed");
        predecessor = (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == predecessor.Id);
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);

        var developer = await PersistenceTests.SeedWorker(app.Store);
        var unrelated = new CommandRecord { Id = Guid.NewGuid().ToString(), RuntimeId = developer.RuntimeId, WorkerId = developer.Id, Kind = "Prompt", State = Delivery.Running };
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), predecessor.WorkerId, "Preserve all assigned work", [developer.Id]));
        Assert.True(await app.Store.CoordinationTick());
        var sourceCommandId = (await app.Store.Coordinations()).Single(x => x.Id == run.Id).DecisionCommandId!;
        await ExpireDecision(app.Store, run.Id, predecessor.WorkerId, sourceCommandId);
        await app.Store.Write(db => { db.Commands.Add(unrelated); return Task.FromResult(true); });

        Assert.True(await app.Store.CoordinationTick());
        var requested = (await app.Store.Coordinations()).Single(x => x.Id == run.Id);
        var recovery = Json.Read<CoordinatorContext>(requested.InputJson).GenerationRecovery!;
        var successor = (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == recovery.SuccessorControlSessionId);
        Assert.Equal("DecisionBudgetRecovery", successor.GenerationReason);
        Assert.Equal(recovery.IntentId, successor.RecoveryIntentId);
        Assert.False(successor.IsCurrent);

        var postsBeforeRecovery = native.CreationPosts;
        native.HideSessions = true;
        native.LoseCreationResponse = true;
        await app.Store.Write(async db => { (await db.Commands.FindAsync(successor.CreationCommandId))!.State = Delivery.Accepted; return true; });
        var backendStarts = await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "BackendStarted"));
        using (var firstAttempt = ActivatorUtilities.CreateInstance<RuntimeSupervisor>(app.Services))
        {
            await firstAttempt.StartAsync(CancellationToken.None);
            await TestApp.Wait(async () => await app.Store.Read(async db =>
                await db.Events.CountAsync(x => x.Type == "BackendStarted") > backendStarts &&
                (await db.Runtimes.FindAsync(service.Id))!.Health == "Healthy" && !(await db.Workers.FindAsync(predecessor.WorkerId))!.Stale),
                "Control service re-observed before automatic creation");
            await app.Store.Write(async db =>
            {
                var source = (await db.Workers.FindAsync(predecessor.WorkerId))!;
                source.HistoryGap = false; source.LastObservedAt = ControlStore.Now;
                var sourceCommand = (await db.Commands.FindAsync(sourceCommandId))!;
                sourceCommand.State = Delivery.Running;
                var observation = (await db.CoordinatorNativeObservations.FindAsync(sourceCommandId))!;
                observation.ObservedAt = source.LastObservedAt.Value; observation.ChildSessionCount = 0;
                var creation = (await db.Commands.FindAsync(successor.CreationCommandId))!;
                creation.State = Delivery.Queued; creation.QueueOrder = ControlStore.Now;
                var binding = (await db.ControlSessions.FindAsync(successor.Id))!;
                binding.State = "Queued";
                return true;
            });
            await TestApp.Wait(async () => await app.Store.Read(async db =>
                (await db.Commands.FindAsync(successor.CreationCommandId))!.State is Delivery.Unknown or Delivery.Cancelled or Delivery.Failed), "Automatic successor response lost");
            await firstAttempt.StopAsync(CancellationToken.None);
        }
        var firstCreation = await app.Store.Read(async db => (await db.Commands.FindAsync(successor.CreationCommandId))!);
        Assert.True(firstCreation.State == Delivery.Unknown, firstCreation.State + ": " + firstCreation.Detail);
        Assert.Equal(postsBeforeRecovery + 1, native.CreationPosts);

        native.HideSessions = false;
        native.LoseCreationResponse = false;
        using (var reconciliation = ActivatorUtilities.CreateInstance<RuntimeSupervisor>(app.Services))
        {
            await reconciliation.StartAsync(CancellationToken.None);
            await TestApp.Wait(async () => (await app.Store.ControlServices()).Single().Sessions
                .Single(x => x.Id == successor.Id).State == "Ready", "Automatic successor discovered");
            await reconciliation.StopAsync(CancellationToken.None);
        }
        Assert.Equal(postsBeforeRecovery + 1, native.CreationPosts);

        await app.Store.Write(async db =>
        {
            var runtime = (await db.Runtimes.FindAsync(service.Id))!;
            runtime.DesiredConnected = true; runtime.Transport = "Connected"; runtime.Health = "Healthy";
            var source = (await db.Workers.FindAsync(predecessor.WorkerId))!;
            source.Stale = false; source.HistoryGap = false; source.Activity = "Idle"; source.LastObservedAt = ControlStore.Now;
            var target = (await db.Workers.FindAsync(successor.WorkerId))!;
            target.Stale = false; target.HistoryGap = false; target.Activity = "Idle"; target.LastObservedAt = ControlStore.Now;
            var command = (await db.Commands.FindAsync(sourceCommandId))!;
            command.State = Delivery.Running; command.NativeMessageId = "caller-budget";
            var observation = (await db.CoordinatorNativeObservations.FindAsync(sourceCommandId))!;
            observation.ObservedAt = ControlStore.Now; observation.ChildSessionCount = 0;
            source.LastObservedAt = observation.ObservedAt;
            return true;
        });
        Assert.True(await app.Store.CoordinationTick());
        var cutover = (await app.Store.Coordinations()).Single(x => x.Id == run.Id);
        Assert.Equal(successor.WorkerId, cutover.CoordinatorWorkerId);
        Assert.Null(cutover.DecisionCommandId);
        Assert.Equal(Delivery.Running, await app.Store.Read(async db => (await db.Commands.FindAsync(sourceCommandId))!.State));
        Assert.Equal(Delivery.Running, await app.Store.Read(async db => (await db.Commands.FindAsync(unrelated.Id))!.State));

        Assert.True(await app.Store.CoordinationTick());
        var replacement = (await app.Store.Coordinations()).Single(x => x.Id == run.Id);
        var replacementRecovery = Json.Read<CoordinatorContext>(replacement.InputJson).GenerationRecovery!;
        Assert.Equal("ReplacementQueued", replacementRecovery.State);
        Assert.Equal(replacement.DecisionCommandId, replacementRecovery.ReplacementDecisionCommandId);
        Assert.Equal(replacement.DecisionCommandId, (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == successor.Id).ReplacementDecisionCommandId);
        Assert.Equal(2, (await app.Store.Snapshot()).Commands.Count(x => x.Origin == "coordinator-decision:" + run.Id));
        await app.Store.Write(async db =>
        {
            var late = (await db.Commands.FindAsync(sourceCommandId))!;
            late.State = Delivery.Finished;
            late.ResultJson = Json.Write(new
            {
                messages = new[] { new { parts = new[] { new { type = "text", text = Json.Write(new CoordinatorDecision("late", [new("send_prompt", developer.Id, "must not dispatch")])) } } } }
            });
            return true;
        });
        await app.Store.CoordinationTick();
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, x => x.Origin == "coordinator:" + run.Id);
        Assert.Equal(Delivery.Running, await app.Store.Read(async db => (await db.Commands.FindAsync(unrelated.Id))!.State));
    }

    [Theory]
    [InlineData("owner-pause")]
    [InlineData("elapsed-hold")]
    public async Task SupersededAutomaticSuccessorIsCancelledAtPreEffectFence(string conflict)
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        var predecessor = await app.Store.CreateControlSession(service.Id,
            new(Guid.NewGuid().ToString(), "Workgroup", "pause-race", "Pause race", "opencode", "big-pickle"));
        await TestApp.Wait(async () => await app.Store.Read(db => db.Workers.AnyAsync(x => x.Id == predecessor.WorkerId && !x.Stale && x.Activity == "Idle")), "Predecessor observed");
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var developer = await PersistenceTests.SeedWorker(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), predecessor.WorkerId, "Pause before effects", [developer.Id]));
        await app.Store.CoordinationTick();
        var sourceCommandId = (await app.Store.Coordinations()).Single(x => x.Id == run.Id).DecisionCommandId!;
        await ExpireDecision(app.Store, run.Id, predecessor.WorkerId, sourceCommandId);
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single(x => x.Id == run.Id);
        var recovery = Json.Read<CoordinatorContext>(run.InputJson).GenerationRecovery!;
        if (conflict == "owner-pause")
        {
            await app.Store.Write(async db => { (await db.Commands.FindAsync(recovery.CreationCommandId))!.State = Delivery.Dispatching; return true; });
            run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        }
        else
        {
            await app.Store.Write(async db =>
            {
                var saved = (await db.CoordinationRuns.FindAsync(run.Id))!;
                var context = Json.Read<CoordinatorContext>(saved.InputJson);
                saved.InputJson = Json.Write(context with
                {
                    GenerationRecovery = context.GenerationRecovery! with { RequestedAt = ControlStore.Now - 900_001 }
                });
                (await db.Commands.FindAsync(recovery.CreationCommandId))!.State = Delivery.Accepted;
                return true;
            });
            Assert.True(await app.Store.CoordinationTick());
            await app.Store.Write(async db => { (await db.Commands.FindAsync(recovery.CreationCommandId))!.State = Delivery.Dispatching; return true; });
            run = (await app.Store.Coordinations()).Single(x => x.Id == run.Id);
        }

        Assert.False(await app.Store.AuthorizeAutomaticControlSessionCreation(recovery.CreationCommandId));
        var creation = await app.Store.Read(async db => (await db.Commands.FindAsync(recovery.CreationCommandId))!);
        var successor = (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == recovery.SuccessorControlSessionId);
        Assert.Equal(Delivery.Cancelled, creation.State);
        Assert.Equal(Delivery.Cancelled, successor.State);
        Assert.Null(successor.CreationAuthorizedAt);
        Assert.Equal(conflict == "owner-pause" ? "Paused" : "Deciding", run.State);
        Assert.Equal(2, native.CreationPosts);
    }

    [Fact]
    public async Task OriginalCompletionWinsBeforeSuccessorEffectAndAppliesOnlyOnce()
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        var predecessor = await app.Store.CreateControlSession(service.Id,
            new(Guid.NewGuid().ToString(), "Workgroup", "completion-race", "Completion race", "opencode", "big-pickle"));
        await TestApp.Wait(async () => await app.Store.Read(db => db.Workers.AnyAsync(x => x.Id == predecessor.WorkerId && !x.Stale && x.Activity == "Idle")), "Predecessor observed");
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var developer = await PersistenceTests.SeedWorker(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), predecessor.WorkerId, "Original may still finish", [developer.Id]));
        await app.Store.CoordinationTick();
        var sourceCommandId = (await app.Store.Coordinations()).Single(x => x.Id == run.Id).DecisionCommandId!;
        await ExpireDecision(app.Store, run.Id, predecessor.WorkerId, sourceCommandId);
        await app.Store.CoordinationTick();
        var recovery = Json.Read<CoordinatorContext>((await app.Store.Coordinations()).Single(x => x.Id == run.Id).InputJson).GenerationRecovery!;
        await app.Store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(sourceCommandId))!;
            command.State = Delivery.Finished;
            command.ResultJson = Json.Write(new
            {
                messages = new[] { new { parts = new[] { new { type = "text", text = Json.Write(new CoordinatorDecision("Original completed", [])) } } } }
            });
            return true;
        });

        Assert.True(await app.Store.CoordinationTick());
        var applied = (await app.Store.Coordinations()).Single(x => x.Id == run.Id);
        var completedRecovery = Json.Read<CoordinatorContext>(applied.InputJson).GenerationRecovery!;
        Assert.Equal("OriginalCompleted", completedRecovery.State);
        Assert.Equal("Original completed", applied.Detail);
        Assert.Null(applied.DecisionCommandId);
        var successor = (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == recovery.SuccessorControlSessionId);
        Assert.Equal(Delivery.Cancelled, successor.State);
        Assert.Null(successor.CreationAuthorizedAt);
        Assert.Equal(2, native.CreationPosts);
        Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "CoordinatorDecisionApplied" && x.CommandId == null).ToListAsync()));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("child")]
    [InlineData("spend")]
    public async Task ChangedControlProcessOrObservedChildHoldsWithoutAllocatingSuccessor(string conflict)
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        var predecessor = await app.Store.CreateControlSession(service.Id,
            new(Guid.NewGuid().ToString(), "Workgroup", "process-fence", "Process fence", "opencode", "big-pickle"));
        await TestApp.Wait(async () => await app.Store.Read(db => db.Workers.AnyAsync(x => x.Id == predecessor.WorkerId && !x.Stale && x.Activity == "Idle")), "Predecessor observed");
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var developer = await PersistenceTests.SeedWorker(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), predecessor.WorkerId, "Require exact process", [developer.Id]));
        await app.Store.CoordinationTick();
        var sourceCommandId = (await app.Store.Coordinations()).Single(x => x.Id == run.Id).DecisionCommandId!;
        await ExpireDecision(app.Store, run.Id, predecessor.WorkerId, sourceCommandId);
        await app.Store.Write(async db =>
        {
            if (conflict == "process") (await db.ControlServices.FindAsync(service.Id))!.IncarnationId = Guid.NewGuid().ToString();
            else if (conflict == "child") (await db.CoordinatorNativeObservations.FindAsync(sourceCommandId))!.ChildSessionCount = 1;
            else db.ModelUsage.AddRange(
                new ModelUsageRecord
                {
                    RuntimeId = service.Id,
                    NativeSessionId = predecessor.NativeSessionId,
                    NativeMessageId = "usage-over-limit",
                    CommandId = sourceCommandId,
                    InputTokens = 2_000_001,
                    ObservedAt = ControlStore.Now - 1
                },
                new ModelUsageRecord
                {
                    RuntimeId = service.Id,
                    NativeSessionId = predecessor.NativeSessionId,
                    NativeMessageId = "usage-later-lower",
                    CommandId = sourceCommandId,
                    InputTokens = 100_000,
                    ObservedAt = ControlStore.Now
                });
            return true;
        });

        Assert.True(await app.Store.CoordinationTick());
        var held = (await app.Store.Coordinations()).Single(x => x.Id == run.Id);
        var context = Json.Read<CoordinatorContext>(held.InputJson);
        Assert.Null(context.GenerationRecovery);
        var reason = conflict == "process" ? "fence changed" : conflict == "child" ? "ownership cannot be proven" : "spend limit";
        Assert.Contains(reason, context.DecisionCheckpoint!.RecoveryHold);
        Assert.DoesNotContain((await app.Store.ControlServices()).Single().Sessions, x => x.PredecessorId == predecessor.Id);
        Assert.Equal(Delivery.Running, await app.Store.Read(async db => (await db.Commands.FindAsync(sourceCommandId))!.State));
    }

    private static Task ExpireDecision(ControlStore store, string runId, string coordinatorId, string commandId) => store.Write(async db =>
    {
        var run = (await db.CoordinationRuns.FindAsync(runId))!;
        var context = Json.Read<CoordinatorContext>(run.InputJson);
        var command = (await db.Commands.FindAsync(commandId))!;
        command.State = Delivery.Running; command.NativeMessageId = "caller-budget";
        var observedAt = ControlStore.Now;
        db.CoordinatorNativeObservations.Add(new CoordinatorNativeObservation
        {
            CommandId = command.Id,
            WorkerId = coordinatorId,
            RuntimeId = command.RuntimeId,
            NativeSessionId = (await db.Workers.FindAsync(coordinatorId))!.NativeSessionId,
            NativeCallerId = command.NativeMessageId,
            ChildSessionCount = 0,
            ObservedAt = observedAt
        });
        var coordinator = (await db.Workers.FindAsync(coordinatorId))!;
        coordinator.Stale = false; coordinator.HistoryGap = false; coordinator.Activity = "Active"; coordinator.LastObservedAt = observedAt;
        run.InputJson = Json.Write(context with
        {
            DecisionCheckpoint = context.DecisionCheckpoint! with
            {
                NativeCallerId = command.NativeMessageId,
                Phase = "Inference",
                StartedAt = ControlStore.Now - 14_400_001,
                PhaseStartedAt = ControlStore.Now - 7_200_001,
                LastEvidenceAt = ControlStore.Now - 7_200_001
            }
        });
        return true;
    });

    [Theory]
    [InlineData("version")]
    [InlineData("health")]
    [InlineData("schema")]
    public async Task ChangedNativeServerIsRejectedBeforeObservationOrProviderConnection(string failure)
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        await Register(app, owner, native);
        await TestApp.Wait(async () => (await app.Store.ControlServices()).Single().Sessions.Single().State == "Ready", "Host scope ready");
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var view = (await app.Store.ControlServices()).Single();
        var factory = app.Services.GetRequiredService<IRuntimeTransportFactory>();
        await using var existing = await factory.Connect(view.Connection, CancellationToken.None);
        if (failure == "version") native.Version = "99.0.0";
        else if (failure == "health") native.Healthy = false;
        else
        {
            native.Identity = native.Identity with { IncarnationId = Guid.NewGuid().ToString() };
            native.MissingCoreRoute = true;
        }
        await Assert.ThrowsAsync<ControlException>(() => existing.ValidateConnection(CancellationToken.None));
        await Assert.ThrowsAsync<ControlException>(() => factory.Connect(view.Connection, CancellationToken.None));
        Assert.Equal(view.Service.IncarnationId, (await app.Store.ControlServices()).Single().Service.IncarnationId);
        Assert.Equal(1, native.CreateCalls);
        Assert.Equal(0, native.UnexpectedMutations);
    }

    [Fact]
    public async Task DirectHttpProvisioningKeepsHostAndWorkgroupConversationsAcrossBackendRestart()
    {
        await using var native = new NativeControlFixture();
        string data, secrets, serviceId;
        string[] sessions;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            using var owner = await app.SignIn();
            var service = await Register(app, owner, native); serviceId = service.Id;
            var input = new CreateControlSessionInput(Guid.NewGuid().ToString(), "Workgroup", "repo-a", "Repo A", "opencode", "big-pickle");
            var response = await owner.PostAsJsonAsync($"/api/v1/control-services/{service.Id}/sessions", input); response.EnsureSuccessStatusCode();
            var binding = (await response.Content.ReadFromJsonAsync<ControlSessionBinding>())!;
            await TestApp.Wait(async () => (await app.Store.ControlServices()).Single().Sessions.All(x => x.State == "Ready"), "Control sessions ready");
            var view = (await app.Store.ControlServices()).Single();
            Assert.Equal(2, view.Sessions.Count);
            Assert.Equal(RuntimeConnections.ControlHttp, view.Connection.ConnectionKind);
            Assert.All((await app.Store.Snapshot()).Workers, x => Assert.Equal(SessionRoles.Coordinator, x.Role));
            Assert.Equal(2, native.CreateCalls);
            var retry = await owner.PostAsJsonAsync($"/api/v1/control-services/{service.Id}/sessions", input with { Id = Guid.NewGuid().ToString() });
            retry.EnsureSuccessStatusCode();
            Assert.Equal(binding.Id, (await retry.Content.ReadFromJsonAsync<ControlSessionBinding>())!.Id);
            sessions = view.Sessions.OrderBy(x => x.Id).Select(x => x.NativeSessionId).ToArray();
        }
        await using (var restarted = new TestApp(data, secrets))
        {
            using var owner = await restarted.SignIn();
            await TestApp.Wait(async () => (await restarted.Store.ControlServices()).Single().Connection.Health == "Healthy", "Direct reconnect");
            var view = (await restarted.Store.ControlServices()).Single();
            Assert.Equal(serviceId, view.Service.Id);
            Assert.Equal(sessions, view.Sessions.OrderBy(x => x.Id).Select(x => x.NativeSessionId));
            Assert.Equal(2, native.CreateCalls);
            Assert.Equal(0, native.UnexpectedMutations);
        }
    }

    [Fact]
    public async Task LostNativeCreationResponseIsDiscoveredWithoutRepeatingPost()
    {
        await using var native = new NativeControlFixture { LoseCreationResponse = true, HideSessions = true };
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        await TestApp.Wait(async () => await app.Store.Read(db => db.Commands.AnyAsync(x => x.RuntimeId == service.Id && x.Kind == "CreateControlSession" && x.State == Delivery.Unknown)), "Uncertain create receipt");
        await app.Store.RuntimeCommand(service.Id, "RefreshState", Guid.NewGuid().ToString());
        await Task.Delay(500);
        Assert.Equal(1, native.CreateCalls);
        native.HideSessions = false;
        await TestApp.Wait(async () => (await app.Store.ControlServices()).Single().Sessions.Single().State == "Ready", "Discover original session");
        Assert.Equal(1, native.CreateCalls);
    }

    [Fact]
    public async Task WrongInstanceAndRedirectCannotRegisterAServiceOrSendMutations()
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        File.WriteAllText(Path.Combine(app.SecretPath, "sidecar-password"), NativeControlFixture.Password);
        var input = new RegisterControlServiceInput(Guid.NewGuid().ToString("N"), "Control", native.Endpoint, Guid.NewGuid().ToString(), "sidecar-password");
        var response = await owner.PostAsJsonAsync("/api/v1/control-services", input);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await app.Store.ControlServices());
        Assert.Equal(0, native.CreateCalls);
        native.RedirectIdentity = true;
        response = await owner.PostAsJsonAsync("/api/v1/control-services", input with { ExpectedInstanceId = native.InstanceId });
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(0, native.RedirectVisits);
    }

    [Fact]
    public async Task ControlServiceCannotBecomeDevelopmentCapacityOrBeStoppedThroughSshLifecycle()
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        await TestApp.Wait(async () => (await app.Store.ControlServices()).Single().Sessions.Single().State == "Ready", "Host scope ready");
        var view = (await app.Store.ControlServices()).Single();
        var create = await owner.PostAsJsonAsync("/api/v1/workers", new CreateWorkerInput(Guid.NewGuid().ToString(), service.Id, "Bad worker", "repo", "/repo", "opencode", "big-pickle"));
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        var stop = await owner.PostAsJsonAsync($"/api/v1/runtimes/{service.Id}/stop", new { id = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.Conflict, stop.StatusCode);
        Assert.True((await app.Store.ControlServices()).Single().Connection.DesiredConnected);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AcquireTerminalRuntime(service.Id, CancellationToken.None));
        view.Connection.ConnectionKind = RuntimeConnections.Ssh;
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntime(view.Connection));
        var worker = (await app.Store.Snapshot()).Workers.Single();
        var delete = await owner.PostAsJsonAsync($"/api/v1/workers/{worker.Id}/delete", new DeleteRegistrationInput(Guid.NewGuid().ToString(), worker.SettingsRevision));
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        var github = await owner.PostAsJsonAsync($"/api/v1/runtimes/{service.Id}/github", new HVO.AgentControl.GitHub.ConfigureGitHubAccess(1, 1, "", [], 0));
        Assert.Equal(HttpStatusCode.Conflict, github.StatusCode);
        Assert.Empty(await app.Store.Read(db => db.GitHubAccess.Where(x => x.Id == service.Id).ToListAsync()));
        Assert.Equal(0, native.UnexpectedMutations);
    }

    [Fact]
    public async Task RestartObservationRetainsTerminalReceiptsAndDoesNotReplayUncertainInstructions()
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        await TestApp.Wait(async () => (await app.Store.ControlServices()).Single().Sessions.Single().State == "Ready", "Host scope ready");
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var binding = (await app.Store.ControlServices()).Single().Sessions.Single();
        var active = Guid.NewGuid().ToString(); var terminal = Guid.NewGuid().ToString();
        await app.Store.Write(db =>
        {
            db.Commands.Add(new CommandRecord { Id = active, RuntimeId = service.Id, WorkerId = binding.WorkerId, Kind = "Prompt", State = Delivery.Accepted, NativeMessageId = "msg_original" });
            db.Commands.Add(new CommandRecord { Id = terminal, RuntimeId = service.Id, WorkerId = binding.WorkerId, Kind = "Prompt", State = Delivery.Finished });
            return Task.FromResult(true);
        });
        var replacement = native.Identity with { IncarnationId = Guid.NewGuid().ToString() };
        await app.Store.ObserveControlService(service.Id, replacement);
        await app.Store.ObserveControlService(service.Id, replacement);
        Assert.Equal(Delivery.Unknown, await app.Store.Read(async db => (await db.Commands.FindAsync(active))!.State));
        Assert.Equal("msg_original", await app.Store.Read(async db => (await db.Commands.FindAsync(active))!.NativeMessageId));
        Assert.Equal(Delivery.Finished, await app.Store.Read(async db => (await db.Commands.FindAsync(terminal))!.State));
        Assert.Equal(1, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "ControlServiceCommandInterrupted")));
        Assert.Equal(binding.NativeSessionId, (await app.Store.ControlServices()).Single().Sessions.Single().NativeSessionId);
    }

    [Fact]
    public async Task PausedMigrationPreservesRunAssignmentsAndRejectsUncertainDecision()
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        var target = await app.Store.CreateControlSession(service.Id, new(Guid.NewGuid().ToString(), "Workgroup", "repo-a", "Repo A", "opencode", "big-pickle"));
        await TestApp.Wait(async () => await app.Store.Read(db => db.Workers.AnyAsync(x => x.Id == target.WorkerId && !x.Stale && x.Activity == "Idle")), "Target observed");
        var old = await PersistenceTests.SeedWorker(app.Store);
        var developer = new WorkerRecord { RuntimeId = old.RuntimeId, NativeSessionId = "ses_developer", Directory = "/repo", Name = "Developer" };
        await app.Store.Write(async db =>
        {
            var coordinator = (await db.Workers.FindAsync(old.Id))!;
            coordinator.Role = SessionRoles.Coordinator; coordinator.Stale = false; coordinator.Activity = "Idle"; coordinator.LastObservedAt = ControlStore.Now;
            db.Workers.Add(developer); return true;
        });
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), old.Id, "Retain assignments", [developer.Id]));
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        var decisionId = Guid.NewGuid().ToString(); var taskId = Guid.NewGuid().ToString();
        await app.Store.Write(async db =>
        {
            (await db.CoordinationRuns.FindAsync(run.Id))!.DecisionCommandId = decisionId;
            db.Commands.Add(new CommandRecord { Id = decisionId, RuntimeId = old.RuntimeId, WorkerId = old.Id, Kind = "Prompt", State = Delivery.Unknown });
            db.Commands.Add(new CommandRecord { Id = taskId, RuntimeId = old.RuntimeId, WorkerId = developer.Id, Kind = "Prompt", State = Delivery.Running, Origin = "coordinator:" + run.Id });
            return true;
        });
        var input = new MigrateControlSessionInput(Guid.NewGuid().ToString(), run.Revision, target.Id);
        var response = await owner.PostAsJsonAsync($"/api/v1/coordinations/{run.Id}/control-session", input);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(old.Id, (await app.Store.Coordinations()).Single().CoordinatorWorkerId);
        await app.Store.Write(async db => { (await db.Commands.FindAsync(decisionId))!.State = Delivery.Finished; return true; });
        response = await owner.PostAsJsonAsync($"/api/v1/coordinations/{run.Id}/control-session", input); response.EnsureSuccessStatusCode();
        var migrated = (await app.Store.Coordinations()).Single();
        Assert.Equal(target.WorkerId, migrated.CoordinatorWorkerId); Assert.Equal(run.Id, migrated.Id); Assert.Equal("Paused", migrated.State);
        Assert.Null(migrated.DecisionCommandId);
        Assert.Equal(Delivery.Running, await app.Store.Read(async db => (await db.Commands.FindAsync(taskId))!.State));
        Assert.Equal(old.NativeSessionId, await app.Store.Read(async db => (await db.Workers.FindAsync(old.Id))!.NativeSessionId));
        response = await owner.PostAsJsonAsync($"/api/v1/coordinations/{run.Id}/control-session", input); response.EnsureSuccessStatusCode();
        Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "CoordinationControlSessionMigrated").ToListAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRetiresOnlyUnappliedRoutingAuthorityAndPreservesOwnerPause(bool backendAlsoRestarted)
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        var target = await app.Store.CreateControlSession(service.Id, new(Guid.NewGuid().ToString(), "Workgroup", "repo-a", "Repo A", "opencode", "big-pickle"));
        await TestApp.Wait(async () => await app.Store.Read(db => db.Workers.AnyAsync(x => x.Id == target.WorkerId && !x.Stale && x.Activity == "Idle")), "Target observed");
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var developer = await PersistenceTests.SeedWorker(app.Store);
        var host = (await app.Store.ControlServices()).Single().Sessions.Single(x => x.ScopeKind == "HostOperations");
        await Assert.ThrowsAsync<ControlException>(() => app.Store.StartCoordination(new(Guid.NewGuid().ToString(), host.WorkerId, "Wrong scope", [developer.Id])));
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), target.WorkerId, "Decide work", [developer.Id]));
        await app.Store.CoordinationTick();
        run = (await app.Store.Coordinations()).Single();
        Assert.NotNull(run.DecisionCommandId);
        var decisionId = run.DecisionCommandId!;
        await app.Store.Write(async db => { (await db.Commands.FindAsync(decisionId))!.State = backendAlsoRestarted ? Delivery.Dispatching : Delivery.Accepted; return true; });
        if (backendAlsoRestarted) await app.Store.Recover();
        await app.Store.ObserveControlService(service.Id, native.Identity with { IncarnationId = Guid.NewGuid().ToString() });
        run = (await app.Store.Coordinations()).Single();
        Assert.Equal("Recovering", run.State); Assert.Null(run.DecisionCommandId);
        Assert.Equal(Delivery.Cancelled, await app.Store.Read(async db => (await db.Commands.FindAsync(decisionId))!.State));
        Assert.Contains("Unknown", (await app.Store.Read(db => db.Events.SingleAsync(x => x.Type == "ControlDecisionAuthorityRetired"))).Payload);
        Assert.Empty(await app.Store.Read(db => db.Commands.Where(x => x.Origin == "coordinator:" + run.Id).ToListAsync()));
        run = await app.Store.ControlCoordination(run.Id, new(run.Revision, "pause"));
        await app.Store.ObserveControlService(service.Id, native.Identity with { IncarnationId = Guid.NewGuid().ToString() });
        Assert.Equal("Paused", (await app.Store.Coordinations()).Single().State);
        Assert.Equal(2, native.CreateCalls);
    }

    [Fact]
    public async Task KnownRejectedCreationCanBeExplicitlyRetriedButUnknownAcknowledgementCannot()
    {
        await using var native = new NativeControlFixture { RejectCreation = true };
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        await TestApp.Wait(async () => (await app.Store.ControlServices()).Single().Sessions.Single().State == Delivery.Failed, "Known rejected creation");
        var binding = (await app.Store.ControlServices()).Single().Sessions.Single();
        native.RejectCreation = false;
        var retry = new RetryControlSessionInput(Guid.NewGuid().ToString(), binding.Revision);
        var response = await owner.PostAsJsonAsync($"/api/v1/control-services/{service.Id}/sessions/{binding.Id}/retry", retry);
        response.EnsureSuccessStatusCode();
        await TestApp.Wait(async () => (await app.Store.ControlServices()).Single().Sessions.Single().State == "Ready", "Retried creation ready");
        response = await owner.PostAsJsonAsync($"/api/v1/control-services/{service.Id}/sessions/{binding.Id}/retry", retry);
        response.EnsureSuccessStatusCode();
        Assert.Equal(2, native.CreationPosts); Assert.Equal(1, native.CreateCalls);

        native.LoseCreationResponse = true; native.HideSessions = true;
        var unknown = await app.Store.CreateControlSession(service.Id, new(Guid.NewGuid().ToString(), "Workgroup", "uncertain", "Uncertain"));
        await TestApp.Wait(async () => await app.Store.Read(db => db.Commands.AnyAsync(x => x.Id == unknown.CreationCommandId && x.State == Delivery.Unknown)), "Uncertain creation");
        await app.Store.EditQueue(unknown.CreationCommandId, "resolveUnknown");
        var current = (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == unknown.Id);
        response = await owner.PostAsJsonAsync($"/api/v1/control-services/{service.Id}/sessions/{unknown.Id}/retry", new RetryControlSessionInput(Guid.NewGuid().ToString(), current.Revision));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        native.HideSessions = false;
        await TestApp.Wait(async () => (await app.Store.ControlServices()).Single().Sessions.Single(x => x.Id == unknown.Id).State == "Ready", "Acknowledged unknown discovered");
        Assert.Equal(3, native.CreationPosts); Assert.Equal(2, native.CreateCalls);
    }

    [Fact]
    public async Task TwoControlTurnsHaveIndependentCapacityWhileThirdWaitsForNativeIdle()
    {
        await using var native = new NativeControlFixture();
        await using var app = new TestApp();
        using var owner = await app.SignIn();
        var service = await Register(app, owner, native);
        var bindings = new List<ControlSessionBinding>();
        for (var index = 0; index < 3; index++)
            bindings.Add(await app.Store.CreateControlSession(service.Id, new(Guid.NewGuid().ToString(), "Workgroup", "group-" + index, "Group " + index, "opencode", "big-pickle")));
        await TestApp.Wait(async () => await app.Store.Read(db => db.Workers.CountAsync(x => x.RuntimeId == service.Id && !x.Stale && x.Activity == "Idle")) == 4, "Four control conversations observed");
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var developer = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(async db => { (await db.Workers.FindAsync(developer.Id))!.Activity = "Active"; return true; });
        foreach (var binding in bindings)
            await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), binding.WorkerId, "Observe the occupied worker", [developer.Id]));
        await app.Store.CoordinationTick();
        using var supervisor = new RuntimeSupervisor(app.Store, null!,
            Microsoft.Extensions.Options.Options.Create(new ControlOptions { GlobalCapacity = 1 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("Claim", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task<CommandRecord?> Claim() => (Task<CommandRecord?>)method.Invoke(supervisor, [service.Id, null])!;
        var claims = await Task.WhenAll(Claim(), Claim(), Claim());
        Assert.Equal(2, claims.Count(x => x is not null));
        Assert.Single(claims, x => x is null);
        var settled = claims.First(x => x is not null)!;
        await app.Store.Write(async db =>
        {
            (await db.Commands.FindAsync(settled.Id))!.State = Delivery.Finished;
            (await db.Workers.FindAsync(settled.WorkerId))!.Activity = "Active";
            return true;
        });
        Assert.Null(await Claim()); // Native activity still occupies the slot after a managed receipt settles.
        await app.Store.Write(async db => { (await db.Workers.FindAsync(settled.WorkerId))!.Activity = "Idle"; return true; });
        Assert.NotNull(await Claim());
        Assert.Equal("Active", await app.Store.Read(async db => (await db.Workers.FindAsync(developer.Id))!.Activity));
        Assert.Equal(0, native.UnexpectedMutations);
    }

    private static async Task<ControlServiceRecord> Register(TestApp app, HttpClient owner, NativeControlFixture native)
    {
        File.WriteAllText(Path.Combine(app.SecretPath, "sidecar-password"), NativeControlFixture.Password);
        var input = new RegisterControlServiceInput(Guid.NewGuid().ToString("N"), "Control", native.Endpoint, native.InstanceId, "sidecar-password");
        var response = await owner.PostAsJsonAsync("/api/v1/control-services", input);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ControlServiceRecord>())!;
    }

    private sealed class NativeControlFixture : IAsyncDisposable
    {
        public const string Password = "sidecar-test-password-0123456789-abcdef";
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<object> sessions = [];
        private readonly Task loop;
        private readonly List<Task> requests = [];
        public string Endpoint { get; }
        public string InstanceId { get; } = Guid.NewGuid().ToString();
        public ControlServiceIdentity Identity { get; set; }
        public string Version { get; set; } = "1.18.29";
        public bool Healthy { get; set; } = true;
        public bool MissingCoreRoute { get; set; }
        public volatile bool HideSessions;
        public bool LoseCreationResponse { get; set; }
        public bool RejectCreation { get; set; }
        public bool RedirectIdentity { get; set; }
        public int CreateCalls, CreationPosts, UnexpectedMutations, RedirectVisits;

        public NativeControlFixture()
        {
            using var port = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            port.Start(); Endpoint = "http://127.0.0.1:" + ((IPEndPoint)port.LocalEndpoint).Port + "/"; port.Stop();
            listener.Prefixes.Add(Endpoint); listener.Start();
            Identity = new(1, InstanceId, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToString("O"), ControlStore.ControlDirectory);
            loop = Run();
        }
        private async Task Run()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(lifetime.Token);
                    requests.Add(Handle(context));
                }
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
        }
        private async Task Handle(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url!.AbsolutePath;
                if (path == "/redirect-target") Interlocked.Increment(ref RedirectVisits);
                if (context.Request.Headers["Authorization"] != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + Password)))
                { context.Response.StatusCode = 401; return; }
                object body;
                if (path == "/file/content")
                {
                    if (RedirectIdentity) { context.Response.Redirect(Endpoint + "redirect-target"); return; }
                    body = new { type = "text", content = Json.Write(Identity) };
                }
                else if (path == "/global/health") body = new { healthy = Healthy, version = Version };
                else if (path == "/doc") body = new { paths = new[] { "/global/event", "/session", "/session/{sessionID}/prompt_async", "/session/{sessionID}/message", "/session/{sessionID}/message/{messageID}", "/session/status", "/provider", "/path" }.Where(x => !MissingCoreRoute || x != "/session").ToDictionary(x => x, _ => new { }) };
                else if (path == "/provider") body = new { connected = new[] { "opencode" }, all = new[] { new { id = "opencode", models = new Dictionary<string, object> { ["big-pickle"] = new { name = "Big Pickle" } } } } };
                else if (path == "/path") body = new { directory = ControlStore.ControlDirectory };
                else if (path == "/session" && context.Request.HttpMethod == "POST")
                {
                    Interlocked.Increment(ref CreationPosts);
                    if (RejectCreation) { context.Response.StatusCode = 401; return; }
                    using var request = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: lifetime.Token);
                    var session = new { id = "ses_" + Guid.NewGuid().ToString("N"), title = request.RootElement.GetProperty("title").GetString(), directory = ControlStore.ControlDirectory, time = new { created = 1L } };
                    lock (sessions) sessions.Add(session);
                    Interlocked.Increment(ref CreateCalls);
                    if (LoseCreationResponse) { context.Response.Abort(); return; }
                    body = session;
                }
                else if (path == "/session") { lock (sessions) body = HideSessions ? Array.Empty<object>() : sessions.ToArray(); }
                else if (path == "/session/status") body = new { };
                else if (path is "/permission" or "/question" || path.EndsWith("/message", StringComparison.Ordinal)) body = Array.Empty<object>();
                else if (path.StartsWith("/session/", StringComparison.Ordinal) && context.Request.HttpMethod == "GET")
                {
                    lock (sessions) body = sessions.SingleOrDefault(x => JsonSerializer.SerializeToElement(x).GetProperty("id").GetString() == path[9..])!;
                    if (body is null) { context.Response.StatusCode = 404; return; }
                }
                else if (path == "/global/event")
                {
                    context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true;
                    while (!lifetime.IsCancellationRequested)
                    {
                        await context.Response.OutputStream.WriteAsync("data: {\"type\":\"server.heartbeat\"}\n\n"u8.ToArray(), lifetime.Token);
                        await context.Response.OutputStream.FlushAsync(lifetime.Token);
                        await Task.Delay(100, lifetime.Token);
                    }
                    return;
                }
                else { if (context.Request.HttpMethod != "GET") Interlocked.Increment(ref UnexpectedMutations); context.Response.StatusCode = 404; return; }
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, body, Json.Options, lifetime.Token);
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            catch (HttpListenerException) { }
            catch (IOException) { }
            finally { try { context.Response.Close(); } catch (ObjectDisposedException) { } }
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync(); listener.Close(); await loop; await Task.WhenAll(requests); lifetime.Dispose();
        }
    }
}
