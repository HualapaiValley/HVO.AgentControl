using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HVO.AgentControl.Runtime;

public sealed class CliProxyPreflightException : InvalidOperationException
{
    public CliProxyPreflightException(string status, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
    }

    public string Status { get; }
}

public sealed record CliProxyRuntimeConfiguration(Uri Endpoint, string Secret, CliProxyModelLane Lane, string? Variant)
{
    internal const int MaximumCatalogResponseBytes = 1024 * 1024;

    public static bool IsCliProxyModel(string model) =>
        model.StartsWith(CliProxyModelCatalog.ProviderId + "/", StringComparison.Ordinal);

    public static CliProxyRuntimeConfiguration LoadRequired(ControlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsCliProxyModel(options.Model))
        {
            throw new InvalidOperationException("The selected model is not a CLIProxy policy lane.");
        }

        var laneId = options.Model[(CliProxyModelCatalog.ProviderId.Length + 1)..];
        var lane = CliProxyProfile.SelectableLanes.SingleOrDefault(
            candidate => string.Equals(candidate.Id, laneId, StringComparison.Ordinal));
        if (lane is null)
        {
            throw new InvalidOperationException(
                $"CLIProxy policy lane '{laneId}' is not selectable in profile {CliProxyProfile.Version}.");
        }
        if (!Uri.TryCreate(options.CliProxyEndpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.Equals(endpoint.AbsolutePath.TrimEnd('/'), "/v1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Control:CliProxyEndpoint must be an absolute HTTP(S) base URL whose path is exactly /v1, with no query or fragment.");
        }

        endpoint = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/v1", UriKind.Absolute);

        if (!Path.IsPathRooted(options.CliProxySecretFile))
        {
            throw new InvalidOperationException("Control:CliProxySecretFile must be an absolute path.");
        }

        string framedSecret;
        try
        {
            framedSecret = File.ReadAllText(options.CliProxySecretFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The configured CLIProxy secret file is unavailable.", exception);
        }

        var secret = RemoveSingleLineFraming(framedSecret);
        if (secret.Length < ControlOptions.MinimumCliProxySecretLength
            || secret.Any(char.IsControl)
            || secret.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"The CLIProxy secret file must contain at least {ControlOptions.MinimumCliProxySecretLength} bytes with no whitespace or control characters; only one final LF or CRLF framing sequence is allowed.");
        }

        var variant = string.IsNullOrWhiteSpace(options.ModelVariant) ? null : options.ModelVariant.Trim();
        if (variant is not null && !lane.AllowedVariants.Contains(variant, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Variant '{variant}' is not advertised for CLIProxy policy lane '{lane.Id}'.");
        }

        return new(endpoint, secret, lane, variant);
    }

    public async Task ValidateCatalogAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = timeout,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(Endpoint.ToString().TrimEnd('/') + "/models", UriKind.Absolute));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                bounded.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "revoked"
                    : "unavailable";
                throw new CliProxyPreflightException(
                    status,
                    $"CLIProxy catalog validation failed with HTTP {(int)response.StatusCode}; response content was not read.");
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                throw CatalogUnavailable("CLIProxy catalog validation returned an unexpected content type.");
            }

            if (response.Content.Headers.ContentLength is > MaximumCatalogResponseBytes)
            {
                throw CatalogUnavailable("CLIProxy catalog validation returned an oversized response.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(bounded.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var rented = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                while (true)
                {
                    var remaining = MaximumCatalogResponseBytes + 1 - checked((int)buffer.Length);
                    if (remaining <= 0)
                    {
                        throw CatalogUnavailable("CLIProxy catalog validation returned an oversized response.");
                    }

                    var read = await stream.ReadAsync(
                        rented.AsMemory(0, Math.Min(rented.Length, remaining)),
                        bounded.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    buffer.Write(rented, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            if (buffer.Length == 0)
            {
                throw CatalogUnavailable("CLIProxy catalog validation returned an empty response.");
            }

            try
            {
                buffer.Position = 0;
                using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: bounded.Token).ConfigureAwait(false);
                if (!ContainsExactLane(document.RootElement, Lane.Id))
                {
                    throw CatalogUnavailable("CLIProxy catalog validation did not advertise the selected policy lane.");
                }
            }
            catch (JsonException exception)
            {
                throw CatalogUnavailable("CLIProxy catalog validation returned malformed JSON.", exception);
            }
        }
        catch (CliProxyPreflightException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new CliProxyPreflightException("unavailable", "CLIProxy catalog validation timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new CliProxyPreflightException("unavailable", "CLIProxy catalog validation could not reach the configured endpoint.", exception);
        }
        catch (IOException exception)
        {
            throw new CliProxyPreflightException("unavailable", "CLIProxy catalog validation could not read the configured endpoint response.", exception);
        }
    }

    private static bool ContainsExactLane(JsonElement root, string selectedLaneId)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), selectedLaneId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static CliProxyPreflightException CatalogUnavailable(string message, Exception? exception = null) =>
        new("unavailable", message, exception);

    private static string RemoveSingleLineFraming(string value)
    {
        if (value.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return value[..^2];
        }

        return value.EndsWith('\n') ? value[..^1] : value;
    }
}
