using System.Text.Json;

namespace HVO.AgentControl.OpenCode;

public sealed record UsageIdentity(string RuntimeId, string SessionId, string MessageId);

public sealed record OpenCodeUsage
{
    public required UsageIdentity Identity { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public long? CreatedAt { get; init; }
    public long? CompletedAt { get; init; }
    public long? TotalTokens { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
    public decimal? Cost { get; init; }
    public string? Currency { get; init; }
    public long ObservedAt { get; init; }

    public bool IsFinal => CompletedAt.HasValue;
}

public sealed record OpenCodeUsageParseResult(OpenCodeUsage? Usage, IReadOnlyList<string> Errors)
{
    public bool Accepted => Usage is not null;
}

public static class OpenCodeUsageParser
{
    public static OpenCodeUsageParseResult Parse(JsonElement message, UsageIdentity identity, long observedAt)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(identity.RuntimeId) || string.IsNullOrWhiteSpace(identity.SessionId) ||
            string.IsNullOrWhiteSpace(identity.MessageId))
            return Rejected("Identity must contain runtime, session and message IDs.");
        if (observedAt < 0) return Rejected("Observed timestamp is negative.");

        if (message.ValueKind != JsonValueKind.Object)
            return Rejected("Assistant message is not an object.");
        var info = message.TryGetProperty("info", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object
            ? wrapped : message;
        var role = StringProperty(info, "role") ?? StringProperty(info, "type");
        if (!string.Equals(role, "assistant", StringComparison.Ordinal))
            return Rejected("Message is not an assistant message.");
        if (StringProperty(info, "id") is { } nativeMessageId && nativeMessageId != identity.MessageId)
            return Rejected("Assistant message ID does not match the supplied identity.");
        if (StringProperty(info, "sessionID") is { } nativeSessionId && nativeSessionId != identity.SessionId)
            return Rejected("Assistant session ID does not match the supplied identity.");

        var time = ObjectProperty(info, "time");
        var created = ReadCounter(time, "created", "created timestamp", errors);
        var completed = ReadCounter(time, "completed", "completed timestamp", errors);
        if (completed.HasValue && !created.HasValue)
        {
            errors.Add("Completed timestamp cannot be trusted without a valid created timestamp.");
            completed = null;
        }
        else if (completed.HasValue && completed < created)
        {
            errors.Add("Completed timestamp precedes created timestamp.");
            completed = null;
        }

        var model = ObjectProperty(info, "model");
        var tokens = ObjectProperty(info, "tokens");
        var cache = ObjectProperty(tokens, "cache");
        return new(new OpenCodeUsage
        {
            Identity = identity,
            ProviderId = StringProperty(info, "providerID") ?? StringProperty(model, "providerID"),
            ModelId = StringProperty(info, "modelID") ?? StringProperty(model, "id"),
            CreatedAt = created,
            CompletedAt = completed,
            TotalTokens = ReadCounter(tokens, "total", "total tokens", errors),
            InputTokens = ReadCounter(tokens, "input", "input tokens", errors),
            OutputTokens = ReadCounter(tokens, "output", "output tokens", errors),
            ReasoningTokens = ReadCounter(tokens, "reasoning", "reasoning tokens", errors),
            CacheReadTokens = ReadCounter(cache, "read", "cache read tokens", errors),
            CacheWriteTokens = ReadCounter(cache, "write", "cache write tokens", errors),
            Cost = ReadCost(info, errors),
            Currency = StringProperty(info, "currency"),
            ObservedAt = observedAt
        }, errors);
    }

    private static OpenCodeUsageParseResult Rejected(string error) => new(null, [error]);

    private static JsonElement ObjectProperty(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;

    private static string? StringProperty(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? ReadCounter(JsonElement parent, string name, string label, List<string> errors)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number < 0 || number != decimal.Truncate(number) || number > long.MaxValue)
        {
            errors.Add($"Malformed {label}; value was ignored.");
            return null;
        }
        return (long)number;
    }

    private static decimal? ReadCost(JsonElement parent, List<string> errors)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty("cost", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var cost) || cost < 0)
        {
            errors.Add("Malformed provider cost; value was ignored.");
            return null;
        }
        return cost;
    }
}

public static class OpenCodeUsageMerger
{
    public static OpenCodeUsage Merge(OpenCodeUsage left, OpenCodeUsage right)
    {
        if (left.Identity != right.Identity) throw new ArgumentException("Usage identities must match.");
        return left with
        {
            ProviderId = MaxText(left.ProviderId, right.ProviderId),
            ModelId = MaxText(left.ModelId, right.ModelId),
            CreatedAt = Min(left.CreatedAt, right.CreatedAt),
            CompletedAt = Max(left.CompletedAt, right.CompletedAt),
            TotalTokens = Max(left.TotalTokens, right.TotalTokens),
            InputTokens = Max(left.InputTokens, right.InputTokens),
            OutputTokens = Max(left.OutputTokens, right.OutputTokens),
            ReasoningTokens = Max(left.ReasoningTokens, right.ReasoningTokens),
            CacheReadTokens = Max(left.CacheReadTokens, right.CacheReadTokens),
            CacheWriteTokens = Max(left.CacheWriteTokens, right.CacheWriteTokens),
            Cost = Max(left.Cost, right.Cost),
            Currency = MaxText(left.Currency, right.Currency),
            ObservedAt = Math.Max(left.ObservedAt, right.ObservedAt)
        };
    }

    public static OpenCodeUsage MergeAll(IEnumerable<OpenCodeUsage> snapshots)
    {
        using var enumerator = snapshots.GetEnumerator();
        if (!enumerator.MoveNext()) throw new ArgumentException("At least one usage snapshot is required.");
        var result = enumerator.Current;
        while (enumerator.MoveNext()) result = Merge(result, enumerator.Current);
        return result;
    }

    private static long? Min(long? left, long? right) => left.HasValue && right.HasValue ? Math.Min(left.Value, right.Value) : left ?? right;
    private static long? Max(long? left, long? right) => left.HasValue && right.HasValue ? Math.Max(left.Value, right.Value) : left ?? right;
    private static decimal? Max(decimal? left, decimal? right) => left.HasValue && right.HasValue ? Math.Max(left.Value, right.Value) : left ?? right;
    private static string? MaxText(string? left, string? right) => left is null ? right : right is null ? left : string.CompareOrdinal(left, right) >= 0 ? left : right;
}
