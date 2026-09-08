using System.ComponentModel.DataAnnotations;

namespace HVO.AgentControl.Core;

public sealed class GitHubMergeIntent
{
    [Key] public string Id { get; set; } = "";
    public string Repository { get; set; } = "";
    public int PullRequestNumber { get; set; }
    public string ExpectedHeadSha { get; set; } = "";
    public string ReviewedHeadSha { get; set; } = "";
    public string BaseSha { get; set; } = "";
    public string AuthorWorkerId { get; set; } = "";
    public string ReviewerWorkerId { get; set; } = "";
    public string ReviewerIdentity { get; set; } = "";
    public string RequiredChecksJson { get; set; } = "[]";
    public string? ObservationId { get; set; }
    public string State { get; set; } = "Requested";
    public string Detail { get; set; } = "";
    public long RequestedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long? AttemptedAt { get; set; }
    public string? MergeCommitSha { get; set; }
    public string ReceiptJson { get; set; } = "{}";
    public long Revision { get; set; }
}

public sealed class GitHubCheckObservation
{
    [Key] public string Id { get; set; } = "";
    public string Repository { get; set; } = "";
    public int PullRequestNumber { get; set; }
    public string HeadSha { get; set; } = "";
    public string BaseSha { get; set; } = "";
    public string PullRequestState { get; set; } = "";
    public bool Mergeable { get; set; }
    public string AuthorIdentity { get; set; } = "";
    public string ApprovedReviewersJson { get; set; } = "[]";
    public int UnresolvedReviewThreads { get; set; }
    public string ChecksJson { get; set; } = "[]";
    public string StatusesJson { get; set; } = "[]";
    public string State { get; set; } = "Unknown";
    public string Detail { get; set; } = "";
    public long ObservedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed record CreateGitHubMergeIntentInput(string Id, string Repository, int PullRequestNumber,
    string ExpectedHeadSha, string ReviewedHeadSha, string BaseSha, string AuthorWorkerId,
    string ReviewerWorkerId, string ReviewerIdentity, string[] RequiredChecks);

public sealed record ObserveGitHubMergeInput(string Id);
public sealed record ExecuteGitHubMergeInput(string Id, long ExpectedRevision);
