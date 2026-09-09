using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.OpenCode;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<UsageBackfillResult> BackfillUsage() => Write(async db =>
    {
        var workers = await db.Workers.ToDictionaryAsync(x => x.Id);
        var transcriptCount = 0;
        var commandCount = 0;
        var accepted = 0;
        var changed = 0;
        var observations = new List<UsageObservation>();

        foreach (var message in await db.Messages.AsNoTracking().ToListAsync())
        {
            if (!workers.TryGetValue(message.WorkerId, out var worker)) continue;
            transcriptCount++;
            observations.Add(new(new(worker.RuntimeId, worker.NativeSessionId, message.NativeId), worker.Id, worker.Role,
                message.Json, message.NativeCreatedAt, UsageSource.Transcript));
        }

        var commands = await db.Commands.AsNoTracking().Where(x => x.ResultJson != "").ToListAsync();
        foreach (var command in commands)
        {
            try
            {
                using var document = JsonDocument.Parse(command.ResultJson);
                foreach (var message in AssistantMessages(document.RootElement))
                {
                    commandCount++;
                    if (!TryIdentity(command.RuntimeId, message, out var identity)) continue;
                    var worker = command.WorkerId is not null ? workers.GetValueOrDefault(command.WorkerId) : null;
                    observations.Add(new(identity, command.WorkerId, worker?.Role ?? "Unknown", message.GetRawText(),
                        command.UpdatedAt, UsageSource.CommandResult));
                }
            }
            catch (JsonException) { }
        }
        foreach (var observation in observations.OrderBy(x => x.ObservedAt).ThenBy(x => x.Source)
                     .ThenBy(x => x.Identity.RuntimeId, StringComparer.Ordinal).ThenBy(x => x.Identity.SessionId, StringComparer.Ordinal)
                     .ThenBy(x => x.Identity.MessageId, StringComparer.Ordinal).ThenBy(x => x.Json, StringComparer.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(observation.Json);
                var result = await UpsertUsage(db, observation.Identity, observation.WorkerId, observation.SessionRole,
                    document.RootElement, observation.ObservedAt, observation.Source);
                if (result.Accepted) accepted++;
                if (result.Changed) changed++;
            }
            catch (JsonException) { }
        }
        return new UsageBackfillResult(transcriptCount, commandCount, accepted, changed);
    });

    public Task<UsageReport> Usage(UsageQuery query) => Read(async db =>
    {
        Validate(query);
        var ledger = db.ModelUsage.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.WorkerId)) ledger = ledger.Where(x => x.WorkerId == query.WorkerId);
        if (!string.IsNullOrWhiteSpace(query.ProviderId)) ledger = ledger.Where(x => x.ProviderId == query.ProviderId);
        if (!string.IsNullOrWhiteSpace(query.ModelId)) ledger = ledger.Where(x => x.ModelId == query.ModelId);
        if (query.From.HasValue) ledger = ledger.Where(x => x.CreatedAt.HasValue && x.CreatedAt >= query.From);
        if (query.To.HasValue) ledger = ledger.Where(x => x.CreatedAt.HasValue && x.CreatedAt <= query.To);
        var rows = (await ledger.ToListAsync()).OrderBy(x => x.CreatedAt is null).ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.RuntimeId, StringComparer.Ordinal).ThenBy(x => x.NativeSessionId, StringComparer.Ordinal)
            .ThenBy(x => x.NativeMessageId, StringComparer.Ordinal).ToList();

        var workerIds = rows.Where(x => x.WorkerId is not null).Select(x => x.WorkerId!).Distinct().ToList();
        var gaps = workerIds.Count == 0 ? 0 : await db.Workers.CountAsync(x => workerIds.Contains(x.Id) && x.HistoryGap);
        var coverage = new UsageCoverage("retained-evidence-only", rows.Where(x => x.CreatedAt.HasValue).Min(x => x.CreatedAt),
            rows.Where(x => x.CreatedAt.HasValue).Max(x => x.CreatedAt), rows.Count, rows.Count(x => x.CompletedAt.HasValue),
            rows.Count(x => !x.CompletedAt.HasValue), rows.Count(x => !x.CreatedAt.HasValue), gaps);
        var groups = rows.GroupBy(x => new { x.SessionRole, x.WorkerId, x.ProviderId, x.ModelId, x.Currency })
            .Select(group => new UsageGroup(group.Key.SessionRole, group.Key.WorkerId, group.Key.ProviderId, group.Key.ModelId,
                group.Key.Currency, group.Count(), group.Count(x => x.CompletedAt.HasValue), Metric(group.Select(x => x.TotalTokens)),
                Metric(group.Select(x => x.InputTokens)), Metric(group.Select(x => x.OutputTokens)), Metric(group.Select(x => x.ReasoningTokens)),
                Metric(group.Select(x => x.CacheReadTokens)), Metric(group.Select(x => x.CacheWriteTokens)), Metric(group.Select(x => x.ProviderCost))))
            .OrderBy(x => x.SessionRole, StringComparer.Ordinal).ThenBy(x => x.WorkerId, StringComparer.Ordinal)
            .ThenBy(x => x.ProviderId, StringComparer.Ordinal).ThenBy(x => x.ModelId, StringComparer.Ordinal)
            .ThenBy(x => x.Currency, StringComparer.Ordinal).ToList();
        return new UsageReport(coverage, groups, rows);
    });

    public async Task<string> ExportUsageCsv(UsageQuery query)
    {
        var report = await Usage(query);
        var output = new StringBuilder("runtimeId,nativeSessionId,nativeMessageId,workerId,sessionRole,providerId,modelId,createdAt,completedAt,totalTokens,inputTokens,outputTokens,reasoningTokens,cacheReadTokens,cacheWriteTokens,providerCost,currency,costProvenance,observedAt,seenInTranscript,seenInCommandResult,parentNativeMessageId,commandId,coordinationRunId,controlSessionId,controlSessionGeneration,recoveryIntentId\r\n");
        foreach (var row in report.Rows)
        {
            var values = new string?[] { row.RuntimeId, row.NativeSessionId, row.NativeMessageId, row.WorkerId, row.SessionRole,
                row.ProviderId, row.ModelId, Number(row.CreatedAt), Number(row.CompletedAt), Number(row.TotalTokens), Number(row.InputTokens),
                Number(row.OutputTokens), Number(row.ReasoningTokens), Number(row.CacheReadTokens), Number(row.CacheWriteTokens),
                Number(row.ProviderCost), row.Currency, row.CostProvenance, Number(row.ObservedAt), row.SeenInTranscript ? "true" : "false",
                row.SeenInCommandResult ? "true" : "false", row.ParentNativeMessageId, row.CommandId, row.CoordinationRunId,
                row.ControlSessionId, Number(row.ControlSessionGeneration), row.RecoveryIntentId };
            output.AppendJoin(',', values.Select(Csv)).Append("\r\n");
        }
        return output.ToString();
    }

    internal static async Task<bool> ObserveUsage(ControlDb db, WorkerRecord worker, JsonElement message, long observedAt)
    {
        var result = await UpsertUsage(db, new(worker.RuntimeId, worker.NativeSessionId, message.GetProperty("info").GetProperty("id").GetString()!),
            worker.Id, worker.Role, message, observedAt, UsageSource.Transcript);
        return result.Changed;
    }

    private static async Task<UsageUpsertResult> UpsertUsage(ControlDb db, UsageIdentity identity, string? workerId,
        string sessionRole, JsonElement message, long observedAt, UsageSource source)
    {
        var parsed = OpenCodeUsageParser.Parse(message, identity, observedAt, source);
        if (parsed.Usage is null) return new(false, false);
        var incoming = parsed.Usage;
        var row = await db.ModelUsage.FindAsync(identity.RuntimeId, identity.SessionId, identity.MessageId);
        if (row is null)
        {
            row = new ModelUsageRecord
            {
                RuntimeId = identity.RuntimeId,
                NativeSessionId = identity.SessionId,
                NativeMessageId = identity.MessageId,
                WorkerId = workerId,
                SessionRole = sessionRole
            };
            Apply(row, incoming);
            await ApplyAttribution(db, row, workerId, message);
            MarkEvidence(row, source);
            db.ModelUsage.Add(row);
            return new(true, true);
        }

        var changed = MarkEvidence(row, source);
        if (row.WorkerId is null && workerId is not null) { row.WorkerId = workerId; changed = true; }
        if (row.SessionRole == "Unknown" && sessionRole != "Unknown") { row.SessionRole = sessionRole; changed = true; }
        changed |= await ApplyAttribution(db, row, workerId, message);
        var existing = ToUsage(row);
        if (!SameRevision(existing, incoming))
        {
            var merged = OpenCodeUsageMerger.Merge(existing, incoming);
            if (merged != existing) { Apply(row, merged); changed = true; }
        }
        return new(true, changed);
    }

    private static IEnumerable<JsonElement> AssistantMessages(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            return messages.EnumerateArray().Where(IsAssistant).ToArray();
        return IsAssistant(root) ? [root] : [];
    }

    private static bool IsAssistant(JsonElement message) => message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
        info.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String && role.GetString() == "assistant";

    private static bool TryIdentity(string runtimeId, JsonElement message, out UsageIdentity identity)
    {
        var info = message.GetProperty("info");
        var sessionId = info.TryGetProperty("sessionID", out var session) && session.ValueKind == JsonValueKind.String ? session.GetString() : null;
        var messageId = info.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        identity = new(runtimeId, sessionId ?? "", messageId ?? "");
        return sessionId is not null && messageId is not null;
    }

    private static OpenCodeUsage ToUsage(ModelUsageRecord row) => new()
    {
        Identity = new(row.RuntimeId, row.NativeSessionId, row.NativeMessageId),
        ProviderId = row.ProviderId,
        ModelId = row.ModelId,
        CreatedAt = row.CreatedAt,
        CompletedAt = row.CompletedAt,
        TotalTokens = row.TotalTokens,
        InputTokens = row.InputTokens,
        OutputTokens = row.OutputTokens,
        ReasoningTokens = row.ReasoningTokens,
        CacheReadTokens = row.CacheReadTokens,
        CacheWriteTokens = row.CacheWriteTokens,
        Cost = row.ProviderCost,
        Currency = row.Currency,
        ObservedAt = row.ObservedAt,
        Source = row.SeenInTranscript ? UsageSource.Transcript : UsageSource.CommandResult
    };

    private static void Apply(ModelUsageRecord row, OpenCodeUsage usage)
    {
        row.ProviderId = usage.ProviderId; row.ModelId = usage.ModelId; row.CreatedAt = usage.CreatedAt; row.CompletedAt = usage.CompletedAt;
        row.TotalTokens = usage.TotalTokens; row.InputTokens = usage.InputTokens; row.OutputTokens = usage.OutputTokens;
        row.ReasoningTokens = usage.ReasoningTokens; row.CacheReadTokens = usage.CacheReadTokens; row.CacheWriteTokens = usage.CacheWriteTokens;
        row.ProviderCost = usage.Cost; row.Currency = usage.Currency; row.CostProvenance = usage.Cost.HasValue ? "provider-reported" : "";
        row.ObservedAt = usage.ObservedAt;
    }

    private static bool SameRevision(OpenCodeUsage left, OpenCodeUsage right) => left with { ObservedAt = 0 } == right with { ObservedAt = 0 };
    private static bool MarkEvidence(ModelUsageRecord row, UsageSource source)
    {
        if (source == UsageSource.Transcript && !row.SeenInTranscript) { row.SeenInTranscript = true; return true; }
        if (source == UsageSource.CommandResult && !row.SeenInCommandResult) { row.SeenInCommandResult = true; return true; }
        return false;
    }

    private static async Task<bool> ApplyAttribution(ControlDb db, ModelUsageRecord row, string? workerId, JsonElement message)
    {
        var info = message.GetProperty("info");
        var parentId = info.TryGetProperty("parentID", out var parent) && parent.ValueKind == JsonValueKind.String
            ? parent.GetString() : null;
        var changed = false;
        if (row.ParentNativeMessageId is null && parentId is not null) { row.ParentNativeMessageId = parentId; changed = true; }
        ControlSessionBinding? binding = null;
        WorkerRecord? worker = null;
        if (workerId is not null)
        {
            worker = await db.Workers.FindAsync(workerId);
            binding = await db.ControlSessions.AsNoTracking().SingleOrDefaultAsync(x => x.WorkerId == workerId);
        }
        if (binding is not null)
        {
            if (row.ControlSessionId is null) { row.ControlSessionId = binding.Id; changed = true; }
            if (row.ControlSessionGeneration is null) { row.ControlSessionGeneration = binding.Generation; changed = true; }
            if (row.RecoveryIntentId is null && binding.RecoveryIntentId is not null) { row.RecoveryIntentId = binding.RecoveryIntentId; changed = true; }
        }
        if (workerId is null) return changed;
        var command = parentId is null ? null : await db.Commands.AsNoTracking()
            .Where(x => x.RuntimeId == row.RuntimeId && x.WorkerId == workerId && x.NativeMessageId == parentId)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (command is null && worker?.HistoryGap == false)
            command = await ResolveCompactionCommand(db, workerId, row.NativeMessageId, message);
        if (command is null) return changed;
        if (row.CommandId is null) { row.CommandId = command.Id; changed = true; }
        const string prefix = "coordinator-decision:";
        if (row.CoordinationRunId is null && command.Origin.StartsWith(prefix, StringComparison.Ordinal))
        { row.CoordinationRunId = command.Origin[prefix.Length..]; changed = true; }
        return changed;
    }

    private static async Task<CommandRecord?> ResolveCompactionCommand(ControlDb db, string workerId, string nativeMessageId,
        JsonElement currentMessage)
    {
        var messages = await db.Messages.AsNoTracking().Where(x => x.WorkerId == workerId).ToListAsync();
        foreach (var tracked in db.Messages.Local.Where(x => x.WorkerId == workerId))
        {
            var index = messages.FindIndex(x => x.NativeId == tracked.NativeId);
            if (index < 0) messages.Add(tracked);
            else messages[index] = tracked;
        }
        messages = messages.OrderBy(x => x.NativeCreatedAt).ThenBy(x => x.NativeId).ToList();
        var currentInfo = currentMessage.GetProperty("info");
        var parentId = currentInfo.TryGetProperty("parentID", out var parent) && parent.ValueKind == JsonValueKind.String
            ? parent.GetString() : null;
        var parentMessage = parentId is null ? null : messages.SingleOrDefault(x => x.NativeId == parentId);
        if (parentMessage is null) return null;
        try
        {
            using var parentDocument = JsonDocument.Parse(parentMessage.Json);
            if (parentDocument.RootElement.GetProperty("info").GetProperty("role").GetString() != "user" ||
                !IsInternalCompactionMessage(parentDocument.RootElement)) return null;
        }
        catch (JsonException) { return null; }
        var current = messages.FindIndex(x => x.NativeId == nativeMessageId);
        if (current < 0)
        {
            var createdAt = currentInfo.GetProperty("time").GetProperty("created").GetInt64();
            current = messages.FindIndex(x => x.NativeCreatedAt > createdAt);
            if (current < 0) current = messages.Count;
        }
        for (var i = current - 1; i >= 0; i--)
        {
            try
            {
                using var document = JsonDocument.Parse(messages[i].Json);
                var message = document.RootElement;
                var info = message.GetProperty("info");
                if (info.GetProperty("role").GetString() != "user") continue;
                if (IsInternalCompactionMessage(message)) continue;
                var callerId = info.GetProperty("id").GetString();
                return callerId is null ? null : await db.Commands.AsNoTracking()
                    .Where(x => x.WorkerId == workerId && x.NativeMessageId == callerId)
                    .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
            }
            catch (JsonException) { return null; }
        }
        return null;
    }

    private static bool IsInternalCompactionMessage(JsonElement message)
    {
        if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return false;
        var values = parts.EnumerateArray().ToArray();
        if (values.Length == 0) return false;
        return values.All(x => x.TryGetProperty("type", out var type) && type.GetString() == "compaction" &&
                x.TryGetProperty("auto", out var automatic) && automatic.ValueKind == JsonValueKind.True) ||
            values.All(x => x.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                x.TryGetProperty("synthetic", out var synthetic) && synthetic.ValueKind == JsonValueKind.True &&
                x.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("compaction_continue", out var continuation) &&
                continuation.ValueKind == JsonValueKind.True);
    }

    private static UsageMetric Metric(IEnumerable<long?> values) => Metric(values.Select(x => x.HasValue ? (decimal?)x.Value : null));
    private static UsageMetric Metric(IEnumerable<decimal?> values)
    {
        var measurements = values.ToList();
        var present = measurements.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return new(present.Count == 0 ? null : present.Sum(), present.Count, measurements.Count - present.Count);
    }

    private static void Validate(UsageQuery query)
    {
        if (query.From is < 0 || query.To is < 0 || query.From.HasValue && query.To.HasValue && query.From > query.To)
            throw new ControlException("Usage time window is invalid.", 400);
    }
    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static string? Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Csv(string? value) => value is null ? "" : value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    private sealed record UsageObservation(UsageIdentity Identity, string? WorkerId, string SessionRole, string Json,
        long ObservedAt, UsageSource Source);
    private sealed record UsageUpsertResult(bool Accepted, bool Changed);
}
