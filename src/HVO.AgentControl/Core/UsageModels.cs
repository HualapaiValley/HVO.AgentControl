namespace HVO.AgentControl.Core;

public sealed class ModelUsageRecord
{
    public string RuntimeId { get; set; } = "";
    public string NativeSessionId { get; set; } = "";
    public string NativeMessageId { get; set; } = "";
    public string? WorkerId { get; set; }
    public string SessionRole { get; set; } = "Unknown";
    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }
    public long? CreatedAt { get; set; }
    public long? CompletedAt { get; set; }
    public long? TotalTokens { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? ReasoningTokens { get; set; }
    public long? CacheReadTokens { get; set; }
    public long? CacheWriteTokens { get; set; }
    public decimal? ProviderCost { get; set; }
    public string? Currency { get; set; }
    public string CostProvenance { get; set; } = "";
    public long ObservedAt { get; set; }
    public bool SeenInTranscript { get; set; }
    public bool SeenInCommandResult { get; set; }
}

public sealed record UsageQuery(string? WorkerId = null, string? ProviderId = null, string? ModelId = null, long? From = null, long? To = null);
public sealed record UsageMetric(decimal? Sum, int Present, int Missing);
public sealed record UsageGroup(string SessionRole, string? WorkerId, string? ProviderId, string? ModelId, string? Currency,
    int Messages, int FinalMessages, UsageMetric TotalTokens, UsageMetric InputTokens, UsageMetric OutputTokens,
    UsageMetric ReasoningTokens, UsageMetric CacheReadTokens, UsageMetric CacheWriteTokens, UsageMetric ProviderCost);
public sealed record UsageCoverage(string Scope, long? EarliestCreatedAt, long? LatestCreatedAt, int Messages,
    int FinalMessages, int ProvisionalMessages, int MissingCreatedAt, int WorkersWithHistoryGap);
public sealed record UsageReport(UsageCoverage Coverage, IReadOnlyList<UsageGroup> Groups, IReadOnlyList<ModelUsageRecord> Rows);
public sealed record UsageBackfillResult(int TranscriptMessages, int CommandMessages, int Accepted, int Changed);
