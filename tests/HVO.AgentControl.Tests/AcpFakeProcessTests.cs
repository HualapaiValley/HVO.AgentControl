using System.Diagnostics;
using System.Text.Json;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpFakeProcessTests
{
    [Fact]
    public async Task InitializeAndSessionNewCorrelateOverStdio()
    {
        await using var fake = FakeSession.Start("happy");

        var initialize = await fake.Session.RequestAsync(
            "initialize",
            new Dictionary<string, object?> { ["protocolVersion"] = 1 },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(1, initialize.GetProperty("protocolVersion").GetInt32());

        var created = await fake.Session.RequestAsync(
            "session/new",
            new Dictionary<string, object?> { ["cwd"] = "/tmp/work", ["mcpServers"] = Array.Empty<object>() },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(AcpFakeServer.DefaultSessionId, created.GetProperty("sessionId").GetString());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var notification = await fake.Session.Notifications.ReadAsync(timeout.Token);
        Assert.Equal("current_mode_update", notification.GetProperty("update").GetProperty("sessionUpdate").GetString());
    }

    [Fact]
    public async Task PromptRoundTripsPermissionRejection()
    {
        await using var fake = FakeSession.Start("happy");
        var captured = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        fake.Session.IncomingRequestHandler = (request, _) =>
        {
            captured.TrySetResult(request.Params!.Value);
            return Task.FromResult(AcpResponse.Ok(PermissionPolicy.BuildRejection(request.Params)));
        };

        var result = await fake.Session.RequestAsync(
            "session/prompt",
            new Dictionary<string, object?>
            {
                ["sessionId"] = AcpFakeServer.DefaultSessionId,
                ["prompt"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = "hello" },
                },
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal("end_turn", result.GetProperty("stopReason").GetString());

        var permissionRequest = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("toolCall", permissionRequest.GetRawText(), StringComparison.Ordinal);

        var optionId = result
            .GetProperty("permissionResponse")
            .GetProperty("result")
            .GetProperty("outcome")
            .GetProperty("optionId")
            .GetString();
        Assert.Equal("reject_once", optionId);
    }

    [Fact]
    public async Task PromptRoundTripsGenericPermissionRejection()
    {
        // Pinned OpenCode 1.18.30 offers generic IDs for read/edit/bash=ask. The
        // host must select `reject` (not `once`/`always`) and the same connection's
        // prompt must still complete.
        await using var fake = FakeSession.Start("permission_generic");
        var captured = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        fake.Session.IncomingRequestHandler = (request, _) =>
        {
            captured.TrySetResult(request.Params!.Value);
            return Task.FromResult(AcpResponse.Ok(PermissionPolicy.BuildRejection(request.Params)));
        };

        var result = await fake.Session.RequestAsync(
            "session/prompt",
            new Dictionary<string, object?>
            {
                ["sessionId"] = AcpFakeServer.DefaultSessionId,
                ["prompt"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = "hello" },
                },
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal("end_turn", result.GetProperty("stopReason").GetString());

        var permissionRequest = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var optionIds = permissionRequest.GetProperty("options").EnumerateArray().Select(x => x.GetProperty("optionId").GetString()!).ToArray();
        Assert.Equal(["once", "always", "reject"], optionIds);

        var optionId = result
            .GetProperty("permissionResponse")
            .GetProperty("result")
            .GetProperty("outcome")
            .GetProperty("optionId")
            .GetString();
        Assert.Equal("reject", optionId);
    }

    [Fact]
    public async Task RemoteErrorIsSurfaced()
    {
        await using var fake = FakeSession.Start("init_error");

        var exception = await Assert.ThrowsAsync<AcpRemoteException>(() => fake.Session.RequestAsync(
            "initialize",
            new Dictionary<string, object?> { ["protocolVersion"] = 1 },
            TimeSpan.FromSeconds(5),
            CancellationToken.None));

        Assert.Equal(-32603, exception.Code);
        Assert.Contains("initialize exploded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutOfOrderResponsesCorrelateToCorrectRequests()
    {
        await using var fake = FakeSession.Start("happy");

        var slow = fake.Session.RequestAsync("test/slow", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        var fast = fake.Session.RequestAsync("test/fast", null, TimeSpan.FromSeconds(5), CancellationToken.None);

        var results = await Task.WhenAll(fast, slow);

        Assert.Equal("test/fast", results[0].GetProperty("method").GetString());
        Assert.Equal("test/slow", results[1].GetProperty("method").GetString());
    }

    [Fact]
    public async Task OversizedFrameFaultsTheSession()
    {
        await using var fake = FakeSession.Start("happy", maxFrameBytes: 1024);

        await fake.Session.NotifyAsync("huge", null, CancellationToken.None);

        await Assert.ThrowsAsync<AcpFrameTooLargeException>(async () => await fake.Session.Completion);
    }

    private sealed class FakeSession : IAsyncDisposable
    {
        private FakeSession(Process process, AcpRpcSession session)
        {
            Process = process;
            Session = session;
        }

        public Process Process { get; }

        public AcpRpcSession Session { get; }

        public static FakeSession Start(string scenario, int maxFrameBytes = AcpJsonCodec.DefaultMaxFrameBytes)
        {
            var home = Directory.CreateTempSubdirectory("acp-home-").FullName;
            var process = AcpFakeServer.Start(scenario, home);
            var session = new AcpRpcSession(process.StandardOutput.BaseStream, (frame, token) => WriteAsync(process, frame, token), maxFrameBytes);
            session.Start();
            return new FakeSession(process, session);
        }

        public async ValueTask DisposeAsync()
        {
            Session.RequestStop();
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
            {
            }

            await Session.DisposeAsync();
            Process.Dispose();
        }

        private static async ValueTask WriteAsync(Process process, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            await process.StandardInput.BaseStream.WriteAsync(frame, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
    }
}
