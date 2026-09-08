using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HVO.AgentControl.Core;

namespace HVO.AgentControl.GitHub;

public static class GitHubMergeTaskAuthority
{
    public static void ValidatePromptScope(GitHubMergeTaskScope scope)
    {
        if (scope is null || scope.Version != GitHubMergeTaskKinds.Version || scope.Purpose != GitHubMergeTaskKinds.Purpose ||
            scope.Role is not (GitHubMergeTaskKinds.Author or GitHubMergeTaskKinds.Reviewer) ||
            !Regex.IsMatch(scope.Repository ?? "", "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ||
            scope.HeadSha is null ||
            scope.Role == GitHubMergeTaskKinds.Reviewer && !IsExact(scope) ||
            scope.Role == GitHubMergeTaskKinds.Author && !IsExact(scope) &&
                (scope.PullRequestNumber < 0 || !string.IsNullOrEmpty(scope.HeadSha)))
            throw new ControlException("Invalid typed GitHub merge task scope.", 400);
    }

    public static void ValidateExactScope(GitHubMergeTaskScope scope)
    {
        ValidatePromptScope(scope);
        if (!IsExact(scope)) throw new ControlException("Typed GitHub merge result requires an exact pull request and head.", 400);
    }

    public static GitHubMergeOutcomeAuthority Create(CommandRecord command, AssignmentRecord assignment,
        OutcomeInput input, long recordedAt)
    {
        var result = input.GitHubMergeResult ?? throw new ControlException("Typed GitHub merge result is required.", 400);
        ValidateExactScope(result.Scope);
        if (result.Version != GitHubMergeTaskKinds.Version || input.Outcome != "VerifiedComplete" ||
            result.Verdict != Verdict(result.Scope.Role))
            throw new ControlException("Typed GitHub merge result and verified outcome do not match the retained task role.", 400);
        PromptInput prompt;
        try { prompt = Json.Read<PromptInput>(command.Payload); }
        catch { throw new ControlException("The retained prompt does not contain typed GitHub merge authority.", 400); }
        if (prompt.GitHubMergeScope is null || !PromptCovers(prompt.GitHubMergeScope, result.Scope))
            throw new ControlException("Typed GitHub merge result does not match the immutable prompt scope.", 400);
        var resultId = ResultIdentity(command);
        if (command.State != Delivery.Finished || string.IsNullOrWhiteSpace(resultId) || command.ResultJson == "{}")
            throw new ControlException("A finished native result is required for typed GitHub merge authority.");
        return new(GitHubMergeTaskKinds.Version, result.Scope, result.Verdict, assignment.WorkerId, command.Id,
            resultId, Hash(command.ResultJson), Hash(input.Evidence), recordedAt);
    }

    public static GitHubMergeOutcomeAuthority RequireCurrent(CommandRecord? command, AssignmentRecord? assignment,
        string workerId, GitHubMergeTaskScope expected)
    {
        if (command is null || assignment is null || command.Id != assignment.Id || command.WorkerId != workerId ||
            assignment.WorkerId != workerId || command.Kind != "Prompt" || command.State != Delivery.Finished ||
            assignment.Outcome != "VerifiedComplete")
            throw new ControlException("Retained typed command and assignment authority is unavailable for the exact worker.");
        GitHubMergeOutcomeAuthority authority;
        PromptInput prompt;
        try
        {
            authority = Json.Read<GitHubMergeOutcomeAuthority>(assignment.GitHubAuthorityJson);
            prompt = Json.Read<PromptInput>(command.Payload);
        }
        catch
        {
            throw new ControlException("Retained typed GitHub merge authority is unavailable.");
        }
        ValidateExactScope(authority.Scope);
        if (authority.Version != GitHubMergeTaskKinds.Version || !SameScope(authority.Scope, expected) ||
            prompt.GitHubMergeScope is null || !PromptCovers(prompt.GitHubMergeScope, expected) ||
            authority.Verdict != Verdict(expected.Role) ||
            authority.WorkerId != workerId || authority.CommandId != command.Id ||
            string.IsNullOrWhiteSpace(ResultIdentity(command)) || authority.CommandResultId != ResultIdentity(command) ||
            authority.CommandResultSha256 != Hash(command.ResultJson) || authority.EvidenceSha256 != Hash(assignment.Evidence))
            throw new ControlException("Retained typed GitHub merge authority changed or does not match the exact task result.");
        return authority;
    }

    public static bool HasRetainedAuthorProvenance(AssignmentRecord assignment, string workerId,
        string repository, int pullRequestNumber)
    {
        try
        {
            var authority = Json.Read<GitHubMergeOutcomeAuthority>(assignment.GitHubAuthorProvenanceJson);
            return authority.Version == GitHubMergeTaskKinds.Version && authority.WorkerId == workerId &&
                authority.CommandId == assignment.Id && authority.Scope.Role == GitHubMergeTaskKinds.Author &&
                authority.Scope.Purpose == GitHubMergeTaskKinds.Purpose && SameRepository(authority.Scope.Repository, repository) &&
                authority.Scope.PullRequestNumber == pullRequestNumber &&
                authority.Verdict == GitHubMergeTaskKinds.PublishedExactHead;
        }
        catch { return false; }
    }

    public static GitHubMergeTaskScope Scope(string role, string repository, int pullRequestNumber, string headSha) =>
        new(GitHubMergeTaskKinds.Version, GitHubMergeTaskKinds.Purpose, role, repository, pullRequestNumber, headSha);

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static bool SameRepository(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static bool SameHead(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static bool SameScope(GitHubMergeTaskScope left, GitHubMergeTaskScope right) =>
        left.Version == right.Version && left.Purpose == right.Purpose && left.Role == right.Role &&
        SameRepository(left.Repository, right.Repository) && left.PullRequestNumber == right.PullRequestNumber &&
        SameHead(left.HeadSha, right.HeadSha);

    public static bool SamePublication(GitHubMergeOutcomeAuthority retained, GitHubMergeOutcomeAuthority current) =>
        retained.Version == GitHubMergeTaskKinds.Version && current.Version == GitHubMergeTaskKinds.Version &&
        retained.Scope.Version == GitHubMergeTaskKinds.Version && current.Scope.Version == GitHubMergeTaskKinds.Version &&
        retained.Scope.Purpose == GitHubMergeTaskKinds.Purpose && current.Scope.Purpose == GitHubMergeTaskKinds.Purpose &&
        retained.Scope.Role == GitHubMergeTaskKinds.Author && current.Scope.Role == GitHubMergeTaskKinds.Author &&
        retained.Verdict == GitHubMergeTaskKinds.PublishedExactHead && current.Verdict == GitHubMergeTaskKinds.PublishedExactHead &&
        retained.WorkerId == current.WorkerId && retained.CommandId == current.CommandId &&
        SameRepository(retained.Scope.Repository, current.Scope.Repository) &&
        retained.Scope.PullRequestNumber == current.Scope.PullRequestNumber;

    private static string ResultIdentity(CommandRecord command) => command.ResultId ?? command.NativeMessageId ?? "";

    private static bool PromptCovers(GitHubMergeTaskScope prompt, GitHubMergeTaskScope exact) =>
        prompt.Version == exact.Version && prompt.Purpose == exact.Purpose && prompt.Role == exact.Role &&
        SameRepository(prompt.Repository, exact.Repository) &&
        (prompt.PullRequestNumber == exact.PullRequestNumber && SameHead(prompt.HeadSha, exact.HeadSha) ||
            prompt.Role == GitHubMergeTaskKinds.Author && string.IsNullOrEmpty(prompt.HeadSha) &&
            (prompt.PullRequestNumber == 0 || prompt.PullRequestNumber == exact.PullRequestNumber));

    private static bool IsExact(GitHubMergeTaskScope scope) => scope.PullRequestNumber > 0 &&
        Regex.IsMatch(scope.HeadSha ?? "", "^[0-9a-f]{40}$", RegexOptions.IgnoreCase);

    private static string Verdict(string role) => role switch
    {
        GitHubMergeTaskKinds.Author => GitHubMergeTaskKinds.PublishedExactHead,
        GitHubMergeTaskKinds.Reviewer => GitHubMergeTaskKinds.ApprovedExactHead,
        _ => throw new ControlException("Invalid typed GitHub merge task role.", 400)
    };
}
