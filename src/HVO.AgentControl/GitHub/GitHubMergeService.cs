using System.Collections.Concurrent;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.GitHub;

public sealed class GitHubMergeService(ControlStore store, Secrets secrets, GitHubAppClient github)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public Task<List<GitHubMergeIntent>> ListIntents() => store.Read(db => db.GitHubMergeIntents.AsNoTracking()
        .OrderByDescending(x => x.RequestedAt).Take(100).ToListAsync());

    public Task<GitHubMergeIntent> CreateIntent(CreateGitHubMergeIntentInput input) => store.Write(async db =>
    {
        Validate(input);
        if (input.AuthorWorkerId == input.ReviewerWorkerId) throw new ControlException("The author and reviewer must be different workers.", 400);
        if (input.ReviewerIdentity.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)) throw new ControlException("A shared GitHub App bot is not independent review evidence.", 400);
        if (!await db.Workers.AnyAsync(x => x.Id == input.AuthorWorkerId) || !await db.Workers.AnyAsync(x => x.Id == input.ReviewerWorkerId))
            throw new ControlException("The author and reviewer must be registered workers.", 400);
        if (await db.GitHubMergeIntents.FindAsync(input.Id) is { } prior)
        {
            if (prior.ExpectedHeadSha != input.ExpectedHeadSha || prior.ReviewedHeadSha != input.ReviewedHeadSha ||
                prior.BaseSha != input.BaseSha || prior.Repository != input.Repository || prior.PullRequestNumber != input.PullRequestNumber ||
                prior.AuthorWorkerId != input.AuthorWorkerId || prior.ReviewerWorkerId != input.ReviewerWorkerId ||
                !string.Equals(prior.ReviewerIdentity, input.ReviewerIdentity, StringComparison.Ordinal) ||
                !Json.Read<string[]>(prior.RequiredChecksJson).SequenceEqual(input.RequiredChecks, StringComparer.Ordinal))
                throw new ControlException("Merge intent identity conflicts with the existing request.");
            return prior;
        }
        var intent = new GitHubMergeIntent
        {
            Id = input.Id,
            Repository = input.Repository,
            PullRequestNumber = input.PullRequestNumber,
            ExpectedHeadSha = input.ExpectedHeadSha,
            ReviewedHeadSha = input.ReviewedHeadSha,
            BaseSha = input.BaseSha,
            AuthorWorkerId = input.AuthorWorkerId,
            ReviewerWorkerId = input.ReviewerWorkerId,
            ReviewerIdentity = input.ReviewerIdentity,
            RequiredChecksJson = Json.Write(input.RequiredChecks),
            Detail = "Merge intent recorded; GitHub state must be freshly observed before execution."
        };
        db.GitHubMergeIntents.Add(intent);
        ControlStore.Event(db, "GitHubMergeIntentRequested", payload: new { intent.Id, intent.Repository, intent.PullRequestNumber, intent.ExpectedHeadSha, intent.AuthorWorkerId, intent.ReviewerWorkerId });
        return intent;
    });

    public async Task<GitHubCheckObservation> Observe(string id)
    {
        var intent = await store.Read(async db => await db.GitHubMergeIntents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new ControlException("Merge intent not found.", 404));
        var credential = await Credential(intent.Repository);
        var remote = await github.Observe(credential.Value, intent.Repository, intent.PullRequestNumber, CancellationToken.None);
        var required = Json.Read<string[]>(intent.RequiredChecksJson);
        var state = Evaluate(intent, remote, required, out var detail);
        return await store.Write(async db =>
        {
            var observation = new GitHubCheckObservation
            {
                Id = Guid.NewGuid().ToString("N"),
                Repository = intent.Repository,
                PullRequestNumber = intent.PullRequestNumber,
                HeadSha = remote.HeadSha,
                BaseSha = remote.BaseSha,
                PullRequestState = remote.State,
                Mergeable = remote.Mergeable,
                AuthorIdentity = remote.AuthorIdentity,
                ApprovedReviewersJson = Json.Write(remote.ApprovedReviews),
                UnresolvedReviewThreads = remote.UnresolvedThreads,
                ChecksJson = Json.Write(remote.Checks),
                StatusesJson = Json.Write(remote.Statuses),
                State = state,
                Detail = detail
            };
            db.GitHubCheckObservations.Add(observation);
            var current = await db.GitHubMergeIntents.FindAsync(id) ?? throw new ControlException("Merge intent disappeared.");
            current.ObservationId = observation.Id; current.State = state == "Ready" ? "Observed" : "Blocked"; current.Detail = detail; current.Revision++;
            ControlStore.Event(db, "GitHubMergeChecksObserved", payload: new { intentId = current.Id, observationId = observation.Id, observation.HeadSha, observation.State });
            return observation;
        });
    }

    public async Task<GitHubMergeIntent> Merge(string id, ExecuteGitHubMergeInput input)
    {
        var intent = await store.Read(async db => await db.GitHubMergeIntents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new ControlException("Merge intent not found.", 404));
        if (intent.Revision != input.ExpectedRevision) throw new ControlException("Merge intent changed; observe it again before merging.");
        var gate = Gates.GetOrAdd(intent.Repository + "#" + intent.BaseSha, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var observation = await Observe(id);
            if (observation.State != "Ready") throw new ControlException("GitHub merge gates are not satisfied: " + observation.Detail);
            var credential = await Credential(intent.Repository);
            await store.Write(db =>
            {
                var current = db.GitHubMergeIntents.Find(id) ?? throw new ControlException("Merge intent disappeared.");
                if (current.ExpectedHeadSha != observation.HeadSha || current.ObservationId != observation.Id)
                    throw new ControlException("Merge intent changed during gate refresh; no merge was attempted.");
                current.State = "Attempted"; current.AttemptedAt = ControlStore.Now; current.Revision++;
                return Task.FromResult(true);
            });
            string? sha;
            try { sha = await github.Merge(credential.Value, intent.Repository, intent.PullRequestNumber, intent.ExpectedHeadSha, CancellationToken.None); }
            catch
            {
                await store.Write(db => UpdateUnknown(db, id));
                throw;
            }
            return await store.Write(async db =>
            {
                var current = await db.GitHubMergeIntents.FindAsync(id) ?? throw new ControlException("Merge intent disappeared.");
                if (sha is null) { current.State = "Blocked"; current.Detail = "GitHub did not confirm the merge."; }
                else { current.State = "Merged"; current.MergeCommitSha = sha; current.Detail = "GitHub confirmed the expected-head merge."; }
                current.ReceiptJson = Json.Write(new { current.Repository, current.PullRequestNumber, current.ExpectedHeadSha, mergeCommitSha = sha, observedAt = ControlStore.Now }); current.Revision++;
                ControlStore.Event(db, "GitHubMergeReceiptRecorded", payload: new { current.Id, current.State, sha });
                return current;
            });
        }
        finally { gate.Release(); }
    }

    private async Task<GitHubInstallationToken> Credential(string repository)
    {
        var access = await store.Read(async db => await db.GitHubAccess.AsNoTracking().ToListAsync());
        var record = access.FirstOrDefault(x => x.State == "Ready" && Json.Read<string[]>(x.RepositoriesJson).Contains(repository, StringComparer.OrdinalIgnoreCase))
            ?? throw new ControlException("No ready GitHub installation covers this repository.");
        if (record.ExactCiInspectionState != "Ready") throw new ControlException("GitHub check-read permission is not verified.");
        return await github.Issue(record.AppId, record.InstallationId, secrets.Read(record.PrivateKeyReference), [repository], CancellationToken.None);
    }

    private static string Evaluate(GitHubMergeIntent intent, GitHubPullRequestObservation remote, string[] required, out string detail)
    {
        if (remote.HeadSha != intent.ExpectedHeadSha || remote.HeadSha != intent.ReviewedHeadSha) { detail = "Pull request head changed since review."; return "Blocked"; }
        if (remote.BaseSha != intent.BaseSha || remote.State != "open" || !remote.Mergeable) { detail = "Pull request base/state is not mergeable."; return "Blocked"; }
        if (remote.UnresolvedThreads > 0) { detail = "Unresolved review threads remain."; return "Blocked"; }
        if (!remote.ApprovedReviews.Any(x => x.StartsWith(intent.ReviewerIdentity + "@", StringComparison.OrdinalIgnoreCase) && x.EndsWith("@" + remote.HeadSha, StringComparison.OrdinalIgnoreCase))) { detail = "No approval from the recorded reviewer covers this exact head."; return "Blocked"; }
        var checks = remote.Checks.Concat(remote.Statuses).ToArray();
        var missing = required.Where(name => !checks.Any(x => x.Name == name && x.Status == "completed" && x.Conclusion.Equals("success", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0) { detail = "Required checks are missing, pending or unsuccessful: " + string.Join(", ", missing); return "Blocked"; }
        detail = "Exact head, base, independent review, resolved threads and required checks verified."; return "Ready";
    }

    private static Task<bool> UpdateUnknown(ControlDb db, string id)
    { var current = db.GitHubMergeIntents.Find(id) ?? throw new ControlException("Merge intent disappeared."); current.State = "Unknown"; current.Detail = "Merge response was uncertain; reconcile GitHub before retrying."; current.Revision++; return Task.FromResult(true); }
    private static void Validate(CreateGitHubMergeIntentInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Id) || input.RequiredChecks is null || input.RequiredChecks.Length is < 1 or > 100 ||
            input.RequiredChecks.Any(string.IsNullOrWhiteSpace) || input.RequiredChecks.Distinct(StringComparer.Ordinal).Count() != input.RequiredChecks.Length ||
            new[] { input.ExpectedHeadSha, input.ReviewedHeadSha, input.BaseSha }.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[0-9a-f]{40}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new ControlException("Invalid merge intent.", 400);
    }
}
