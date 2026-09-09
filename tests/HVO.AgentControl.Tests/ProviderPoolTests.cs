using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ProviderPoolTests
{
    private static JsonElement Error(int status, string body = "", string retry = "") => JsonSerializer.SerializeToElement(new
    {
        name = "APIError",
        data = new { statusCode = status, responseBody = body, responseHeaders = new Dictionary<string, string> { ["Retry-After"] = retry }, message = "secret-do-not-retain" }
    });

    [Fact]
    public void ClassifiesOnlyStructuredProviderEvidenceAndHonorsRetryAfter()
    {
        const long now = 100000;
        Assert.Equal(new ProviderFailure("Throttled", 429, 220000), ProviderFailure.Parse(Error(429, "quota exhausted in untrusted prose", "120"), now));
        Assert.Equal("Exhausted", ProviderFailure.Parse(Error(429, "{\"error\":{\"code\":\"insufficient_quota\"}}"), now)!.Category);
        Assert.Equal("AuthenticationRequired", ProviderFailure.Parse(Error(401), now)!.Category);
        Assert.Equal("Unavailable", ProviderFailure.Parse(Error(503), now)!.Category);
        Assert.Null(ProviderFailure.Parse(JsonSerializer.SerializeToElement(new { name = "StructuredOutputError" }), now));
        Assert.Null(ProviderFailure.Parse(JsonSerializer.SerializeToElement(new { name = "APIError", data = new { statusCode = "429" } }), now));
        Assert.Null(ProviderFailure.Parse(Error(400, "not JSON"), now));
        var date = DateTimeOffset.FromUnixTimeMilliseconds(now + 60000).ToString("R");
        Assert.Equal(now + 60000, ProviderFailure.Parse(Error(429, retry: date), now)!.RetryAt);
    }

    private static CommandRecord Command(WorkerRecord worker, string provider = "opencode-go") => new()
    {
        Id = Guid.NewGuid().ToString(),
        RuntimeId = worker.RuntimeId,
        WorkerId = worker.Id,
        Kind = "Prompt",
        State = Delivery.Queued,
        Payload = Json.Write(new PromptInput(Guid.NewGuid().ToString(), "Do not replay effects", 0, provider, "model",
            RiskLevel: TaskRiskLevels.Low, RiskPolicyVersion: "risk-floor-v1", RiskRouteMaximum: TaskRiskLevels.Low)),
        ProviderPoolId = "provider:" + provider
    };

    private static void ExternalReady(ControlDb db, WorkerRecord worker) => db.Set<ProviderReadinessReceipt>().Add(new()
    {
        Id = ProviderKeyService.ProviderId + ":" + worker.RuntimeId,
        RuntimeId = worker.RuntimeId,
        ProviderId = ProviderKeyService.ProviderId,
        State = "Ready",
        Detail = "External canary evidence recorded."
    });

    private static JsonElement RetryStatus(int attempt, string provider, long next) => JsonSerializer.SerializeToElement(new
    {
        type = "retry",
        attempt,
        message = "five-hour quota reset delay; private URL must not persist",
        action = new
        {
            reason = "account_rate_limit",
            provider,
            title = "Account rate limit",
            message = "free usage exceeded; secret details must not persist",
            label = "Wait",
            link = "https://private.example/reset"
        },
        next
    });

    private static NativeSnapshot RetrySnapshot(WorkerRecord worker, CommandRecord command, JsonElement status) => new(
        JsonSerializer.SerializeToElement(new { time = new { created = 1L } }),
        [JsonSerializer.SerializeToElement(new
        {
            info = new { id = command.NativeMessageId, role = "user", sessionID = worker.NativeSessionId, time = new { created = 1L } },
            parts = Array.Empty<object>()
        })],
        "retry", status, [], []);

    private static Task<bool> Reconcile(TestApp app, WorkerRecord worker, NativeSnapshot snapshot)
    {
        var supervisor = new HVO.AgentControl.Services.RuntimeSupervisor(app.Store, null!,
            Microsoft.Extensions.Options.Options.Create(new ControlOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HVO.AgentControl.Services.RuntimeSupervisor>.Instance);
        var method = supervisor.GetType().GetMethod("Reconcile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task<bool>)method.Invoke(supervisor, [worker.Id, snapshot])!;
    }

    private static Task<bool> Complete(TestApp app, string commandId, string state)
    {
        var supervisor = new RuntimeSupervisor(app.Store, null!,
            Microsoft.Extensions.Options.Options.Create(new ControlOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RuntimeSupervisor>.Instance);
        var method = supervisor.GetType().GetMethod("Complete", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task<bool>)method.Invoke(supervisor, [commandId, state, "fixture transition"])!;
    }

    private static NativeSnapshot ErrorSnapshot(WorkerRecord worker, CommandRecord command) => new(
        JsonSerializer.SerializeToElement(new { }),
        [
            JsonSerializer.SerializeToElement(new
            {
                info = new { id = command.NativeMessageId, role = "user", sessionID = worker.NativeSessionId, time = new { created = 1L } },
                parts = Array.Empty<object>()
            }),
            JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = "msg_provider_error", role = "assistant", parentID = command.NativeMessageId, sessionID = worker.NativeSessionId,
                    time = new { created = 2L, completed = 3L },
                    error = new { name = "APIError", data = new { statusCode = 429, responseBody = "{\"error\":{\"code\":\"insufficient_quota\"}}" } }
                },
                parts = Array.Empty<object>()
            })
        ], "idle", JsonSerializer.SerializeToElement(new { }), [], []);

    private static async Task<CommandRecord> AcceptedPrompt(TestApp app, WorkerRecord worker)
    {
        var command = await app.Store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), "Do not replay effects", worker.Revision,
            "opencode-go", "model", RiskLevel: TaskRiskLevels.Low));
        return await app.Store.Write(async db =>
        {
            (await db.Workers.FindAsync(worker.Id))!.ModelsJson = Json.Write(new List<ModelChoice>
                { new("openai", "luna", "Luna"), new("opencode-go", "model", "Original") });
            var saved = (await db.Commands.FindAsync(command.Id))!;
            saved.State = Delivery.Accepted;
            saved.NativeMessageId = "msg_user";
            saved.ProviderPoolId = "provider:opencode-go";
            return saved;
        });
    }

    [Fact]
    public void NativeRetryParserRequiresStructuredAccountLimitAndDoesNotTreatNextAsReset()
    {
        var retry = NativeRetryFailure.Parse(RetryStatus(7, "opencode-go", 123456));
        Assert.NotNull(retry);
        Assert.Equal(7, retry.Attempt);
        Assert.Equal("opencode-go", retry.ProviderId);
        Assert.Equal(123456, retry.Next);
        Assert.Equal("Exhausted", retry.Failure.Category);
        Assert.Null(retry.Failure.RetryAt);
        var genericRetry = JsonSerializer.SerializeToElement(new { type = "retry", attempt = 8, message = "free usage exceeded", next = 123456 });
        Assert.Null(NativeRetryFailure.Parse(genericRetry));
    }

    [Fact]
    public async Task NativeRetryStatusBlocksPinnedPoolOnceAndSurvivesRestartWithoutReplay()
    {
        string data, secrets;
        await using (var app = new TestApp())
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            worker.ProviderId = "openai"; // The prompt override, not the current worker default, owns the pool.
            var command = Command(worker, "opencode-go");
            command.NativeMessageId = "msg_retry";
            command.State = Delivery.Accepted;
            await app.Store.Write(async db =>
            {
                (await db.Workers.FindAsync(worker.Id))!.ProviderId = worker.ProviderId;
                db.Commands.Add(command);
                return true;
            });
            var snapshot = RetrySnapshot(worker, command, RetryStatus(3, "opencode-go", 987654));
            await Reconcile(app, worker, snapshot);
            await Reconcile(app, worker, snapshot);
            var pool = Assert.Single(await app.Store.ProviderPools());
            Assert.Equal("provider:opencode-go", pool.Id);
            Assert.Equal("Exhausted", pool.State);
            Assert.Null(pool.RetryAt);
            Assert.Equal(1, pool.ConsecutiveFailures);
            var receipts = await app.Store.Read(db => db.Set<ProviderFailureReceipt>().ToListAsync());
            Assert.Single(receipts);
            Assert.Equal(command.Id + ":retry:3", receipts[0].Id);
            await app.Store.Write(async db =>
            {
                var peer = new WorkerRecord { ProviderId = "opencode-go" };
                Assert.False(await ControlStore.ProviderDispatchAllowed(db, peer, Command(peer, "opencode-go")));
                Assert.True(await ControlStore.ProviderDispatchAllowed(db, peer, Command(peer, "openai")));
                Assert.Equal(Delivery.Running, (await db.Commands.FindAsync(command.Id))!.State);
                return true;
            });
            var events = await app.Store.Read(db => db.Events.ToListAsync());
            var retryEvent = Assert.Single(events, x => x.Type == "ProviderNativeRetryObserved");
            using var retryPayload = JsonDocument.Parse(retryEvent.Payload);
            Assert.Equal(3, retryPayload.RootElement.GetProperty("attempt").GetInt32());
            Assert.Equal(987654, retryPayload.RootElement.GetProperty("next").GetInt64());
            Assert.False(retryPayload.RootElement.TryGetProperty("reset", out _));
            Assert.DoesNotContain("private.example", Json.Write(events));
            Assert.DoesNotContain("secret details", Json.Write(events));
            data = app.DataPath;
            secrets = app.SecretPath;
        }
        await using var restarted = new TestApp(data, secrets);
        var workerAfterRestart = (await restarted.Store.Snapshot()).Workers.Single();
        var commandAfterRestart = (await restarted.Store.Detail(workerAfterRestart.Id)).Commands.Single();
        var snapshotAfterRestart = RetrySnapshot(workerAfterRestart, commandAfterRestart, RetryStatus(3, "opencode-go", 987654));
        await Reconcile(restarted, workerAfterRestart, snapshotAfterRestart);
        var persistedPool = Assert.Single(await restarted.Store.ProviderPools());
        Assert.Equal("Exhausted", persistedPool.State);
        Assert.Equal(1, persistedPool.ConsecutiveFailures);
        Assert.Single(await restarted.Store.Read(db => db.Set<ProviderFailureReceipt>().ToListAsync()));
        Assert.Equal(Delivery.Running, (await restarted.Store.Detail(workerAfterRestart.Id)).Commands.Single().State);
    }

    [Fact]
    public async Task SharedFailuresAreDeduplicatedPersistedAndDoNotBlockAnotherProvider()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-pool-" + Guid.NewGuid().ToString("N"));
        await using (var app = new TestApp(directory))
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var first = Command(worker);
            await app.Store.Write(db => { db.Commands.Add(first); return Task.FromResult(true); });
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => app.Store.Write(async db =>
            {
                await ControlStore.ObserveProviderFailure(db, worker, first, "msg_failure", ProviderFailure.Parse(Error(429, retry: "180"), ControlStore.Now)!);
                return true;
            })));
            var pool = Assert.Single(await app.Store.ProviderPools());
            Assert.Equal(1, pool.ConsecutiveFailures);
            Assert.True(pool.RetryAt >= pool.ObservedAt + 180000);
            await app.Store.Write(async db =>
            {
                var otherWorker = new WorkerRecord { RuntimeId = "other-runtime", ProviderId = "opencode-go" };
                var waiting = Command(otherWorker);
                ExternalReady(db, otherWorker);
                Assert.False(await ControlStore.ProviderDispatchAllowed(db, otherWorker, waiting));
                Assert.Contains("Remaining allowance unknown", waiting.Detail);
                Assert.True(await ControlStore.ProviderDispatchAllowed(db, otherWorker, Command(otherWorker, "openai")));
                return true;
            });
            var receipts = await app.Store.Read(db => db.Set<ProviderFailureReceipt>().ToListAsync());
            Assert.Single(receipts);
            Assert.DoesNotContain("secret-do-not-retain", Json.Write(receipts));
            Assert.DoesNotContain("secret-do-not-retain", Json.Write(await app.Store.Read(db => db.Events.ToListAsync())));
        }
        await using var restarted = new TestApp(directory);
        Assert.Equal("Throttled", Assert.Single(await restarted.Store.ProviderPools()).State);
    }

    [Fact]
    public async Task ExpiredCooldownAdmitsOnlyOneRecoveryAndNeverReplaysFailedCommand()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var failed = Command(worker); failed.State = Delivery.Failed;
        var next = Command(worker);
        var another = Command(worker);
        await app.Store.Write(async db =>
        {
            ExternalReady(db, worker);
            db.Commands.AddRange(failed, next, another);
            await ControlStore.ObserveProviderFailure(db, worker, failed, "msg_failed", new("Throttled", 429, null));
            (await db.Set<ProviderPool>().FindAsync(failed.ProviderPoolId))!.RetryAt = ControlStore.Now - 1;
            return true;
        });
        await app.Store.Write(async db =>
        {
            Assert.True(await ControlStore.ProviderDispatchAllowed(db, worker, next));
            Assert.False(await ControlStore.ProviderDispatchAllowed(db, worker, another));
            Assert.Equal(Delivery.Failed, (await db.Commands.FindAsync(failed.Id))!.State);
            await ControlStore.ObserveProviderCompletion(db, next, true);
            Assert.True(await ControlStore.ProviderDispatchAllowed(db, worker, another));
            return true;
        });
    }

    [Fact]
    public async Task LateFailureDoesNotReplaceAnUnresolvedRecoveryLease()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var failed = Command(worker, "openai"); failed.State = Delivery.Failed;
        var recovery = Command(worker, "openai"); recovery.State = Delivery.Running;
        var late = Command(worker, "openai"); late.State = Delivery.Running;
        var waiting = Command(worker, "openai");
        await app.Store.Write(async db =>
        {
            db.Commands.AddRange(failed, recovery, late, waiting);
            await ControlStore.ObserveProviderFailure(db, worker, failed, "first", new("Throttled", 429, null));
            var pool = (await db.Set<ProviderPool>().FindAsync(failed.ProviderPoolId))!;
            pool.RetryAt = ControlStore.Now - 1;
            Assert.True(await ControlStore.ProviderDispatchAllowed(db, worker, recovery));
            await ControlStore.ObserveProviderFailure(db, worker, late, "late", new("Throttled", 429, null));
            Assert.Equal(recovery.Id, pool.RecoveryCommandId);
            Assert.Equal("Throttled", pool.State);
            Assert.False(await ControlStore.ProviderDispatchAllowed(db, worker, waiting));
            return true;
        });
        Assert.Equal(2, await app.Store.Read(db => db.Set<ProviderFailureReceipt>().CountAsync()));
    }

    [Fact]
    public async Task CancellingKnownUnsentRecoverySettlesLeaseWithoutClaimingAvailability()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var failed = Command(worker, "openai"); failed.State = Delivery.Failed;
        var recovery = Command(worker, "openai");
        await app.Store.Write(async db =>
        {
            db.Commands.AddRange(failed, recovery);
            await ControlStore.ObserveProviderFailure(db, worker, failed, "first", new("Throttled", 429, null));
            (await db.Set<ProviderPool>().FindAsync(failed.ProviderPoolId))!.RetryAt = ControlStore.Now - 1;
            Assert.True(await ControlStore.ProviderDispatchAllowed(db, worker, recovery));
            return true;
        });
        await app.Store.EditQueue(recovery.Id, "cancel");
        var pool = Assert.Single(await app.Store.ProviderPools());
        Assert.Equal("RecoveryRequired", pool.State);
        Assert.Empty(pool.RecoveryCommandId);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.EditQueue(recovery.Id, "cancel"));
        Assert.Equal("RecoveryRequired", Assert.Single(await app.Store.ProviderPools()).State);
    }

    [Theory]
    [InlineData("Exhausted")]
    [InlineData("AuthenticationRequired")]
    public async Task ManualHoldSurvivesRestartAndSuccessfulReservedAttempt(string category)
    {
        string data, secrets, recoveryId;
        await using (var app = new TestApp())
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var failed = Command(worker, "openai"); failed.State = Delivery.Failed;
            var recovery = Command(worker, "openai");
            var late = Command(worker, "openai"); late.State = Delivery.Running;
            recoveryId = recovery.Id;
            await app.Store.Write(async db =>
            {
                db.Commands.AddRange(failed, recovery, late);
                await ControlStore.ObserveProviderFailure(db, worker, failed, "first", new("Throttled", 429, null));
                var pool = (await db.Set<ProviderPool>().FindAsync(failed.ProviderPoolId))!;
                pool.RetryAt = ControlStore.Now - 1;
                Assert.True(await ControlStore.ProviderDispatchAllowed(db, worker, recovery));
                recovery.State = Delivery.Running;
                await ControlStore.ObserveProviderFailure(db, worker, late, "manual", new(category, 401, null));
                Assert.Equal(recovery.Id, pool.RecoveryCommandId);
                Assert.Equal(category, pool.State);
                return true;
            });
            data = app.DataPath; secrets = app.SecretPath;
        }
        await using var restarted = new TestApp(data, secrets);
        await restarted.Store.Write(async db =>
        {
            var recovery = (await db.Commands.FindAsync(recoveryId))!;
            await ControlStore.ObserveProviderCompletion(db, recovery, true);
            return true;
        });
        var persisted = Assert.Single(await restarted.Store.ProviderPools());
        Assert.Equal(category, persisted.State);
        Assert.Empty(persisted.RecoveryCommandId);
        Assert.Equal(2, await restarted.Store.Read(db => db.Set<ProviderFailureReceipt>().CountAsync()));
    }

    [Fact]
    public async Task BusyPreflightRequeueKeepsReservationAcrossLateFailureAndRestart()
    {
        string data, secrets;
        await using (var app = new TestApp())
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var failed = Command(worker, "openai"); failed.State = Delivery.Failed;
            var recovery = Command(worker, "openai"); recovery.State = Delivery.Dispatching;
            var late = Command(worker, "openai"); late.State = Delivery.Running;
            var peer = Command(worker, "openai");
            await app.Store.Write(async db =>
            {
                db.Commands.AddRange(failed, recovery, late, peer);
                await ControlStore.ObserveProviderFailure(db, worker, failed, "first", new("Throttled", 429, null));
                var pool = (await db.Set<ProviderPool>().FindAsync(failed.ProviderPoolId))!;
                pool.RetryAt = ControlStore.Now - 1;
                Assert.True(await ControlStore.ProviderDispatchAllowed(db, worker, recovery));
                return true;
            });
            Assert.True(await Complete(app, recovery.Id, Delivery.Queued));
            await app.Store.Write(async db =>
            {
                await ControlStore.ObserveProviderFailure(db, worker, late, "late", new("Throttled", 429, ControlStore.Now - 1));
                var pool = (await db.Set<ProviderPool>().FindAsync(failed.ProviderPoolId))!;
                pool.RetryAt = ControlStore.Now - 1;
                Assert.Equal(recovery.Id, pool.RecoveryCommandId);
                Assert.False(await ControlStore.ProviderDispatchAllowed(db, worker, peer));
                Assert.Equal(Delivery.Running, late.State);
                return true;
            });
            data = app.DataPath; secrets = app.SecretPath;
        }
        await using var restarted = new TestApp(data, secrets);
        var poolAfterRestart = Assert.Single(await restarted.Store.ProviderPools());
        Assert.NotEmpty(poolAfterRestart.RecoveryCommandId);
        var workerAfterRestart = (await restarted.Store.Snapshot()).Workers.Single();
        await restarted.Store.Write(async db =>
        {
            Assert.False(await ControlStore.ProviderDispatchAllowed(db, workerAfterRestart, Command(workerAfterRestart, "openai")));
            return true;
        });
    }

    [Fact]
    public async Task AcceptedAbortSettlesReservedAttemptWithoutClaimingRecovery()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var failed = Command(worker, "openai"); failed.State = Delivery.Failed;
        var recovery = Command(worker, "openai");
        var abort = new CommandRecord { Id = Guid.NewGuid().ToString(), RuntimeId = worker.RuntimeId, WorkerId = worker.Id, Kind = "Abort", State = Delivery.Accepted };
        await app.Store.Write(async db =>
        {
            db.Commands.AddRange(failed, recovery, abort);
            await ControlStore.ObserveProviderFailure(db, worker, failed, "first", new("Throttled", 429, null));
            var pool = (await db.Set<ProviderPool>().FindAsync(failed.ProviderPoolId))!;
            pool.RetryAt = ControlStore.Now - 1;
            Assert.True(await ControlStore.ProviderDispatchAllowed(db, worker, recovery));
            recovery.State = Delivery.Accepted; recovery.NativeMessageId = "msg_recovery";
            return true;
        });
        var idle = new NativeSnapshot(JsonSerializer.SerializeToElement(new { }), [], "idle", JsonSerializer.SerializeToElement(new { }), [], []);
        await Reconcile(app, worker, idle);
        await Reconcile(app, worker, idle);
        var pool = Assert.Single(await app.Store.ProviderPools());
        Assert.Equal("RecoveryRequired", pool.State);
        Assert.Empty(pool.RecoveryCommandId);
        Assert.Equal(Delivery.Cancelled, await app.Store.Read(async db => (await db.Commands.FindAsync(recovery.Id))!.State));
        Assert.Equal(Delivery.Finished, await app.Store.Read(async db => (await db.Commands.FindAsync(abort.Id))!.State));
    }

    [Fact]
    public async Task DispatcherBlocksCoordinatorPromptsButKeepsAbortAvailable()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = Command(worker);
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(worker.Id))!;
            saved.Role = SessionRoles.Coordinator; saved.Stale = false; saved.Activity = "Idle";
            db.Commands.Add(command);
            await ControlStore.ObserveProviderFailure(db, saved, command, "quota", new("Exhausted", 429, null));
            return true;
        });
        var supervisor = new HVO.AgentControl.Services.RuntimeSupervisor(app.Store, null!,
            Microsoft.Extensions.Options.Options.Create(new ControlOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HVO.AgentControl.Services.RuntimeSupervisor>.Instance);
        var method = supervisor.GetType().GetMethod("Claim", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task<CommandRecord?> Claim() => (Task<CommandRecord?>)method.Invoke(supervisor, [worker.RuntimeId, null])!;
        Assert.Null(await Claim());
        await app.Store.Write(db =>
        {
            db.Commands.Add(new CommandRecord { Id = "abort-test", RuntimeId = worker.RuntimeId, WorkerId = worker.Id, Kind = "Abort" });
            return Task.FromResult(true);
        });
        Assert.Equal("Abort", (await Claim())!.Kind);
        Assert.Equal(Delivery.Queued, await app.Store.Read(async db => (await db.Commands.FindAsync(command.Id))!.State));
    }

    [Fact]
    public async Task RefreshCompletedRemainsBlockedUntilExternalCanaryRecordsReady()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = Command(worker, "opencode-go");
        await app.Store.Write(async db =>
        {
            db.Set<ProviderReadinessReceipt>().Add(new()
            {
                Id = "opencode-go:" + worker.RuntimeId,
                RuntimeId = worker.RuntimeId,
                ProviderId = "opencode-go",
                State = "RefreshCompleted",
                Detail = "Model access is not yet tested."
            });
            Assert.False(await ControlStore.ProviderDispatchAllowed(db, worker, command));
            var receipt = await db.Set<ProviderReadinessReceipt>().FindAsync("opencode-go:" + worker.RuntimeId);
            receipt!.State = "Ready"; receipt.Detail = "External canary evidence recorded.";
            Assert.True(await ControlStore.ProviderDispatchAllowed(db, worker, command));
            return true;
        });
    }

    [Fact]
    public async Task MissingReadinessReceiptBlocksProviderDispatch()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = Command(worker, "opencode-go");
        await app.Store.Write(async db =>
        {
            Assert.False(await ControlStore.ProviderDispatchAllowed(db, worker, command));
            Assert.Contains("no receipt", command.Detail, StringComparison.OrdinalIgnoreCase);
            return true;
        });
    }

    [Fact]
    public async Task RepeatedFailuresRequireManualRecoveryWithoutExtendingOnDuplicateObservation()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = Command(worker);
        await app.Store.Write(async db =>
        {
            db.Commands.Add(command);
            for (var i = 0; i < 3; i++)
                await ControlStore.ObserveProviderFailure(db, worker, command, "failure-" + i, new("Throttled", 429, null));
            return true;
        });
        var pool = Assert.Single(await app.Store.ProviderPools());
        Assert.Equal("RecoveryRequired", pool.State); Assert.Null(pool.RetryAt);
        Assert.Equal(3, pool.ConsecutiveFailures);
        await app.Store.Write(async db =>
        {
            Assert.False(await ControlStore.ProviderDispatchAllowed(db, worker, Command(worker)));
            return true;
        });
    }

    [Fact]
    public async Task ExhaustionCannotBeClearedByInflightSuccessAndResumeRequiresFreshOwnerVerification()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = Command(worker);
        await app.Store.Write(async db =>
        {
            db.Commands.Add(command);
            await ControlStore.ObserveProviderFailure(db, worker, command, "exhausted", new("Exhausted", 429, null));
            await ControlStore.ObserveProviderFailure(db, worker, command, "transient", new("Throttled", 429, null));
            await ControlStore.ObserveProviderCompletion(db, command, true);
            return true;
        });
        var pool = Assert.Single(await app.Store.ProviderPools());
        Assert.Equal("Exhausted", pool.State); Assert.Null(pool.RetryAt);
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/providers/pools")).StatusCode);
        using var owner = await app.SignIn();
        var url = "/api/v1/providers/pools/" + Uri.EscapeDataString(pool.Id) + "/resume";
        Assert.False((await owner.PostAsJsonAsync(url, new ResumeProviderPool(pool.Revision, false))).IsSuccessStatusCode);
        Assert.False((await owner.PostAsJsonAsync(url, new ResumeProviderPool(pool.Revision - 1, true))).IsSuccessStatusCode);
        (await owner.PostAsJsonAsync(url, new ResumeProviderPool(pool.Revision, true))).EnsureSuccessStatusCode();
        Assert.Equal("Available", Assert.Single(await app.Store.ProviderPools()).State);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.False((await owner.PostAsJsonAsync(url, new ResumeProviderPool(pool.Revision + 1, true))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task SafeFallbackRecommendationIsPersistedIdempotentlyWithoutDispatch()
    {
        string data, secrets, commandId, receiptId;
        await using (var app = new TestApp())
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var command = Command(worker); command.State = Delivery.Finished;
            receiptId = Guid.NewGuid().ToString(); commandId = command.Id;
            await app.Store.Write(async db =>
            {
                var saved = (await db.Workers.FindAsync(worker.Id))!;
                saved.Stale = false; saved.Activity = "Idle";
                saved.ModelsJson = Json.Write(new List<ModelChoice> { new("openai", "luna", "Luna") });
                db.Commands.Add(command);
                db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = worker.Id, Outcome = "Failed" });
                db.Add(new ProviderPool { Id = command.ProviderPoolId, ProviderId = "opencode-go", State = "Exhausted" });
                db.Add(new ProviderFailureReceipt { Id = command.Id + ":native", CommandId = command.Id, PoolId = command.ProviderPoolId, Category = "Exhausted", ObservedAt = ControlStore.Now });
                return true;
            });
            worker = (await app.Store.Snapshot()).Workers.Single(x => x.Id == worker.Id);
            var input = new ProviderFallbackRecommendation(receiptId, command.Id, worker.Revision, "openai", "luna");
            var receipt = await app.Store.RecordFallbackRecommendation(input);
            Assert.Equal("provider:opencode-go", receipt.SourcePoolId);
            Assert.Equal("provider:openai", receipt.TargetPoolId);
            Assert.Equal(command.Id + ":native", receipt.SourceFailureReceiptId);
            Assert.Equal("Exhausted", receipt.SourceFailureCategory);
            Assert.Equal(Delivery.Finished, receipt.SourceTerminalState);
            Assert.Equal("Failed", receipt.SourceTaskOutcome);
            Assert.Equal(receipt.Id, (await app.Store.RecordFallbackRecommendation(input)).Id);
            Assert.Single(await app.Store.ProviderFallbacks(command.Id));
            Assert.Single((await app.Store.Snapshot()).Commands);
            Assert.Equal(Delivery.Finished, await app.Store.Read(async db => (await db.Commands.FindAsync(command.Id))!.State));
            data = app.DataPath; secrets = app.SecretPath;
        }
        await using var restarted = new TestApp(data, secrets);
        var workerAfterRestart = (await restarted.Store.Snapshot()).Workers.Single();
        var retry = new ProviderFallbackRecommendation(receiptId, commandId, workerAfterRestart.Revision, "openai", "luna");
        Assert.Equal(receiptId, (await restarted.Store.RecordFallbackRecommendation(retry)).Id);
        Assert.Single(await restarted.Store.ProviderFallbacks(commandId));
    }

    [Fact]
    public async Task FallbackRecommendationRejectsUnsafeRoutesAndUnreconciledWork()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = await AcceptedPrompt(app, worker);
        await Reconcile(app, worker, ErrorSnapshot(worker, command));
        await app.Store.Write(db =>
        {
            db.Add(new ProviderPool { Id = "provider:blocked", ProviderId = "blocked", State = "Exhausted" });
            return Task.FromResult(true);
        });
        worker = (await app.Store.Snapshot()).Workers.Single(x => x.Id == worker.Id);
        Task Recommend(string provider, string model, long revision = -1) => app.Store.RecordFallbackRecommendation(
            new(Guid.NewGuid().ToString(), command.Id, revision < 0 ? worker.Revision : revision, provider, model));
        await Assert.ThrowsAsync<ControlException>(() => Recommend("opencode-go", "model"));
        await Assert.ThrowsAsync<ControlException>(() => Recommend("blocked", "model"));
        await Assert.ThrowsAsync<ControlException>(() => Recommend("openai", "missing"));
        await Assert.ThrowsAsync<ControlException>(() => Recommend("openai", "luna", worker.Revision + 1));
        foreach (var state in new[] { Delivery.Queued, Delivery.Dispatching, Delivery.Running, Delivery.Accepted, Delivery.Unknown, Delivery.Failed })
        {
            await app.Store.Write(async db => { (await db.Commands.FindAsync(command.Id))!.State = state; return true; });
            await Assert.ThrowsAsync<ControlException>(() => Recommend("openai", "luna"));
        }
        Assert.Empty(await app.Store.ProviderFallbacks(command.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciledNativeProviderFailureRetainsProvenanceAcrossRestartAndRetry(bool cancelled)
    {
        string data, secrets, commandId;
        var state = cancelled ? Delivery.Cancelled : Delivery.Finished;
        var outcome = cancelled ? "Cancelled" : "Failed";
        await using (var app = new TestApp())
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var command = await AcceptedPrompt(app, worker); commandId = command.Id;
            var retry = RetrySnapshot(worker, command, RetryStatus(1, "opencode-go", 123456));
            await Reconcile(app, worker, retry);
            var running = (await app.Store.Detail(worker.Id)).Worker;
            await Assert.ThrowsAsync<ControlException>(() => app.Store.RecordFallbackRecommendation(
                new(Guid.NewGuid().ToString(), command.Id, running.Revision, "openai", "luna")));
            if (cancelled)
            {
                var abort = await app.Store.Abort(worker.Id, Guid.NewGuid().ToString());
                await app.Store.Write(async db => { (await db.Commands.FindAsync(abort.Id))!.State = Delivery.Accepted; return true; });
                await Reconcile(app, worker, retry with { Status = "idle", StatusDetail = JsonSerializer.SerializeToElement(new { type = "idle" }) });
                Assert.Equal(Delivery.Finished, (await app.Store.Detail(worker.Id)).Commands.Single(x => x.Id == abort.Id).State);
            }
            else await Reconcile(app, worker, ErrorSnapshot(worker, command));
            var detail = await app.Store.Detail(worker.Id);
            Assert.Equal(state, detail.Commands.Single(x => x.Id == command.Id).State);
            Assert.Equal(outcome, detail.Assignments.Single().Outcome);
            Assert.Empty(await app.Store.ProviderFallbacks(command.Id));
            data = app.DataPath; secrets = app.SecretPath;
        }
        ProviderFallbackRecommendation input;
        string receiptJson;
        await using (var restarted = new TestApp(data, secrets))
        {
            var worker = (await restarted.Store.Snapshot()).Workers.Single();
            await Reconcile(restarted, worker, new(JsonSerializer.SerializeToElement(new { }), [], "idle", JsonSerializer.SerializeToElement(new { }), [], []));
            // Later worker-wide outcome changes must not redefine this terminal assignment.
            await restarted.Store.Write(async db => { (await db.Workers.FindAsync(worker.Id))!.Outcome = "NeedsReview"; return true; });
            worker = (await restarted.Store.Snapshot()).Workers.Single();
            input = new(Guid.NewGuid().ToString(), commandId, worker.Revision, "openai", "luna");
            var receipts = await Task.WhenAll(restarted.Store.RecordFallbackRecommendation(input), restarted.Store.RecordFallbackRecommendation(input));
            var receipt = receipts[0]; receiptJson = Json.Write(receipt);
            Assert.Equal(receiptJson, Json.Write(receipts[1]));
            Assert.Equal(state, receipt.SourceTerminalState);
            Assert.Equal(outcome, receipt.SourceTaskOutcome);
            Assert.Equal("Exhausted", receipt.SourceFailureCategory);
            var failure = await restarted.Store.Read(async db => (await db.Set<ProviderFailureReceipt>().FindAsync(receipt.SourceFailureReceiptId))!);
            Assert.Equal(commandId, failure.CommandId);
            Assert.Equal(receipt.SourcePoolId, failure.PoolId);
            await Assert.ThrowsAsync<ControlException>(() => restarted.Store.RecordFallbackRecommendation(input with { Id = Guid.NewGuid().ToString() }));
            await Assert.ThrowsAsync<ControlException>(() => restarted.Store.RecordFallbackRecommendation(input with { ModelId = "different" }));
            var detail = await restarted.Store.Detail(worker.Id);
            Assert.Equal(cancelled ? 2 : 1, detail.Commands.Count);
            Assert.Equal(state, detail.Commands.Single(x => x.Id == commandId).State);
            var audit = Assert.Single(await restarted.Store.Read(db => db.Events.Where(x => x.Type == "ProviderFallbackRecommended").ToListAsync()));
            Assert.Equal(commandId, audit.CommandId);
            using var auditPayload = JsonDocument.Parse(audit.Payload);
            Assert.Equal(outcome, auditPayload.RootElement.GetProperty("sourceTaskOutcome").GetString());
        }
        await using var replayed = new TestApp(data, secrets);
        Assert.Equal(receiptJson, Json.Write(await replayed.Store.RecordFallbackRecommendation(input)));
        Assert.Single(await replayed.Store.ProviderFallbacks(commandId));
    }

    [Fact]
    public async Task ReconciledAbortFinishesAbortAndCancelsInterruptedPrompt()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var prompt = await AcceptedPrompt(app, worker);
        var abort = new CommandRecord { Id = Guid.NewGuid().ToString(), RuntimeId = worker.RuntimeId, WorkerId = worker.Id, Kind = "Abort", State = Delivery.Accepted };
        await app.Store.Write(db => { db.Commands.Add(abort); return Task.FromResult(true); });
        await Reconcile(app, worker, new NativeSnapshot(JsonSerializer.SerializeToElement(new { }), [], "idle", JsonSerializer.SerializeToElement(new { }), [], []));
        var commands = (await app.Store.Detail(worker.Id)).Commands;
        Assert.Equal(Delivery.Finished, commands.Single(x => x.Id == abort.Id).State);
        Assert.Equal(Delivery.Cancelled, commands.Single(x => x.Id == prompt.Id).State);
        var current = (await app.Store.Detail(worker.Id)).Worker;
        await Assert.ThrowsAsync<ControlException>(() => app.Store.RecordFallbackRecommendation(
            new(Guid.NewGuid().ToString(), prompt.Id, current.Revision, "openai", "luna")));
    }

    [Theory]
    [InlineData("Throttled")]
    [InlineData("Unavailable")]
    [InlineData("AuthenticationRequired")]
    public async Task CancelledPromptWithOnlyNonQuotaFailureCannotRecommendFallback(string category)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var prompt = await AcceptedPrompt(app, worker);
        var abort = await app.Store.Abort(worker.Id, Guid.NewGuid().ToString());
        await app.Store.Write(async db =>
        {
            await ControlStore.ObserveProviderFailure(db, worker, prompt, "native", new(category, null, null));
            (await db.Commands.FindAsync(abort.Id))!.State = Delivery.Accepted;
            return true;
        });
        await Reconcile(app, worker, new(JsonSerializer.SerializeToElement(new { }), [], "idle", JsonSerializer.SerializeToElement(new { }), [], []));
        var detail = await app.Store.Detail(worker.Id);
        Assert.Equal("Cancelled", detail.Assignments.Single().Outcome);
        Assert.Equal(Delivery.Cancelled, detail.Commands.Single(x => x.Id == prompt.Id).State);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.RecordFallbackRecommendation(
            new(Guid.NewGuid().ToString(), prompt.Id, detail.Worker.Revision, "openai", "luna")));
        Assert.Empty(await app.Store.ProviderFallbacks(prompt.Id));
    }

    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Cancelled", false)]
    [InlineData("NeedsReview", false)]
    [InlineData("VerifiedComplete", false)]
    [InlineData("Running", false)]
    public async Task FinishedSourceRequiresItsOwnFailedAssignment(string outcome, bool eligible)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = Command(worker); command.State = Delivery.Finished;
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(worker.Id))!;
            saved.Stale = false; saved.Activity = "Idle";
            saved.ModelsJson = Json.Write(new List<ModelChoice> { new("openai", "luna", "Luna") });
            db.Commands.Add(command);
            db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = worker.Id, Outcome = outcome });
            db.Add(new ProviderPool { Id = command.ProviderPoolId, ProviderId = "opencode-go", State = "Exhausted" });
            db.Add(new ProviderFailureReceipt { Id = command.Id + ":native", CommandId = command.Id, PoolId = command.ProviderPoolId, Category = "Exhausted", ObservedAt = ControlStore.Now });
            return true;
        });
        var current = (await app.Store.Snapshot()).Workers.Single();
        var input = new ProviderFallbackRecommendation(Guid.NewGuid().ToString(), command.Id, current.Revision, "openai", "luna");
        if (eligible)
        {
            var receipt = await app.Store.RecordFallbackRecommendation(input);
            Assert.Equal(Delivery.Finished, receipt.SourceTerminalState);
            Assert.Equal("Failed", receipt.SourceTaskOutcome);
            Assert.Equal(command.Id + ":native", receipt.SourceFailureReceiptId);
        }
        else
        {
            await Assert.ThrowsAsync<ControlException>(() => app.Store.RecordFallbackRecommendation(input));
            Assert.Empty(await app.Store.ProviderFallbacks(command.Id));
        }
    }

    [Fact]
    public async Task FallbackRejectsAProviderHoldRecordedByAnotherCommand()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var source = Command(worker); source.State = Delivery.Finished;
        var other = Command(worker); other.State = Delivery.Failed;
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(worker.Id))!;
            saved.Stale = false; saved.Activity = "Idle"; saved.ModelsJson = Json.Write(new List<ModelChoice> { new("openai", "luna", "Luna") });
            db.Commands.AddRange(source, other);
            db.Assignments.Add(new AssignmentRecord { Id = source.Id, WorkerId = worker.Id, Outcome = "Failed" });
            await ControlStore.ObserveProviderFailure(db, saved, other, "native", new("Exhausted", 429, null));
            return true;
        });
        var current = (await app.Store.Snapshot()).Workers.Single();
        await Assert.ThrowsAsync<ControlException>(() => app.Store.RecordFallbackRecommendation(
            new(Guid.NewGuid().ToString(), source.Id, current.Revision, "openai", "luna")));
    }

    [Fact]
    public async Task NativeRetryFollowedBySuccessfulFinalMustNotRecommendFallback()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = await AcceptedPrompt(app, worker);
        var retry = RetrySnapshot(worker, command, RetryStatus(1, "opencode-go", 123456));
        await Reconcile(app, worker, retry);
        var success = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg_final_success",
                role = "assistant",
                parentID = command.NativeMessageId,
                sessionID = worker.NativeSessionId,
                time = new { created = 2L, completed = 3L },
                finish = "stop"
            },
            parts = new[] { new { type = "text", text = "Task completed successfully after retry" } }
        });
        await Reconcile(app, worker, retry with
        {
            Status = "idle",
            StatusDetail = JsonSerializer.SerializeToElement(new { type = "idle" }),
            Messages = [.. retry.Messages, success]
        });
        var detail = await app.Store.Detail(worker.Id);
        Assert.Equal(Delivery.Finished, detail.Commands.Single().State);
        Assert.Equal("NeedsReview", detail.Assignments.Single().Outcome);
        Assert.Equal("Exhausted", Assert.Single(await app.Store.ProviderPools()).State);
        Assert.Single(await app.Store.Read(db => db.Set<ProviderFailureReceipt>().Where(x => x.CommandId == command.Id).ToListAsync()));
        await app.Store.Write(async db => { (await db.Workers.FindAsync(worker.Id))!.Outcome = "Failed"; return true; });
        await Assert.ThrowsAsync<ControlException>(() => app.Store.RecordFallbackRecommendation(
            new(Guid.NewGuid().ToString(), command.Id, detail.Worker.Revision, "openai", "luna")));
        Assert.Empty(await app.Store.ProviderFallbacks(command.Id));
        Assert.Single((await app.Store.Detail(worker.Id)).Commands);
    }

}
