using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using HVO.AgentControl.Core;
using HVO.AgentControl.GitHub;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WatchdogStatusTests
{
    [Fact]
    public async Task MonitoringReadDoesNotBlockHeartbeatWrites()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var blocker = new PauseWatchdogRead();
        var connection = await app.Store.Read(db => Task.FromResult(db.Database.GetConnectionString()!));
        var factory = new PooledDbContextFactory<ControlDb>(new DbContextOptionsBuilder<ControlDb>()
            .UseSqlite(connection).AddInterceptors(blocker).Options);
        var store = new ControlStore(factory, app.Services.GetRequiredService<IOptions<ControlOptions>>(), app.Services.GetRequiredService<Secrets>());
        var reading = store.WatchdogStatus();
        Task<bool>? writing = null;
        try
        {
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            writing = Task.Run(() => app.Store.Write(async db =>
            {
                (await db.Workers.FindAsync(worker.Id))!.LastObservedAt = 123;
                return true;
            }));
            Assert.True(await writing.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            blocker.Release.TrySetResult();
            await reading;
            if (writing is not null) await writing;
        }
    }

    private sealed class PauseWatchdogRead : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Commands\"", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task CompactStatusRetainsOldUnresolvedWorkWithoutDownloadingEvidence()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var evidence = new string('x', 3_000_000);
        await app.Store.Write(async db =>
        {
            (await db.Workers.FindAsync(worker.Id))!.ModelsJson = evidence;
            (await db.Runtimes.FindAsync(worker.RuntimeId))!.ModelsJson = evidence;
            db.Commands.Add(new CommandRecord
            {
                Id = "old-unresolved",
                RuntimeId = worker.RuntimeId,
                WorkerId = worker.Id,
                Kind = "Prompt",
                State = Delivery.Unknown,
                CreatedAt = 1,
                Payload = evidence,
                ResultJson = evidence
            });
            for (var i = 0; i < 510; i++)
                db.Commands.Add(new CommandRecord { Id = "finished-" + i, RuntimeId = worker.RuntimeId, WorkerId = worker.Id, State = Delivery.Finished });
            db.CoordinationRuns.Add(new CoordinationRun
            {
                Id = "run",
                CoordinatorWorkerId = worker.Id,
                WorkerIdsJson = Json.Write(new[] { worker.Id }),
                Instruction = evidence,
                InputJson = Json.Write(new
                {
                    evidence,
                    repair = new { attempt = 2 },
                    recovery = new { attempt = 3 },
                    nativeFailure = new { held = true, category = "Exhausted", status = 429 }
                })
            });
            db.Requests.Add(new PendingRequest { Id = "approval", WorkerId = worker.Id, Json = evidence });
            db.GitHubAccess.Add(new GitHubAccess { Id = worker.RuntimeId, State = "Blocked", Detail = evidence, ExpiresAt = 1 });
            return true;
        });
        using var client = await app.SignIn();
        var response = await client.GetStringAsync("/api/v1/watchdog");
        var status = Json.Read<WatchdogStatus>(response);
        Assert.True(status.Complete);
        Assert.Equal(1, status.Version);
        Assert.InRange(ControlStore.Now - status.ObservedAt, 0, 10000);
        Assert.True(response.Length < 10000);
        Assert.DoesNotContain("payload", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resultJson", response, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old-unresolved", Assert.Single(status.Snapshot.Commands).Id);
        Assert.Equal("approval", Assert.Single(status.Snapshot.Requests).Id);
        Assert.Equal("Blocked", Assert.Single(status.Snapshot.GitHubAccess).State);
        var run = Assert.Single(status.Coordinations);
        Assert.Contains("\"attempt\":2", run.InputJson);
        Assert.Contains("\"attempt\":3", run.InputJson);
        Assert.Contains("\"category\":\"Exhausted\"", run.InputJson);
        Assert.DoesNotContain("evidence", run.InputJson);
    }

    [Fact]
    public async Task CapacityOverflowAndMalformedLedgerCannotClaimCompleteObservation()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(db =>
        {
            for (var i = 0; i < 513; i++)
                db.Requests.Add(new PendingRequest { Id = "request-" + i, WorkerId = worker.Id, NativeId = "native-" + i });
            db.CoordinationRuns.Add(new CoordinationRun { Id = "bad-context", CoordinatorWorkerId = worker.Id, InputJson = "malformed" });
            return Task.FromResult(true);
        });
        var status = await app.Store.WatchdogStatus();
        Assert.False(status.Complete);
        Assert.Equal(513, status.Snapshot.Requests.Count);
        Assert.Single(status.Coordinations);
        await app.Store.Write(db => { db.Requests.RemoveRange(db.Requests); return Task.FromResult(true); });
        Assert.False((await app.Store.WatchdogStatus()).Complete);
        await app.Store.Write(async db => { (await db.CoordinationRuns.FindAsync("bad-context"))!.InputJson = "{}"; return true; });
        Assert.True((await app.Store.WatchdogStatus()).Complete);
    }

    [Fact]
    public async Task RecentProviderFailuresRemainVisibleUntilNewerWorkSupersedesThem()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var now = ControlStore.Now;
        await app.Store.Write(db =>
        {
            db.Commands.Add(new CommandRecord
            {
                Id = "failed",
                RuntimeId = worker.RuntimeId,
                WorkerId = worker.Id,
                Kind = "Prompt",
                State = Delivery.Failed,
                CreatedAt = now - 1000,
                ResultJson = Json.Write(new
                {
                    messages = new[] { new { info = new { error = new { name = "ContextOverflowError", message = "SECRET" } },
                    parts = new[] { new { type = "text", text = "SECRET" } } } }
                })
            });
            db.Add(new ProviderFailureReceipt { Id = "failure", CommandId = "failed", Category = "AuthenticationRequired", Status = 401, ObservedAt = now });
            db.Add(new ProviderFailureReceipt { Id = "old-failure", CommandId = "failed", Category = "Exhausted", ObservedAt = now - 1_900_000 });
            return Task.FromResult(true);
        });
        var status = await app.Store.WatchdogStatus();
        Assert.Equal(401, Assert.Single(status.Snapshot.ProviderFailures).Status);
        Assert.Equal("ContextOverflowError", Assert.Single(status.Snapshot.NativeErrors).Name);
        Assert.DoesNotContain("SECRET", Json.Write(status));
        await app.Store.Write(db =>
        {
            db.Commands.Add(new CommandRecord { Id = "next", RuntimeId = worker.RuntimeId, WorkerId = worker.Id, Kind = "Prompt", State = Delivery.Running, CreatedAt = now });
            return Task.FromResult(true);
        });
        Assert.Empty((await app.Store.WatchdogStatus()).Snapshot.ProviderFailures);
        Assert.Empty((await app.Store.WatchdogStatus()).Snapshot.NativeErrors);
    }

    [Fact]
    public async Task StatusRequiresOwnerAuthenticationAndPreservesPause()
    {
        await using var app = new TestApp();
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/watchdog")).StatusCode);
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(db =>
        {
            db.CoordinationRuns.Add(new CoordinationRun { Id = "paused", CoordinatorWorkerId = worker.Id, State = "Paused" });
            db.CoordinationRuns.Add(new CoordinationRun { Id = "completed", CoordinatorWorkerId = worker.Id, State = "Completed" });
            return Task.FromResult(true);
        });
        using var client = await app.SignIn();
        var status = await client.GetFromJsonAsync<WatchdogStatus>("/api/v1/watchdog");
        Assert.Equal("Paused", Assert.Single(status!.Coordinations).State);
    }
}
