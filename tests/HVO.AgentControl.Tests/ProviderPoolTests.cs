using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
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
        Payload = Json.Write(new PromptInput(Guid.NewGuid().ToString(), "Do not replay effects", 0, provider, "model")),
        ProviderPoolId = "provider:" + provider
    };

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
            var command = Command(worker); command.State = Delivery.Failed;
            receiptId = Guid.NewGuid().ToString(); commandId = command.Id;
            await app.Store.Write(async db =>
            {
                var saved = (await db.Workers.FindAsync(worker.Id))!;
                saved.Stale = false; saved.Activity = "Idle";
                saved.ModelsJson = Json.Write(new List<ModelChoice> { new("openai", "luna", "Luna") });
                db.Commands.Add(command);
                db.Add(new ProviderPool { Id = command.ProviderPoolId, ProviderId = "opencode-go", State = "Exhausted" });
                return true;
            });
            worker = (await app.Store.Snapshot()).Workers.Single(x => x.Id == worker.Id);
            var input = new ProviderFallbackRecommendation(receiptId, command.Id, worker.Revision, "openai", "luna");
            var receipt = await app.Store.RecordFallbackRecommendation(input);
            Assert.Equal("provider:opencode-go", receipt.SourcePoolId);
            Assert.Equal("provider:openai", receipt.TargetPoolId);
            Assert.Equal(receipt.Id, (await app.Store.RecordFallbackRecommendation(input)).Id);
            Assert.Single(await app.Store.ProviderFallbacks(command.Id));
            Assert.Single((await app.Store.Snapshot()).Commands);
            Assert.Equal(Delivery.Failed, await app.Store.Read(async db => (await db.Commands.FindAsync(command.Id))!.State));
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
        var command = Command(worker); command.State = Delivery.Failed;
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(worker.Id))!;
            saved.Stale = false; saved.Activity = "Idle";
            saved.ModelsJson = Json.Write(new List<ModelChoice> { new("openai", "luna", "Luna"), new("opencode-go", "model", "Original") });
            db.Commands.Add(command);
            db.Add(new ProviderPool { Id = command.ProviderPoolId, ProviderId = "opencode-go", State = "Exhausted" });
            db.Add(new ProviderPool { Id = "provider:blocked", ProviderId = "blocked", State = "Exhausted" });
            return true;
        });
        worker = (await app.Store.Snapshot()).Workers.Single(x => x.Id == worker.Id);
        Task Recommend(string provider, string model, long revision = -1) => app.Store.RecordFallbackRecommendation(
            new(Guid.NewGuid().ToString(), command.Id, revision < 0 ? worker.Revision : revision, provider, model));
        await Assert.ThrowsAsync<ControlException>(() => Recommend("opencode-go", "model"));
        await Assert.ThrowsAsync<ControlException>(() => Recommend("blocked", "model"));
        await Assert.ThrowsAsync<ControlException>(() => Recommend("openai", "missing"));
        await Assert.ThrowsAsync<ControlException>(() => Recommend("openai", "luna", worker.Revision + 1));
        await app.Store.Write(async db => { (await db.Commands.FindAsync(command.Id))!.State = Delivery.Running; return true; });
        await Assert.ThrowsAsync<ControlException>(() => Recommend("openai", "luna"));
        Assert.Empty(await app.Store.ProviderFallbacks(command.Id));
    }
}
