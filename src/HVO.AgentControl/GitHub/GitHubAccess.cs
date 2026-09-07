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
    public string ChecksPermission { get; set; } = GitHubPermissionState.Unknown;
    public string CommitStatusesPermission { get; set; } = GitHubPermissionState.Unknown;
    public string ActionsPermission { get; set; } = GitHubPermissionState.Unknown;
    public long? PermissionsVerifiedAt { get; set; }
    public string ExactCiInspectionState => new[] { ChecksPermission, CommitStatusesPermission, ActionsPermission } switch
    {
        var permissions when permissions.Any(x => x == GitHubPermissionState.Denied) => "PermissionDenied",
        var permissions when permissions.Any(x => x != GitHubPermissionState.Granted) => "Unknown",
        _ when State == "Ready" => "Ready",
        _ => "CredentialUnavailable"
    };
}

public static class GitHubPermissionState
{
    public const string Unknown = "Unknown";
    public const string Granted = "Granted";
    public const string Denied = "Denied";
}

public sealed record ConfigureGitHubAccess(long AppId, long InstallationId, string PrivateKey,
    string[] Repositories, long ExpectedRevision, string? SourceRuntimeId = null, long? SourceRevision = null);
