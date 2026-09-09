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
        var home = Home(app.Store, async (id, before, beforeId) =>
        {
            var loaded = await app.Store.Detail(id, before, beforeId);
            if (id == a.Id && before is null && firstA)
            {
                firstA = false; started.SetResult(); await release.Task;
            }
            return loaded;
        });

        var staleA = home.Navigate(a.Id); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
        var home = Home(app.Store, async (id, before, beforeId) =>
        {
            var loaded = await app.Store.Detail(id, before, beforeId);
            if (id == a.Id && before is null && firstA)
            {
                firstA = false; started.SetResult(); await release.Task;
            }
            return loaded;
        });

        var staleA = home.Navigate(a.Id); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await app.Store.Write(async db => { (await db.Workers.FindAsync(a.Id))!.Name = "Final A"; return true; });
        await home.Navigate(b.Id); await home.Navigate(a.Id);
        release.SetResult(); await staleA;

        Assert.Equal(a.Id, home.Visible!.Worker.Id);
        Assert.Equal("Final A", home.Visible.Worker.Name);
    }

    [Fact]
    public async Task DraftSurvivesLateAbaSelectionAndStillTargetsFinalWorker()
    {
        await using var app = new TestApp();
        var (a, b) = await SeedWorkers(app.Store);
        var startedB = Source(); var releaseB = Source();
        var home = Home(app.Store, async (id, before, beforeId) =>
        {
            var loaded = await app.Store.Detail(id, before, beforeId);
            if (id == b.Id)
            {
                startedB.SetResult();
                await releaseB.Task;
            }
            return loaded;
        });

        await home.Navigate(a.Id);
        home.SetDraft("draft typed during selection");
        var pendingB = home.Navigate(b.Id);
        await startedB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await home.Navigate(a.Id);
        releaseB.SetResult();
        await pendingB;

        Assert.Equal(a.Id, home.Visible!.Worker.Id);
        Assert.Equal("draft typed during selection", home.Draft);
        await home.Send("final worker instruction");

        var command = Assert.Single((await app.Store.Detail(a.Id)).Commands);
        Assert.Equal(a.Id, command.WorkerId);
        Assert.Equal(a.Revision, Json.Read<PromptInput>(command.Payload).ExpectedRevision);
        Assert.Empty((await app.Store.Detail(b.Id)).Commands);
    }

    [Fact]
    public async Task StaleRenderedComposerInputStaysWithRenderedWorkerDuringDelayedNavigation()
    {
        await using var app = new TestApp();
        var (a, b) = await SeedWorkers(app.Store);
        var startedB = Source(); var releaseB = Source();
        var home = Home(app.Store, async (id, before, beforeId) =>
        {
            var loaded = await app.Store.Detail(id, before, beforeId);
            if (id == b.Id)
            {
                startedB.SetResult();
                await releaseB.Task;
            }
            return loaded;
        });

        await home.Navigate(a.Id);
        var pendingB = home.Navigate(b.Id);
        await startedB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        home.SetRenderedDraft(a.Id, "draft-from-rendered-A-25x");
        await home.SendRendered(a.Id, a.Revision);
        Assert.NotNull(home.VisibleError);
        Assert.Empty((await app.Store.Detail(a.Id)).Commands);
        Assert.Empty((await app.Store.Detail(b.Id)).Commands);
        releaseB.SetResult();
        await pendingB;

        Assert.Equal(string.Empty, home.Draft);
        await home.Navigate(a.Id);
        Assert.Equal("draft-from-rendered-A-25x", home.Draft);
        await home.Navigate(b.Id);
        Assert.Equal(string.Empty, home.Draft);
        Assert.Empty((await app.Store.Detail(b.Id)).Commands);

        await home.Navigate(a.Id);
        await home.Send("final A instruction");
        var command = Assert.Single((await app.Store.Detail(a.Id)).Commands);
        Assert.Equal(a.Id, command.WorkerId);
        Assert.Equal(a.Revision, Json.Read<PromptInput>(command.Payload).ExpectedRevision);
        Assert.DoesNotContain((await app.Store.Detail(b.Id)).Commands, x =>
            x.Kind == "Prompt" && Json.Read<PromptInput>(x.Payload).Text == "draft-from-rendered-A-25x");
    }

    [Fact]
    public async Task DelayedAcceptedASendDoesNotClearNewlySelectedBDraft()
    {
        await using var app = new TestApp();
        var (a, b) = await SeedWorkers(app.Store);
        var home = Home(app.Store, (id, before, beforeId) => app.Store.Detail(id, before, beforeId));
        await home.Navigate(a.Id);
        home.SetRenderedDraft(a.Id, "submitted A draft");
        var gate = WriterGate(app.Store);

        await gate.WaitAsync();
        try
        {
            var send = home.SendRendered(a.Id, a.Revision);
            await home.Navigate(b.Id);
            home.SetRenderedDraft(b.Id, "new unsent B draft");
            gate.Release();
            await send;
        }
        finally
        {
            if (gate.CurrentCount == 0) gate.Release();
        }

        Assert.Equal("new unsent B draft", home.Draft);
        Assert.Equal("submitted A draft", Json.Read<PromptInput>(Assert.Single((await app.Store.Detail(a.Id)).Commands).Payload).Text);
        Assert.Empty((await app.Store.Detail(b.Id)).Commands);
        await home.Navigate(a.Id);
        Assert.Equal(string.Empty, home.Draft);
        await home.Navigate(b.Id);
        Assert.Equal("new unsent B draft", home.Draft);
    }

    [Fact]
    public async Task DelayedAcceptedASendDoesNotClearNewerSameWorkerDraft()
    {
        await using var app = new TestApp();
        var (a, _) = await SeedWorkers(app.Store);
        var home = Home(app.Store, (id, before, beforeId) => app.Store.Detail(id, before, beforeId));
        await home.Navigate(a.Id);
        home.SetRenderedDraft(a.Id, "submitted A draft");
        var gate = WriterGate(app.Store);

        await gate.WaitAsync();
        try
        {
            var send = home.SendRendered(a.Id, a.Revision);
            home.SetRenderedDraft(a.Id, "newer unsent A draft");
            gate.Release();
            await send;
        }
        finally
        {
            if (gate.CurrentCount == 0) gate.Release();
        }

        Assert.Equal("newer unsent A draft", home.Draft);
        var command = Assert.Single((await app.Store.Detail(a.Id)).Commands);
        var input = Json.Read<PromptInput>(command.Payload);
        Assert.Equal("submitted A draft", input.Text);
        Assert.Equal(a.Revision, input.ExpectedRevision);
    }

    [Fact]
    public async Task DelayedErrorFromFirstAIsRejectedAfterAThenBThenA()
    {
        await using var app = new TestApp();
        var (a, b) = await SeedWorkers(app.Store);
        var started = Source(); var release = Source();
        var firstA = true;
        var home = Home(app.Store, async (id, before, beforeId) =>
        {
            if (id == a.Id && before is null && firstA)
            {
                firstA = false; started.SetResult(); await release.Task;
                throw new InvalidOperationException("delayed stale detail error");
            }
            return await app.Store.Detail(id, before, beforeId);
        });

        var staleA = home.Navigate(a.Id); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
        var home = Home(app.Store, async (id, before, beforeId) =>
        {
            var loaded = await app.Store.Detail(id, before, beforeId);
            if (before is not null) { started.SetResult(); await release.Task; }
            return loaded;
        });
        await home.Navigate(a.Id);

        var history = home.LoadOlder(); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await home.BackgroundRefresh(); release.SetResult(); await history;

        Assert.Contains(home.Older, message => message.NativeId == "message-01");
    }

    [Fact]
    public async Task EmptyHistoryShowsNoticeWithoutAConnectionError()
    {
        await using var app = new TestApp();
        var (worker, _) = await SeedWorkers(app.Store);
        var home = Home(app.Store, (id, before, beforeId) => app.Store.Detail(id, before, beforeId));
        await home.Navigate(worker.Id);
        await home.LoadOlder();
        Assert.Null(home.VisibleError);
        Assert.Equal("No earlier messages are stored locally. Native OpenCode history remains on the runtime.", home.Notice);
    }

    [Fact]
    public async Task MixedCaseTimestampTiesUseOrdinalCursorOrdering()
    {
        await using var app = new TestApp();
        var (worker, _) = await SeedWorkers(app.Store);
        await app.Store.Write(db =>
        {
            foreach (var id in new[] { "a", "Z", "Y", "X" }) db.Messages.Add(new TranscriptMessage
            {
                WorkerId = worker.Id,
                NativeId = id,
                Role = "user",
                NativeCreatedAt = 42,
                Json = "{\"info\":{},\"parts\":[{\"type\":\"text\",\"text\":\"message\"}]}"
            });
            return Task.FromResult(true);
        });
        var home = Home(app.Store, (id, before, beforeId) => app.Store.Detail(id, before, beforeId));
        await home.Navigate(worker.Id); await home.LoadOlder();
        Assert.Equal("X", home.LastBeforeId);
    }

    [Fact]
    public async Task DelayedOlderHistoryIsRejectedAfterAThenBThenA()
    {
        await using var app = new TestApp();
        var (a, b) = await SeedWorkers(app.Store, messages: 201);
        var started = Source(); var release = Source();
        var home = Home(app.Store, async (id, before, beforeId) =>
        {
            var loaded = await app.Store.Detail(id, before, beforeId);
            if (id == a.Id && before is not null) { started.SetResult(); await release.Task; }
            return loaded;
        });
        await home.Navigate(a.Id);

        var history = home.LoadOlder(); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
        var home = Home(app.Store, async (id, before, beforeId) =>
        {
            var loaded = await app.Store.Detail(id, before, beforeId);
            started.SetResult(); await release.Task; return loaded;
        });

        var pending = home.Navigate(a.Id); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await home.DisposeAsync(); release.SetResult(); await pending;

        Assert.Null(home.Visible);
    }

    private static TestHome Home(ControlStore store, Func<string, long?, string?, Task<WorkerDetail>> read) => new(store, read);
    [Fact]
    public async Task BackgroundRefreshCannotReplacePinnedOutcomeRevision()
    {
        await using var app = new TestApp();
        var (a, _) = await SeedWorkers(app.Store);
        var command = await app.Store.Prompt(a.Id, new(Guid.NewGuid().ToString(), "Review this assignment", a.Revision));
        await app.Store.Write(async db => { (await db.Commands.FindAsync(command.Id))!.State = Delivery.Finished; return true; });
        var home = Home(app.Store, (id, before, beforeId) => app.Store.Detail(id, before, beforeId));

        await home.Navigate(a.Id);
        await app.Store.Write(async db => { (await db.Workers.FindAsync(a.Id))!.Revision++; return true; });
        await home.BackgroundRefresh();
        await home.Record("VerifiedComplete", "Evidence from the stale review form.");

        Assert.NotNull(home.VisibleError);
        var detail = await app.Store.Detail(a.Id);
        Assert.Equal("Assigned", detail.Assignments.Single(x => x.Id == command.Id).Outcome);
        Assert.Equal("Unassigned", detail.Worker.Outcome);
    }

    [Fact]
    public async Task BackgroundRefreshClearsReviewWhenReviewedAssignmentLeavesDetailWindow()
    {
        await using var app = new TestApp();
        var (worker, _) = await SeedWorkers(app.Store);
        var reviewed = await app.Store.Prompt(worker.Id, new(Guid.NewGuid().ToString(), "Review this assignment", worker.Revision));
        await app.Store.Write(async db =>
        {
            var original = (await db.Commands.FindAsync(reviewed.Id))!;
            original.State = Delivery.Finished;
            for (var index = 1; index <= 100; index++)
                db.Commands.Add(new CommandRecord
                {
                    Id = $"newer-{index}",
                    WorkerId = worker.Id,
                    RuntimeId = worker.RuntimeId,
                    Kind = "Prompt",
                    State = Delivery.Finished,
                    CreatedAt = original.CreatedAt + index,
                    QueueOrder = original.QueueOrder + index,
                    Payload = "{}"
                });
            return true;
        });
        var home = Home(app.Store, (id, before, beforeId) => app.Store.Detail(id, before, beforeId));

        await home.Navigate(worker.Id);
        home.SetReview(reviewed.Id, worker.Revision, "Failed", "Evidence for the reviewed assignment.");
        await home.BackgroundRefresh();

        Assert.Empty(home.ReviewedCommandId);
        Assert.Equal("ReportedComplete", home.ReviewOutcome);
        Assert.Empty(home.ReviewEvidence);
        Assert.Equal(0, home.ReviewExpectedRevision);
    }

    private static TaskCompletionSource Source() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SemaphoreSlim WriterGate(ControlStore store) =>
        (SemaphoreSlim)typeof(ControlStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

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
        private readonly Func<string, long?, string?, Task<WorkerDetail>> read;
        public TestHome(ControlStore store, Func<string, long?, string?, Task<WorkerDetail>> read)
        {
            Store = store; Authentication = new Authenticated(); this.read = read;
        }

        public WorkerDetail? Visible => Field<WorkerDetail?>("detail");
        public string? VisibleError => error;
        public string? Notice => Field<string?>("notice");
        public string Draft => Field<string>("promptText");
        public string? LastBeforeId { get; private set; }
        public IReadOnlyList<TranscriptMessage> Older => Field<List<TranscriptMessage>>("olderMessages");
        public string ReviewedCommandId => Field<string>("outcomeCommandId");
        public string ReviewOutcome => Field<string>("outcome");
        public string ReviewEvidence => Field<string>("evidence");
        public long ReviewExpectedRevision => Field<long>("outcomeExpectedRevision");
        public Task Navigate(string id) { WorkerId = id; return OnParametersSetAsync(); }
        public Task BackgroundRefresh() => SnapshotChanged();
        public Task LoadOlder() => Invoke("OlderHistory");
        public Task Send(string text)
        {
            var worker = Visible!.Worker;
            SetRenderedDraft(worker.Id, text);
            return InvokeTask("SendPrompt", worker.Id, worker.Revision);
        }
        public Task SendRendered(string workerId, long revision) => InvokeTask("SendPrompt", workerId, revision);
        public void SetDraft(string text) => SetRenderedDraft(Visible!.Worker.Id, text);
        public void SetRenderedDraft(string workerId, string text) => Invoke("SetPromptText", workerId, text);
        protected override Task<WorkerDetail> ReadDetail(string workerId, long? before = null) => read(workerId, before, null);
        protected override Task<WorkerDetail> ReadDetail(string workerId, long? before, string beforeId)
        {
            LastBeforeId = beforeId;
            return read(workerId, before, beforeId);
        }
        public Task Record(string outcome, string evidence)
        {
            SetField("outcome", outcome); SetField("evidence", evidence); return Invoke("RecordOutcome");
        }
        public void SetReview(string commandId, long expectedRevision, string outcome, string evidence)
        {
            SetField("outcomeCommandId", commandId); SetField("outcomeExpectedRevision", expectedRevision);
            SetField("outcome", outcome); SetField("evidence", evidence);
        }

        private T Field<T>(string name) => (T)typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        private void SetField(string name, object value) => typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, value);
        private Task Invoke(string name) => (Task)typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null)!;
        private Task InvokeTask(string name, params object[] args) => (Task)typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, args)!;
        private void Invoke(string name, params object[] args) => typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, args);
    }

    private sealed class Authenticated : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], "test"))));
    }
}
