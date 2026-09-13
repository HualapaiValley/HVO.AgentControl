using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// A model reference as reported by the native session: <see cref="ProviderId"/>
/// plus the provider-scoped <see cref="ModelId"/> (which may itself contain a
/// slash for custom providers). <see cref="Reference"/> is the canonical
/// <c>provider/model</c> string used by ACP config options and the control API.
/// </summary>
public sealed record NativeModelReference(string ProviderId, string ModelId, string? Variant)
{
    public string Reference => $"{ProviderId}/{ModelId}";
}

/// <summary>
/// Outcome of reading the authoritative session model. <see cref="Available"/>
/// is false when the native query could not be completed; in that case the
/// caller must retain its last observed model rather than report a false
/// default. When <see cref="Available"/> is true but <see cref="Model"/> is
/// null, the server answered and reported no model (reported as unknown).
/// </summary>
public readonly record struct SessionModelSnapshot(bool Available, NativeModelReference? Model);

/// <summary>
/// Best-effort client for the OpenCode native HTTP API exposed alongside ACP.
/// Every operation is optional: failures return false/null and never fault the
/// control host. Basic auth uses the ephemeral server credentials.
/// </summary>
public sealed class OpenCodeNativeClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;
    private readonly string? _workspaceDirectory;

    public OpenCodeNativeClient(
        string baseUrl,
        string username,
        string password,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        string? workspaceDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _baseUrl = baseUrl.TrimEnd('/');
        _workspaceDirectory = string.IsNullOrWhiteSpace(workspaceDirectory) ? null : workspaceDirectory;
        _client = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    public async Task<bool> TrySetSessionTitleAsync(string sessionId, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        try
        {
            using var response = await _client.PatchAsJsonAsync(
                $"{_baseUrl}/session/{Uri.EscapeDataString(sessionId)}",
                new { title },
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    public async Task<bool> TryAbortSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        try
        {
            using var response = await _client.PostAsync(
                $"{_baseUrl}/session/{Uri.EscapeDataString(sessionId)}/abort",
                content: null,
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Returns "idle", "busy" or null when the native status is unavailable.
    /// When the status map is sparse, the exact session is fetched (with the
    /// canonical workspace directory query) and idle is only reported when the
    /// returned id matches exactly.
    /// </summary>
    public async Task<string?> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        try
        {
            var status = await TryReadStatusAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return status ?? await TryConfirmSessionIdleAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the authoritative session model from
    /// <c>GET /session/{id}?directory=...</c>. Returns an unavailable snapshot
    /// on any transport/parse failure so callers retain their last observation;
    /// an available snapshot with a null model means the server reported none.
    /// </summary>
    public async Task<SessionModelSnapshot> GetSessionModelAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new SessionModelSnapshot(false, null);
        }

        try
        {
            var url = $"{_baseUrl}/session/{Uri.EscapeDataString(sessionId)}";
            if (_workspaceDirectory is not null)
            {
                url += "?directory=" + Uri.EscapeDataString(_workspaceDirectory);
            }

            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new SessionModelSnapshot(false, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("model", out var model)
                || model.ValueKind != JsonValueKind.Object)
            {
                return new SessionModelSnapshot(true, null);
            }

            var providerId = ReadString(model, "providerID");
            var modelId = ReadString(model, "id");
            if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId))
            {
                return new SessionModelSnapshot(true, null);
            }

            return new SessionModelSnapshot(true, new NativeModelReference(providerId, modelId, ReadString(model, "variant")));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return new SessionModelSnapshot(false, null);
        }
    }

    /// <summary>
    /// Reads <c>GET /provider</c> and returns the models advertised by the
    /// connected providers only. Returns null when the catalog is unavailable
    /// (transport/parse failure) so the caller does not clobber a prior
    /// selection with a false empty catalog. An empty list means the server
    /// answered with no connected providers.
    /// </summary>
    public async Task<IReadOnlyList<ControlModel>?> GetConnectedModelCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync($"{_baseUrl}/provider", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("connected", out var connectedElement)
                || connectedElement.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("all", out var allElement)
                || allElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var connected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in connectedElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } id)
                {
                    connected.Add(id);
                }
            }

            var models = new List<ControlModel>();
            foreach (var provider in allElement.EnumerateArray())
            {
                if (provider.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var providerId = ReadString(provider, "id");
                if (string.IsNullOrEmpty(providerId) || !connected.Contains(providerId))
                {
                    continue;
                }

                if (!provider.TryGetProperty("models", out var modelsElement) || modelsElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var model in modelsElement.EnumerateObject())
                {
                    if (model.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var modelId = ReadString(model.Value, "id");
                    if (string.IsNullOrEmpty(modelId))
                    {
                        modelId = model.Name;
                    }

                    if (string.IsNullOrEmpty(modelId))
                    {
                        continue;
                    }

                    models.Add(new ControlModel
                    {
                        Id = $"{providerId}/{modelId}",
                        Name = ReadString(model.Value, "name") ?? modelId,
                        Provider = providerId,
                    });
                }
            }

            models.Sort(static (left, right) =>
            {
                var provider = string.CompareOrdinal(left.Provider, right.Provider);
                return provider != 0 ? provider : string.CompareOrdinal(left.Id, right.Id);
            });

            return models;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the session model through <c>POST /api/session/{id}/model</c>
    /// using the native <c>ModelRef</c> shape. Returns false on any non-success
    /// response or transport failure; never throws for expected failures.
    /// </summary>
    public async Task<bool> TrySetSessionModelAsync(string sessionId, NativeModelReference model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(model.ProviderId) || string.IsNullOrWhiteSpace(model.ModelId))
        {
            return false;
        }

        try
        {
            object body = string.IsNullOrEmpty(model.Variant)
                ? new { model = new { id = model.ModelId, providerID = model.ProviderId } }
                : new { model = new { id = model.ModelId, providerID = model.ProviderId, variant = model.Variant } };

            using var response = await _client.PostAsJsonAsync(
                $"{_baseUrl}/api/session/{Uri.EscapeDataString(sessionId)}/model",
                body,
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private async Task<string?> TryReadStatusAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"{_baseUrl}/session/status", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(sessionId, out var entry))
        {
            return null;
        }

        string? type = null;
        if (entry.ValueKind == JsonValueKind.Object
            && entry.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String)
        {
            type = typeElement.GetString();
        }
        else if (entry.ValueKind == JsonValueKind.String)
        {
            type = entry.GetString();
        }

        return Normalize(type);
    }

    private async Task<string?> TryConfirmSessionIdleAsync(string sessionId, CancellationToken cancellationToken)
    {
        var url = $"{_baseUrl}/session/{Uri.EscapeDataString(sessionId)}";
        if (_workspaceDirectory is not null)
        {
            url += "?directory=" + Uri.EscapeDataString(_workspaceDirectory);
        }

        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return string.Equals(idElement.GetString(), sessionId, StringComparison.Ordinal) ? "idle" : null;
    }

    public void Dispose() => _client.Dispose();

    private static string? Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "idle" => "idle",
            "busy" or "retry" or "working" or "running" or "compacting" => "busy",
            var other => other,
        };
    }

    private static bool IsExpected(Exception exception)
    {
        return exception is HttpRequestException
            or OperationCanceledException
            or JsonException
            or InvalidOperationException
            or NotSupportedException;
    }
}
