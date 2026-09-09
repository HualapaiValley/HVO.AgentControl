using System.Net;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task AuthenticationCsrfAndDurableRegistrationSurviveRestart()
    {
        string data, secrets;
        string id;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/snapshot")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/hubs/activity/negotiate?negotiateVersion=1", null)).StatusCode);
            foreach (var path in new[] { "/", "/runtimes", "/workers" })
                Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(path)).StatusCode);
            using var client = await app.SignIn();
            var runtime = Profile(); id = runtime.Id;
            var response = await client.PostAsJsonAsync("/api/v1/runtimes", runtime);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(TestApp.OwnerPassword, await client.GetStringAsync("/api/v1/snapshot"));
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/runtimes", Profile())).StatusCode);
        }
        await using var restarted = new TestApp(data, secrets);
        Assert.Contains((await restarted.Store.Snapshot()).Runtimes, x => x.Id == id);
    }

    [Fact]
    public async Task ConcurrentRetryRecordsOneCommandAndStaleRevisionIsRejected()
    {
        await using var app = new TestApp();
        var worker = await SeedWorker(app.Store);
        var input = new PromptInput(Guid.NewGuid().ToString(), "quotes ' \" ; $(touch forbidden)\nfollow-up", worker.Revision,
            RiskLevel: TaskRiskLevels.Low);
        var results = await Task.WhenAll(app.Store.Prompt(worker.Id, input), app.Store.Prompt(worker.Id, input));
        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Single((await app.Store.Snapshot()).Commands);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.Prompt(worker.Id, input with { Text = "different" }));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.Prompt(worker.Id, input with { Id = Guid.NewGuid().ToString() }));
        await app.Store.EditQueue(input.Id, "cancel");
        Assert.Equal(Delivery.Cancelled, (await app.Store.Detail(worker.Id)).Commands.Single().State);
    }

    [Fact]
    public async Task CrashClaimBecomesUnknownAndDatabaseFailureNeverAcknowledgesCommand()
    {
        await using var app = new TestApp();
        var worker = await SeedWorker(app.Store);
        var command = await app.Store.Prompt(worker.Id,
            new(Guid.NewGuid().ToString(), "task", 0, RiskLevel: TaskRiskLevels.Low));
        await app.Store.Write(async db => { (await db.Commands.FindAsync(command.Id))!.State = Delivery.Dispatching; return true; });
        await app.Store.Recover();
        Assert.Equal(Delivery.Unknown, (await app.Store.Detail(worker.Id)).Commands.Single().State);
        var factory = app.Services.GetRequiredService<IDbContextFactory<ControlDb>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_command BEFORE INSERT ON Commands BEGIN SELECT RAISE(ABORT, 'fixture database failure'); END;");
        var next = new PromptInput(Guid.NewGuid().ToString(), "must not be acknowledged", 1, RiskLevel: TaskRiskLevels.Low);
        await Assert.ThrowsAsync<DbUpdateException>(() => app.Store.Prompt(worker.Id, next));
        Assert.False(await app.Store.Read(storeDb => storeDb.Commands.AnyAsync(x => x.Id == next.Id)));
    }

    [Fact]
    public async Task PendingReplyIdentityAndWorkspaceBoundaryAreEnforced()
    {
        await using var app = new TestApp();
        var worker = await SeedWorker(app.Store);
        var pending = new PendingRequest { WorkerId = worker.Id, Kind = "permission", NativeId = "per_fixture", Json = "{}" };
        await app.Store.Write(db => { db.Requests.Add(pending); return Task.FromResult(true); });
        var input = new ReplyInput(Guid.NewGuid().ToString(), pending.Id, "once", null);
        Assert.Equal((await app.Store.Reply(input)).Id, (await app.Store.Reply(input)).Id);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.Reply(input with { Id = Guid.NewGuid().ToString() }));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.EditQueue(input.Id, "cancel"));
        await app.Store.Write(async db => { (await db.Commands.FindAsync(input.Id))!.State = Delivery.Unknown; return true; });
        await app.Store.EditQueue(input.Id, "resolveUnknown");
        Assert.Equal(Delivery.Queued, (await app.Store.Reply(input with { Id = Guid.NewGuid().ToString() })).State);
        var profile = Profile(); profile.AllowedRoots = "/";
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntime(profile));
        using var held = new ReplicaLock(Path.Combine(app.DataPath, "duplicate"));
        Assert.Throws<InvalidOperationException>(() => new ReplicaLock(Path.Combine(app.DataPath, "duplicate")));
    }

    [Fact]
    public async Task UnchangedRemoteRootAliasAllowsProfileEditButChangedRootOrHostIsRejected()
    {
        await using var app = new TestApp();
        var profile = Profile();
        profile.AllowedRoots = "/home/agent/workspaces/.";
        var runtime = await app.Store.SaveRuntime(profile);
        await app.Store.Write(db =>
        {
            db.Workers.Add(new WorkerRecord
            {
                RuntimeId = runtime.Id,
                ManagedServerId = runtime.ManagedServerId,
                NativeSessionId = "ses_canonical_root",
                Directory = "/home/agent/workspaces/a",
                Name = "Canonical worker"
            });
            return Task.FromResult(true);
        });

        runtime.Name = "Renamed runtime";
        runtime = await app.Store.SaveRuntime(runtime);
        Assert.Equal("Renamed runtime", runtime.Name);

        var changedRoot = Json.Read<RuntimeRecord>(Json.Write(runtime));
        changedRoot.AllowedRoots = "/home/agent/workspaces/b";
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntime(changedRoot));

        var changedHost = Json.Read<RuntimeRecord>(Json.Write(runtime));
        changedHost.Host = "127.0.0.2";
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntime(changedHost));

        var changedHostKey = Json.Read<RuntimeRecord>(Json.Write(runtime));
        changedHostKey.HostKeySha256 = "SHA256:" + new string('B', 43);
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SaveRuntime(changedHostKey));
    }

    [Fact]
    public async Task RuntimeApiPreservesUnchangedRemoteSymlinkAliasButRejectsChangedRoot()
    {
        await using var app = new TestApp();
        var profile = Profile();
        profile.AllowedRoots = "/home/agent/workspace-link";
        var runtime = await app.Store.SaveRuntime(profile);
        await app.Store.Write(db =>
        {
            db.Workers.Add(new WorkerRecord
            {
                RuntimeId = runtime.Id,
                ManagedServerId = runtime.ManagedServerId,
                NativeSessionId = "ses_symlink_root",
                Directory = "/home/agent/workspaces/a",
                Name = "Canonical worker"
            });
            return Task.FromResult(true);
        });
        using var owner = await app.SignIn();

        runtime.Name = "API rename";
        var saved = await owner.PostAsJsonAsync("/api/v1/runtimes", runtime);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        runtime = (await saved.Content.ReadFromJsonAsync<RuntimeRecord>())!;
        Assert.Equal("API rename", runtime.Name);

        runtime.AllowedRoots = "/home/agent/workspaces/b";
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/runtimes", runtime)).StatusCode);
    }

    internal static RuntimeRecord Profile() => new()
    {
        Name = "Test",
        Host = "127.0.0.1",
        Username = "agent",
        HostKeySha256 = "SHA256:" + new string('A', 43),
        CredentialReference = "fixture-key",
        ServerPasswordReference = "server-password",
        StateDirectory = "/home/agent/state",
        AllowedRoots = "/home/agent/workspaces"
    };
    internal static async Task<WorkerRecord> SeedWorker(ControlStore store)
    {
        var runtime = await store.SaveRuntime(Profile());
        var worker = new WorkerRecord { RuntimeId = runtime.Id, ManagedServerId = runtime.ManagedServerId, NativeSessionId = "ses_fixture", Directory = "/home/agent/workspaces/a", Name = "Worker" };
        await store.Write(db => { db.Workers.Add(worker); return Task.FromResult(true); });
        return worker;
    }
}
