using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.AgentControl.Tests;

public sealed class ControlServiceTests
{
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
        await app.Store.Write(async db => { (await db.Workers.FindAsync(old.Id))!.Role = SessionRoles.Coordinator; db.Workers.Add(developer); return true; });
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
        public ControlServiceIdentity Identity { get; }
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
                else if (path == "/global/health") body = new { healthy = true, version = "1.18.29" };
                else if (path == "/doc") body = new { paths = new[] { "/global/event", "/session", "/session/{sessionID}/prompt_async", "/session/{sessionID}/message", "/session/status", "/provider", "/path" }.ToDictionary(x => x, _ => new { }) };
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
                { lock (sessions) body = sessions.Single(x => JsonSerializer.SerializeToElement(x).GetProperty("id").GetString() == path[9..]); }
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
