using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ProviderRefreshContinuityTests
{
    [Theory]
    [InlineData(RuntimeConnections.Ssh)]
    [InlineData(RuntimeConnections.ControlHttp)]
    public async Task AcknowledgementRetainsAllProviderHoldUntilDelayedAttributedEvent(string kind)
    {
        await using var fixture = await Fixture.Create(kind);
        fixture.Native.Delayed = true;
        var applying = fixture.Service.Apply(fixture.Runtime.Id, new(1), CancellationToken.None);
        await fixture.Native.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(applying.IsCompleted);
        Assert.False(await fixture.Allowed("openai"));
        Assert.False(await fixture.Allowed(ProviderKeyService.ProviderId));
        Assert.NotEmpty((await fixture.Instance()).PendingDisposalJson);
        await fixture.Native.Complete();
        Assert.Equal("RefreshCompleted", Assert.Single((await applying).Readiness).State);
        Assert.Equal("RefreshCompleted", (await fixture.Instance()).State);
        Assert.Empty((await fixture.Instance()).PendingDisposalJson);
        Assert.True(await fixture.Allowed("openai"));
        Assert.False(await fixture.Allowed(ProviderKeyService.ProviderId));
        await fixture.Service.AttestReady(fixture.Runtime.Id, new(1, true));
        Assert.True(await fixture.Allowed(ProviderKeyService.ProviderId));
        Assert.Equal(4, fixture.Probes);
        Assert.True(fixture.Native.ContentDisposed);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ReplacementBeforePostAfterEventOrAfterCatalogueCannotComplete(int changeAt)
    {
        await using var fixture = await Fixture.Create();
        fixture.ChangeAt = changeAt;
        var result = await fixture.Service.Apply(fixture.Runtime.Id, new(1), CancellationToken.None);
        Assert.Equal("Unknown", Assert.Single(result.Readiness).State);
        Assert.Equal("StoredOnRuntime", Assert.Single(result.Deliveries).State);
        Assert.Equal("Unknown", (await fixture.Instance()).State);
        Assert.False(await fixture.Allowed("openai"));
        Assert.Equal(changeAt == 2 ? 0 : 1, fixture.Native.Disposals);
        Assert.True(fixture.Native.ContentDisposed);
        Assert.Equal(changeAt != 2, (await fixture.Instance()).PendingDisposalJson.Length > 0);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("stale")]
    [InlineData("wrong-owner")]
    public async Task UnsupportedCachedOrForeignProcessEvidenceCannotDispose(string evidence)
    {
        await using var fixture = await Fixture.Create();
        fixture.Evidence = evidence;
        var result = await fixture.Service.Apply(fixture.Runtime.Id, new(1), CancellationToken.None);
        Assert.Equal("Unknown", Assert.Single(result.Readiness).State);
        Assert.Equal(0, fixture.Native.Disposals);
        Assert.False(await fixture.Allowed("openai"));
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("wrong-envelope")]
    [InlineData("wrong-payload")]
    [InlineData("post-error")]
    [InlineData("post-false")]
    public async Task MissingAmbiguousOrFailedCompletionRetainsAttemptAndDisposesStream(string mode)
    {
        await using var fixture = await Fixture.Create();
        fixture.Native.Mode = mode;
        var result = await fixture.Service.Apply(fixture.Runtime.Id, new(1), CancellationToken.None);
        Assert.Equal("Unknown", Assert.Single(result.Readiness).State);
        Assert.Equal("StoredOnRuntime", Assert.Single(result.Deliveries).State);
        Assert.Equal("Unknown", (await fixture.Instance()).State);
        Assert.NotEmpty((await fixture.Instance()).PendingDisposalJson);
        Assert.False(await fixture.Allowed("openai"));
        Assert.True(fixture.Native.ContentDisposed);
    }

    [Fact]
    public async Task CancelledWaitRetainsHoldAndCannotConsumeLateEventOnSameProcessRetry()
    {
        await using var fixture = await Fixture.Create();
        fixture.Native.Delayed = true;
        using var stop = new CancellationTokenSource();
        var applying = fixture.Service.Apply(fixture.Runtime.Id, new(1), stop.Token);
        await fixture.Native.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        Assert.Equal("Unknown", Assert.Single((await applying).Readiness).State);
        var marker = (await fixture.Instance()).PendingDisposalJson;
        fixture.Native.Delayed = false;
        await fixture.Service.Apply(fixture.Runtime.Id, new(1), CancellationToken.None);
        Assert.Equal(1, fixture.Native.Disposals);
        Assert.Equal(marker, (await fixture.Instance()).PendingDisposalJson);
        Assert.False(await fixture.Allowed("openai"));
    }

    [Fact]
    public async Task PendingAttemptSurvivesEventPruningRestartAndKeyRotationUntilVerifiedReplacement()
    {
        string data, secrets, runtimeId, pending;
        await using (var fixture = await Fixture.Create())
        {
            fixture.Native.Mode = "lost";
            await fixture.Service.Apply(fixture.Runtime.Id, new(1), CancellationToken.None);
            pending = (await fixture.Instance()).PendingDisposalJson;
            Assert.NotEmpty(pending);
            await fixture.App.Store.Write(async db => { await db.Events.ExecuteDeleteAsync(); return true; });
            data = fixture.App.DataPath; secrets = fixture.App.SecretPath; runtimeId = fixture.Runtime.Id;
        }
        await using var restarted = await Fixture.Create(data: data, secrets: secrets, runtimeId: runtimeId);
        await restarted.Service.Save(new("rotated-disposable-key", 1));
        await restarted.Service.Apply(runtimeId, new(2), CancellationToken.None);
        Assert.Equal(0, restarted.Native.Disposals);
        Assert.Equal(pending, (await restarted.Instance()).PendingDisposalJson);
        Assert.False(await restarted.Allowed("openai"));
        restarted.Replaced = true;
        await restarted.Service.Apply(runtimeId, new(2), CancellationToken.None);
        Assert.Equal(1, restarted.Native.Disposals);
        Assert.Equal("RefreshCompleted", (await restarted.Instance()).State);
        Assert.Empty((await restarted.Instance()).PendingDisposalJson);
        Assert.True(await restarted.Allowed("openai"));
        var started = Assert.Single(await restarted.App.Store.Read(db => db.Events.Where(x => x.Type == "ProviderDisposalStarted").ToListAsync()));
        Assert.NotEqual(Json.Read<ProviderDisposalAttempt>(pending).OperationId, Json.Read<ProviderDisposalAttempt>(started.Payload).OperationId);
    }

    [Fact]
    public async Task SavingNewKeyWithoutApplyingCannotReusePriorCanary()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.Apply(fixture.Runtime.Id, new(1), CancellationToken.None);
        await fixture.Service.AttestReady(fixture.Runtime.Id, new(1, true));
        await fixture.Service.Save(new("new-disposable-key", 1));
        await Assert.ThrowsAsync<ControlException>(() => fixture.Service.AttestReady(fixture.Runtime.Id, new(2, true)));
        Assert.False(await fixture.Allowed(ProviderKeyService.ProviderId));
        Assert.True(await fixture.Allowed("openai"));
        Assert.Equal(1, fixture.Native.Disposals);
    }

    [Fact]
    public async Task LegacyUnattributedRefreshCannotBeRetriedEvenAfterProcessChange()
    {
        await using var fixture = await Fixture.Create();
        await fixture.App.Store.Write(db =>
        {
            db.Add(new ProviderReadinessReceipt
            {
                Id = "instance:" + fixture.Runtime.Id,
                RuntimeId = fixture.Runtime.Id,
                ProviderId = "instance",
                State = "Unknown",
                PendingDisposalJson = ProviderKeyService.LegacyPendingDisposal
            });
            return Task.FromResult(true);
        });
        fixture.Replaced = true;
        await fixture.Service.Apply(fixture.Runtime.Id, new(1), CancellationToken.None);
        Assert.Equal(0, fixture.Native.Disposals);
        Assert.Equal(ProviderKeyService.LegacyPendingDisposal, (await fixture.Instance()).PendingDisposalJson);
        Assert.False(await fixture.Allowed("openai"));
    }

    [Fact]
    public async Task SshBootstrapInstanceOutsideWorkspaceRootsIsIncludedInIdleAndDisposalScope()
    {
        await using var fixture = await Fixture.Create();
        await fixture.App.Store.Write(async db => { (await db.Runtimes.FindAsync(fixture.Runtime.Id))!.StateDirectory = "/bootstrap"; return true; });
        fixture.Native.Mode = "bootstrap-busy";
        await fixture.Service.Apply(fixture.Runtime.Id, new(1), CancellationToken.None);
        Assert.Equal(0, fixture.Native.Disposals);
        Assert.Contains("/bootstrap", fixture.Native.StatusDirectories);
        fixture.Native.Mode = "";
        await fixture.Service.Apply(fixture.Runtime.Id, new(1), CancellationToken.None);
        Assert.Equal(2, fixture.Native.Disposals);
        Assert.Equal("RefreshCompleted", (await fixture.Instance()).State);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public TestApp App = default!;
        public RuntimeRecord Runtime = default!;
        public WorkerRecord Worker = default!;
        public ProviderKeyService Service = default!;
        public NativeHandler Native = new();
        public int Probes, ChangeAt;
        public bool Replaced;
        public string Evidence = "";
        public static async Task<Fixture> Create(string kind = RuntimeConnections.Ssh, string? data = null, string? secrets = null, string? runtimeId = null)
        {
            var fixture = new Fixture { App = new TestApp(data, secrets) };
            foreach (var supervisor in fixture.App.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>()) await supervisor.StopAsync(CancellationToken.None);
            if (runtimeId is null)
            {
                fixture.Runtime = new() { DesiredConnected = true, Health = "Healthy", ConnectionKind = kind };
                fixture.Worker = new() { RuntimeId = fixture.Runtime.Id, ManagedServerId = fixture.Runtime.ManagedServerId, NativeSessionId = "session", Directory = kind == RuntimeConnections.ControlHttp ? ControlStore.ControlDirectory : "/workspace", ProviderId = "openai", ModelId = "model" };
                await fixture.App.Store.Write(db => { db.Runtimes.Add(fixture.Runtime); db.Workers.Add(fixture.Worker); return Task.FromResult(true); });
            }
            else
            {
                fixture.Runtime = await fixture.App.Store.Read(async db => (await db.Runtimes.FindAsync(runtimeId))!);
                fixture.Worker = await fixture.App.Store.Read(db => db.Workers.SingleAsync(x => x.RuntimeId == runtimeId));
                await fixture.App.Store.Write(async db => { (await db.Runtimes.FindAsync(runtimeId))!.Health = "Healthy"; return true; });
            }
            fixture.Service = new(fixture.App.Store, fixture.App.Services.GetRequiredService<Secrets>(), new Factory(fixture));
            if (runtimeId is null) await fixture.Service.Save(new("disposable-test-key", 0));
            return fixture;
        }
        public Task<ProviderReadinessReceipt> Instance() => App.Store.Read(async db => (await db.Set<ProviderReadinessReceipt>().FindAsync("instance:" + Runtime.Id))!);
        public Task<bool> Allowed(string provider) => App.Store.Read(db => ControlStore.ProviderDispatchAllowed(db, Worker,
            new CommandRecord { RuntimeId = Runtime.Id, WorkerId = Worker.Id, Kind = "Prompt", Payload = Json.Write(new PromptInput("proof", "no native prompt", 0, provider, "model")) }));
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
    private sealed class Factory(Fixture fixture) : IRuntimeTransportFactory
    {
        public Task<IRuntimeTransport> Connect(RuntimeRecord runtime, CancellationToken token)
        { fixture.Probes = 0; return Task.FromResult<IRuntimeTransport>(new Transport(fixture)); }
    }
    private sealed class Transport(Fixture fixture) : IRuntimeTransport
    {
        public OpenCodeClient Api { get; } = new(new HttpClient(fixture.Native, disposeHandler: false) { BaseAddress = new("http://fixture"), Timeout = Timeout.InfiniteTimeSpan });
        public bool Connected => true;
        public string Platform => "fixture";
        public Task<RuntimeProcessIdentity?> ProbeProcessIdentity(CancellationToken token)
        {
            fixture.Probes++;
            var changed = fixture.Replaced || fixture.ChangeAt > 0 && fixture.Probes >= fixture.ChangeAt;
            return Task.FromResult<RuntimeProcessIdentity?>(fixture.Evidence == "unsupported" ? null :
                new(fixture.Runtime.ConnectionKind, fixture.Evidence == "wrong-owner" ? "foreign" : fixture.Runtime.ManagedServerId,
                    changed ? "22222222-2222-4222-8222-222222222222" : "11111111-1111-4111-8111-111111111111",
                    fixture.Runtime.ConnectionKind == RuntimeConnections.ControlHttp ? null : changed ? 72 : 71,
                    fixture.Evidence == "stale" ? 1 : ControlStore.Now));
        }
        public Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken token) => throw new NotSupportedException();
        public Task StopOwnedServer(CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Api.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class NativeHandler : HttpMessageHandler
    {
        public bool Delayed, ContentDisposed;
        public string Mode = "";
        public int Disposals;
        public List<string> StatusDirectories = [];
        private string directory = "/workspace";
        public TaskCompletionSource Acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Pipe events = new();
        public async Task Complete()
        {
            if (Mode != "lost")
                await events.Writer.WriteAsync(Encoding.UTF8.GetBytes("data: " + Json.Write(new
                {
                    directory = Mode == "wrong-envelope" ? "/other" : directory,
                    project = "fixture",
                    workspace = "fixture",
                    payload = new { type = "server.instance.disposed", properties = new { directory = Mode == "wrong-payload" ? "/other" : directory } }
                }) + "\n\n"));
            await events.Writer.CompleteAsync();
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/opencode-go") return Reply(true);
            if (path == "/session") return Reply(new[] { new { id = "session" } });
            if (path == "/session/status")
            {
                var scope = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["directory"].ToString();
                StatusDirectories.Add(scope);
                return Reply(Mode == "bootstrap-busy" && scope == "/bootstrap"
                    ? new Dictionary<string, object> { ["unlisted-default-child"] = new { type = "busy" } } : new Dictionary<string, object>());
            }
            if (path == "/global/event")
            {
                events = new(); ContentDisposed = false;
                var content = new TrackingContent(events.Reader.AsStream(), () => ContentDisposed = true);
                content.Headers.ContentType = new("text/event-stream");
                return new(HttpStatusCode.OK) { Content = content };
            }
            if (path == "/instance/dispose")
            {
                directory = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["directory"].ToString();
                Disposals++; Acknowledged.TrySetResult();
                if (Mode == "post-error") throw new HttpRequestException("Synthetic failed POST");
                if (Mode == "post-false") return Reply(false);
                if (!Delayed) await Complete();
                return Reply(true);
            }
            if (path == "/provider") return Reply(new { connected = new[] { "opencode-go" }, all = new[] { new { id = "opencode-go", models = new Dictionary<string, object> { ["model"] = new { } } } } });
            throw new InvalidOperationException("Unexpected fixture request: " + path);
        }
        private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
    private sealed class TrackingContent(Stream stream, Action disposed) : StreamContent(stream)
    {
        protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
    }
}
