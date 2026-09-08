using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using HVO.AgentControl.Core;
using HVO.AgentControl.Ssh;

namespace HVO.AgentControl.OpenCode;

public sealed record AdapterCapabilities(bool CanAbort, bool CanReplyToPermissions, bool CanReplyToQuestions,
    bool CanSupplyMessageId, bool CanSteerActiveTurn = false);
public sealed record NativeSnapshot(JsonElement Session, JsonElement[] Messages, string Status, JsonElement StatusDetail,
    JsonElement[] Permissions, JsonElement[] Questions, string? IdleToolFailureMessageId = null);
public sealed class NativeRejectedException(int status) : Exception($"OpenCode rejected the request (HTTP {status}).")
{
    public int Status { get; } = status;
}
public sealed class NativeHistoryObservationException(Exception inner)
    : IOException("Native conversation history could not be read within the validated observation limits.", inner);

public sealed class OpenCodeClient(HttpClient http) : IDisposable
{
    private bool hasAgents;
    public AdapterCapabilities Capabilities { get; private set; } = new(false, false, false, false);
    public static string Scope(string path, string directory) => path + (path.Contains('?') ? "&" : "?") + "directory=" + Uri.EscapeDataString(directory);
    public static string Id(string id) => Uri.EscapeDataString(id);

    public static string NewMessageId(NativeSnapshot snapshot)
    {
        // OpenCode orders legacy messages by its 48-bit time/counter prefix. A random UUID
        // or an ID assigned while queued can sort before prior turns. Anchor to native evidence,
        // avoiding assumptions about clock synchronization between the two machines.
        var latest = snapshot.Messages.Select(x => x.GetProperty("info").GetProperty("id").GetString()!)
            .Where(x => x.StartsWith("msg_", StringComparison.Ordinal) && x.Length >= 16)
            .Select(x => long.TryParse(x.AsSpan(4, 12), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .DefaultIfEmpty(0).Max();
        if (latest == 0) latest = (snapshot.Session.GetProperty("time").GetProperty("created").GetInt64() << 12) & 0xffffffffffff;
        return "msg_" + ((latest + 1) & 0xffffffffffff).ToString("x12", CultureInfo.InvariantCulture) +
               Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(7));
    }

    public async Task<string> Verify(CancellationToken token)
    {
        var health = await Get("/global/health", token);
        var version = health.GetProperty("version").GetString() ?? "unknown";
        if (!health.GetProperty("healthy").GetBoolean() || version != BootstrapScript.Version)
            throw new ControlException($"Incompatible OpenCode version {version}; this adapter requires {BootstrapScript.Version}.");
        var schema = await Get("/doc", token, 12_000_000);
        var paths = schema.GetProperty("paths");
        foreach (var route in new[] { "/global/event", "/session", "/session/{sessionID}/prompt_async", "/session/{sessionID}/message", "/session/status", "/provider", "/path" })
            if (!paths.TryGetProperty(route, out _)) throw new ControlException("OpenCode schema is missing a core route: " + route);
        hasAgents = paths.TryGetProperty("/agent", out _);
        Capabilities = new(paths.TryGetProperty("/session/{sessionID}/abort", out _),
            paths.TryGetProperty("/permission/{requestID}/reply", out _), paths.TryGetProperty("/question/{requestID}/reply", out _), true);
        return version;
    }

    public async Task<List<ModelChoice>> Models(string directory, CancellationToken token)
    {
        var providers = await Get(Scope("/provider", directory), token, 8_000_000);
        var connected = providers.GetProperty("connected").EnumerateArray().Select(x => x.GetString()).ToHashSet();
        string[] agents = [];
        if (hasAgents)
        {
            var available = await Get(Scope("/agent", directory), token);
            agents = available.EnumerateArray().Where(x => (!x.TryGetProperty("hidden", out var hidden) || !hidden.GetBoolean()) &&
                (!x.TryGetProperty("mode", out var mode) || mode.GetString() is "primary" or "all"))
                .Select(x => x.GetProperty("name").GetString()!).ToArray();
        }
        var result = new List<ModelChoice>();
        foreach (var provider in providers.GetProperty("all").EnumerateArray())
        {
            var providerId = provider.GetProperty("id").GetString()!;
            if (!connected.Contains(providerId)) continue;
            foreach (var model in provider.GetProperty("models").EnumerateObject())
                result.Add(new ModelChoice(providerId, model.Name, model.Value.TryGetProperty("name", out var name) ? name.GetString() ?? model.Name : model.Name,
                    model.Value.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Object
                        ? variants.EnumerateObject().Select(x => x.Name).ToArray() : [], agents));
        }
        return result;
    }

    public async Task<JsonElement> CreateSession(string directory, string title, CancellationToken token)
    {
        var context = await Get(Scope("/path", directory), token);
        if (context.GetProperty("directory").GetString() != directory) throw new ControlException("OpenCode directory does not match the verified workspace.");
        return await Send(HttpMethod.Post, Scope("/session", directory), new { title }, token);
    }

    public Task<JsonElement> Sessions(string directory, CancellationToken token) => Get(Scope("/session", directory), token);
    public Task<JsonElement> SetProviderKey(string providerId, string key, CancellationToken token) =>
        Send(HttpMethod.Put, $"/auth/{Id(providerId)}", new { type = "api", key }, token);
    public Task<JsonElement> ProviderAuthMethods(string directory, CancellationToken token) => Get(Scope("/provider/auth", directory), token);
    public Task<JsonElement> AuthorizeChatGpt(string directory, int method, CancellationToken token) =>
        Send(HttpMethod.Post, Scope("/provider/openai/oauth/authorize", directory), new { method }, token);
    public Task<JsonElement> CompleteChatGpt(string directory, int method, CancellationToken token) =>
        Send(HttpMethod.Post, Scope("/provider/openai/oauth/callback", directory), new { method }, token,
            timeoutDuration: TimeSpan.FromMinutes(11));
    public Task<JsonElement> Prompt(WorkerRecord worker, CommandRecord command, PromptInput input, CancellationToken token) =>
        Send(HttpMethod.Post, Scope($"/session/{Id(worker.NativeSessionId)}/prompt_async", worker.Directory),
            new
            {
                messageID = command.NativeMessageId,
                tools = command.Origin.StartsWith("coordinator-decision:", StringComparison.Ordinal) ? new Dictionary<string, bool> { ["*"] = false } : null,
                model = new { providerID = input.ProviderId ?? worker.ProviderId, modelID = input.ModelId ?? worker.ModelId },
                agent = string.IsNullOrEmpty(input.Agent) ? null : input.Agent,
                variant = string.IsNullOrEmpty(input.Variant) ? null : input.Variant,
                parts = new[] { new { type = "text", text = input.Text } }
            }, token);
    public Task<JsonElement> Abort(WorkerRecord worker, CancellationToken token) =>
        Send(HttpMethod.Post, Scope($"/session/{Id(worker.NativeSessionId)}/abort", worker.Directory), new { }, token);
    public Task<JsonElement> Reply(WorkerRecord worker, PendingRequest request, ReplyInput input, CancellationToken token)
    {
        var route = $"/{request.Kind}/{Id(request.NativeId)}/{(request.Kind == "question" && input.Reject ? "reject" : "reply")}";
        object body = request.Kind == "permission" ? new { reply = input.Permission } : input.Reject ? new { } : new { answers = input.Answers };
        return Send(HttpMethod.Post, Scope(route, worker.Directory), body, token);
    }

    public async Task<NativeSnapshot> Snapshot(WorkerRecord worker, int limit, CancellationToken token, bool verifyToolFailureStop = false)
    {
        var sessionPath = $"/session/{Id(worker.NativeSessionId)}";
        var session = await Get(Scope(sessionPath, worker.Directory), token);
        if (session.GetProperty("directory").GetString() != worker.Directory) throw new ControlException("Native session directory changed; dispatch is blocked.");
        var history = await History(Scope(sessionPath + $"/message?limit={limit}", worker.Directory), token);
        var messages = history.EnumerateArray().ToArray();
        var toolFailure = verifyToolFailureStop ? NativeTurnEvidence.CompletedToolFailureId(messages) : null;
        var statuses = await Get(Scope("/session/status", worker.Directory), token);
        var statusDetail = statuses.TryGetProperty(worker.NativeSessionId, out var item) ? item.Clone() : default;
        var status = statusDetail.ValueKind == JsonValueKind.Object && statusDetail.TryGetProperty("type", out var statusType)
            ? statusType.GetString() ?? "unknown" : "idle";
        string? idleToolFailureMessageId = null;
        if (status == "idle" && toolFailure is not null)
        {
            // The completed failed step proves this turn started before the idle observation.
            // Re-read after idle so a continuing turn's later final response cannot be missed.
            history = await History(Scope(sessionPath + $"/message?limit={limit}", worker.Directory), token);
            messages = history.EnumerateArray().ToArray();
            idleToolFailureMessageId = toolFailure;
        }
        var permissions = Capabilities.CanReplyToPermissions ? await Get(Scope("/permission", worker.Directory), token) : default;
        var questions = Capabilities.CanReplyToQuestions ? await Get(Scope("/question", worker.Directory), token) : default;
        using var ancestryDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        ancestryDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        var ownership = new NativeRequestOwnership(worker.NativeSessionId, worker.Directory,
            (id, cancellation) => Get(Scope($"/session/{Id(id)}", worker.Directory), cancellation));
        return new(session, messages, status, statusDetail,
            await ownership.Filter(permissions, ancestryDeadline.Token), await ownership.Filter(questions, ancestryDeadline.Token), idleToolFailureMessageId);
    }

    public Task<HttpResponseMessage> Subscribe(CancellationToken token) => SubscribeCore(token);

    private async Task<JsonElement> History(string route, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new NativeRejectedException((int)response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await NativeHistoryProjection.ReadAsync(stream, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new NativeHistoryObservationException(ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new NativeHistoryObservationException(ex);
        }
    }

    private async Task<HttpResponseMessage> SubscribeCore(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var response = await http.GetAsync("/global/event", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        { response.Dispose(); throw new InvalidDataException("OpenCode SSE did not return text/event-stream."); }
        return response;
    }

    public Task<JsonElement> Get(string route, CancellationToken token, int limit = 2_000_000) => Send(HttpMethod.Get, route, null, token, limit);
    private async Task<JsonElement> Send(HttpMethod method, string route, object? body, CancellationToken token, int limit = 2_000_000, TimeSpan? timeoutDuration = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(timeoutDuration ?? TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: new JsonSerializerOptions(Json.Options) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        // Mutations are attempted exactly once. A failed response/timeout is reconciled by the dispatcher.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new NativeRejectedException((int)response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (bytes.Length + count > limit) throw new InvalidDataException("OpenCode response size limit exceeded.");
            bytes.Write(buffer, 0, count);
        }
        if (bytes.Length == 0) return default;
        using var document = JsonDocument.Parse(bytes.ToArray());
        return document.RootElement.Clone();
    }
    public void Dispose() => http.Dispose();
}
