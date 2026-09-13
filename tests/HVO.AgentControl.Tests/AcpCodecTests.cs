using System.Text;
using System.Text.Json;
using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpCodecTests
{
    [Fact]
    public void EncodeThenParseRoundTripsRequest()
    {
        var frame = AcpJsonCodec.Encode(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 7,
            ["method"] = "session/new",
            ["params"] = new Dictionary<string, object?> { ["cwd"] = "/tmp/work" },
        });

        Assert.Equal((byte)'\n', frame[^1]);

        var envelope = AcpEnvelope.Parse(frame);
        Assert.True(envelope.IsRequest);
        Assert.False(envelope.IsNotification);
        Assert.False(envelope.IsResponse);
        Assert.Equal("session/new", envelope.Method);
        Assert.Equal("n:7", envelope.IdKey);
        Assert.Equal("/tmp/work", envelope.Params!.Value.GetProperty("cwd").GetString());
    }

    [Fact]
    public void ParseClassifiesNotification()
    {
        var envelope = AcpEnvelope.Parse(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s"}}"""));

        Assert.True(envelope.IsNotification);
        Assert.Null(envelope.Id);
        Assert.Null(envelope.IdKey);
    }

    [Fact]
    public void ParseClassifiesResponseAndError()
    {
        var success = AcpEnvelope.Parse(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":3,"result":{"sessionId":"s"}}"""));
        Assert.True(success.IsResponse);
        Assert.NotNull(success.Result);

        var failure = AcpEnvelope.Parse(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":"abc","error":{"code":-32601,"message":"nope"}}"""));
        Assert.True(failure.IsResponse);
        Assert.NotNull(failure.Error);
        Assert.Equal("s:abc", failure.IdKey);
    }

    [Fact]
    public void ParseRejectsInvalidJson()
    {
        Assert.Throws<AcpProtocolException>(() => AcpEnvelope.Parse(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public async Task ReadFrameParsesMultipleFramesFromOneBuffer()
    {
        var codec = new AcpJsonCodec(new MemoryStream(Encoding.UTF8.GetBytes("one\ntwo\nthree")));

        Assert.Equal("one", Encoding.UTF8.GetString((await codec.ReadFrameAsync(default))!));
        Assert.Equal("two", Encoding.UTF8.GetString((await codec.ReadFrameAsync(default))!));
        Assert.Equal("three", Encoding.UTF8.GetString((await codec.ReadFrameAsync(default))!));
        Assert.Null(await codec.ReadFrameAsync(default));
    }

    [Fact]
    public async Task ReadFrameHandlesCarriageReturn()
    {
        var codec = new AcpJsonCodec(new MemoryStream(Encoding.UTF8.GetBytes("value\r\n")));
        Assert.Equal("value", Encoding.UTF8.GetString((await codec.ReadFrameAsync(default))!));
    }

    [Fact]
    public async Task ReadFrameEnforcesBoundedSize()
    {
        var payload = new string('x', 2048) + "\n";
        var codec = new AcpJsonCodec(new MemoryStream(Encoding.UTF8.GetBytes(payload)), maxFrameBytes: 256);

        var exception = await Assert.ThrowsAsync<AcpFrameTooLargeException>(async () => await codec.ReadFrameAsync(default));
        Assert.Equal(256, exception.MaxBytes);
        Assert.True(exception.ObservedBytes > 256);
    }

    [Fact]
    public void EncodeOmitsNullParameters()
    {
        var frame = AcpJsonCodec.Encode(new Dictionary<string, object?> { ["method"] = "session/cancel", ["params"] = null });
        using var document = JsonDocument.Parse(frame);
        Assert.False(document.RootElement.TryGetProperty("params", out _));
    }

    [Fact]
    public void EncodePreservesExplicitNullResult()
    {
        var frame = AcpJsonCodec.Encode(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["result"] = null,
        });

        using var document = JsonDocument.Parse(frame);
        Assert.True(document.RootElement.TryGetProperty("result", out var result));
        Assert.Equal(JsonValueKind.Null, result.ValueKind);
    }
}
