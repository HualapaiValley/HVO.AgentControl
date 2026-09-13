using System.Text.Json;

namespace HVO.AgentControl.OpenCode;

public sealed record AutomaticCompactionFailure(
    JsonElement Summary,
    string CompactionMessageId,
    string SummaryMessageId,
    string SessionId,
    string? ProviderId,
    string? ModelId,
    string? ErrorName,
    JsonElement Error);

public static class NativeTurnEvidence
{
    public static JsonElement[] AssistantMessages(JsonElement[] messages, string? callerId)
    {
        if (string.IsNullOrEmpty(callerId)) return [];
        var ordered = messages.OrderBy(x => x.GetProperty("info").GetProperty("time").GetProperty("created").GetInt64())
            .ThenBy(x => x.GetProperty("info").GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
        var start = Array.FindIndex(ordered, x => x.GetProperty("info").GetProperty("id").GetString() == callerId);
        if (start < 0) return [];
        var caller = ordered[start].GetProperty("info");
        var session = caller.TryGetProperty("sessionID", out var sessionId) ? sessionId.GetString() : null;
        var parents = new HashSet<string>(StringComparer.Ordinal) { callerId };
        var result = new List<JsonElement>();
        var autoCompaction = false;
        foreach (var message in ordered.Skip(start + 1))
        {
            var info = message.GetProperty("info");
            if (session is not null && (!info.TryGetProperty("sessionID", out var currentSession) || currentSession.GetString() != session)) continue;
            if (info.GetProperty("role").GetString() == "user")
            {
                var parts = message.GetProperty("parts").EnumerateArray().ToArray();
                if (parts.Length > 0 && parts.All(x => x.GetProperty("type").GetString() == "compaction" && IsTrue(x, "auto")))
                { autoCompaction = true; continue; }
                if (autoCompaction && parts.Length > 0 && parts.All(x => x.GetProperty("type").GetString() == "text" &&
                    IsTrue(x, "synthetic") && x.TryGetProperty("metadata", out var metadata) && IsTrue(metadata, "compaction_continue")))
                { parents.Add(info.GetProperty("id").GetString()!); autoCompaction = false; continue; }
                // A real user prompt is a new turn, even if its text resembles an internal continuation.
                break;
            }
            if (info.GetProperty("role").GetString() == "assistant" && !IsTrue(info, "summary") &&
                info.TryGetProperty("parentID", out var parent) && parents.Contains(parent.GetString() ?? ""))
                result.Add(message);
        }
        return result.ToArray();
    }

    public static bool IsAutomaticCompactionInProgress(JsonElement[] messages, string? callerId, string expectedSessionId)
    {
        if (string.IsNullOrEmpty(callerId) || string.IsNullOrEmpty(expectedSessionId)) return false;
        var ordered = messages.OrderBy(x => x.GetProperty("info").GetProperty("time").GetProperty("created").GetInt64())
            .ThenBy(x => x.GetProperty("info").GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
        var start = Array.FindIndex(ordered, x => x.GetProperty("info").GetProperty("id").GetString() == callerId);
        if (start < 0) return false;
        var caller = ordered[start].GetProperty("info");
        if (caller.GetProperty("role").GetString() != "user" || BoundedText(caller, "sessionID") != expectedSessionId) return false;
        var compacting = false;
        foreach (var message in ordered.Skip(start + 1))
        {
            var info = message.GetProperty("info");
            if (BoundedText(info, "sessionID") != expectedSessionId) continue;
            if (info.GetProperty("role").GetString() != "user") continue;
            var parts = message.GetProperty("parts").EnumerateArray().ToArray();
            if (parts.Length > 0 && parts.All(x => x.GetProperty("type").GetString() == "compaction" && IsTrue(x, "auto")))
            { compacting = true; continue; }
            if (compacting && parts.Length > 0 && parts.All(x => x.GetProperty("type").GetString() == "text" &&
                IsTrue(x, "synthetic") && x.TryGetProperty("metadata", out var metadata) && IsTrue(metadata, "compaction_continue")))
            { compacting = false; continue; }
            break;
        }
        return compacting;
    }

    public static AutomaticCompactionFailure? CompletedAutomaticCompactionFailure(JsonElement[] messages, string? callerId,
        string expectedSessionId)
    {
        if (string.IsNullOrEmpty(callerId) || string.IsNullOrEmpty(expectedSessionId)) return null;
        var ordered = messages.OrderBy(x => x.GetProperty("info").GetProperty("time").GetProperty("created").GetInt64())
            .ThenBy(x => x.GetProperty("info").GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
        var start = Array.FindIndex(ordered, x => x.GetProperty("info").GetProperty("id").GetString() == callerId);
        if (start < 0) return null;
        var caller = ordered[start].GetProperty("info");
        var session = BoundedText(caller, "sessionID");
        if (caller.GetProperty("role").GetString() != "user" || session != expectedSessionId) return null;
        string? compactionId = null;
        AutomaticCompactionFailure? failure = null;
        foreach (var message in ordered.Skip(start + 1))
        {
            var info = message.GetProperty("info");
            if (session is not null && (!info.TryGetProperty("sessionID", out var currentSession) || currentSession.GetString() != session)) continue;
            if (info.GetProperty("role").GetString() == "user")
            {
                var parts = message.GetProperty("parts").EnumerateArray().ToArray();
                if (parts.Length > 0 && parts.All(x => x.GetProperty("type").GetString() == "compaction" && IsTrue(x, "auto")))
                {
                    compactionId = BoundedText(info, "id");
                    failure = null;
                    continue;
                }
                if (compactionId is not null && parts.Length > 0 && parts.All(x => x.GetProperty("type").GetString() == "text" &&
                    IsTrue(x, "synthetic") && x.TryGetProperty("metadata", out var metadata) && IsTrue(metadata, "compaction_continue")))
                {
                    compactionId = null;
                    failure = null;
                    continue;
                }
                break;
            }
            if (compactionId is null || info.GetProperty("role").GetString() != "assistant" || !IsTrue(info, "summary") ||
                !info.TryGetProperty("parentID", out var parent) || parent.GetString() != compactionId ||
                !info.GetProperty("time").TryGetProperty("completed", out var completed) || completed.ValueKind != JsonValueKind.Number ||
                !info.TryGetProperty("error", out var error) || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            var summaryId = BoundedText(info, "id");
            var summarySession = BoundedText(info, "sessionID");
            if (summaryId is null || summarySession is null) continue;
            var providerId = ConsistentRoute(info, "providerID", "providerID");
            var modelId = ConsistentRoute(info, "modelID", "id");
            failure = new(message, compactionId, summaryId, summarySession, providerId, modelId,
                error.ValueKind == JsonValueKind.Object ? BoundedText(error, "name") : null, error);
        }
        return failure;
    }

    public static bool IsTerminalAssistantResponse(JsonElement message)
    {
        var info = message.GetProperty("info");
        if (!info.GetProperty("time").TryGetProperty("completed", out var completed) || completed.ValueKind != JsonValueKind.Number) return false;
        // Native cleanup completes failed messages without necessarily setting a finish reason.
        if (info.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return true;
        if (info.TryGetProperty("structured", out _)) return true;
        if (!info.TryGetProperty("finish", out var finish) || finish.ValueKind != JsonValueKind.String ||
            finish.GetString() is null or "" or "tool-calls" or "unknown") return false;
        // OpenCode continues ordinary tool calls even when a provider reports "stop".
        // Only provider-executed calls and cleanup-marked interrupted orphans allow exit.
        return message.GetProperty("parts").EnumerateArray().All(part =>
            part.GetProperty("type").GetString() != "tool" || IsSettledTerminalTool(part));
    }

    public static string? CompletedToolFailureId(JsonElement[] messages)
    {
        var latest = messages.OrderBy(x => x.GetProperty("info").GetProperty("time").GetProperty("created").GetInt64())
            .ThenBy(x => x.GetProperty("info").GetProperty("id").GetString(), StringComparer.Ordinal).LastOrDefault();
        return latest.ValueKind == JsonValueKind.Object && IsCompletedToolFailure(latest)
            ? latest.GetProperty("info").GetProperty("id").GetString() : null;
    }

    public static bool IsCompletedToolFailure(JsonElement message)
    {
        var info = message.GetProperty("info");
        if (info.GetProperty("role").GetString() != "assistant" || IsTrue(info, "summary") ||
            !info.GetProperty("time").TryGetProperty("completed", out var completed) || completed.ValueKind != JsonValueKind.Number ||
            IsTerminalAssistantResponse(message)) return false;
        var tools = message.GetProperty("parts").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "tool").ToArray();
        return tools.Any(x => x.TryGetProperty("state", out var state) && state.TryGetProperty("status", out var status) && status.GetString() == "error") &&
            tools.All(x => x.TryGetProperty("state", out var state) && state.TryGetProperty("status", out var status) &&
                status.GetString() is "completed" or "error" or "cancelled");
    }

    private static bool IsSettledTerminalTool(JsonElement part)
    {
        if (!part.TryGetProperty("state", out var state) || !state.TryGetProperty("status", out var status) ||
            status.GetString() is not ("completed" or "error" or "cancelled")) return false;
        return part.TryGetProperty("metadata", out var metadata) && IsTrue(metadata, "providerExecuted") ||
            status.GetString() == "error" && state.TryGetProperty("metadata", out var stateMetadata) && IsTrue(stateMetadata, "interrupted");
    }

    private static bool IsTrue(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.True;

    private static string? ConsistentRoute(JsonElement info, string topLevelName, string nestedName)
    {
        var direct = BoundedText(info, topLevelName);
        var nested = info.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object
            ? BoundedText(model, nestedName) : null;
        return direct is not null && nested is not null && direct != nested ? null : direct ?? nested;
    }

    private static string? BoundedText(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found) || found.ValueKind != JsonValueKind.String) return null;
        var text = found.GetString();
        return text is { Length: > 0 and <= 200 } && !string.IsNullOrWhiteSpace(text) && text.All(x => !char.IsControl(x))
            ? text : null;
    }
}
