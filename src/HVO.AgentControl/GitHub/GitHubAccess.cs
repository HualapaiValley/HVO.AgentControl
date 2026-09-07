using System.Text.Json.Serialization;

namespace HVO.AgentControl.GitHub;

public sealed class GitHubAccess
{
    public string Id { get; set; } = ""; // Runtime ID: credentials are shared by sessions on this runtime.
    public long AppId { get; set; }
    public long InstallationId { get; set; }
    [JsonIgnore] public string PrivateKeyReference { get; set; } = "";
    public string RepositoriesJson { get; set; } = "[]";
    public string State { get; set; } = "Pending";
    public string Detail { get; set; } = "";
    public long? ExpiresAt { get; set; }
    public long Revision { get; set; }
    public long RetryAt { get; set; }
}

public sealed record ConfigureGitHubAccess(long AppId, long InstallationId, string PrivateKey,
    string[] Repositories, long ExpectedRevision, string? SourceRuntimeId = null, long? SourceRevision = null);
