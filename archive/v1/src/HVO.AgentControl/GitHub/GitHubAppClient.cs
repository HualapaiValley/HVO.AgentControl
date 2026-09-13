using System.Globalization;
using System.Net;
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

    public async Task<GitHubPullRequestObservation> Observe(string token, string repository, int number,
        CancellationToken cancellationToken)
    {
        ValidateRepository(repository);
        if (number <= 0) throw new ControlException("Pull request number must be positive.", 400);
        using var pull = await SendToken(HttpMethod.Get, $"repos/{repository}/pulls/{number}", token, null, cancellationToken);
        var head = Sha(pull.RootElement, "head", "sha");
        var baseBranch = String(pull.RootElement, "base", "ref");
        using var target = await SendToken(HttpMethod.Get, $"repos/{repository}/git/ref/heads/{Uri.EscapeDataString(baseBranch)}", token, null, cancellationToken);
        var reviews = await Pages(token, $"repos/{repository}/pulls/{number}/reviews?per_page=100", null, cancellationToken);
        var checks = await Pages(token, $"repos/{repository}/commits/{head}/check-runs?per_page=100&filter=latest", "check_runs", cancellationToken);
        var statuses = await Pages(token, $"repos/{repository}/commits/{head}/statuses?per_page=100", null, cancellationToken);
        var owner = repository.Split('/')[0];
        var threads = await Threads(token, owner, repository.Split('/')[1], number, cancellationToken);
        bool? mergeable = pull.RootElement.TryGetProperty("mergeable", out var mergeableElement) && mergeableElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? mergeableElement.GetBoolean() : null;
        return new GitHubPullRequestObservation(
            head, Sha(target.RootElement, "object", "sha"), baseBranch, String(pull.RootElement, "state"), mergeable,
            Boolean(pull.RootElement, "merged"), OptionalString(pull.RootElement, "merge_commit_sha"), String(pull.RootElement, "user", "login"),
            Reviews(reviews.Items), Checks(checks.Items), Statuses(statuses.Items), threads.Unresolved,
            reviews.Complete && checks.Complete && statuses.Complete && threads.Complete && mergeable is not null);
    }

    public async Task<string?> Merge(string token, string repository, int number, string expectedHeadSha,
        CancellationToken cancellationToken)
    {
        ValidateRepository(repository);
        if (number <= 0 || !ShaPattern().IsMatch(expectedHeadSha)) throw new ControlException("Invalid pull request merge identity.", 400);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new ControlException("GitHub credential is unavailable.");
        using var request = new HttpRequestMessage(HttpMethod.Put, ApiRoot + $"repos/{repository}/pulls/{number}/merge");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("HVO.AgentControl/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Content = JsonContent.Create(new { sha = expectedHeadSha, merge_method = "merge" });
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or
            HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new ControlException($"GitHub merge response was uncertain (HTTP {(int)response.StatusCode}).");
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True
                ? document.RootElement.GetProperty("sha").GetString() : null;
        }
        catch (JsonException) { throw new ControlException("GitHub returned an invalid merge response."); }
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

    private async Task<JsonDocument> SendToken(HttpMethod method, string path, string token, object? body, CancellationToken cancellationToken)
    {
        var result = await SendTokenPage(method, path, token, body, cancellationToken);
        return result.Document;
    }

    private async Task<GitHubPage> SendTokenPage(HttpMethod method, string path, string token, object? body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new ControlException("GitHub credential is unavailable.");
        var target = path.StartsWith("https://", StringComparison.Ordinal) ? new Uri(path) : new Uri(ApiRoot + path);
        if (target.Scheme != Uri.UriSchemeHttps || target.Host != "api.github.com")
            throw new ControlException("GitHub pagination returned an invalid API location.");
        using var request = new HttpRequestMessage(method, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("HVO.AgentControl/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ControlException($"GitHub request failed (HTTP {(int)response.StatusCode}); refresh the installation credential.");
        try
        {
            var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var next = response.Headers.TryGetValues("Link", out var links) ? Next(links) : null;
            return new(document, next);
        }
        catch (JsonException) { throw new ControlException("GitHub returned an invalid response."); }
    }

    private async Task<GitHubPages> Pages(string token, string path, string? property, CancellationToken cancellationToken)
    {
        var values = new List<JsonElement>();
        for (var page = 0; page < 20; page++)
        {
            var response = await SendTokenPage(HttpMethod.Get, path, token, null, cancellationToken);
            using (response.Document)
            {
                var items = property is null ? response.Document.RootElement : response.Document.RootElement.GetProperty(property);
                values.AddRange(items.EnumerateArray().Select(x => x.Clone()));
            }
            if (response.Next is null) return new(values.ToArray(), true);
            path = response.Next;
        }
        return new(values.ToArray(), false);
    }

    private async Task<GitHubThreads> Threads(string token, string owner, string name, int number, CancellationToken cancellationToken)
    {
        const string query = "query($owner:String!,$name:String!,$number:Int!,$after:String){repository(owner:$owner,name:$name){pullRequest(number:$number){reviewThreads(first:100,after:$after){nodes{isResolved}pageInfo{hasNextPage endCursor}}}}}";
        string? after = null;
        var unresolved = 0;
        for (var page = 0; page < 20; page++)
        {
            using var response = await SendToken(HttpMethod.Post, "graphql", token,
                new { query, variables = new { owner, name, number, after } }, cancellationToken);
            if (response.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                throw new ControlException("GitHub review-thread observation returned partial data with errors.");
            var threads = response.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest").GetProperty("reviewThreads");
            unresolved += threads.GetProperty("nodes").EnumerateArray().Count(x => !x.GetProperty("isResolved").GetBoolean());
            var pageInfo = threads.GetProperty("pageInfo");
            if (!pageInfo.GetProperty("hasNextPage").GetBoolean()) return new(unresolved, true);
            after = pageInfo.GetProperty("endCursor").GetString();
            if (string.IsNullOrEmpty(after)) return new(unresolved, false);
        }
        return new(unresolved, false);
    }

    private static string Sha(JsonElement document, string parent, string property) =>
        document.GetProperty(parent).GetProperty(property).GetString() ?? throw new ControlException("GitHub returned an invalid pull request response.");
    private static string String(JsonElement document, string parent, string property) =>
        document.GetProperty(parent).GetProperty(property).GetString() ?? "";
    private static string String(JsonElement document, string property) => document.GetProperty(property).GetString() ?? "";
    private static string? OptionalString(JsonElement document, string property) =>
        document.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Boolean(JsonElement document, string property) =>
        document.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static GitHubReview[] Reviews(IEnumerable<JsonElement> elements) => elements
        .Select(x => new GitHubReview(x.GetProperty("id").GetInt64(), String(x, "user", "login"), String(x, "state"),
            OptionalString(x, "commit_id") ?? "", OptionalString(x, "submitted_at") ?? "")).ToArray();
    private static GitHubCheck[] Checks(IEnumerable<JsonElement> elements) => elements
        .Select(x => new GitHubCheck(String(x, "name"), String(x, "status"), x.TryGetProperty("conclusion", out var conclusion) ? conclusion.GetString() ?? "" : "")).ToArray();
    private static GitHubCheck[] Statuses(IEnumerable<JsonElement> elements) => elements
        .GroupBy(x => String(x, "context"), StringComparer.Ordinal).Select(x => x.First())
        .Select(x => new GitHubCheck(String(x, "context"), String(x, "state") == "pending" ? "in_progress" : "completed", String(x, "state"))).ToArray();
    private static string? Next(IEnumerable<string> links)
    {
        foreach (var link in links.SelectMany(x => x.Split(',')))
        {
            var parts = link.Split(';', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && parts.Skip(1).Any(x => x == "rel=\"next\"") && parts[0] is ['<', .., '>'])
                return parts[0][1..^1];
        }
        return null;
    }
    private static void ValidateRepository(string repository)
    { if (!RepositoryPattern().IsMatch(repository)) throw new ControlException("Repository must use owner/repository form.", 400); }

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
    [GeneratedRegex(@"^[0-9a-f]{40}$", RegexOptions.IgnoreCase)]
    private static partial Regex ShaPattern();
}

public sealed record GitHubCheck(string Name, string Status, string Conclusion);
public sealed record GitHubReview(long Id, string User, string State, string CommitId, string SubmittedAt);
public sealed record GitHubPullRequestObservation(string HeadSha, string BaseSha, string BaseBranch, string State, bool? Mergeable,
    bool Merged, string? MergeCommitSha, string AuthorIdentity, GitHubReview[] Reviews, GitHubCheck[] Checks,
    GitHubCheck[] Statuses, int UnresolvedThreads, bool Complete);
internal sealed record GitHubPage(JsonDocument Document, string? Next);
internal sealed record GitHubPages(JsonElement[] Items, bool Complete);
internal sealed record GitHubThreads(int Unresolved, bool Complete);
