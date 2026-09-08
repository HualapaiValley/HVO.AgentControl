using System.Net;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class InventoryTests
{
    private static string Id() => Guid.NewGuid().ToString("N");
    private static CreateHostInput NewHost() => new(Id(), Id(), "Build host", "PhysicalMachine");
    private static CreateProjectInput NewProject(string repository = "https://github.com/RoySalisbury/HVO.AgentControl.git") => new(Id(), Id(), "AgentControl", repository);

    [Fact]
    public async Task ConcurrentEditsCommitOneRevisionAndOnlyItsReceipt()
    {
        await using var app = new TestApp();
        var host = await app.Store.CreateHost(NewHost());
        var edits = new[] { new UpdateHostInput(Id(), host.Revision, "First"), new UpdateHostInput(Id(), host.Revision, "Second") };
        var tasks = edits.Select(async input =>
        {
            try { await app.Store.UpdateHost(host.Id, input); return true; }
            catch (InventoryException ex) { Assert.Equal("revision_conflict", ex.Code); return false; }
        });
        Assert.Single(await Task.WhenAll(tasks), x => x);
        Assert.Equal(2, (await app.Store.Host(host.Id)).Revision);
        Assert.Equal(2, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
        Assert.Equal(2, await app.Store.Read(db => db.Events.CountAsync(x => x.Type.StartsWith("Inventory"))));
    }

    [Fact]
    public async Task ConcurrentRepeatedCreatesProduceOneHostAndOneEvent()
    {
        await using var app = new TestApp();
        var input = NewHost();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => app.Store.CreateHost(input)));
        Assert.Single(results.Select(x => x.Sequence).Distinct());
        Assert.True(results[0].Sequence > 0);
        Assert.Single((await app.Store.Hosts()).Items);
        Assert.Equal(1, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
        Assert.Equal(1, await app.Store.Read(db => db.Events.CountAsync(x => x.Type == "InventoryCreate")));
    }

    [Fact]
    public async Task ReplaysSurviveRestartAndReturnOriginalResultWithoutUndoingNewerEdits()
    {
        var input = NewProject();
        string data, secrets;
        UpdateProjectInput update;
        ArchiveInventoryInput archive;
        ProjectRecord created, updated;
        await using (var app = new TestApp())
        {
            created = await app.Store.CreateProject(input);
            update = new(Id(), created.Revision, "Renamed", "release/next");
            updated = await app.Store.UpdateProject(created.Id, update);
            archive = new(Id(), updated.Revision);
            await app.Store.ArchiveProject(created.Id, archive);
            data = app.DataPath; secrets = app.SecretPath;
        }
        await using var restarted = new TestApp(data, secrets);
        Assert.Equal(Json.Write(created), Json.Write(await restarted.Store.CreateProject(input)));
        Assert.Equal(Json.Write(updated), Json.Write(await restarted.Store.UpdateProject(created.Id, update)));
        Assert.True((await restarted.Store.ArchiveProject(created.Id, archive)).Archived);
        var current = await restarted.Store.Project(created.Id);
        Assert.Equal(3, current.Revision);
        Assert.True(current.Archived);
        Assert.Equal("Renamed", current.Name);
        Assert.Equal(3, await restarted.Store.Read(db => db.InventoryMutations.CountAsync()));
        Assert.Equal("Project", (await restarted.Store.InventoryMutation(input.RequestId)).ResourceKind);
    }

    [Fact]
    public async Task RequestIdentityCannotBeReusedForDifferentResourceActionOrPayload()
    {
        await using var app = new TestApp();
        var input = NewHost();
        var host = await app.Store.CreateHost(input);
        await Conflict(() => app.Store.CreateHost(input with { Name = "Changed" }), "idempotency_conflict");
        await Conflict(() => app.Store.CreateHost(input with { Id = Id() }), "idempotency_conflict");
        await Conflict(() => app.Store.UpdateHost(host.Id, new(input.RequestId, 1, "Changed")), "idempotency_conflict");
        await Conflict(() => app.Store.CreateProject(NewProject() with { RequestId = input.RequestId }), "idempotency_conflict");
        await Conflict(() => app.Store.CreateHost(input with { RequestId = Id() }), "identity_exists");
        Assert.Equal(1, (await app.Store.Host(host.Id)).Revision);
        Assert.Equal(1, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
    }

    [Fact]
    public async Task CanonicalRepositoryPreventsDuplicateProjectsIncludingArchivedOnes()
    {
        await using var app = new TestApp();
        var project = await app.Store.CreateProject(NewProject());
        Assert.Equal("https://github.com/roysalisbury/hvo.agentcontrol", project.RepositoryUrl);
        await app.Store.ArchiveProject(project.Id, new(Id(), project.Revision));
        Assert.Equal(project.Id, (await app.Store.ProjectByRepository("git@github.com:ROYSALISBURY/hvo.agentcontrol.git")).Id);
        await Conflict(() => app.Store.CreateProject(NewProject("git@github.com:ROYSALISBURY/hvo.agentcontrol.git")), "repository_exists");
        var second = await app.Store.CreateProject(NewProject("https://github.com/RoySalisbury/HVO.SkyMonitor"));
        Assert.NotEqual(project.Id, second.Id);
        Assert.Equal(2, (await app.Store.Projects(includeArchived: true)).Items.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RoySalisbury/HVO.AgentControl")]
    [InlineData("https://gitlab.com/owner/repository")]
    [InlineData("http://github.com/owner/repository")]
    [InlineData("https://token@github.com/owner/repository")]
    [InlineData("https://github.com:443/owner/repository")]
    [InlineData("https://github.com/owner/repository?token=secret")]
    [InlineData("https://github.com/owner/repository#branch")]
    [InlineData("https://github.com/owner/../repository")]
    [InlineData("https://github.com/owner/%2e%2e")]
    [InlineData("https://github.com/owner/repository/tree/main")]
    [InlineData("https://github.com/owner/..")]
    [InlineData(null)]
    public void MissingAmbiguousOrCredentialBearingRepositoryIsRejected(string? repository)
    {
        Assert.Equal("validation", Assert.Throws<InventoryException>(() => ControlStore.CanonicalProjectRepository(repository)).Code);
    }

    [Fact]
    public async Task FailedValidationDoesNotReserveRequestIdOrChangeInventory()
    {
        await using var app = new TestApp();
        var input = NewProject() with { BaseBranch = "main~1" };
        await Assert.ThrowsAsync<InventoryException>(() => app.Store.CreateProject(input));
        Assert.Empty((await app.Store.Projects()).Items);
        Assert.Equal(0, await app.Store.Read(db => db.InventoryMutations.CountAsync()));
        var project = await app.Store.CreateProject(input with { BaseBranch = "main" });
        Assert.Equal(1, project.Revision);
    }

    [Fact]
    public async Task PaginationUsesStableSequenceAndArchiveRetainsDetailAndIdentity()
    {
        await using var app = new TestApp();
        var first = await app.Store.CreateHost(NewHost());
        var second = await app.Store.CreateHost(NewHost());
        var third = await app.Store.CreateHost(NewHost());
        var page = await app.Store.Hosts(take: 1);
        Assert.Equal(first.Id, Assert.Single(page.Items).Id);
        Assert.Equal(first.Sequence, page.NextAfter);
        await app.Store.ArchiveHost(first.Id, new(Id(), first.Revision));
        var remaining = await app.Store.Hosts(after: page.NextAfter!.Value, take: 2);
        Assert.Equal(new[] { second.Id, third.Id }, remaining.Items.Select(x => x.Id));
        Assert.Null(remaining.NextAfter);
        Assert.Equal(2, (await app.Store.Hosts()).Items.Count);
        Assert.Equal(3, (await app.Store.Hosts(includeArchived: true)).Items.Count);
        var archived = await app.Store.Host(first.Id);
        Assert.True(archived.Archived);
        var restored = await app.Store.ArchiveHost(first.Id, new(Id(), archived.Revision, false));
        Assert.False(restored.Archived);
        Assert.Equal(first.Sequence, restored.Sequence);
        await Assert.ThrowsAsync<InventoryException>(() => app.Store.Hosts(take: 101));
        await Assert.ThrowsAsync<InventoryException>(() => app.Store.Projects(after: -1));
    }

    [Fact]
    public async Task AdditiveMigrationPreservesLegacyRuntimeSessionAndCommandWithoutInventingBindings()
    {
        await using var app = new TestApp();
        var database = Path.Combine(app.DataPath, "agentcontrol.db");
        var runtime = new RuntimeRecord { Id = Id(), Name = "Legacy SSH", CredentialReference = "retained-key", DesiredConnected = false };
        var worker = new WorkerRecord
        {
            Id = Id(),
            RuntimeId = runtime.Id,
            ManagedServerId = runtime.ManagedServerId,
            NativeSessionId = "ses_legacy",
            Directory = "/home/agent/workspaces/repo",
            Project = "ambiguous display label"
        };
        var command = new CommandRecord { Id = Id(), RuntimeId = runtime.Id, WorkerId = worker.Id, State = Delivery.Finished, NativeMessageId = "msg_legacy" };
        var options = new DbContextOptionsBuilder<ControlDb>().UseSqlite($"Data Source={database}").Options;
        await using (var db = new ControlDb(options))
        {
            var previous = db.Database.GetMigrations().Last(x => !x.EndsWith("HostProjectInventory", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(previous);
            db.Runtimes.Add(runtime); db.Workers.Add(worker); db.Commands.Add(command);
            db.Messages.Add(new TranscriptMessage { WorkerId = worker.Id, NativeId = "msg_legacy", Role = "user", Json = "{\"legacy\":true}" });
            await db.SaveChangesAsync();
        }
        Assert.Empty((await app.Store.Hosts()).Items);
        Assert.Empty((await app.Store.Projects()).Items);
        var saved = await app.Store.Read(async db => new
        {
            Runtime = await db.Runtimes.FindAsync(runtime.Id),
            Worker = await db.Workers.FindAsync(worker.Id),
            Command = await db.Commands.FindAsync(command.Id),
            Message = await db.Messages.SingleAsync(x => x.WorkerId == worker.Id),
            Pending = await db.Database.GetPendingMigrationsAsync()
        });
        Assert.Equal(runtime.CredentialReference, saved.Runtime!.CredentialReference);
        Assert.Equal(worker.NativeSessionId, saved.Worker!.NativeSessionId);
        Assert.Equal(worker.Directory, saved.Worker.Directory);
        Assert.Equal(worker.Project, saved.Worker.Project);
        Assert.Equal(command.NativeMessageId, saved.Command!.NativeMessageId);
        Assert.Equal("{\"legacy\":true}", saved.Message.Json);
        Assert.Empty(saved.Pending);
    }

    [Theory]
    [InlineData("hosts")]
    [InlineData("projects")]
    public async Task OwnerApiRequiresAuthenticationAndCsrfAndReturnsStableConflicts(string resource)
    {
        await using var app = new TestApp();
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        var route = "/api/v1/" + resource;
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        using var owner = await app.SignIn();
        object input = resource == "hosts" ? NewHost() : NewProject();
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(route, input)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(route, input)).StatusCode);
        using var authorized = await app.SignIn();
        var created = await authorized.PostAsJsonAsync(route, input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.StartsWith(route + "/", created.Headers.Location!.ToString());
        var body = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = body.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await authorized.GetAsync(route + "/" + id)).StatusCode);
        object edit = resource == "hosts" ? new UpdateHostInput(Id(), 9, "Changed") : new UpdateProjectInput(Id(), 9, "Changed", "main");
        var conflict = await authorized.PutAsJsonAsync(route + "/" + id, edit);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("revision_conflict", (await conflict.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, (await authorized.PostAsJsonAsync(route, input)).StatusCode);
        object validEdit = resource == "hosts" ? new UpdateHostInput(Id(), 1, "Changed") : new UpdateProjectInput(Id(), 1, "Changed", "main");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(route + "/" + id, validEdit)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(route + "/" + id, validEdit)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await authorized.PutAsJsonAsync(route + "/" + id, validEdit)).StatusCode);
        var archiveInput = new ArchiveInventoryInput(Id(), 2);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(route + "/" + id + "/archive", archiveInput)).StatusCode);
        var archive = await authorized.PostAsJsonAsync(route + "/" + id + "/archive", archiveInput);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await authorized.GetAsync(route + "/" + Id())).StatusCode);
        var requestId = resource == "hosts" ? ((CreateHostInput)input).RequestId : ((CreateProjectInput)input).RequestId;
        Assert.Equal(HttpStatusCode.OK, (await authorized.GetAsync("/api/v1/inventory/requests/" + requestId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/inventory/requests/" + requestId)).StatusCode);
        var list = await authorized.GetFromJsonAsync<System.Text.Json.JsonElement>(route);
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await authorized.GetAsync(route + "?take=101")).StatusCode);
        if (resource == "projects")
        {
            var found = await authorized.GetAsync(route + "/by-repository?repositoryUrl=" + Uri.EscapeDataString(((CreateProjectInput)input).RepositoryUrl));
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            var redirected = await authorized.PutAsJsonAsync(route + "/" + id, new
            {
                requestId = Id(),
                expectedRevision = 3,
                name = "Changed",
                baseBranch = "main",
                repositoryUrl = "https://github.com/other/repo"
            });
            Assert.Equal(HttpStatusCode.BadRequest, redirected.StatusCode);
            Assert.Equal("https://github.com/roysalisbury/hvo.agentcontrol", (await app.Store.Project(id!)).RepositoryUrl);
        }
    }

    private static async Task Conflict<T>(Func<Task<T>> action, string code)
    {
        var error = await Assert.ThrowsAsync<InventoryException>(action);
        Assert.Equal(409, error.Status);
        Assert.Equal(code, error.Code);
    }
}
