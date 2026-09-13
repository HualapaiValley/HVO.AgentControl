using System.ComponentModel.DataAnnotations;

namespace HVO.AgentControl.Core;

public sealed class GitHubMergePolicy
{
    public string Id { get; set; } = "";
    public string Repository { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string RequiredChecksJson { get; set; } = "[]";
    public string State { get; set; } = "Active";
    public long Revision { get; set; }
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class GitHubReviewReceipt
{
    [Key] public string Id { get; set; } = "";
    public string Repository { get; set; } = "";
    public int PullRequestNumber { get; set; }
    public string HeadSha { get; set; } = "";
    public string AuthorWorkerId { get; set; } = "";
    public string ReviewerWorkerId { get; set; } = "";
    public string AuthorCommandId { get; set; } = "";
    public string ReviewCommandId { get; set; } = "";
    public long GitHubReviewId { get; set; }
    public string GitHubReviewerIdentity { get; set; } = "";
    public string EvidenceJson { get; set; } = "{}";
    public long RecordedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class GitHubMergeIntent
{
    [Key] public string Id { get; set; } = "";
    public string Repository { get; set; } = "";
    public int PullRequestNumber { get; set; }
    public string ExpectedHeadSha { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string BaseSha { get; set; } = "";
    public string ReviewReceiptId { get; set; } = "";
    public string PolicyId { get; set; } = "";
    public long PolicyRevision { get; set; }
    public string RequiredChecksJson { get; set; } = "[]";
    public string? ObservationId { get; set; }
    public string State { get; set; } = "Requested";
    public string Detail { get; set; } = "";
    public long RequestedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string? MergeCommitSha { get; set; }
    public string ReceiptJson { get; set; } = "{}";
    public long Revision { get; set; }
}

public sealed class GitHubCheckObservation
{
    [Key] public string Id { get; set; } = "";
    public string IntentId { get; set; } = "";
    public string Repository { get; set; } = "";
    public int PullRequestNumber { get; set; }
    public string HeadSha { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string BaseSha { get; set; } = "";
    public string PullRequestState { get; set; } = "";
    public bool Mergeable { get; set; }
    public bool Merged { get; set; }
    public string? MergeCommitSha { get; set; }
    public string AuthorIdentity { get; set; } = "";
    public string ReviewsJson { get; set; } = "[]";
    public int UnresolvedReviewThreads { get; set; }
    public string ChecksJson { get; set; } = "[]";
    public string StatusesJson { get; set; } = "[]";
    public bool Complete { get; set; }
    public string State { get; set; } = "Unknown";
    public string Detail { get; set; } = "";
    public long ObservedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class GitHubMergeAttempt
{
    [Key] public string Id { get; set; } = "";
    public string IntentId { get; set; } = "";
    public string Repository { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string HeadSha { get; set; } = "";
    public string ObservationId { get; set; } = "";
    public string State { get; set; } = "Attempted";
    public string Detail { get; set; } = "";
    public long StartedAt { get; set; }
    public long? CompletedAt { get; set; }
    public string? MergeCommitSha { get; set; }
    public string ReceiptJson { get; set; } = "{}";
}

public sealed class GitHubMergeLease
{
    [Key] public string Id { get; set; } = "";
    public string Repository { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string IntentId { get; set; } = "";
    public long AcquiredAt { get; set; }
}

public static class GitHubMergeTaskKinds
{
    public const int Version = 1;
    public const string Purpose = "PullRequestMergeAuthority";
    public const string Author = "Author";
    public const string Reviewer = "Reviewer";
    public const string PublishedExactHead = "PublishedExactHead";
    public const string ApprovedExactHead = "ApprovedExactHead";
}

public sealed record GitHubMergeTaskScope(int Version, string Purpose, string Role, string Repository,
    int PullRequestNumber, string HeadSha);

public sealed record GitHubMergeTaskResult(int Version, string Verdict, GitHubMergeTaskScope Scope);

public sealed record GitHubMergeOutcomeAuthority(int Version, GitHubMergeTaskScope Scope, string Verdict,
    string WorkerId, string CommandId, string CommandResultId, string CommandResultSha256,
    string EvidenceSha256, long RecordedAt);

public sealed record GitHubReviewAuthoritySnapshot(int Version, GitHubMergeOutcomeAuthority Author,
    GitHubMergeOutcomeAuthority Reviewer);

public sealed record ConfigureGitHubMergePolicyInput(string Id, string Repository, string BaseBranch,
    string[] RequiredChecks, long ExpectedRevision, bool Active = true);
public sealed record RecordGitHubReviewInput(string Id, string Repository, int PullRequestNumber, string HeadSha,
    string AuthorWorkerId, string ReviewerWorkerId, string AuthorCommandId, string ReviewCommandId,
    long GitHubReviewId, string GitHubReviewerIdentity);
public sealed record CreateGitHubMergeIntentInput(string Id, string Repository, int PullRequestNumber,
    string ExpectedHeadSha, string BaseBranch, string BaseSha, string ReviewReceiptId,
    string PolicyId, long PolicyRevision);
public sealed record ExecuteGitHubMergeInput(string Id, long ExpectedRevision);
