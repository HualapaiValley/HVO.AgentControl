using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ReplyPreflightRecoveryTests
{
    [Theory]
    [InlineData("permission")]
    [InlineData("question")]
    public async Task KnownUnsentPreflightFailureReleasesOnlyTheMatchingPendingRequest(string kind)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var other = await AddRequest(app, worker, kind == "permission" ? "question" : "permission", "other");
        var request = await AddRequest(app, worker, kind, "target");
        var otherReply = await app.Store.Reply(Input(other));
        var reply = await app.Store.Reply(Input(request));
        await Claim(app, reply.Id);

        var handler = new ReplyHandler(failPreflight: true);
        await Dispatch(app, worker, reply, new Transport(handler), CancellationToken.None);

        var detail = await app.Store.Detail(worker.Id);
        var failed = detail.Commands.Single(x => x.Id == reply.Id);
        Assert.Equal(Delivery.Failed, failed.State);
        Assert.Contains("was not sent", failed.Detail);
        Assert.Equal(0, handler.Posts);
        Assert.Equal("Pending", await RequestState(app, request.Id));
        Assert.Null(await ReplyBinding(app, request.Id));
        Assert.Equal(otherReply.Id, await ReplyBinding(app, other.Id));
        Assert.Equal(reply.Id, (await app.Store.Reply(Input(request) with { Id = reply.Id })).Id);
        var retry = await app.Store.Reply(Input(request) with { Id = Guid.NewGuid().ToString() });
        Assert.Equal(Delivery.Queued, retry.State);
        Assert.Equal(retry.Id, await ReplyBinding(app, request.Id));
    }

    [Theory]
    [InlineData("permission", HttpStatusCode.Unauthorized)]
    [InlineData("question", HttpStatusCode.NotFound)]
    public async Task PreflightHttpRejectionReleasesPendingReplyWithoutPosting(string kind, HttpStatusCode status)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var request = await AddRequest(app, worker, kind, "http-rejection");
        var reply = await app.Store.Reply(Input(request));
        await Claim(app, reply.Id);
        var handler = new ReplyHandler(preflightStatus: status);

        await Dispatch(app, worker, reply, new Transport(handler), CancellationToken.None);

        Assert.Equal(0, handler.Posts);
        Assert.Equal(Delivery.Failed, (await app.Store.Detail(worker.Id)).Commands.Single(x => x.Id == reply.Id).State);
        Assert.Null(await ReplyBinding(app, request.Id));
        Assert.Equal("Pending", await RequestState(app, request.Id));
        Assert.NotEqual(reply.Id, (await app.Store.Reply(Input(request) with { Id = Guid.NewGuid().ToString() })).Id);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("question")]
    public async Task PostSendFailureRemainsUnknownAndCannotBeRetried(string kind)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var request = await AddRequest(app, worker, kind, "ambiguous");
        var reply = await app.Store.Reply(Input(request));
        await Claim(app, reply.Id);

        var handler = new ReplyHandler(directory: worker.Directory, failPost: true, nativeKind: kind, nativeId: request.NativeId, sessionId: worker.NativeSessionId);
        await Dispatch(app, worker, reply, new Transport(handler), CancellationToken.None);

        Assert.Equal(1, handler.Posts);
        Assert.Equal(Delivery.Unknown, (await app.Store.Detail(worker.Id)).Commands.Single(x => x.Id == reply.Id).State);
        Assert.Equal(reply.Id, await ReplyBinding(app, request.Id));
        Assert.Equal("Pending", await RequestState(app, request.Id));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.Reply(Input(request) with { Id = Guid.NewGuid().ToString() }));
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("question")]
    public async Task ShutdownDuringReplyPreflightAllowsAReplacementReply(string kind)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var request = await AddRequest(app, worker, kind, "shutdown");
        var reply = await app.Store.Reply(Input(request));
        await Claim(app, reply.Id);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Dispatch(app, worker, reply, new Transport(new ReplyHandler(failPreflight: true)), cancellation.Token);

        Assert.Equal(Delivery.Failed, (await app.Store.Detail(worker.Id)).Commands.Single(x => x.Id == reply.Id).State);
        Assert.Null(await ReplyBinding(app, request.Id));
        Assert.NotEqual(reply.Id, (await app.Store.Reply(Input(request) with { Id = Guid.NewGuid().ToString() })).Id);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("rebound")]
    [InlineData("resolved")]
    public async Task PreflightRecoveryFinalizesOnlyItsDispatchingCommandWhenRequestChanges(string race)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var request = await AddRequest(app, worker, "permission", race);
        var reply = await app.Store.Reply(Input(request));
        await Claim(app, reply.Id);
        var replacement = Guid.NewGuid().ToString();
        await app.Store.Write(async db =>
        {
            var current = await db.Requests.FindAsync(request.Id);
            if (race == "missing") db.Requests.Remove(current!);
            else if (race == "rebound") current!.ReplyCommandId = replacement;
            else { current!.State = "NoLongerPending"; current.ReplyCommandId = null; }
            return true;
        });

        Assert.True(await app.Store.RecordUnsentReplyPreflightFailure(reply.Id, "synthetic preflight failure"));

        Assert.Equal(Delivery.Failed, (await app.Store.Detail(worker.Id)).Commands.Single(x => x.Id == reply.Id).State);
        if (race == "missing") return;
        Assert.Equal(race == "rebound" ? replacement : null, await ReplyBinding(app, request.Id));
        Assert.Equal(race == "resolved" ? "NoLongerPending" : "Pending", await RequestState(app, request.Id));
    }

    private static ReplyInput Input(PendingRequest request) => new(Guid.NewGuid().ToString(), request.Id,
        request.Kind == "permission" ? "once" : null, null, Reject: request.Kind == "question");

    private static async Task<PendingRequest> AddRequest(TestApp app, WorkerRecord worker, string kind, string suffix)
    {
        var request = new PendingRequest { WorkerId = worker.Id, Kind = kind, NativeId = kind + "-" + suffix };
        await app.Store.Write(db => { db.Requests.Add(request); return Task.FromResult(true); });
        return request;
    }

    private static Task<string> RequestState(TestApp app, string requestId) =>
        app.Store.Read(async db => (await db.Requests.FindAsync(requestId))!.State);

    private static Task<string?> ReplyBinding(TestApp app, string requestId) =>
        app.Store.Read(async db => (await db.Requests.FindAsync(requestId))!.ReplyCommandId);

    private static Task Claim(TestApp app, string commandId) => app.Store.Write(async db =>
    {
        (await db.Commands.FindAsync(commandId))!.State = Delivery.Dispatching;
        return true;
    });

    private static async Task Dispatch(TestApp app, WorkerRecord worker, CommandRecord command, IRuntimeTransport transport, CancellationToken token)
    {
        var runtime = (await app.Store.Snapshot()).Runtimes.Single(x => x.Id == worker.RuntimeId);
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(supervisor, [command, runtime, transport, token])!;
    }

    private sealed class Transport : IRuntimeTransport
    {
        public Transport(ReplyHandler handler)
        {
            Api = new OpenCodeClient(new HttpClient(handler) { BaseAddress = new Uri("http://native.test") });
            typeof(OpenCodeClient).GetProperty(nameof(OpenCodeClient.Capabilities))!.SetValue(Api, new AdapterCapabilities(false, true, true, true));
        }

        public OpenCodeClient Api { get; }
        public bool Connected => true;
        public string Platform => "linux";
        public Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopOwnedServer(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReplyHandler(bool failPreflight = false, string directory = "/workspace", bool failPost = false,
        string? nativeKind = null, string? nativeId = null, string? sessionId = null, HttpStatusCode? preflightStatus = null) : HttpMessageHandler
    {
        public int Posts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failPreflight) throw new HttpRequestException("native snapshot unavailable");
            if (preflightStatus is not null) return Task.FromResult(new HttpResponseMessage(preflightStatus.Value));
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                if (failPost) throw new HttpRequestException("reply delivery unknown");
            }
            var path = request.RequestUri!.AbsolutePath;
            object body = path == "/permission" && nativeKind == "permission" ? new[] { new { id = nativeId, sessionID = sessionId } }
                : path == "/question" && nativeKind == "question" ? new[] { new { id = nativeId, sessionID = sessionId } }
                : path.EndsWith("/message", StringComparison.Ordinal) ? Array.Empty<object>()
                : path == "/session/status" ? new { }
                : new { directory, time = new { created = 1L } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}
