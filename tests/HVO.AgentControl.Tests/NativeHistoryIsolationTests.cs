using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class NativeHistoryIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidHistoryKeepsTransportAndOtherWorkerHealthyThenRecoversWithoutReplay(bool historyTimeout)
    {
        await using var app = new TestApp();
        await app.Services.GetServices<IHostedService>().OfType<RuntimeSupervisor>().Single().StopAsync(CancellationToken.None);
        var worker = await PersistenceTests.SeedWorker(app.Store);
        worker.NativeSessionId = "ses_bad";
        var other = new WorkerRecord { RuntimeId = worker.RuntimeId, NativeSessionId = "ses_good", Directory = "/home/agent/workspaces/b", Name = "Other" };
        var prompt = new CommandRecord { Id = Guid.NewGuid().ToString(), RuntimeId = worker.RuntimeId, WorkerId = worker.Id, Kind = "Prompt", State = Delivery.Accepted, NativeMessageId = "msg_user", Attempts = 1 };
        var refresh = new CommandRecord { Id = Guid.NewGuid().ToString(), RuntimeId = worker.RuntimeId, Kind = "RefreshState", State = Delivery.Queued };
        await app.Store.Write(async db =>
        {
            (await db.Workers.FindAsync(worker.Id))!.NativeSessionId = worker.NativeSessionId;
            (await db.Runtimes.FindAsync(worker.RuntimeId))!.DesiredConnected = true;
            db.Workers.Add(other); db.Commands.AddRange(prompt, refresh);
            return true;
        });
        var handler = new Handler(worker.Directory, other.Directory, historyTimeout);
        var factory = new Factory(handler);
        using var supervisor = new RuntimeSupervisor(app.Store, factory, Options.Create(new ControlOptions { PollMilliseconds = 100 }), NullLogger<RuntimeSupervisor>.Instance);
        await supervisor.StartAsync(CancellationToken.None);
        try
        {
            await TestApp.Wait(() => app.Store.Read(async db => handler.BadReads >= 3 &&
                (await db.Workers.FindAsync(worker.Id))!.Activity == "Unknown" && !(await db.Workers.FindAsync(other.Id))!.Stale), "Isolated history failure");
            var failed = await app.Store.Detail(worker.Id);
            Assert.True(failed.Worker.Stale);
            Assert.Equal(Delivery.Accepted, failed.Commands.Single().State);
            Assert.Equal(Delivery.Queued, await app.Store.Read(async db => (await db.Commands.FindAsync(refresh.Id))!.State));
            var runtime = await app.Store.Read(async db => (await db.Runtimes.FindAsync(worker.RuntimeId))!);
            Assert.Equal("Connected", runtime.Transport);
            Assert.Equal("Degraded", runtime.Health);
            Assert.Equal(1, factory.Connections);
            Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "HistoryObservationUnavailable").ToListAsync()));

            handler.Recovered = true;
            await TestApp.Wait(() => app.Store.Read(async db =>
                (await db.Commands.FindAsync(prompt.Id))!.State == Delivery.Finished &&
                (await db.Commands.FindAsync(refresh.Id))!.State == Delivery.Finished), "History recovery");
            var recovered = await app.Store.Detail(worker.Id);
            Assert.False(recovered.Worker.Stale);
            Assert.Equal("Idle", recovered.Worker.Activity);
            Assert.Equal("", recovered.Worker.CurrentAction);
            var recoveredRuntime = await app.Store.Read(async db => (await db.Runtimes.FindAsync(worker.RuntimeId))!);
            Assert.Equal("Healthy", recoveredRuntime.Health);
            Assert.DoesNotContain("history could not be read", recoveredRuntime.Diagnostic);
            Assert.Equal(worker.NativeSessionId, recovered.Worker.NativeSessionId);
            Assert.Equal(1, recovered.Commands.Single().Attempts);
            Assert.Contains("Verified final", ControlStore.ResponseText((await app.Store.Command(recovered.Commands.Single().Id)).ResultJson));
            Assert.Equal(1, factory.Connections);
            Assert.Equal(0, handler.Mutations);
            Assert.Single(await app.Store.Read(db => db.Commands.Where(x => x.Kind == "Prompt").ToListAsync()));
        }
        finally { await supervisor.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task OwnerCancellationDuringHistoryReadPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler("/work", "/other", historyTimeout: true, ownerCancellation: cancellation);
        using var api = new OpenCodeClient(new HttpClient(handler) { BaseAddress = new("http://native.test") });
        var worker = new WorkerRecord { NativeSessionId = "ses_bad", Directory = "/work" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.Snapshot(worker, 200, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, handler.BadReads);
    }

    private sealed class Handler(string directory, string otherDirectory, bool historyTimeout = false, CancellationTokenSource? ownerCancellation = null) : HttpMessageHandler
    {
        public volatile bool Recovered;
        public int BadReads;
        public int Mutations;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) { Interlocked.Increment(ref Mutations); throw new InvalidOperationException("No native mutation expected"); }
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/global/event")
            {
                var content = new StreamContent(new IdleEventStream());
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            if (path == "/global/health") return Reply(new { healthy = true, version = BootstrapScript.Version });
            if (path == "/doc") return Reply(new { paths = new[] { "/global/event", "/session", "/session/{sessionID}/prompt_async", "/session/{sessionID}/message", "/session/{sessionID}/message/{messageID}", "/session/status", "/provider", "/path" }.ToDictionary(x => x, _ => new { }) });
            if (path == "/provider") return Reply(new { connected = Array.Empty<string>(), all = Array.Empty<object>() });
            if (path == "/session/status") return Reply(new { });
            if (path == "/session/ses_bad/message")
            {
                Interlocked.Increment(ref BadReads);
                if (!Recovered)
                {
                    ownerCancellation?.Cancel();
                    if (historyTimeout) throw new OperationCanceledException(cancellationToken);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[") });
                }
                return Reply(new object[]
                {
                    new { info = new { id = "msg_user", sessionID = "ses_bad", role = "user", time = new { created = 1L } }, parts = Array.Empty<object>() },
                    new { info = new { id = "msg_final", sessionID = "ses_bad", role = "assistant", parentID = "msg_user", time = new { created = 2L, completed = 3L }, finish = "stop" }, parts = new[] { new { type = "text", text = "Verified final" } } }
                });
            }
            if (path == "/session/ses_good/message") return Reply(Array.Empty<object>());
            if (path == "/session/ses_bad") return Reply(new { directory, time = new { created = 1L } });
            if (path == "/session/ses_good") return Reply(new { directory = otherDirectory, time = new { created = 1L } });
            throw new InvalidOperationException("Unexpected fixture route " + path);
        }

        private static Task<HttpResponseMessage> Reply(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) });
    }

    private sealed class IdleEventStream() : MemoryStream("data: {\"type\":\"server.heartbeat\"}\n\n"u8.ToArray())
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class Factory(Handler handler) : IRuntimeTransportFactory
    {
        public int Connections;
        public Task<IRuntimeTransport> Connect(RuntimeRecord runtime, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Connections);
            return Task.FromResult<IRuntimeTransport>(new Transport(new(new HttpClient(handler) { BaseAddress = new("http://native.test") })));
        }
    }

    private sealed class Transport(OpenCodeClient api) : IRuntimeTransport
    {
        public OpenCodeClient Api => api;
        public bool Connected => true;
        public string Platform => "fixture";
        public Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopOwnedServer(CancellationToken cancellationToken) => throw new InvalidOperationException("Do not stop native execution for a history failure");
        public ValueTask DisposeAsync() { api.Dispose(); return ValueTask.CompletedTask; }
    }
}
