using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Ssh;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HVO.AgentControl.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task SseHandlesPartialUtf8MultilineCommentsAndUnknownEvents()
    {
        var content = ": heartbeat\r\ndata: {\r\ndata: \"type\":\"future.event\", \"text\":\"café 🛰\"}\r\n\r\n" +
                      "data: {\"type\":\"second\"}\n\n" + "data: {\"incomplete\":";
        await using var stream = new FragmentStream(Encoding.UTF8.GetBytes(content));
        var events = new List<JsonElement>();
        await foreach (var item in SseReader.Read(stream)) events.Add(item);
        Assert.Equal(2, events.Count);
        Assert.Equal("café 🛰", events[0].GetProperty("text").GetString());
        Assert.Equal("second", events[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task SseRejectsOversizedAndMalformedFrames()
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in SseReader.Read(new MemoryStream(Encoding.UTF8.GetBytes("data: " + new string('a', 1000))), 64)) { }
        });
        await Assert.ThrowsAnyAsync<JsonException>(async () =>
        {
            await foreach (var _ in SseReader.Read(new MemoryStream("data: invalid\n\n"u8.ToArray()))) { }
        });
    }

    [Fact]
    public void PathsAndPromptTransportRemainDistinct()
    {
        const string path = "/work/a 'quoted' \"double\" $(touch nope);\nnext";
        ControlStore.ValidatePath(path);
        Assert.Equal("'a'\"'\"'b'", BootstrapScript.Quote("a'b"));
        Assert.Contains("directory=%2Fwork", OpenCodeClient.Scope("/session", path));
        Assert.False(ControlStore.IsWithin("/work-elsewhere/a", "/work"));
        Assert.True(ControlStore.IsWithin("/work/a", "/work"));
        Assert.Throws<ControlException>(() => ControlStore.ValidatePath("/work/../outside"));
        Assert.Throws<ControlException>(() => BootstrapScript.Quote("bad\0path"));
    }

    [Fact]
    public async Task RemoteCommandWrapperPreservesQuotedDataAndExplicitPath()
    {
        if (!File.Exists("/bin/sh")) return;
        const string value = "a 'quoted' \"double\" $(printf WRONG);\nsecond line";
        var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(RemoteEnvironment.Command("printf '%s\\n' \"$PATH\"; printf '%s' " + BootstrapScript.Quote(value)));
        start.Environment["PATH"] = "/test/explicit/bin:/usr/bin:/bin";
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.StartsWith("/test/explicit/bin:/usr/bin:/bin", output);
        Assert.EndsWith("\n" + value, output);
    }

    private sealed class FragmentStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
