using HVO.AgentControl.Runtime;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpSanitizedLogBufferTests
{
    [Fact]
    public void CapturesCompleteLinesAndFlushesPartial()
    {
        var buffer = new SanitizedLogBuffer();
        buffer.Append("first line\nsecond");
        buffer.Flush();

        var snapshot = buffer.Snapshot();
        Assert.Equal(new[] { "first line", "second" }, snapshot);
    }

    [Fact]
    public void BoundsRetainedLines()
    {
        var buffer = new SanitizedLogBuffer(maxLines: 3);
        for (var i = 0; i < 10; i++)
        {
            buffer.Append($"line {i}\n");
        }

        var snapshot = buffer.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal("line 7", snapshot[0]);
        Assert.Equal("line 9", snapshot[2]);
    }

    [Fact]
    public void RedactsEphemeralSecretAndCredentialShapes()
    {
        var buffer = new SanitizedLogBuffer(secret: "s3cr3t-value");
        buffer.Append("password=s3cr3t-value\n");
        buffer.Append("Authorization: Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ==\n");
        buffer.Append("token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789\n");
        buffer.Append("key AKIAIOSFODNN7EXAMPLE\n");
        buffer.Append("""{"token":"very-secret-token"}""" + "\n");
        buffer.Flush();

        var text = buffer.SnapshotText();
        Assert.DoesNotContain("s3cr3t-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("QWxhZGRpbjpvcGVuIHNlc2FtZQ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret-token", text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsMultipleSimultaneousSecretsWithoutReplacementRegression()
    {
        const string serverPassword = "disposable-server-password-243";
        const string proxyKey = "disposable-cliproxy-key-243";
        var buffer = new SanitizedLogBuffer(secret: serverPassword);
        buffer.AddSecret(proxyKey);

        buffer.Append($"{serverPassword} {proxyKey}\n");
        buffer.Append($$"""{"password":"{{serverPassword}}","api_key":"{{proxyKey}}"}""" + "\n");
        buffer.Flush();

        var text = buffer.SnapshotText();
        Assert.DoesNotContain(serverPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(proxyKey, text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);

        buffer.SetSecrets([proxyKey]);
        Assert.Contains(serverPassword, buffer.Redact(serverPassword), StringComparison.Ordinal);
        Assert.Equal("***", buffer.Redact(proxyKey));
    }

    [Fact]
    public void TruncatesOverlongLines()
    {
        var buffer = new SanitizedLogBuffer(maxLineLength: 10);
        buffer.Append(new string('a', 50) + "\n");
        buffer.Flush();

        var snapshot = buffer.Snapshot();
        Assert.Single(snapshot);
        Assert.StartsWith("aaaaaaaaaa", snapshot[0], StringComparison.Ordinal);
    }
}
