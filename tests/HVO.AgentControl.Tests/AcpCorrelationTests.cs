using System.Text;
using System.Text.Json;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpCorrelationTests
{
    [Fact]
    public async Task MatchingResponseCompletesPendingCall()
    {
        var correlator = new AcpRpcCorrelator();
        var call = correlator.Register("initialize", CancellationToken.None);
        Assert.Equal(1, correlator.PendingCount);

        var handled = correlator.TryComplete(Response(call.Id, """{"protocolVersion":1}"""));

        Assert.True(handled);
        Assert.Equal(0, correlator.PendingCount);
        var result = await call.Task;
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task ErrorResponseFaultsWithRemoteException()
    {
        var correlator = new AcpRpcCorrelator();
        var call = correlator.Register("session/load", CancellationToken.None);

        correlator.TryComplete(Parse("{\"jsonrpc\":\"2.0\",\"id\":" + call.Id + ",\"error\":{\"code\":-32001,\"message\":\"missing\"}}"));

        var exception = await Assert.ThrowsAsync<AcpRemoteException>(async () => await call.Task);
        Assert.Equal(-32001, exception.Code);
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationRemovesPendingCall()
    {
        var correlator = new AcpRpcCorrelator();
        using var source = new CancellationTokenSource();
        var call = correlator.Register("session/prompt", source.Token);

        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call.Task);
        Assert.Equal(0, correlator.PendingCount);
    }

    [Fact]
    public void UnknownResponseIsIgnored()
    {
        var correlator = new AcpRpcCorrelator();
        Assert.False(correlator.TryComplete(Parse("""{"jsonrpc":"2.0","id":99,"result":{}}""")));
    }

    [Fact]
    public async Task ExplicitNullResultResolvesRequest()
    {
        var correlator = new AcpRpcCorrelator();
        var call = correlator.Register("session/set_mode", CancellationToken.None);

        Assert.True(correlator.TryComplete(Parse("{\"jsonrpc\":\"2.0\",\"id\":" + call.Id + ",\"result\":null}")));

        var result = await call.Task;
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.ValueKind);
    }

    [Fact]
    public async Task ConcurrentRequestsCorrelateIndependently()
    {
        var correlator = new AcpRpcCorrelator();
        var first = correlator.Register("a", CancellationToken.None);
        var second = correlator.Register("b", CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);

        correlator.TryComplete(Response(second.Id, """{"value":"second"}"""));
        correlator.TryComplete(Response(first.Id, """{"value":"first"}"""));

        Assert.Equal("second", (await second.Task).GetProperty("value").GetString());
        Assert.Equal("first", (await first.Task).GetProperty("value").GetString());
    }

    [Fact]
    public async Task FailAllFaultsEveryPendingCall()
    {
        var correlator = new AcpRpcCorrelator();
        var call = correlator.Register("slow", CancellationToken.None);

        correlator.FailAll(new AcpSessionClosedException("closed"));

        await Assert.ThrowsAsync<AcpSessionClosedException>(async () => await call.Task);
        Assert.Equal(0, correlator.PendingCount);
    }

    [Fact]
    public void PendingLimitIsEnforced()
    {
        var correlator = new AcpRpcCorrelator(maxPending: 1);
        _ = correlator.Register("one", CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => correlator.Register("two", CancellationToken.None));
    }

    private static AcpEnvelope Response(long id, string resultJson)
    {
        return Parse("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}");
    }

    private static AcpEnvelope Parse(string json) => AcpEnvelope.Parse(Encoding.UTF8.GetBytes(json));
}
