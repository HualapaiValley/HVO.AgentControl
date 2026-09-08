using System.Text.RegularExpressions;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.GitHub;

public sealed class GitHubMergeService(ControlStore store, Secrets secrets, GitHubAppClient github)
{
    public Task<List<GitHubMergePolicy>> ListPolicies() => store.Read(db => db.GitHubMergePolicies.AsNoTracking()
        .OrderBy(x => x.Repository).ThenBy(x => x.BaseBranch).ThenByDescending(x => x.Revision).ToListAsync());

    public Task<GitHubMergePolicy> ConfigurePolicy(ConfigureGitHubMergePolicyInput input) => store.Write(async db =>
    {
        ValidateRepository(input.Repository);
        if (string.IsNullOrWhiteSpace(input.Id) || string.IsNullOrWhiteSpace(input.BaseBranch) || input.RequiredChecks is null ||
            input.RequiredChecks.Length is < 1 or > 100 || input.RequiredChecks.Any(string.IsNullOrWhiteSpace) ||
            input.RequiredChecks.Distinct(StringComparer.Ordinal).Count() != input.RequiredChecks.Length)
            throw new ControlException("Invalid GitHub merge policy.", 400);

        var latest = await db.GitHubMergePolicies.Where(x => x.Id == input.Id).OrderByDescending(x => x.Revision).FirstOrDefaultAsync();
        if (latest is not null && (!GitHubMergeTaskAuthority.SameRepository(latest.Repository, input.Repository) || latest.BaseBranch != input.BaseBranch))
            throw new ControlException("Merge policy identity conflicts with the existing repository target.");
        if (latest is not null && latest.Revision == input.ExpectedRevision + 1 &&
            latest.State == (input.Active ? "Active" : "Disabled") &&
            Json.Read<string[]>(latest.RequiredChecksJson).SequenceEqual(input.RequiredChecks, StringComparer.Ordinal))
            return latest;
        if ((latest?.Revision ?? 0) != input.ExpectedRevision)
            throw new ControlException("Merge policy changed; refresh before saving.");

        var policy = new GitHubMergePolicy
        {
            Id = input.Id,
            Repository = input.Repository,
            BaseBranch = input.BaseBranch,
            RequiredChecksJson = Json.Write(input.RequiredChecks),
            State = input.Active ? "Active" : "Disabled",
            Revision = input.ExpectedRevision + 1,
            UpdatedAt = ControlStore.Now
        };
        db.GitHubMergePolicies.Add(policy);
        ControlStore.Event(db, "GitHubMergePolicyConfigured", payload: new
        {
            policy.Id,
            policy.Repository,
            policy.BaseBranch,
            requiredChecks = input.RequiredChecks,
            policy.State,
            policy.Revision
        }, provenance: "user");
        return policy;
    });

    public async Task<GitHubReviewReceipt> RecordReview(RecordGitHubReviewInput input)
    {
        ValidateReview(input);
        var prior = await store.Read(db => db.GitHubReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.Id));
        if (prior is not null) return SameReview(prior, input);

        var evidence = await store.Read(async db => new
        {
            Author = await db.Workers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.AuthorWorkerId),
            Reviewer = await db.Workers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ReviewerWorkerId),
            AuthorCommand = await db.Commands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.AuthorCommandId),
            ReviewCommand = await db.Commands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ReviewCommandId),
            AuthorAssignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.AuthorCommandId),
            ReviewAssignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ReviewCommandId)
        });
        if (evidence.Author is null || evidence.Reviewer is null || input.AuthorWorkerId == input.ReviewerWorkerId)
            throw new ControlException("The author and reviewer must be distinct registered workers.", 400);
        RequireAssignment(evidence.AuthorCommand, evidence.AuthorAssignment, input.AuthorWorkerId, input.Repository,
            input.PullRequestNumber, input.HeadSha, GitHubMergeTaskKinds.Author);
        RequireAssignment(evidence.ReviewCommand, evidence.ReviewAssignment, input.ReviewerWorkerId, input.Repository,
            input.PullRequestNumber, input.HeadSha, GitHubMergeTaskKinds.Reviewer);
        if (await store.Read(db => HasAuthorEvidence(db, input.ReviewerWorkerId, input.Repository, input.PullRequestNumber, input.HeadSha)))
            throw new ControlException("A worker with retained author-role evidence for this pull request cannot act as its reviewer.");

        var credential = await Credential(input.Repository);
        var remote = await github.Observe(credential.Value, input.Repository, input.PullRequestNumber, CancellationToken.None);
        if (!remote.Complete || !GitHubMergeTaskAuthority.SameHead(remote.HeadSha, input.HeadSha))
            throw new ControlException("Complete GitHub review evidence for the exact head is unavailable.");
        var review = remote.Reviews.SingleOrDefault(x => x.Id == input.GitHubReviewId);
        if (review is null || review.State != "APPROVED" || !GitHubMergeTaskAuthority.SameHead(review.CommitId, input.HeadSha) ||
            !string.Equals(review.User, input.GitHubReviewerIdentity, StringComparison.OrdinalIgnoreCase))
            throw new ControlException("The GitHub review receipt is not an approval for the exact head.");
        var latest = LatestReviews(remote.Reviews);
        if (!latest.TryGetValue(review.User, out var current) || current.State != "APPROVED" || !GitHubMergeTaskAuthority.SameHead(current.CommitId, input.HeadSha))
            throw new ControlException("The recorded GitHub reviewer no longer approves the exact head.");
        if (string.Equals(remote.AuthorIdentity, review.User, StringComparison.OrdinalIgnoreCase))
            throw new ControlException("The pull request author cannot supply native GitHub approval.");

        return await store.Write(async db =>
        {
            if (await db.GitHubReviewReceipts.FindAsync(input.Id) is { } existing) return SameReview(existing, input);
            var currentAuthor = await db.Workers.SingleOrDefaultAsync(x => x.Id == input.AuthorWorkerId);
            var currentReviewer = await db.Workers.SingleOrDefaultAsync(x => x.Id == input.ReviewerWorkerId);
            var currentAuthorCommand = await db.Commands.SingleOrDefaultAsync(x => x.Id == input.AuthorCommandId);
            var currentReviewCommand = await db.Commands.SingleOrDefaultAsync(x => x.Id == input.ReviewCommandId);
            var currentAuthorAssignment = await db.Assignments.SingleOrDefaultAsync(x => x.Id == input.AuthorCommandId);
            var currentReviewAssignment = await db.Assignments.SingleOrDefaultAsync(x => x.Id == input.ReviewCommandId);
            if (currentAuthor is null || currentReviewer is null || input.AuthorWorkerId == input.ReviewerWorkerId)
                throw new ControlException("The author and reviewer must remain distinct registered workers.");
            var authorAuthority = RequireAssignment(currentAuthorCommand, currentAuthorAssignment, input.AuthorWorkerId,
                input.Repository, input.PullRequestNumber, input.HeadSha, GitHubMergeTaskKinds.Author);
            var reviewerAuthority = RequireAssignment(currentReviewCommand, currentReviewAssignment, input.ReviewerWorkerId,
                input.Repository, input.PullRequestNumber, input.HeadSha, GitHubMergeTaskKinds.Reviewer);
            if (await HasAuthorEvidence(db, input.ReviewerWorkerId, input.Repository, input.PullRequestNumber, input.HeadSha))
                throw new ControlException("A worker with retained author-role evidence for this pull request cannot act as its reviewer.");
            var receipt = new GitHubReviewReceipt
            {
                Id = input.Id,
                Repository = input.Repository,
                PullRequestNumber = input.PullRequestNumber,
                HeadSha = input.HeadSha,
                AuthorWorkerId = input.AuthorWorkerId,
                ReviewerWorkerId = input.ReviewerWorkerId,
                AuthorCommandId = input.AuthorCommandId,
                ReviewCommandId = input.ReviewCommandId,
                GitHubReviewId = input.GitHubReviewId,
                GitHubReviewerIdentity = input.GitHubReviewerIdentity,
                EvidenceJson = Json.Write(new GitHubReviewAuthoritySnapshot(GitHubMergeTaskKinds.Version,
                    authorAuthority, reviewerAuthority)),
                RecordedAt = ControlStore.Now
            };
            db.GitHubReviewReceipts.Add(receipt);
            ControlStore.Event(db, "GitHubReviewReceiptRecorded", workerId: input.ReviewerWorkerId,
                commandId: input.ReviewCommandId, payload: new { receipt.Id, receipt.Repository, receipt.PullRequestNumber, receipt.HeadSha, receipt.GitHubReviewId });
            return receipt;
        });
    }

    public Task<List<GitHubMergeIntent>> ListIntents() => store.Read(db => db.GitHubMergeIntents.AsNoTracking()
        .OrderByDescending(x => x.RequestedAt).Take(100).ToListAsync());

    public async Task<GitHubMergeIntent> CreateIntent(CreateGitHubMergeIntentInput input)
    {
        ValidateIntent(input);
        var prior = await store.Read(db => db.GitHubMergeIntents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.Id));
        if (prior is not null) return SameIntent(prior, input);

        var authority = await store.Read(async db => new
        {
            Policy = await db.GitHubMergePolicies.AsNoTracking().Where(x => x.Id == input.PolicyId).OrderByDescending(x => x.Revision).FirstOrDefaultAsync(),
            Review = await db.GitHubReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ReviewReceiptId)
        });
        ValidateAuthority(input, authority.Policy, authority.Review);
        await store.Read(db => RequireReceiptAuthority(db, authority.Review!));
        var credential = await Credential(input.Repository);
        var remote = await github.Observe(credential.Value, input.Repository, input.PullRequestNumber, CancellationToken.None);
        if (!remote.Complete || !GitHubMergeTaskAuthority.SameHead(remote.HeadSha, input.ExpectedHeadSha) ||
            !GitHubMergeTaskAuthority.SameHead(remote.BaseSha, input.BaseSha) || remote.BaseBranch != input.BaseBranch)
            throw new ControlException("Complete GitHub identity evidence does not match the requested head and target branch.");

        return await store.Write(async db =>
        {
            if (await db.GitHubMergeIntents.FindAsync(input.Id) is { } existing) return SameIntent(existing, input);
            var policy = await db.GitHubMergePolicies.AsNoTracking().Where(x => x.Id == input.PolicyId).OrderByDescending(x => x.Revision).FirstOrDefaultAsync();
            var review = await db.GitHubReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ReviewReceiptId);
            ValidateAuthority(input, policy, review);
            await RequireReceiptAuthority(db, review!);
            var intent = new GitHubMergeIntent
            {
                Id = input.Id,
                Repository = input.Repository,
                PullRequestNumber = input.PullRequestNumber,
                ExpectedHeadSha = input.ExpectedHeadSha,
                BaseBranch = input.BaseBranch,
                BaseSha = input.BaseSha,
                ReviewReceiptId = input.ReviewReceiptId,
                PolicyId = input.PolicyId,
                PolicyRevision = input.PolicyRevision,
                RequiredChecksJson = policy!.RequiredChecksJson,
                State = "Requested",
                Detail = "Merge intent recorded from versioned policy and retained review evidence; execution requires a fresh complete observation.",
                RequestedAt = ControlStore.Now
            };
            db.GitHubMergeIntents.Add(intent);
            ControlStore.Event(db, "GitHubMergeIntentRequested", payload: new
            {
                intent.Id,
                intent.Repository,
                intent.PullRequestNumber,
                intent.ExpectedHeadSha,
                intent.BaseBranch,
                intent.PolicyId,
                intent.PolicyRevision,
                intent.ReviewReceiptId
            });
            return intent;
        });
    }

    public async Task<GitHubCheckObservation> Observe(string id)
    {
        var intent = await GetIntent(id);
        var authority = await store.Read(async db =>
        {
            var receipt = await db.GitHubReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == intent.ReviewReceiptId);
            if (receipt is not null) await RequireReceiptAuthority(db, receipt);
            return new
            {
                Receipt = receipt,
                Policy = await db.GitHubMergePolicies.AsNoTracking().Where(x => x.Id == intent.PolicyId).OrderByDescending(x => x.Revision).FirstOrDefaultAsync()
            };
        });
        var receipt = authority.Receipt ?? throw new ControlException("The retained review receipt is unavailable.");
        var credential = await Credential(intent.Repository);
        var remote = await github.Observe(credential.Value, intent.Repository, intent.PullRequestNumber, CancellationToken.None);
        var state = Evaluate(intent, receipt, authority.Policy, remote, out var detail);
        return await PersistObservation(intent, remote, state, detail);
    }

    public async Task<GitHubMergeIntent> Merge(string id, ExecuteGitHubMergeInput input)
    {
        if (string.IsNullOrWhiteSpace(id) || id != input.Id)
            throw new ControlException("The execution request ID must match the stored merge intent ID.", 400);
        var intent = await GetIntent(id);
        if (intent.State == "Merged") return intent;
        if (await HasAttempt(id))
        {
            await Observe(id);
            return await GetIntent(id);
        }
        if (intent.Revision != input.ExpectedRevision)
            throw new ControlException("Merge intent changed; refresh before executing.");
        var leaseId = await AcquireLease(intent);
        if (leaseId is null)
            throw new ControlException("Another merge intent owns the durable repository target-branch lease.", 409);

        GitHubCheckObservation observation;
        try { observation = await Observe(id); }
        catch (Exception exception)
        {
            await HoldBeforeAttempt(id, leaseId, "GitHub prerequisites could not be completely observed; no merge was attempted. " + exception.Message);
            if (exception is ControlException) throw;
            throw new ControlException("GitHub prerequisites are unavailable; no merge was attempted.");
        }
        if (observation.State != "Ready")
        {
            await ReleaseLease(id, leaseId);
            throw new ControlException("GitHub merge gates are not satisfied: " + observation.Detail);
        }
        GitHubInstallationToken credential;
        try { credential = await Credential(intent.Repository); }
        catch
        {
            await HoldBeforeAttempt(id, leaseId, "GitHub merge credentials are unavailable; no merge was attempted.");
            throw;
        }
        bool started;
        try { started = await StartAttempt(intent, observation, leaseId); }
        catch (ControlException exception)
        {
            await HoldBeforeAttempt(id, leaseId, "GitHub merge authority changed before effect admission; no merge was attempted. " + exception.Message);
            throw;
        }
        if (!started)
        {
            await ReleaseUnusedLease(id, leaseId);
            await Observe(id);
            return await GetIntent(id);
        }

        string? sha;
        try { sha = await github.Merge(credential.Value, intent.Repository, intent.PullRequestNumber, intent.ExpectedHeadSha, CancellationToken.None); }
        catch (Exception exception)
        {
            await CompleteAttempt(intent, observation.Id, leaseId, "Unknown", "The merge response was lost; reconcile GitHub before any further execution. " + exception.Message, null, false);
            throw new ControlException("The GitHub merge outcome is unknown; no repeat PUT is permitted until reconciliation.");
        }
        if (sha is null)
            return await CompleteAttempt(intent, observation.Id, leaseId, "Rejected", "GitHub returned a definite response without confirming the merge.", null, true);
        return await CompleteAttempt(intent, observation.Id, leaseId, "Merged", "GitHub confirmed the expected-head merge.", sha, true);
    }

    private async Task<GitHubCheckObservation> PersistObservation(GitHubMergeIntent intent, GitHubPullRequestObservation remote,
        string state, string detail) => await store.Write(async db =>
    {
        var observation = new GitHubCheckObservation
        {
            Id = Guid.NewGuid().ToString("N"),
            IntentId = intent.Id,
            Repository = intent.Repository,
            PullRequestNumber = intent.PullRequestNumber,
            HeadSha = remote.HeadSha,
            BaseBranch = remote.BaseBranch,
            BaseSha = remote.BaseSha,
            PullRequestState = remote.State,
            Mergeable = remote.Mergeable == true,
            Merged = remote.Merged,
            MergeCommitSha = remote.MergeCommitSha,
            AuthorIdentity = remote.AuthorIdentity,
            ReviewsJson = Json.Write(remote.Reviews),
            UnresolvedReviewThreads = remote.UnresolvedThreads,
            ChecksJson = Json.Write(remote.Checks),
            StatusesJson = Json.Write(remote.Statuses),
            Complete = remote.Complete,
            State = state,
            Detail = detail,
            ObservedAt = ControlStore.Now
        };
        db.GitHubCheckObservations.Add(observation);
        var current = await db.GitHubMergeIntents.FindAsync(intent.Id) ?? throw new ControlException("Merge intent disappeared.");
        current.ObservationId = observation.Id;
        current.Revision++;
        var attempted = await db.GitHubMergeAttempts.AnyAsync(x => x.IntentId == intent.Id && x.State == "Attempted");
        var unknown = await db.GitHubMergeAttempts.AnyAsync(x => x.IntentId == intent.Id && x.State == "Unknown");
        var rejected = await db.GitHubMergeAttempts.AnyAsync(x => x.IntentId == intent.Id && x.State == "Rejected");
        if (state == "Merged")
        {
            if (!await db.GitHubMergeAttempts.AnyAsync(x => x.IntentId == intent.Id && x.State == "Merged"))
                db.GitHubMergeAttempts.Add(Receipt(intent, observation.Id, "Merged", detail, remote.MergeCommitSha));
            if (current.State != "Merged")
            {
                current.State = "Merged";
                current.Detail = detail;
                current.MergeCommitSha = remote.MergeCommitSha;
                current.ReceiptJson = Json.Write(new { intent.Repository, intent.PullRequestNumber, intent.ExpectedHeadSha, remote.MergeCommitSha, observation.ObservedAt });
            }
            db.GitHubMergeLeases.RemoveRange(db.GitHubMergeLeases.Where(x => x.IntentId == intent.Id));
        }
        else if (current.State == "Merged")
        {
            current.Detail = "A later observation did not alter the retained confirmed-merge receipt.";
        }
        else if (attempted)
        {
            current.State = unknown ? "Unknown" : rejected ? "Blocked" : "Attempted";
            current.Detail = unknown ? "A persisted attempt remains unconfirmed; no repeat PUT is permitted."
                : rejected ? "GitHub definitively rejected the persisted merge attempt; no repeat PUT is permitted for this request."
                : "A persisted attempt is awaiting a definitive GitHub observation; no repeat PUT is permitted.";
        }
        else if (current.State != "Unknown")
        {
            current.State = state == "Ready" ? "Observed" : "Blocked";
            current.Detail = detail;
        }
        ControlStore.Event(db, "GitHubMergeChecksObserved", payload: new
        {
            intentId = current.Id,
            observationId = observation.Id,
            observation.HeadSha,
            observation.State,
            observation.Complete
        });
        return observation;
    });

    private async Task<string?> AcquireLease(GitHubMergeIntent intent)
    {
        var leaseId = Guid.NewGuid().ToString("N");
        try
        {
            return await store.Write(async db =>
            {
                var lease = await db.GitHubMergeLeases.AsNoTracking().SingleOrDefaultAsync(x => x.Repository == intent.Repository && x.BaseBranch == intent.BaseBranch);
                if (lease is not null) return null;
                db.GitHubMergeLeases.Add(new GitHubMergeLease
                {
                    Id = leaseId,
                    Repository = intent.Repository,
                    BaseBranch = intent.BaseBranch,
                    IntentId = intent.Id,
                    AcquiredAt = ControlStore.Now
                });
                return leaseId;
            });
        }
        catch (DbUpdateException)
        {
            var lease = await store.Read(db => db.GitHubMergeLeases.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Repository == intent.Repository && x.BaseBranch == intent.BaseBranch));
            if (lease is null) throw;
            return null;
        }
    }

    private async Task<bool> StartAttempt(GitHubMergeIntent intent, GitHubCheckObservation observation, string leaseId)
    {
        try
        {
            return await store.Write(async db =>
            {
                if (await db.GitHubMergeAttempts.AnyAsync(x => x.IntentId == intent.Id)) return false;
                var current = await db.GitHubMergeIntents.FindAsync(intent.Id) ?? throw new ControlException("Merge intent disappeared.");
                var currentObservation = await db.GitHubCheckObservations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == observation.Id);
                var lease = await db.GitHubMergeLeases.SingleOrDefaultAsync(x => x.Id == leaseId && x.IntentId == intent.Id &&
                    x.Repository == intent.Repository && x.BaseBranch == intent.BaseBranch);
                var policy = await db.GitHubMergePolicies.AsNoTracking().Where(x => x.Id == current.PolicyId)
                    .OrderByDescending(x => x.Revision).FirstOrDefaultAsync();
                var review = await db.GitHubReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == current.ReviewReceiptId);
                if (lease is null) throw new ControlException("The exact merge execution lease is no longer owned by this request.");
                if (current.ObservationId != observation.Id || currentObservation is null || currentObservation.State != "Ready" ||
                    !currentObservation.Complete || currentObservation.IntentId != current.Id ||
                    !GitHubMergeTaskAuthority.SameRepository(currentObservation.Repository, current.Repository) ||
                    currentObservation.PullRequestNumber != current.PullRequestNumber ||
                    !GitHubMergeTaskAuthority.SameHead(currentObservation.HeadSha, current.ExpectedHeadSha) ||
                    currentObservation.BaseBranch != current.BaseBranch ||
                    !GitHubMergeTaskAuthority.SameHead(currentObservation.BaseSha, current.BaseSha))
                    throw new ControlException("The exact ready observation changed before merge effect admission.");
                ValidateAuthority(current, policy, review);
                await RequireReceiptAuthority(db, review!);
                db.GitHubMergeAttempts.Add(Receipt(intent, observation.Id, "Attempted",
                    "Durable receipt recorded before the GitHub merge request.", null, leaseId));
                current.State = "Attempted";
                current.Detail = "A merge request may have been sent; persisted reconciliation controls any retry.";
                current.Revision++;
                return true;
            });
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private Task<GitHubMergeIntent> CompleteAttempt(GitHubMergeIntent intent, string observationId, string leaseId,
        string state, string detail, string? mergeCommitSha, bool releaseLease) => store.Write(async db =>
    {
        if (!await db.GitHubMergeAttempts.AnyAsync(x => x.IntentId == intent.Id && x.State == state))
            db.GitHubMergeAttempts.Add(Receipt(intent, observationId, state, detail, mergeCommitSha));
        var current = await db.GitHubMergeIntents.FindAsync(intent.Id) ?? throw new ControlException("Merge intent disappeared.");
        if (current.State != "Merged")
        {
            current.State = state == "Rejected" ? "Blocked" : state;
            current.Detail = detail;
            current.Revision++;
            if (state == "Merged" && current.MergeCommitSha is null)
            {
                current.MergeCommitSha = mergeCommitSha;
                current.ReceiptJson = Json.Write(new { intent.Repository, intent.PullRequestNumber, intent.ExpectedHeadSha, mergeCommitSha, recordedAt = ControlStore.Now });
            }
        }
        if (releaseLease) db.GitHubMergeLeases.RemoveRange(db.GitHubMergeLeases.Where(x => x.Id == leaseId && x.IntentId == intent.Id));
        ControlStore.Event(db, "GitHubMergeReceiptRecorded", payload: new { current.Id, state, mergeCommitSha });
        return current;
    });

    private Task ReleaseLease(string intentId, string leaseId) => store.Write(async db =>
    {
        db.GitHubMergeLeases.RemoveRange(await db.GitHubMergeLeases.Where(x => x.Id == leaseId && x.IntentId == intentId).ToListAsync());
        return true;
    });

    private Task ReleaseUnusedLease(string intentId, string leaseId) => store.Write(async db =>
    {
        var lease = await db.GitHubMergeLeases.SingleOrDefaultAsync(x => x.Id == leaseId && x.IntentId == intentId);
        if (lease is not null && !await db.GitHubMergeAttempts.AnyAsync(x => x.Id == leaseId && x.IntentId == intentId))
            db.GitHubMergeLeases.Remove(lease);
        return true;
    });

    private Task HoldBeforeAttempt(string intentId, string leaseId, string detail) => store.Write(async db =>
    {
        var current = await db.GitHubMergeIntents.FindAsync(intentId) ?? throw new ControlException("Merge intent disappeared.");
        var lease = await db.GitHubMergeLeases.SingleOrDefaultAsync(x => x.Id == leaseId && x.IntentId == intentId);
        if (lease is null || await db.GitHubMergeAttempts.AnyAsync(x => x.IntentId == intentId)) return false;
        current.State = "Blocked";
        current.Detail = detail;
        current.Revision++;
        db.GitHubMergeLeases.Remove(lease);
        return true;
    });

    private Task<bool> HasAttempt(string intentId) => store.Read(db => db.GitHubMergeAttempts.AsNoTracking().AnyAsync(x => x.IntentId == intentId));

    private Task<GitHubMergeIntent> GetIntent(string id) => store.Read(async db =>
        await db.GitHubMergeIntents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id)
        ?? throw new ControlException("Merge intent not found.", 404));

    private async Task<GitHubInstallationToken> Credential(string repository)
    {
        var access = await store.Read(db => db.GitHubAccess.AsNoTracking().ToListAsync());
        var record = access.FirstOrDefault(x => x.State == "Ready" && Json.Read<string[]>(x.RepositoriesJson).Contains(repository, StringComparer.OrdinalIgnoreCase))
            ?? throw new ControlException("No ready GitHub installation covers this repository.");
        if (record.ExactCiInspectionState != "Ready") throw new ControlException("GitHub check-read permission is not verified.");
        return await github.Issue(record.AppId, record.InstallationId, secrets.Read(record.PrivateKeyReference), [repository], CancellationToken.None);
    }

    private static string Evaluate(GitHubMergeIntent intent, GitHubReviewReceipt receipt, GitHubMergePolicy? policy,
        GitHubPullRequestObservation remote, out string detail)
    {
        if (!GitHubMergeTaskAuthority.SameHead(remote.HeadSha, intent.ExpectedHeadSha)) { detail = "Pull request head changed since review."; return "Blocked"; }
        if (remote.BaseBranch != intent.BaseBranch) { detail = "Pull request target branch changed."; return "Blocked"; }
        if (remote.Merged && !string.IsNullOrWhiteSpace(remote.MergeCommitSha)) { detail = "GitHub confirms that the exact-head pull request is merged."; return "Merged"; }
        if (!GitHubMergeTaskAuthority.SameHead(remote.BaseSha, intent.BaseSha)) { detail = "Pull request target branch or base changed."; return "Blocked"; }
        if (policy is null || policy.State != "Active" || policy.Revision != intent.PolicyRevision)
        { detail = "The intent's trusted repository policy revision is no longer current and active."; return "Blocked"; }
        if (!remote.Complete) { detail = "GitHub observation is incomplete; review, thread, mergeability or check pagination could not be proven complete."; return "Blocked"; }
        if (remote.State != "open" || remote.Mergeable != true) { detail = "Pull request state is not currently mergeable."; return "Blocked"; }
        if (remote.UnresolvedThreads > 0) { detail = "Unresolved review threads remain, including outdated threads."; return "Blocked"; }
        var exactReview = remote.Reviews.SingleOrDefault(x => x.Id == receipt.GitHubReviewId);
        if (exactReview is null || exactReview.State != "APPROVED" || !GitHubMergeTaskAuthority.SameHead(exactReview.CommitId, intent.ExpectedHeadSha) ||
            !string.Equals(exactReview.User, receipt.GitHubReviewerIdentity, StringComparison.OrdinalIgnoreCase))
        { detail = "The retained central review receipt does not match an exact-head GitHub approval."; return "Blocked"; }
        var latest = LatestReviews(remote.Reviews);
        if (latest.Values.Any(x => x.State == "CHANGES_REQUESTED")) { detail = "A reviewer's latest applicable state requests changes."; return "Blocked"; }
        if (!latest.TryGetValue(receipt.GitHubReviewerIdentity, out var currentReview) || currentReview.State != "APPROVED" ||
            !GitHubMergeTaskAuthority.SameHead(currentReview.CommitId, intent.ExpectedHeadSha) || string.Equals(currentReview.User, remote.AuthorIdentity, StringComparison.OrdinalIgnoreCase))
        { detail = "Native GitHub approval by the retained independent reviewer is not current for the exact head."; return "Blocked"; }
        var checks = remote.Checks.Concat(remote.Statuses).ToArray();
        var missing = Json.Read<string[]>(intent.RequiredChecksJson).Where(name =>
        {
            var matches = checks.Where(x => x.Name == name).ToArray();
            return matches.Length != 1 || matches[0].Status != "completed" || !matches[0].Conclusion.Equals("success", StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        if (missing.Length > 0) { detail = "Policy-required checks are missing, ambiguous, pending or unsuccessful: " + string.Join(", ", missing); return "Blocked"; }
        detail = "Exact head/base, retained independent review, latest GitHub review state, resolved threads and policy-required checks verified.";
        return "Ready";
    }

    private static Dictionary<string, GitHubReview> LatestReviews(IEnumerable<GitHubReview> reviews) => reviews
        .Where(x => x.State is "APPROVED" or "CHANGES_REQUESTED" or "DISMISSED")
        .GroupBy(x => x.User, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.SubmittedAt, StringComparer.Ordinal).ThenByDescending(y => y.Id).First(), StringComparer.OrdinalIgnoreCase);

    private static GitHubMergeAttempt Receipt(GitHubMergeIntent intent, string observationId, string state, string detail,
        string? sha, string? id = null) => new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            IntentId = intent.Id,
            Repository = intent.Repository,
            BaseBranch = intent.BaseBranch,
            HeadSha = intent.ExpectedHeadSha,
            ObservationId = observationId,
            State = state,
            Detail = detail,
            StartedAt = ControlStore.Now,
            CompletedAt = state == "Attempted" ? null : ControlStore.Now,
            MergeCommitSha = sha,
            ReceiptJson = Json.Write(new { intent.Repository, intent.PullRequestNumber, intent.ExpectedHeadSha, state, detail, mergeCommitSha = sha, recordedAt = ControlStore.Now })
        };

    private static GitHubMergeOutcomeAuthority RequireAssignment(CommandRecord? command, AssignmentRecord? assignment,
        string workerId, string repository, int pullRequestNumber, string headSha, string role) =>
        GitHubMergeTaskAuthority.RequireCurrent(command, assignment, workerId,
            GitHubMergeTaskAuthority.Scope(role, repository, pullRequestNumber, headSha));

    private static async Task<bool> HasAuthorEvidence(ControlDb db, string workerId, string repository, int pullRequestNumber, string headSha)
    {
        var assignments = await db.Assignments.AsNoTracking().Where(x => x.WorkerId == workerId).ToListAsync();
        return assignments.Any(assignment => GitHubMergeTaskAuthority.HasRetainedAuthorProvenance(
            assignment, workerId, repository, pullRequestNumber));
    }

    private static async Task<GitHubReviewAuthoritySnapshot> RequireReceiptAuthority(ControlDb db, GitHubReviewReceipt receipt)
    {
        if (receipt.AuthorWorkerId == receipt.ReviewerWorkerId ||
            !await db.Workers.AsNoTracking().AnyAsync(x => x.Id == receipt.AuthorWorkerId) ||
            !await db.Workers.AsNoTracking().AnyAsync(x => x.Id == receipt.ReviewerWorkerId))
            throw new ControlException("The retained author and reviewer registrations are unavailable or no longer distinct.");
        GitHubReviewAuthoritySnapshot retained;
        try { retained = Json.Read<GitHubReviewAuthoritySnapshot>(receipt.EvidenceJson); }
        catch { throw new ControlException("The review receipt lacks typed retained task authority."); }
        if (retained.Version != GitHubMergeTaskKinds.Version)
            throw new ControlException("The review receipt uses an unsupported typed authority version.");
        var authorCommand = await db.Commands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == receipt.AuthorCommandId);
        var reviewCommand = await db.Commands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == receipt.ReviewCommandId);
        var authorAssignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == receipt.AuthorCommandId);
        var reviewAssignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == receipt.ReviewCommandId);
        var author = RequireAssignment(authorCommand, authorAssignment, receipt.AuthorWorkerId, receipt.Repository,
            receipt.PullRequestNumber, receipt.HeadSha, GitHubMergeTaskKinds.Author);
        var reviewer = RequireAssignment(reviewCommand, reviewAssignment, receipt.ReviewerWorkerId, receipt.Repository,
            receipt.PullRequestNumber, receipt.HeadSha, GitHubMergeTaskKinds.Reviewer);
        if (!SameAuthority(author, retained.Author) || !SameAuthority(reviewer, retained.Reviewer))
            throw new ControlException("The typed task authority retained by the review receipt has changed.");
        if (await HasAuthorEvidence(db, receipt.ReviewerWorkerId, receipt.Repository, receipt.PullRequestNumber, receipt.HeadSha))
            throw new ControlException("A worker with retained author-role evidence for this pull request cannot act as its reviewer.");
        return retained;
    }

    private static GitHubReviewReceipt SameReview(GitHubReviewReceipt prior, RecordGitHubReviewInput input)
    {
        if (!GitHubMergeTaskAuthority.SameRepository(prior.Repository, input.Repository) || prior.PullRequestNumber != input.PullRequestNumber ||
            !GitHubMergeTaskAuthority.SameHead(prior.HeadSha, input.HeadSha) ||
            prior.AuthorWorkerId != input.AuthorWorkerId || prior.ReviewerWorkerId != input.ReviewerWorkerId ||
            prior.AuthorCommandId != input.AuthorCommandId || prior.ReviewCommandId != input.ReviewCommandId ||
            prior.GitHubReviewId != input.GitHubReviewId || !string.Equals(prior.GitHubReviewerIdentity, input.GitHubReviewerIdentity, StringComparison.OrdinalIgnoreCase))
            throw new ControlException("Review receipt ID conflicts with the existing immutable evidence.");
        return prior;
    }

    private static GitHubMergeIntent SameIntent(GitHubMergeIntent prior, CreateGitHubMergeIntentInput input)
    {
        if (!GitHubMergeTaskAuthority.SameRepository(prior.Repository, input.Repository) || prior.PullRequestNumber != input.PullRequestNumber ||
            !GitHubMergeTaskAuthority.SameHead(prior.ExpectedHeadSha, input.ExpectedHeadSha) || prior.BaseBranch != input.BaseBranch ||
            !GitHubMergeTaskAuthority.SameHead(prior.BaseSha, input.BaseSha) || prior.ReviewReceiptId != input.ReviewReceiptId ||
            prior.PolicyId != input.PolicyId || prior.PolicyRevision != input.PolicyRevision)
            throw new ControlException("Merge intent ID conflicts with the existing immutable request.");
        return prior;
    }

    private static void ValidateAuthority(CreateGitHubMergeIntentInput input, GitHubMergePolicy? policy, GitHubReviewReceipt? review)
    {
        if (policy is null || policy.State != "Active" || policy.Revision != input.PolicyRevision ||
            !GitHubMergeTaskAuthority.SameRepository(policy.Repository, input.Repository) || policy.BaseBranch != input.BaseBranch)
            throw new ControlException("The requested trusted repository policy revision is unavailable.");
        if (review is null || !GitHubMergeTaskAuthority.SameRepository(review.Repository, input.Repository) ||
            review.PullRequestNumber != input.PullRequestNumber || !GitHubMergeTaskAuthority.SameHead(review.HeadSha, input.ExpectedHeadSha))
            throw new ControlException("The retained central review receipt does not cover the exact pull request head.");
    }

    private static void ValidateAuthority(GitHubMergeIntent intent, GitHubMergePolicy? policy, GitHubReviewReceipt? review)
    {
        if (policy is null || policy.State != "Active" || policy.Revision != intent.PolicyRevision ||
            !GitHubMergeTaskAuthority.SameRepository(policy.Repository, intent.Repository) || policy.BaseBranch != intent.BaseBranch ||
            policy.RequiredChecksJson != intent.RequiredChecksJson)
            throw new ControlException("The intent's trusted repository policy revision is no longer current and active.");
        if (review is null || !GitHubMergeTaskAuthority.SameRepository(review.Repository, intent.Repository) ||
            review.PullRequestNumber != intent.PullRequestNumber || !GitHubMergeTaskAuthority.SameHead(review.HeadSha, intent.ExpectedHeadSha) ||
            review.Id != intent.ReviewReceiptId)
            throw new ControlException("The retained central review receipt no longer covers the exact pull request head.");
    }

    private static bool SameAuthority(GitHubMergeOutcomeAuthority left, GitHubMergeOutcomeAuthority right) =>
        left.Version == right.Version && GitHubMergeTaskAuthority.SameScope(left.Scope, right.Scope) &&
        left.Verdict == right.Verdict && left.WorkerId == right.WorkerId && left.CommandId == right.CommandId &&
        left.CommandResultId == right.CommandResultId && left.CommandResultSha256 == right.CommandResultSha256 &&
        left.EvidenceSha256 == right.EvidenceSha256 && left.RecordedAt == right.RecordedAt;

    private static void ValidateReview(RecordGitHubReviewInput input)
    {
        ValidateRepository(input.Repository);
        if (new[] { input.Id, input.AuthorWorkerId, input.ReviewerWorkerId, input.AuthorCommandId, input.ReviewCommandId, input.GitHubReviewerIdentity }.Any(string.IsNullOrWhiteSpace) ||
            input.PullRequestNumber <= 0 || input.GitHubReviewId <= 0 || !ValidSha(input.HeadSha))
            throw new ControlException("Invalid GitHub review receipt.", 400);
    }

    private static void ValidateIntent(CreateGitHubMergeIntentInput input)
    {
        ValidateRepository(input.Repository);
        if (new[] { input.Id, input.BaseBranch, input.ReviewReceiptId, input.PolicyId }.Any(string.IsNullOrWhiteSpace) ||
            input.PullRequestNumber <= 0 || input.PolicyRevision <= 0 || !ValidSha(input.ExpectedHeadSha) || !ValidSha(input.BaseSha))
            throw new ControlException("Invalid merge intent.", 400);
    }

    private static bool ValidSha(string value) => Regex.IsMatch(value ?? "", "^[0-9a-f]{40}$", RegexOptions.IgnoreCase);
    private static void ValidateRepository(string repository) => GitHubAppClient.ValidateRepositories([repository]);
}
