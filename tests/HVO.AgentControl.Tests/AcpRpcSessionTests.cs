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
