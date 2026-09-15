using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpRpcSessionTests
{
    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    public async Task DisposeAsyncSwallowsExpectedPipeReadFailureButCompletionRetainsIt(string failureKind)
    {
        Exception failure = failureKind == "io"
            ? new IOException("The pipe has been ended.")
            : new ObjectDisposedException("stdout");

        var session = new AcpRpcSession(new ThrowingStream(failure), static (_, _) => ValueTask.CompletedTask);
        session.Start();

        var observed = await Assert.ThrowsAnyAsync<Exception>(() => session.Completion);
        Assert.Same(failure, observed);

        await session.DisposeAsync();

        Assert.True(session.Notifications.Completion.IsCompleted);
    }

    [Fact]
    public async Task BeginRequestPublishesExactIdBeforeMatchingResponseCanComplete()
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}\n"));
        long registeredId = 0;
        await using var session = new AcpRpcSession(input, static (_, _) => ValueTask.CompletedTask);
        var request = session.BeginRequest(
            "test/exact-id",
            null,
            TimeSpan.FromSeconds(2),
            CancellationToken.None,
            id => registeredId = id);
        session.Start();

        var result = await request.Completion;

        Assert.Equal(request.Id, registeredId);
        Assert.Equal(1, request.Id);
        Assert.True(result.GetProperty("ok").GetBoolean());
        await session.Completion;
    }

    [Fact]
    public async Task IncomingFrameHookObservesOrderedChunksBeforeAdjacentResultCompletionUnderChannelPressure()
    {
        var inputText = """
            {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"ses","update":{"sessionUpdate":"agent_message_chunk","content":{"text":"one"}}}}
            {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"ses","update":{"sessionUpdate":"agent_message_chunk","content":{"text":"two"}}}}
            {"jsonrpc":"2.0","method":"session/update","params":{"n":3}}
            {"jsonrpc":"2.0","id":1,"result":{"stopReason":"end_turn"}}
            """;
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(inputText));
        var observed = new List<string>();
        await using var session = new AcpRpcSession(
            input,
            static (_, _) => ValueTask.CompletedTask,
            notificationCapacity: 1);
        session.IncomingFrameHook = envelope =>
        {
            if (envelope.IsNotification
                && envelope.Params is { } parameters
                && parameters.TryGetProperty("update", out var update)
                && update.TryGetProperty("content", out var content)
                && content.TryGetProperty("text", out var text))
            {
                observed.Add(text.GetString()!);
            }
        };
        var request = session.RequestAsync(
            "session/prompt",
            null,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        session.Start();

        var result = await request;

        Assert.Equal("end_turn", result.GetProperty("stopReason").GetString());
        Assert.Equal(new[] { "one", "two" }, observed);
        await session.Completion;
        var retained = await session.Notifications.ReadAsync();
        Assert.Equal(3, retained.GetProperty("n").GetInt32());
    }

    private sealed class ThrowingStream : Stream
    {
        private readonly Exception _failure;

        public ThrowingStream(Exception failure) => _failure = failure;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw _failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(_failure);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
