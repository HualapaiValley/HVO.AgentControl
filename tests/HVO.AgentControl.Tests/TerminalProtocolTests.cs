using HVO.AgentControl.Terminal;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class TerminalProtocolTests
{
    [Theory]
    [InlineData("https://control.example", "https", "control.example", true)]
    [InlineData("https://control.example:5054", "https", "control.example:5054", true)]
    [InlineData("https://control.example:443", "https", "control.example", true)]
    [InlineData("https://control.example:8443", "https", "control.example:5054", false)]
    [InlineData("HTTP://CONTROL.EXAMPLE", "http", "control.example", true)]
    [InlineData("http://control.example", "https", "control.example", false)]
    [InlineData("https://evil.example", "https", "control.example", false)]
    [InlineData("null", "https", "control.example", false)]
    [InlineData("", "https", "control.example", false)]
    [InlineData("not a uri", "https", "control.example", false)]
    [InlineData(null, "https", "control.example", false)]
    [InlineData("https://control.example", "", "control.example", false)]
    [InlineData("https://control.example", "https", "", false)]
    public void IsSameOriginRequiresMatchingSchemeAndAuthority(string? origin, string scheme, string host, bool expected)
    {
        Assert.Equal(expected, TerminalProtocol.IsSameOrigin(origin, scheme, host));
    }

    [Theory]
    [InlineData(80, 24, 80, 24)]
    [InlineData(20, 5, 20, 5)]
    [InlineData(300, 100, 300, 100)]
    [InlineData(19, 4, 20, 5)]
    [InlineData(301, 101, 300, 100)]
    [InlineData(0, 0, 20, 5)]
    [InlineData(int.MinValue, int.MaxValue, 20, 100)]
    public void ClampSizeBoundsTerminalDimensions(int columns, int rows, int expectedColumns, int expectedRows)
    {
        var (clampedColumns, clampedRows) = TerminalProtocol.ClampSize(columns, rows);
        Assert.Equal(expectedColumns, clampedColumns);
        Assert.Equal(expectedRows, clampedRows);
    }

    [Theory]
    [InlineData("agentcontrol", true)]
    [InlineData("agent-control_2", true)]
    [InlineData("ABC123", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("agent control", false)]
    [InlineData("agentcontrol:1", false)]
    [InlineData("agentcontrol.0", false)]
    [InlineData("agent;rm -rf /", false)]
    [InlineData("session\nname", false)]
    public void IsValidSessionNameRestrictsTargetSyntax(string? sessionName, bool expected)
    {
        Assert.Equal(expected, TerminalProtocol.IsValidSessionName(sessionName));
    }

    [Fact]
    public void SessionNameLengthIsBounded()
    {
        Assert.True(TerminalProtocol.IsValidSessionName(new string('a', TerminalProtocol.MaxSessionNameLength)));
        Assert.False(TerminalProtocol.IsValidSessionName(new string('a', TerminalProtocol.MaxSessionNameLength + 1)));
    }

    [Fact]
    public void InputFrameIsParsed()
    {
        Assert.True(TerminalProtocol.TryParseClientFrame("{\"type\":\"input\",\"data\":\"echo hi\\n\"}", out var frame));
        Assert.Equal(TerminalFrameKind.Input, frame.Kind);
        Assert.Equal("echo hi\n", frame.Data);
    }

    [Fact]
    public void UnicodeInputFrameIsPreserved()
    {
        Assert.True(TerminalProtocol.TryParseClientFrame("{\"type\":\"input\",\"data\":\"héllo 🙂 世界\"}", out var frame));
        Assert.Equal(TerminalFrameKind.Input, frame.Kind);
        Assert.Equal("héllo 🙂 世界", frame.Data);
    }

    [Fact]
    public void EmptyInputFrameIsAccepted()
    {
        Assert.True(TerminalProtocol.TryParseClientFrame("{\"type\":\"input\",\"data\":\"\"}", out var frame));
        Assert.Equal(TerminalFrameKind.Input, frame.Kind);
        Assert.Equal(string.Empty, frame.Data);
    }

    [Fact]
    public void ResizeFrameIsClamped()
    {
        Assert.True(TerminalProtocol.TryParseClientFrame("{\"type\":\"resize\",\"cols\":5,\"rows\":500}", out var frame));
        Assert.Equal(TerminalFrameKind.Resize, frame.Kind);
        Assert.Equal(TerminalProtocol.MinColumns, frame.Columns);
        Assert.Equal(TerminalProtocol.MaxRows, frame.Rows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"bogus\"}")]
    [InlineData("{\"type\":42}")]
    [InlineData("{\"type\":\"input\"}")]
    [InlineData("{\"type\":\"input\",\"data\":42}")]
    [InlineData("{\"type\":\"input\",\"data\":null}")]
    [InlineData("{\"type\":\"resize\",\"cols\":80}")]
    [InlineData("{\"type\":\"resize\",\"cols\":80,\"rows\":\"24\"}")]
    [InlineData("{\"type\":\"resize\",\"cols\":true,\"rows\":24}")]
    public void InvalidFramesAreRejected(string json)
    {
        Assert.False(TerminalProtocol.TryParseClientFrame(json, out _));
    }

    [Fact]
    public void OversizedInputDataIsRejected()
    {
        var oversized = new string('a', TerminalProtocol.MaxInputDataCharacters + 1);
        var json = "{\"type\":\"input\",\"data\":\"" + oversized + "\"}";
        Assert.False(TerminalProtocol.TryParseClientFrame(json, out _));
    }

    [Fact]
    public void OversizedMessageIsRejected()
    {
        var json = "{\"type\":\"input\",\"data\":\"" + new string('a', TerminalProtocol.MaxClientMessageBytes) + "\"}";
        Assert.False(TerminalProtocol.TryParseClientFrame(json, out _));
    }
}
