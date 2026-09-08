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

    private static OpenCodeClient Client(WorkerRecord worker, List<string> routes, Func<JsonElement[]> history, string status) =>
        new(new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            routes.Add(path);
            if (path.EndsWith("/message", StringComparison.Ordinal)) return history();
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
        info = new { id = "msg_final", role = "assistant", time = new { created = 3L, completed = 4L }, finish = "stop" },
        parts = new[] { new { type = "text", text = "Completed after denial" } }
    });

    private sealed class Handler(Func<HttpRequestMessage, object> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(respond(request)) });
    }
}
