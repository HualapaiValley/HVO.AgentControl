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
    public void AttributesParentSummaryFinishAndCacheInclusiveInput()
    {
        var result = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            parentID = "msg_parent",
            summary = true,
            finish = "stop",
            time = new { created = 100L, completed = 200L },
            tokens = new { input = 5L, cache = new { read = 2L, write = 3L } }
        })).Usage!;

        Assert.Equal("msg_parent", result.ParentMessageId);
        Assert.True(result.IsSummary);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(10, result.EffectiveInputTokens);
    }

    [Fact]
    public void MissingCacheCountersKeepEffectiveInputUnknown()
    {
        var result = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            time = new { created = 100L },
            tokens = new { input = 5L }
        })).Usage!;

        Assert.Null(result.EffectiveInputTokens);
    }

    [Fact]
    public void ValidIndividualCountersCannotWrapEffectiveInputNegative()
    {
        var result = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            time = new { created = 100L, completed = 200L },
            tokens = new { input = long.MaxValue, cache = new { read = 1L, write = 0L } }
        }));

        Assert.True(result.Accepted);
        Assert.Equal(long.MaxValue, result.Usage!.InputTokens);
        Assert.Equal(1, result.Usage.CacheReadTokens);
        Assert.Equal(0, result.Usage.CacheWriteTokens);
        // The cache-inclusive sum would overflow Int64; it must be unknown, never a wrapped negative.
        Assert.Null(result.Usage.EffectiveInputTokens);
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

    [Theory]
    [InlineData("provider", "nested-provider", "model", "model")]
    [InlineData("provider", "provider", "model", "nested-model")]
    public void RejectsConflictingTopLevelAndNestedModelIdentity(string provider, string nestedProvider,
        string model, string nestedModel)
    {
        var result = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            providerID = provider,
            modelID = model,
            model = new { providerID = nestedProvider, id = nestedModel },
            time = new { created = 100L }
        }));

        Assert.False(result.Accepted);
        Assert.Single(result.Errors);
        Assert.Contains("disagree", result.Errors[0]);
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
    public void FinalSnapshotWinsEvenWhenItsCountersAreLower()
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

        var merged = OpenCodeUsageMerger.Merge(partial with { InputTokens = 100 }, final with { InputTokens = 80 });
        Assert.Equal(final with { InputTokens = 80 }, merged);
        Assert.True(merged.IsFinal);
        Assert.Equal(80, merged.InputTokens);
        Assert.Equal("provider-final", merged.ProviderId);
        Assert.Equal("model-final", merged.ModelId);
        Assert.Equal(1.25m, merged.Cost);
    }

    [Fact]
    public void LateStaleProvisionalSnapshotCannotDemoteFinalSnapshot()
    {
        var final = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            modelID = "final",
            providerID = "provider",
            time = new { created = 100L, completed = 300L },
            tokens = new { input = 80L }
        }), 300).Usage!;
        var stale = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            modelID = "partial",
            providerID = "provider",
            time = new { created = 100L },
            tokens = new { input = 100L }
        }), 400).Usage!;

        Assert.Equal(final, OpenCodeUsageMerger.Merge(final, stale));
        Assert.Equal(final, OpenCodeUsageMerger.Merge(stale, final));
    }

    [Fact]
    public void EqualRankConflictsChooseOneWholeSnapshotInEitherOrder()
    {
        var usd = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            modelID = "model-a",
            providerID = "provider-a",
            time = new { created = 100L, completed = 300L },
            cost = 1m,
            currency = "USD",
            tokens = new { input = 10L }
        }), 300).Usage!;
        var eur = Parse(Assistant(new
        {
            id = "msg_test",
            sessionID = "ses_test",
            role = "assistant",
            modelID = "model-b",
            providerID = "provider-b",
            time = new { created = 100L, completed = 300L },
            cost = 2m,
            currency = "EUR",
            tokens = new { input = 20L }
        }), 300).Usage!;

        var forward = OpenCodeUsageMerger.Merge(usd, eur);
        var reverse = OpenCodeUsageMerger.Merge(eur, usd);
        Assert.Equal(forward, reverse);
        Assert.True(forward == usd || forward == eur);
        Assert.Equal(forward.ProviderId == "provider-a" ? "model-a" : "model-b", forward.ModelId);
        Assert.Equal(forward.ProviderId == "provider-a" ? "USD" : "EUR", forward.Currency);
        Assert.Equal(forward.ProviderId == "provider-a" ? 10 : 20, forward.InputTokens);
    }

    [Fact]
    public void NullAndLiteralDashUseDistinctStableMergeOrdering()
    {
        var missing = Snapshot(provider: null, model: "model");
        var literal = Snapshot(provider: "-", model: "model");

        var forward = OpenCodeUsageMerger.Merge(missing, literal);
        var reverse = OpenCodeUsageMerger.Merge(literal, missing);
        Assert.Same(literal, forward);
        Assert.Same(literal, reverse);
        Assert.Same(literal, OpenCodeUsageMerger.Merge(literal, literal));
    }

    [Fact]
    public void DelimiterContainingProviderAndModelUseStableMergeOrdering()
    {
        var splitProvider = Snapshot(provider: "a|b", model: "c");
        var splitModel = Snapshot(provider: "a", model: "b|c");

        var forward = OpenCodeUsageMerger.Merge(splitProvider, splitModel);
        var reverse = OpenCodeUsageMerger.Merge(splitModel, splitProvider);
        Assert.NotEqual(splitProvider, splitModel);
        Assert.Same(forward, reverse);
        Assert.Same(forward, OpenCodeUsageMerger.MergeAll([splitProvider, splitModel]));
        Assert.Same(forward, OpenCodeUsageMerger.MergeAll([splitModel, splitProvider]));
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

    private static OpenCodeUsage Snapshot(string? provider, string? model) => new()
    {
        Identity = Identity,
        ProviderId = provider,
        ModelId = model,
        CreatedAt = 100,
        CompletedAt = 300,
        InputTokens = 10,
        ObservedAt = 300
    };
}
