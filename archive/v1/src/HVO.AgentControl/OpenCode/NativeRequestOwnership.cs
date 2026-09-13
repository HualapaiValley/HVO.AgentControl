using System.Text.Json;

namespace HVO.AgentControl.OpenCode;

// Requests are attributed by native ancestry, never by a model-supplied task description.
public sealed class NativeRequestOwnership(string rootSessionId, string directory,
    Func<string, CancellationToken, Task<JsonElement>> readSession)
{
    private readonly Dictionary<string, bool> owners = new() { [rootSessionId] = true };
    private int reads;

    public async Task<JsonElement[]> Filter(JsonElement requests, CancellationToken token)
    {
        if (requests.ValueKind != JsonValueKind.Array) return [];
        var result = new List<JsonElement>();
        foreach (var request in requests.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (request.TryGetProperty("sessionID", out var session) && session.ValueKind == JsonValueKind.String &&
                await Owns(session.GetString()!, token)) result.Add(request.Clone());
        }
        return result.ToArray();
    }

    private async Task<bool> Owns(string id, CancellationToken token)
    {
        var path = new HashSet<string>();
        bool Complete(bool owned) { foreach (var item in path) owners[item] = owned; return owned; }
        while (!owners.TryGetValue(id, out _))
        {
            if (!path.Add(id)) return Complete(false);
            if (path.Count > 32 || ++reads > 128)
                throw new InvalidDataException("Native request ancestry exceeds the observation limit.");
            JsonElement session;
            try { session = await readSession(id, token); }
            catch (NativeRejectedException ex) when (ex.Status == 404) { return Complete(false); }
            if (!session.TryGetProperty("id", out var identity) || identity.GetString() != id ||
                !session.TryGetProperty("directory", out var scope) || scope.GetString() != directory ||
                !session.TryGetProperty("parentID", out var parent) || parent.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(parent.GetString())) return Complete(false);
            id = parent.GetString()!;
        }
        return Complete(owners[id]);
    }
}
