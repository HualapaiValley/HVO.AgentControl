using System.Reflection;
using System.Security.Claims;
using HVO.AgentControl.Components.Pages;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.AspNetCore.Components.Authorization;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class HomeConversationRaceTests
{
    [Fact]
    public async Task DelayedDetailCannotReplaceFinalWorkerAndPromptUsesItsIdentityAndRevision()
    {
        await using var app = new TestApp();
        var (a, b) = await SeedWorkers(app.Store);
        var started = Source(); var release = Source();
        var firstA = true;
        var home = Home(app.Store, async (id, before) =>
        {
            var loaded = await app.Store.Detail(id, before);
            if (id == a.Id && before is null && firstA)
            {
                firstA = false; started.SetResult(); await release.Task;
            }
            return loaded;
        });

        var staleA = home.Navigate(a.Id); await started.Task;
        await home.Navigate(b.Id); release.SetResult(); await staleA;

        Assert.Equal(b.Id, home.Visible!.Worker.Id);
        Assert.Equal(b.Revision, home.Visible.Worker.Revision);
        await home.Send("final worker instruction");

        var command = Assert.Single((await app.Store.Detail(b.Id)).Commands);
        Assert.Equal(b.Id, command.WorkerId);
        var input = Json.Read<PromptInput>(command.Payload);
        Assert.Equal(b.Revision, input.ExpectedRevision);
        Assert.Equal("final worker instruction", input.Text);
        Assert.Empty((await app.Store.Detail(a.Id)).Commands);
    }

    [Fact]
    public async Task DelayedSuccessFromFirstAIsRejectedAfterAThenBThenA()
    {
        await using var app = new TestApp();
        var (a, b) = await SeedWorkers(app.Store);
        var started = Source(); var release = Source();
        var firstA = true;
        var home = Home(app.Store, async (id, before) =>
        {
            var loaded = await app.Store.Detail(id, before);
            if (id == a.Id && before is null && firstA)
            {
                firstA = false; started.SetResult(); await release.Task;
            }
            return loaded;
        });

        var staleA = home.Navigate(a.Id); await started.Task;
        await app.Store.Write(async db => { (await db.Workers.FindAsync(a.Id))!.Name = "Final A"; return true; });
        await home.Navigate(b.Id); await home.Navigate(a.Id);
        release.SetResult(); await staleA;

        Assert.Equal(a.Id, home.Visible!.Worker.Id);
        Assert.Equal("Final A", home.Visible.Worker.Name);
    }

    [Fact]
    public async Task DelayedErrorFromFirstAIsRejectedAfterAThenBThenA()
    {
        await using var app = new TestApp();
        var (a, b) = await SeedWorkers(app.Store);
        var started = Source(); var release = Source();
        var firstA = true;
        var home = Home(app.Store, async (id, before) =>
        {
            if (id == a.Id && before is null && firstA)
            {
                firstA = false; started.SetResult(); await release.Task;
                throw new InvalidOperationException("delayed stale detail error");
            }
            return await app.Store.Detail(id, before);
        });

        var staleA = home.Navigate(a.Id); await started.Task;
        await home.Navigate(b.Id); await home.Navigate(a.Id);
        release.SetResult(); await staleA;

        Assert.Equal(a.Id, home.Visible!.Worker.Id);
        Assert.Null(home.VisibleError);
    }

    [Fact]
    public async Task SameWorkerRefreshDoesNotDiscardDelayedOlderHistory()
    {
        await using var app = new TestApp();
        var (a, _) = await SeedWorkers(app.Store, messages: 201);
        var started = Source(); var release = Source();
        var home = Home(app.Store, async (id, before) =>
        {
            var loaded = await app.Store.Detail(id, before);
            if (before is not null) { started.SetResult(); await release.Task; }
            return loaded;
        });
        await home.Navigate(a.Id);

        var history = home.LoadOlder(); await started.Task;
        await home.BackgroundRefresh(); release.SetResult(); await history;

        Assert.Contains(home.Older, message => message.NativeId == "message-01");
    }

    [Fact]
    public async Task DelayedOlderHistoryIsRejectedAfterAThenBThenA()
    {
        await using var app = new TestApp();
        var (a, b) = await SeedWorkers(app.Store, messages: 201);
        var started = Source(); var release = Source();
        var home = Home(app.Store, async (id, before) =>
        {
            var loaded = await app.Store.Detail(id, before);
            if (id == a.Id && before is not null) { started.SetResult(); await release.Task; }
            return loaded;
        });
        await home.Navigate(a.Id);

        var history = home.LoadOlder(); await started.Task;
        await home.Navigate(b.Id); await home.Navigate(a.Id);
        release.SetResult(); await history;

        Assert.Empty(home.Older);
        Assert.Equal(a.Id, home.Visible!.Worker.Id);
    }

    [Fact]
    public async Task DisposalRejectsDelayedDetailCompletion()
    {
        await using var app = new TestApp();
        var (a, _) = await SeedWorkers(app.Store);
        var started = Source(); var release = Source();
        var home = Home(app.Store, async (id, before) =>
        {
            var loaded = await app.Store.Detail(id, before);
            started.SetResult(); await release.Task; return loaded;
        });

        var pending = home.Navigate(a.Id); await started.Task;
        await home.DisposeAsync(); release.SetResult(); await pending;

        Assert.Null(home.Visible);
    }

    private static TestHome Home(ControlStore store, Func<string, long?, Task<WorkerDetail>> read) => new(store, read);
    private static TaskCompletionSource Source() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<(WorkerRecord A, WorkerRecord B)> SeedWorkers(ControlStore store, int messages = 0)
    {
        var runtime = await store.SaveRuntime(PersistenceTests.Profile());
        var a = new WorkerRecord { Id = "worker-a", RuntimeId = runtime.Id, ManagedServerId = runtime.ManagedServerId, NativeSessionId = "session-a", Directory = "/work/a", Name = "Stale A", Revision = 7 };
        var b = new WorkerRecord { Id = "worker-b", RuntimeId = runtime.Id, ManagedServerId = runtime.ManagedServerId, NativeSessionId = "session-b", Directory = "/work/b", Name = "Worker B", Revision = 7 };
        await store.Write(db =>
        {
            db.Workers.AddRange(a, b);
            for (var index = 1; index <= messages; index++) db.Messages.Add(new TranscriptMessage
            {
                WorkerId = a.Id,
                NativeId = $"message-{index:00}",
                Role = "user",
                NativeCreatedAt = index,
                Json = "{\"info\":{},\"parts\":[{\"type\":\"text\",\"text\":\"message\"}]}"
            });
            return Task.FromResult(true);
        });
        return (a, b);
    }

    private sealed class TestHome : Home
    {
        private readonly Func<string, long?, Task<WorkerDetail>> read;

        public TestHome(ControlStore store, Func<string, long?, Task<WorkerDetail>> read)
        {
            Store = store; Authentication = new Authenticated(); this.read = read;
        }

        public WorkerDetail? Visible => Field<WorkerDetail?>("detail");
        public string? VisibleError => error;
        public IReadOnlyList<TranscriptMessage> Older => Field<List<TranscriptMessage>>("olderMessages");
        public Task Navigate(string id) { WorkerId = id; return OnParametersSetAsync(); }
        public Task BackgroundRefresh() => SnapshotChanged();
        public Task LoadOlder() => Invoke("OlderHistory");
        public Task Send(string text) { SetField("promptText", text); return Invoke("SendPrompt"); }
        protected override Task<WorkerDetail> ReadDetail(string workerId, long? before = null) => read(workerId, before);

        private T Field<T>(string name) => (T)typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        private void SetField(string name, object value) => typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, value);
        private Task Invoke(string name) => (Task)typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null)!;
    }

    private sealed class Authenticated : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], "test"))));
    }
}
