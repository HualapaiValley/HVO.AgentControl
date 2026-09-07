using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.AgentControl.Core;

namespace HVO.AgentControl.GitHub;

// Tokens stay inside the service/credential delivery boundary, never in command payloads.
public sealed class GitHubInstallationToken(string value, DateTimeOffset expiresAt, string actor = "agentcontrol[bot]",
    string checksPermission = GitHubPermissionState.Unknown, string commitStatusesPermission = GitHubPermissionState.Unknown,
    string actionsPermission = GitHubPermissionState.Unknown, DateTimeOffset? permissionsVerifiedAt = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Value { get; } = value;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public string Actor { get; } = actor;
    public string ChecksPermission { get; } = checksPermission;
    public string CommitStatusesPermission { get; } = commitStatusesPermission;
    public string ActionsPermission { get; } = actionsPermission;
    public DateTimeOffset? PermissionsVerifiedAt { get; } = permissionsVerifiedAt;
    public override string ToString() => "GitHub installation credential (redacted)";
}

public sealed partial class GitHubAppClient(HttpClient http, TimeProvider clock)
{
    public const string ApiRoot = "https://api.github.com/";

    public static string[] ValidateRepositories(IEnumerable<string> repositories)
    {
        var values = repositories.Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length is < 1 or > 50 || values.Any(x => !RepositoryPattern().IsMatch(x)))
            throw new ControlException("Choose 1–50 repositories using owner/repository names.", 400);
        if (values.Select(x => x.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new ControlException("One installation grant must belong to a single GitHub account.", 400);
        return values;
    }

    public string CreateJwt(long appId, string privateKey)
    {
        if (appId <= 0) throw new ControlException("Enter a positive GitHub App ID.", 400);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKey);
            if (rsa.KeySize < 2048) throw new CryptographicException();
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            var body = Base64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")) + "." +
                Base64(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 540, iss = appId.ToString(CultureInfo.InvariantCulture) }));
            return body + "." + Base64(rsa.SignData(Encoding.ASCII.GetBytes(body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        { throw new ControlException("The GitHub App private key must be a valid RSA PEM private key.", 400); }
    }

    public async Task<GitHubInstallationToken> Issue(long appId, long installationId, string privateKey,
        IEnumerable<string> repositories, CancellationToken cancellationToken)
    {
        var scope = ValidateRepositories(repositories);
        if (installationId <= 0) throw new ControlException("Enter a positive installation ID.", 400);
        var jwt = CreateJwt(appId, privateKey);
        using var installation = await Send(HttpMethod.Get, $"app/installations/{installationId}", jwt, null, cancellationToken);
        string? account;
        string? slug;
        JsonElement installationPermissions;
        try
        {
            account = installation.RootElement.GetProperty("account").GetProperty("login").GetString();
            slug = installation.RootElement.GetProperty("app_slug").GetString();
            installationPermissions = installation.RootElement.GetProperty("permissions");
            if (installationPermissions.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        { throw new ControlException("GitHub returned an invalid installation response."); }
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 200 || slug.Any(char.IsControl))
            throw new ControlException("GitHub did not identify the installation's App.");
        if (!string.Equals(account, scope[0].Split('/')[0], StringComparison.OrdinalIgnoreCase))
            throw new ControlException("The installation belongs to a different account than the selected repositories.", 400);
        var requestedPermissions = new Dictionary<string, string>
        {
            ["contents"] = "write",
            ["issues"] = "write",
            ["pull_requests"] = "write"
        };
        foreach (var permission in new[] { "checks", "statuses", "actions" })
            if (HasReadAccess(installationPermissions, permission)) requestedPermissions[permission] = "read";
        var requested = new { repositories = scope.Select(x => x.Split('/')[1]).ToArray(), permissions = requestedPermissions };
        using var response = await Send(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt, requested, cancellationToken);
        var root = response.RootElement;
        string? token;
        DateTimeOffset expires;
        string[] granted;
        JsonElement permissions;
        try
        {
            token = root.GetProperty("token").GetString();
            expires = root.GetProperty("expires_at").GetDateTimeOffset();
            granted = root.GetProperty("repositories").EnumerateArray().Select(x => x.GetProperty("full_name").GetString() ?? "").ToArray();
            permissions = root.GetProperty("permissions");
            if (permissions.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new ControlException("GitHub returned an invalid credential response."); }
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Any(char.IsControl) ||
            expires <= clock.GetUtcNow().AddMinutes(5) || expires > clock.GetUtcNow().AddHours(2) ||
            !root.TryGetProperty("repository_selection", out var selection) || selection.ValueKind != JsonValueKind.String || selection.GetString() != "selected" ||
            !new HashSet<string>(scope, StringComparer.OrdinalIgnoreCase).SetEquals(granted) ||
            permissions.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() > 1) ||
            requestedPermissions.Any(x => !PermissionMatches(permissions, x.Key, x.Value)) ||
            permissions.EnumerateObject().Any(x => IsUnexpectedPermission(x, requestedPermissions)))
            throw new ControlException("GitHub returned a credential with unexpected scope, permissions or expiry; it was not delivered.");
        var observedAt = clock.GetUtcNow();
        return new GitHubInstallationToken(token, expires, slug + "[bot]",
            PermissionState(requestedPermissions, "checks"), PermissionState(requestedPermissions, "statuses"),
            PermissionState(requestedPermissions, "actions"), observedAt);
    }

    private async Task<JsonDocument> Send(HttpMethod method, string path, string jwt, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, ApiRoot + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.UserAgent.ParseAdd("HVO.AgentControl/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ControlException($"GitHub credential request failed (HTTP {(int)response.StatusCode}). Check App installation, repository access and permissions.");
        try { return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); }
        catch (JsonException) { throw new ControlException("GitHub returned an invalid credential response."); }
    }

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool HasReadAccess(JsonElement permissions, string name)
    {
        if (!permissions.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind != JsonValueKind.String)
            throw new ControlException("GitHub returned an invalid installation response.");
        return value.GetString() is "read" or "write";
    }

    private static string PermissionState(Dictionary<string, string> permissions, string name) =>
        permissions.ContainsKey(name) ? GitHubPermissionState.Granted : GitHubPermissionState.Denied;

    private static bool PermissionMatches(JsonElement permissions, string name, string expected) =>
        permissions.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static bool IsUnexpectedPermission(JsonProperty permission, Dictionary<string, string> requested) =>
        permission.Value.ValueKind != JsonValueKind.String || (permission.Name == "metadata"
            ? permission.Value.GetString() != "read"
            : !requested.ContainsKey(permission.Name) && permission.Value.GetString() != "none");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$")]
    private static partial Regex RepositoryPattern();
}
