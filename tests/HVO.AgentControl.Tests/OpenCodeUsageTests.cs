using System.Text.Json;
using HVO.AgentControl.OpenCode;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class OpenCodeUsageTests
{
    private static readonly UsageIdentity Identity = new("runtime-1", "ses_test", "msg_test");

    [Fact]
    public void ParsesInfoWrappedAssistantUsageWithoutStepFinishDuplication()
    {
        var result = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            modelID = "gpt-5.6-luna",
            providerID = "openai",
            time = new { created = 100L, completed = 200L },
            cost = 0.0m,
            tokens = new { total = 12L, input = 5L, output = 4L, reasoning = 3L, cache = new { read = 2L, write = 0L } }
        }));

        Assert.True(result.Accepted);
        Assert.Empty(result.Errors);
        var usage = result.Usage!;
        Assert.Equal("openai", usage.ProviderId);
        Assert.Equal("gpt-5.6-luna", usage.ModelId);
        Assert.Equal(12, usage.TotalTokens);
        Assert.Equal(2, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheWriteTokens);
        Assert.Equal(0m, usage.Cost);
        Assert.True(usage.IsFinal);
        Assert.Null(usage.Currency);
        Assert.Null(OpenCodeUsageParser.Parse(JsonSerializer.SerializeToElement(new { type = "step-finish", cost = 1 }), Identity, 300).Usage);
    }

    [Fact]
    public void ParsesDirectAssistantModelVariantAndKeepsMissingDistinctFromZero()
    {
        var result = Parse(new
        {
            id = "msg_test",
            type = "assistant",
            model = new { providerID = "anthropic", id = "claude" },
            time = new { created = 100L },
            tokens = new { input = 0L, output = 0L, reasoning = 0L, cache = new { read = 0L, write = 0L } }
        });

        Assert.True(result.Accepted);
        Assert.False(result.Usage!.IsFinal);
        Assert.Equal("anthropic", result.Usage.ProviderId);
        Assert.Equal("claude", result.Usage.ModelId);
        Assert.Equal(0, result.Usage.InputTokens);
        Assert.Null(result.Usage.TotalTokens);
        Assert.Null(result.Usage.Cost);
        Assert.Null(result.Usage.Currency);
    }

    [Fact]
    public void MalformedCountersAndTimesAreIgnoredWithoutFabrication()
    {
        var result = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            time = new { created = -1L, completed = 1L },
            tokens = new { input = -2L, output = 1.5m, reasoning = "bad", cache = new { read = 1L, write = 9223372036854775808m } },
            cost = -0.1m
        }));

        Assert.True(result.Accepted);
        Assert.True(result.Errors.Count >= 5);
        Assert.Null(result.Usage!.CreatedAt);
        Assert.Null(result.Usage.CompletedAt);
        Assert.Null(result.Usage.InputTokens);
        Assert.Null(result.Usage.OutputTokens);
        Assert.Null(result.Usage.ReasoningTokens);
        Assert.Equal(1, result.Usage.CacheReadTokens);
        Assert.Null(result.Usage.CacheWriteTokens);
        Assert.Null(result.Usage.Cost);
        Assert.False(result.Usage.IsFinal);
        Assert.False(Parse(Assistant(new { id = "msg_test", sessionID = "ses_test", role = "assistant", time = new { created = 1L } }), -1).Accepted);
    }

    [Fact]
    public void MergeIsIdempotentOrderIndependentAndFinalCannotRegress()
    {
        var partial = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            modelID = "model-old",
            providerID = "provider-old",
            time = new { created = 100L },
            cost = 0m,
            tokens = new { input = 0L, output = 0L, reasoning = 0L, cache = new { read = 0L, write = 0L } }
        }), 100).Usage!;
        var final = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            modelID = "model-final",
            providerID = "provider-final",
            time = new { created = 100L, completed = 300L },
            cost = 1.25m,
            tokens = new { total = 9L, input = 4L, output = 3L, reasoning = 2L, cache = new { read = 7L, write = 1L } }
        }), 300).Usage!;

        var forward = OpenCodeUsageMerger.Merge(partial, final);
        var reverse = OpenCodeUsageMerger.Merge(final, partial);
        var repeated = OpenCodeUsageMerger.Merge(forward, partial);
        Assert.Equal(forward, reverse);
        Assert.Equal(forward, repeated);
        Assert.True(forward.IsFinal);
        Assert.Equal(300, forward.CompletedAt);
        Assert.Equal(4, forward.InputTokens);
        Assert.Equal(1.25m, forward.Cost);
        Assert.Equal(300, forward.ObservedAt);
    }

    [Fact]
    public void MergeRejectsDifferentIdentities()
    {
        var usage = Parse(Assistant(new { id = "msg_test", sessionID = "ses_test", role = "assistant", time = new { created = 1L } })).Usage!;
        var other = usage with { Identity = usage.Identity with { MessageId = "msg_other" } };
        Assert.Throws<ArgumentException>(() => OpenCodeUsageMerger.Merge(usage, other));
    }

    private static OpenCodeUsageParseResult Parse(object info, long observedAt = 1) =>
        OpenCodeUsageParser.Parse(JsonSerializer.SerializeToElement(new { info, parts = Array.Empty<object>() }), Identity, observedAt);

    private static object Assistant(object info) => info;
}
