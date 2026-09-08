using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.OpenCode;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class NativeTurnEvidenceTests
{
    [Fact]
    public void MarkedAutoCompactionPreservesFinalResultButExcludesSummaryAndNextTask()
    {
        var history = new[]
        {
            User(1), Assistant(2, 1, "Earlier progress"), Compact(3), Assistant(4, 3, "Internal summary", summary: true),
            Continue(5), Assistant(6, 5, "CLEAN SHA abc123"), User(7), Assistant(8, 7, "Unrelated result")
        };
        var result = NativeTurnEvidence.AssistantMessages(history.Reverse().ToArray(), "msg_01");
        Assert.Equal(new[] { "msg_02", "msg_06" }, result.Select(x => x.GetProperty("info").GetProperty("id").GetString()));
        Assert.Contains("CLEAN SHA abc123", ControlStoreText(result));
        Assert.DoesNotContain("Internal summary", ControlStoreText(result));
        Assert.DoesNotContain("Unrelated", ControlStoreText(result));
    }

    [Fact]
    public void RepeatedCompactionChainsRemainWithinTheOriginalTurn()
    {
        var history = new[] { User(1), Compact(2), Continue(3), Assistant(4, 3, "Progress"), Compact(5), Continue(6), Assistant(7, 6, "Final") };
        Assert.Equal(2, NativeTurnEvidence.AssistantMessages(history, "msg_01").Length);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void UnmarkedOrManualContinuationCannotBeAttributed(bool automatic, bool synthetic, bool marker)
    {
        var history = new[] { User(1), Assistant(2, 1, "Original"), Compact(3, automatic), Continue(4, synthetic, marker), Assistant(5, 4, "Other") };
        Assert.Single(NativeTurnEvidence.AssistantMessages(history, "msg_01"));
    }

    [Fact]
    public void MarkerAloneOrMissingCallerCannotBridgeHistory()
    {
        var history = new[] { User(1), Continue(2), Assistant(3, 2, "Other") };
        Assert.Empty(NativeTurnEvidence.AssistantMessages(history, "msg_01"));
        Assert.Empty(NativeTurnEvidence.AssistantMessages(history, "msg_missing"));
    }

    [Fact]
    public void OtherSessionMessagesCannotContaminateEvidence()
    {
        var history = new[] { User(1), Assistant(2, 1, "Wrong session", session: "ses_other"), Assistant(3, 1, "Right session") };
        Assert.Equal("msg_03", Assert.Single(NativeTurnEvidence.AssistantMessages(history, "msg_01")).GetProperty("info").GetProperty("id").GetString());
    }

    [Fact]
    public void TerminalEvidenceRequiresCompletedErrorAndRejectsToolCallBoundary()
    {
        var incompleteError = JsonSerializer.SerializeToElement(new
        {
            info = new { id = "msg_error", role = "assistant", time = new { created = 1L }, error = new { name = "Error" } },
            parts = Array.Empty<object>()
        });
        var completedError = JsonSerializer.SerializeToElement(new
        {
            info = new { id = "msg_error", role = "assistant", time = new { created = 1L, completed = 2L }, error = new { name = "Error" } },
            parts = Array.Empty<object>()
        });
        var settledToolCall = JsonSerializer.SerializeToElement(new
        {
            info = new { id = "msg_tool", role = "assistant", time = new { created = 1L, completed = 2L }, finish = "tool-calls" },
            parts = new[] { new { type = "tool", state = new { status = "completed" } } }
        });
        var finishedToolCall = JsonSerializer.SerializeToElement(new
        {
            info = new { id = "msg_tool", role = "assistant", time = new { created = 1L, completed = 2L }, finish = "stop" },
            parts = new[] { new { type = "tool", state = new { status = "completed" } } }
        });
        Assert.False(NativeTurnEvidence.IsTerminalAssistantResponse(incompleteError));
        Assert.True(NativeTurnEvidence.IsTerminalAssistantResponse(completedError));
        Assert.False(NativeTurnEvidence.IsTerminalAssistantResponse(settledToolCall));
        Assert.False(NativeTurnEvidence.IsTerminalAssistantResponse(finishedToolCall));
    }

    [Fact]
    public void CompactionContinuationKeepsTerminalClassificationWithinOriginalTurn()
    {
        var history = new[]
        {
            User(1), Compact(2), Continue(3),
            JsonSerializer.SerializeToElement(new { info = new { id = "msg_04", role = "assistant", parentID = "msg_03", sessionID = "ses_test", time = new { created = 4L, completed = 5L }, finish = "stop" }, parts = new[] { new { type = "text", text = "Final" } } }),
            User(6), Assistant(7, 6, "Next")
        };
        var result = NativeTurnEvidence.AssistantMessages(history, "msg_01");
        Assert.Equal(["msg_04"], result.Select(x => x.GetProperty("info").GetProperty("id").GetString()));
        Assert.True(NativeTurnEvidence.IsTerminalAssistantResponse(result[^1]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("tool-calls")]
    public void CompletedMessageWithoutTerminalFinishDoesNotEndTurn(string? finish)
    {
        var info = new Dictionary<string, object> { ["time"] = new { created = 1L, completed = 2L } };
        if (finish is not null) info["finish"] = finish;
        var message = JsonSerializer.SerializeToElement(new { info, parts = new[] { new { type = "text", text = "Still working" } } });

        Assert.False(NativeTurnEvidence.IsTerminalAssistantResponse(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tool-calls")]
    [InlineData("unknown")]
    public void CompletedErrorWithInterruptedToolEndsTurnWithoutTerminalFinish(string? finish)
    {
        var info = new Dictionary<string, object>
        {
            ["time"] = new { created = 1L, completed = 2L },
            ["error"] = new { name = "MessageAbortedError" }
        };
        if (finish is not null) info["finish"] = finish;
        var message = JsonSerializer.SerializeToElement(new
        {
            info,
            parts = new[] { new { type = "tool", state = new { status = "error", metadata = new { interrupted = true } } } }
        });

        Assert.True(NativeTurnEvidence.IsTerminalAssistantResponse(message));
    }

    [Theory]
    [InlineData("completed", true, false, true)]
    [InlineData("running", true, false, false)]
    [InlineData("error", false, true, true)]
    [InlineData("error", false, false, false)]
    [InlineData("completed", false, false, false)]
    public void StopWithToolsRequiresSettledProviderCallOrInterruptedOrphan(string status, bool providerExecuted, bool interrupted, bool expected)
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            info = new { time = new { created = 1L, completed = 2L }, finish = "stop" },
            parts = new[] { new { type = "tool", metadata = new { providerExecuted }, state = new { status, metadata = new { interrupted } } } }
        });

        Assert.Equal(expected, NativeTurnEvidence.IsTerminalAssistantResponse(message));
    }

    [Fact]
    public void CompletedStructuredOutputIsTerminalAtToolBoundary()
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            info = new { time = new { created = 1L, completed = 2L }, finish = "tool-calls", structured = new { result = "complete" } },
            parts = new[] { new { type = "tool", state = new { status = "completed" } } }
        });

        Assert.True(NativeTurnEvidence.IsTerminalAssistantResponse(message));
    }

    private static string ControlStoreText(JsonElement[] result) => HVO.AgentControl.Infrastructure.ControlStore.ResponseText(Json.Write(new { messages = result }));
    private static JsonElement User(int id) => Message(id, "user", null, new[] { new { type = "text", text = "Task" } });
    private static JsonElement Compact(int id, bool automatic = true) => Message(id, "user", null, new[] { new { type = "compaction", auto = automatic } });
    private static JsonElement Continue(int id, bool synthetic = true, bool marker = true) => Message(id, "user", null,
        new[] { new { type = "text", text = "Continue if you have next steps", synthetic, metadata = new { compaction_continue = marker } } });
    private static JsonElement Assistant(int id, int parent, string text, bool summary = false, string session = "ses_test") =>
        Message(id, "assistant", $"msg_{parent:00}", new[] { new { type = "text", text } }, summary, session);
    private static JsonElement Message(int id, string role, string? parent, object parts, bool summary = false, string session = "ses_test") =>
        JsonSerializer.SerializeToElement(new { info = new { id = $"msg_{id:00}", role, parentID = parent, sessionID = session, summary, time = new { created = id } }, parts });
}
