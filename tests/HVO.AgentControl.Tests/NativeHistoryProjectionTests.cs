using System.Text;
using System.Text.Json;
using HVO.AgentControl.OpenCode;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class NativeHistoryProjectionTests
{
    [Fact]
    public async Task OversizedUserDiffIsMarkedWhileTurnEvidenceIsPreserved()
    {
        var diff = new string('b', 8_500_000);
        var json = """
            [{"info":{"id":"msg_caller","sessionID":"ses_live","role":"user","summary":{"title":"edited","diffs":[{"file":"src/App.cs","before":"
            """ + diff + """
            ","after":"
            """ + diff + """
            "}]},"unknown":{"keep":[1,true,"yes"]}},"parts":[{"type":"text","text":"caller prompt"}]},{"info":{"id":"msg_final","sessionID":"ses_live","parentID":"msg_caller","role":"assistant","summary":true,"time":{"created":10,"completed":20},"finish":"stop","error":null,"tokens":{"input":101,"output":202,"cache":{"read":33}},"cost":0.125,"future":"kept"},"parts":[{"type":"tool","tool":"bash","callID":"call_1","state":{"status":"completed","output":"done"}},{"type":"text","text":"final answer"}]}]
            """;

        await using var stream = new FragmentStream(Encoding.UTF8.GetBytes(json), 4093);
        var projected = await NativeHistoryProjection.ReadAsync(stream, 32_000_000, 1_000_000);

        var messages = projected.EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length);
        var caller = messages[0];
        Assert.Equal("msg_caller", caller.GetProperty("info").GetProperty("id").GetString());
        Assert.Equal("ses_live", caller.GetProperty("info").GetProperty("sessionID").GetString());
        Assert.Equal("yes", caller.GetProperty("info").GetProperty("unknown").GetProperty("keep")[2].GetString());
        Assert.Equal("caller prompt", caller.GetProperty("parts")[0].GetProperty("text").GetString());
        var omission = caller.GetProperty("info").GetProperty("summary").GetProperty("diffs");
        Assert.Equal(JsonValueKind.Object, omission.ValueKind);
        Assert.True(omission.GetProperty("omitted").GetBoolean());
        Assert.Equal("omitted-native-user-summary-diffs", omission.GetProperty("$hvo").GetString());
        Assert.Equal("native-message", omission.GetProperty("recovery").GetProperty("kind").GetString());
        Assert.Equal("enclosing-info", omission.GetProperty("recovery").GetProperty("scope").GetString());
        Assert.Equal("$hvoNativeHistoryOmission", omission.GetProperty("recovery").GetProperty("receipt").GetString());
        var receipt = caller.GetProperty("info").GetProperty("$hvoNativeHistoryOmission");
        Assert.Equal("native-message-recovery", receipt.GetProperty("$hvo").GetString());
        Assert.Equal("ses_live", receipt.GetProperty("sessionID").GetString());
        Assert.Equal("msg_caller", receipt.GetProperty("messageID").GetString());

        var assistant = messages[1];
        var info = assistant.GetProperty("info");
        Assert.Equal("msg_caller", info.GetProperty("parentID").GetString());
        Assert.True(info.GetProperty("summary").GetBoolean());
        Assert.Equal("stop", info.GetProperty("finish").GetString());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("error").ValueKind);
        Assert.Equal(101, info.GetProperty("tokens").GetProperty("input").GetInt32());
        Assert.Equal(33, info.GetProperty("tokens").GetProperty("cache").GetProperty("read").GetInt32());
        Assert.Equal(0.125, info.GetProperty("cost").GetDouble());
        Assert.Equal("kept", info.GetProperty("future").GetString());
        Assert.Equal("call_1", assistant.GetProperty("parts")[0].GetProperty("callID").GetString());
        Assert.Equal("final answer", assistant.GetProperty("parts")[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task EscapedValuesAndUserRoleAfterSummarySurviveFragmentedReads()
    {
        const string json = """
            [{"info":{"summary":{"note":"line\n\"quoted\" café 🚀","diffs":[{"before":"old\\path","after":"new\tpath"}]},"future-name":"future-value","r\u006fle":"user","sessionID":"ses_\u0031","id":"msg_\u0031"},"parts":[{"type":"text","text":"A\u0026B"}]}]
            """;
        await using var stream = new FragmentStream(Encoding.UTF8.GetBytes(json), 1);

        var projected = await NativeHistoryProjection.ReadAsync(stream, 10_000, 10_000);

        var info = projected[0].GetProperty("info");
        Assert.Equal("user", info.GetProperty("role").GetString());
        Assert.Equal("line\n\"quoted\" café 🚀", info.GetProperty("summary").GetProperty("note").GetString());
        Assert.True(info.GetProperty("summary").GetProperty("diffs").GetProperty("omitted").GetBoolean());
        Assert.Equal("future-value", info.GetProperty("future-name").GetString());
        Assert.Equal("ses_1", info.GetProperty("$hvoNativeHistoryOmission").GetProperty("sessionID").GetString());
        Assert.Equal("msg_1", info.GetProperty("$hvoNativeHistoryOmission").GetProperty("messageID").GetString());
        Assert.Equal("A&B", projected[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task NonUserDiffFieldsArePreservedOrFailSafelyWhenRoleIsNotKnown()
    {
        const string assistant = """
            [{"info":{"id":"msg_a","sessionID":"ses_a","role":"assistant","summary":{"diffs":[{"before":"one","after":"two"}]}},"parts":[]}]
            """;
        var preserved = await NativeHistoryProjection.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(assistant)), 10_000, 10_000);
        Assert.Equal("one", preserved[0].GetProperty("info").GetProperty("summary").GetProperty("diffs")[0].GetProperty("before").GetString());

        const string unknownRole = """
            [{"info":{"summary":{"diffs":[1]},"id":"msg_a","sessionID":"ses_a","role":"assistant"},"parts":[]}]
            """;
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeHistoryProjection.ReadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(unknownRole)), 10_000, 10_000));

        const string missingLocator = """
            [{"info":{"summary":{"diffs":[1]},"role":"user"},"parts":[]}]
            """;
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeHistoryProjection.ReadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(missingLocator)), 10_000, 10_000));

        const string receiptCollision = """
            [{"info":{"summary":{"diffs":[1]},"id":"msg_a","sessionID":"ses_a","role":"user","$hvoNativeHistoryOmission":{"native":true}},"parts":[]}]
            """;
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeHistoryProjection.ReadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(receiptCollision)), 10_000, 10_000));
    }

    [Fact]
    public async Task TruncatedMalformedAndBoundedInputNeverReturnsAProjection()
    {
        await Assert.ThrowsAnyAsync<JsonException>(() => Read("[{\"info\":", 1000, 1000));
        await Assert.ThrowsAnyAsync<JsonException>(() => Read("[{\"info\": invalid}]", 1000, 1000));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read("[] ", 2, 1000));

        var largeNonDiff = "[{\"info\":{\"id\":\"msg_a\",\"sessionID\":\"ses_a\",\"role\":\"assistant\"},\"parts\":[{\"text\":\"" +
            new string('x', 2000) + "\"}]}]";
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(largeNonDiff, 10_000, 500));
    }

    [Fact]
    public async Task CancellationStopsProjection()
    {
        using var cancelled = new CancellationTokenSource();
        await using var stream = new CancellingStream("[{\"info\":{\"id\":\"msg_partial\"}}]"u8.ToArray(), cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeHistoryProjection.ReadAsync(
            stream, cancellationToken: cancelled.Token));
    }

    private static Task<JsonElement> Read(string json, int wireLimit, int projectionLimit) =>
        NativeHistoryProjection.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), wireLimit, projectionLimit);

    private sealed class FragmentStream(byte[] bytes, int fragmentSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(fragmentSize, buffer.Length)], cancellationToken);
    }

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer[..Math.Min(8, buffer.Length)], cancellationToken);
            cancellation.Cancel();
            return read;
        }
    }
}
