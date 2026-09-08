using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.OpenCode;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class NativeSnapshotOrderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedToolIsRereadAfterIdleAndAnyLaterFinalIsRetained(bool continuesAfterDenial)
    {
        var worker = new WorkerRecord { NativeSessionId = "ses_test", Directory = "/workspace" };
        var routes = new List<string>();
        var reads = 0;
        using var api = Client(worker, routes, () =>
        {
            reads++;
            return continuesAfterDenial && reads == 2 ? new[] { Denied(), Final() } : [Denied()];
        }, "idle");

        var snapshot = await api.Snapshot(worker, 20, CancellationToken.None, verifyToolFailureStop: true);

        Assert.Equal(["/session/ses_test", "/session/ses_test/message", "/session/status", "/session/ses_test/message"], routes);
        Assert.Equal("msg_denied", snapshot.IdleToolFailureMessageId);
        Assert.Equal(continuesAfterDenial ? "msg_final" : "msg_denied", snapshot.Messages[^1].GetProperty("info").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("busy", true, false)]
    [InlineData("idle", false, false)]
    [InlineData("idle", true, true)]
    public async Task BusyTurnsSettledSuccessAndNonManagedHistoryDoNotTriggerExtraRead(string status, bool verify, bool successfulTool)
    {
        var worker = new WorkerRecord { NativeSessionId = "ses_test", Directory = "/workspace" };
        var routes = new List<string>();
        using var api = Client(worker, routes, () => [Denied(successfulTool ? "completed" : "error")], status);

        var snapshot = await api.Snapshot(worker, 20, CancellationToken.None, verify);

        Assert.Null(snapshot.IdleToolFailureMessageId);
        Assert.Single(routes, x => x.EndsWith("/message", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedFollowupReadCannotEstablishStoppedTurn()
    {
        var worker = new WorkerRecord { NativeSessionId = "ses_test", Directory = "/workspace" };
        var reads = 0;
        using var api = Client(worker, [], () => ++reads == 1 ? [Denied()] : throw new IOException("Read interrupted"), "idle");

        await Assert.ThrowsAsync<IOException>(() => api.Snapshot(worker, 20, CancellationToken.None, true));
    }

    [Fact]
    public async Task MissingPendingCallerIsReadByIdentityAndReconciledWithBoundedHistory()
    {
        var worker = new WorkerRecord { NativeSessionId = "ses_test", Directory = "/workspace" };
        var routes = new List<string>();
        using var api = Client(worker, routes, () => [Final()], "idle", path => path.EndsWith("/message/msg_user", StringComparison.Ordinal));

        var snapshot = await api.Snapshot(worker, 20, CancellationToken.None, pendingMessageIds: ["msg_user"]);

        Assert.Equal(["/session/ses_test", "/session/ses_test/message", "/session/ses_test/message/msg_user", "/session/status"], routes);
        Assert.Equal(["msg_final", "msg_user"], snapshot.Messages.Select(x => x.GetProperty("info").GetProperty("id").GetString()));
        Assert.Single(NativeTurnEvidence.AssistantMessages(snapshot.Messages, "msg_user"));
    }

    [Fact]
    public async Task MissingCallerReadsRemainBounded()
    {
        var worker = new WorkerRecord { NativeSessionId = "ses_test", Directory = "/workspace" };
        var routes = new List<string>();
        using var api = Client(worker, routes, () => [], "busy",
            path => path.StartsWith("/session/ses_test/message/", StringComparison.Ordinal),
            callerStatus: HttpStatusCode.NotFound);

        await api.Snapshot(worker, 20, CancellationToken.None, pendingMessageIds: Enumerable.Range(0, 20).Select(x => "msg_" + x).ToArray());

        Assert.Equal(8, routes.Count(x => x.StartsWith("/session/ses_test/message/", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("session")]
    [InlineData("role")]
    public async Task ContradictoryMissingCallerIdentityFailsClosed(string contradiction)
    {
        var worker = new WorkerRecord { NativeSessionId = "ses_test", Directory = "/workspace" };
        using var api = Client(worker, [], () => [Final()], "busy", path => path.EndsWith("/message/msg_user", StringComparison.Ordinal),
            () => Caller(contradiction));

        await Assert.ThrowsAsync<NativeHistoryObservationException>(() =>
            api.Snapshot(worker, 20, CancellationToken.None, pendingMessageIds: ["msg_user"]));
    }

    [Fact]
    public async Task MissingCaller404RemainsUnavailableEvidence()
    {
        var worker = new WorkerRecord { NativeSessionId = "ses_test", Directory = "/workspace" };
        using var api = Client(worker, [], () => [Final()], "busy", path => path.EndsWith("/message/msg_user", StringComparison.Ordinal),
            callerStatus: HttpStatusCode.NotFound);

        var snapshot = await api.Snapshot(worker, 20, CancellationToken.None, pendingMessageIds: ["msg_user"]);

        Assert.DoesNotContain(snapshot.Messages, x => x.GetProperty("info").GetProperty("id").GetString() == "msg_user");
    }

    private static OpenCodeClient Client(WorkerRecord worker, List<string> routes, Func<JsonElement[]> history, string status,
        Func<string, bool>? callerRoute = null, Func<JsonElement>? caller = null, HttpStatusCode callerStatus = HttpStatusCode.OK) =>
        new(new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            routes.Add(path);
            if (path.EndsWith("/message", StringComparison.Ordinal)) return history();
            if (callerRoute?.Invoke(path) == true) return new HttpResponseMessage(callerStatus) { Content = JsonContent.Create(caller?.Invoke() ?? Caller()) };
            if (path == "/session/status") return status == "idle" ? new Dictionary<string, object>() : new() { [worker.NativeSessionId] = new { type = status } };
            return new { directory = worker.Directory };
        }))
        { BaseAddress = new Uri("http://native.test") });

    private static JsonElement Denied(string status = "error") => JsonSerializer.SerializeToElement(new
    {
        info = new { id = "msg_denied", role = "assistant", time = new { created = 1L, completed = 2L }, finish = "tool-calls" },
        parts = new[] { new { type = "tool", state = new { status } } }
    });

    private static JsonElement Final() => JsonSerializer.SerializeToElement(new
    {
        info = new { id = "msg_final", role = "assistant", parentID = "msg_user", sessionID = "ses_test", time = new { created = 3L, completed = 4L }, finish = "stop" },
        parts = new[] { new { type = "text", text = "Completed after denial" } }
    });

    private static JsonElement Caller() => JsonSerializer.SerializeToElement(new
    {
        info = new { id = "msg_user", role = "user", sessionID = "ses_test", time = new { created = 0L } },
        parts = new[] { new { type = "text", text = "Task" } }
    });

    private static JsonElement Caller(string contradiction) => JsonSerializer.SerializeToElement(new
    {
        info = new
        {
            id = contradiction == "id" ? "msg_other" : "msg_user",
            role = contradiction == "role" ? "assistant" : "user",
            sessionID = contradiction == "session" ? "ses_other" : "ses_test",
            time = new { created = 0L }
        },
        parts = new[] { new { type = "text", text = "Task" } }
    });

    private sealed class Handler(Func<HttpRequestMessage, object> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var result = respond(request);
            return Task.FromResult(result is HttpResponseMessage response
                ? response
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
    }
}
