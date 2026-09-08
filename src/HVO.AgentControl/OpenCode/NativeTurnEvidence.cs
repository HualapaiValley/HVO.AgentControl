using System.Text.Json;

namespace HVO.AgentControl.OpenCode;

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

    public static bool IsTerminalAssistantResponse(JsonElement message)
    {
        var info = message.GetProperty("info");
        if (!info.GetProperty("time").TryGetProperty("completed", out var completed) || completed.ValueKind != JsonValueKind.Number) return false;
        var parts = message.GetProperty("parts").EnumerateArray().ToArray();
        if (parts.Any(part => part.GetProperty("type").GetString() == "tool") &&
            (!info.TryGetProperty("finish", out var finish) || finish.ValueKind != JsonValueKind.String || finish.GetString() is "tool-calls" or null)) return false;
        if (info.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return true;
        return parts.All(part => part.GetProperty("type").GetString() != "tool" ||
            part.TryGetProperty("state", out var state) && state.TryGetProperty("status", out var status) &&
            status.GetString() is "completed" or "error" or "cancelled");
    }

    private static bool IsTrue(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.True;
}
