using System.Net;
using System.Security.Cryptography;
using HVO.AgentControl.Core;
using HVO.AgentControl.GitHub;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class GitHubMergeTests
{
    [Fact]
    public async Task LaterChangesRequestedOnPaginatedReviewsBlocksTheOlderApproval()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.ReviewPages =
        [
            [Remote.Review(1, "reviewer-gh", "APPROVED", "2026-09-08T10:00:00Z")],
            [Remote.Review(2, "reviewer-gh", "CHANGES_REQUESTED", "2026-09-08T11:00:00Z")]
        ];

        var observation = await fixture.Service.Observe("intent");

        Assert.Equal("Blocked", observation.State);
        Assert.Contains("latest applicable state requests changes", observation.Detail);
        Assert.Equal(2, fixture.Remote.ReviewCalls);
    }

    [Fact]
    public async Task UnresolvedOutdatedThreadStillBlocksExecution()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.ThreadPages = [[], [(false, true)]];

        var observation = await fixture.Service.Observe("intent");

        Assert.Equal("Blocked", observation.State);
        Assert.Equal(1, observation.UnresolvedReviewThreads);
        Assert.Equal(2, fixture.Remote.GraphqlCalls);
    }

    [Fact]
    public async Task LaterCommentDoesNotEraseApplicableApproval()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.ReviewPages =
        [[
            Remote.Review(1, "reviewer-gh", "APPROVED", "2026-09-08T10:00:00Z"),
            Remote.Review(2, "reviewer-gh", "COMMENTED", "2026-09-08T11:00:00Z")
        ]];

        var observation = await fixture.Service.Observe("intent");

        Assert.Equal("Ready", observation.State);
    }

    [Fact]
    public async Task IncompleteReviewPaginationFailsClosed()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.NeverEndingReviews = true;

        var observation = await fixture.Service.Observe("intent");

        Assert.False(observation.Complete);
        Assert.Equal("Blocked", observation.State);
        Assert.Equal(20, fixture.Remote.ReviewCalls);
    }

    [Fact]
    public async Task IncompleteThreadPaginationFailsClosed()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.NeverEndingThreads = true;

        var observation = await fixture.Service.Observe("intent");

        Assert.False(observation.Complete);
        Assert.Equal("Blocked", observation.State);
        Assert.Equal(20, fixture.Remote.GraphqlCalls);
    }

    [Fact]
    public async Task AmbiguousSameNameCheckAndStatusFailsClosed()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.StatusPages = [[new { context = "build", state = "failure" }]];

        var observation = await fixture.Service.Observe("intent");

        Assert.Equal("Blocked", observation.State);
        Assert.Contains("ambiguous", observation.Detail);
    }

    [Fact]
    public async Task CheckRunAndStatusPaginationMustBothBeComplete()
    {
        await using var fixture = await Fixture.Create();
        await fixture.App.Store.Write(async db =>
        {
            var intent = await db.GitHubMergeIntents.SingleAsync(x => x.Id == "intent");
            intent.RequiredChecksJson = Json.Write(new[] { "build", "deploy" });
            return true;
        });
        fixture.Remote.CheckPages =
        [
            [new { name = "lint", status = "completed", conclusion = "success" }],
            [new { name = "build", status = "completed", conclusion = "success" }]
        ];
        fixture.Remote.StatusPages =
        [
            [new { context = "legacy", state = "success" }],
            [new { context = "deploy", state = "success" }]
        ];

        var observation = await fixture.Service.Observe("intent");

        Assert.Equal("Ready", observation.State);
        Assert.Equal(2, fixture.Remote.CheckCalls);
        Assert.Equal(2, fixture.Remote.StatusCalls);
    }

    [Fact]
    public async Task IntentSnapshotsChecksOnlyFromTrustedVersionedPolicy()
    {
        await using var fixture = await Fixture.Create(seedIntent: false);
        await fixture.App.Store.Write(async db =>
        {
            var policy = await db.GitHubMergePolicies.SingleAsync(x => x.Id == "policy" && x.Revision == 1);
            policy.RequiredChecksJson = Json.Write(new[] { "trusted-policy-check" });
            return true;
        });

        var intent = await fixture.Service.CreateIntent(Fixture.IntentInput("intent"));
        var observation = await fixture.Service.Observe(intent.Id);

        Assert.Equal(new[] { "trusted-policy-check" }, Json.Read<string[]>(intent.RequiredChecksJson));
        Assert.Equal("Blocked", observation.State);
        Assert.Contains("trusted-policy-check", observation.Detail);
    }

    [Fact]
    public async Task PolicyUpdatesAppendRevisionAndInvalidateOlderIntent()
    {
        await using var fixture = await Fixture.Create();

        var updated = await fixture.Service.ConfigurePolicy(new("policy", Fixture.Repository, "main", ["release"], 1));
        var replay = await fixture.Service.ConfigurePolicy(new("policy", Fixture.Repository, "main", ["release"], 1));
        var policies = await fixture.App.Store.Read(db => db.GitHubMergePolicies.AsNoTracking()
            .Where(x => x.Id == "policy").OrderBy(x => x.Revision).ToArrayAsync());
        var observation = await fixture.Service.Observe("intent");

        Assert.Equal(2, updated.Revision);
        Assert.Equal(updated.Revision, replay.Revision);
        Assert.Equal(new long[] { 1, 2 }, policies.Select(x => x.Revision));
        Assert.Equal(new[] { "build" }, Json.Read<string[]>(policies[0].RequiredChecksJson));
        Assert.Equal("Blocked", observation.State);
        Assert.Contains("no longer current", observation.Detail);
    }

    [Fact]
    public async Task ReviewReceiptRequiresVerifiedRetainedReviewerAssignment()
    {
        await using var fixture = await Fixture.Create(seedIntent: false);
        await fixture.App.Store.Write(async db =>
        {
            var assignment = await db.Assignments.FindAsync("review-command") ?? throw new InvalidOperationException();
            assignment.Outcome = "ReportedComplete";
            return true;
        });

        var error = await Assert.ThrowsAsync<ControlException>(() => fixture.Service.RecordReview(new(
            "new-receipt", Fixture.Repository, 12, Fixture.Head, "author", "reviewer", "author-command", "review-command", 1, "reviewer-gh")));

        Assert.Contains("typed command and assignment authority", error.Message);
    }

    [Fact]
    public async Task WorkerOutcomeCannotSupplyMissingExactRepositoryBinding()
    {
        await using var fixture = await Fixture.Create(seedIntent: false);
        await fixture.App.Store.Write(async db =>
        {
            var command = await db.Commands.FindAsync("review-command") ?? throw new InvalidOperationException();
            var assignment = await db.Assignments.FindAsync("review-command") ?? throw new InvalidOperationException();
            command.Payload = Json.Write(Json.Read<PromptInput>(command.Payload) with
            {
                GitHubMergeScope = GitHubMergeTaskAuthority.Scope(GitHubMergeTaskKinds.Reviewer,
                    "OtherOwner/Repository", 12, Fixture.Head)
            });
            assignment.Evidence = $"Reviewed {Fixture.Repository} pull request #12 at head {Fixture.Head}.";
            return true;
        });

        var error = await Assert.ThrowsAsync<ControlException>(() => fixture.Service.RecordReview(new(
            "new-receipt", Fixture.Repository, 12, Fixture.Head, "author", "reviewer", "author-command", "review-command", 1, "reviewer-gh")));

        Assert.Contains("changed or does not match", error.Message);
    }

    [Fact]
    public async Task CallerCannotSwapCentrallyAssignedAuthorAndReviewerRoles()
    {
        await using var fixture = await Fixture.Create(seedIntent: false);

        var error = await Assert.ThrowsAsync<ControlException>(() => fixture.Service.RecordReview(new(
            "new-receipt", Fixture.Repository, 12, Fixture.Head, "reviewer", "author", "review-command", "author-command", 1, "reviewer-gh")));

        Assert.Contains("does not match", error.Message);
    }

    [Fact]
    public async Task ContributorWithAuthorRoleCannotAlsoSupplyReviewReceipt()
    {
        await using var fixture = await Fixture.Create(seedIntent: false);
        await fixture.App.Store.Write(async db =>
        {
            var assignment = await db.Assignments.SingleAsync(x => x.Id == "review-command");
            assignment.GitHubAuthorProvenanceJson = Json.Write(Fixture.Authority(
                "review-command", "reviewer", GitHubMergeTaskKinds.Author));
            return true;
        });

        var error = await Assert.ThrowsAsync<ControlException>(() => fixture.Service.RecordReview(new(
            "new-receipt", Fixture.Repository, 12, Fixture.Head, "author", "reviewer", "author-command", "review-command", 1, "reviewer-gh")));

        Assert.Contains("author-role evidence", error.Message);
    }

    [Fact]
    public async Task ContributorToEarlierHeadCannotReviewCorrectedHeadOfSamePullRequest()
    {
        await using var fixture = await Fixture.Create(seedIntent: false);
        var earlierHead = new string('d', 40);
        var admission = new GitHubMergeTaskScope(GitHubMergeTaskKinds.Version, GitHubMergeTaskKinds.Purpose,
            GitHubMergeTaskKinds.Author, Fixture.Repository, 12, "");
        var exact = admission with { HeadSha = earlierHead };
        var command = new CommandRecord
        {
            Id = "reviewer-earlier-contribution",
            RuntimeId = (await fixture.App.Store.Detail("reviewer")).Worker.RuntimeId,
            WorkerId = "reviewer",
            Kind = "Prompt",
            State = Delivery.Finished,
            Payload = Json.Write(new PromptInput("reviewer-earlier-contribution", "Implement the correction.", 0,
                GitHubMergeScope: admission)),
            NativeMessageId = "msg_contribution",
            ResultJson = Json.Write(new { publishedHead = earlierHead })
        };
        await fixture.App.Store.Write(db =>
        {
            db.Commands.Add(command);
            db.Assignments.Add(new AssignmentRecord
            {
                Id = command.Id,
                WorkerId = "reviewer",
                Prompt = "Implement the correction."
            });
            return Task.FromResult(true);
        });
        await fixture.App.Store.SetOutcome("reviewer", new(command.Id, 0, "VerifiedComplete",
            "Earlier contribution was published.", new(GitHubMergeTaskKinds.Version,
                GitHubMergeTaskKinds.PublishedExactHead, exact)));

        var error = await Assert.ThrowsAsync<ControlException>(() => fixture.Service.RecordReview(new(
            "new-receipt", Fixture.Repository, 12, Fixture.Head, "author", "reviewer",
            "author-command", "review-command", 1, "reviewer-gh")));

        Assert.Contains("author-role evidence", error.Message);
    }

    [Fact]
    public async Task ReviewEvidenceIsRevalidatedAfterGitHubObservation()
    {
        await using var fixture = await Fixture.Create(seedIntent: false);
        await fixture.App.Store.Write(async db =>
        {
            db.GitHubReviewReceipts.Remove(await db.GitHubReviewReceipts.SingleAsync(x => x.Id == "receipt"));
            return true;
        });
        fixture.Remote.BeforeReviewResponse = () => fixture.App.Store.SetOutcome("reviewer",
            new("review-command", 0, "Failed", "The owner withdrew the verified review result."));

        await Assert.ThrowsAsync<ControlException>(() => fixture.Service.RecordReview(new(
            "recorded-review", Fixture.Repository, 12, Fixture.Head, "author", "reviewer", "author-command", "review-command", 1, "reviewer-gh")));

        Assert.False(await fixture.App.Store.Read(db => db.GitHubReviewReceipts.AnyAsync()));
    }

    [Fact]
    public async Task ValidCentralAndNativeReviewEvidenceCreatesImmutableReceipt()
    {
        await using var fixture = await Fixture.Create(seedIntent: false);
        await fixture.App.Store.Write(async db =>
        {
            db.GitHubReviewReceipts.Remove(await db.GitHubReviewReceipts.SingleAsync(x => x.Id == "receipt"));
            return true;
        });

        var receipt = await fixture.Service.RecordReview(new(
            "recorded-review", Fixture.Repository, 12, Fixture.Head, "author", "reviewer", "author-command", "review-command", 1, "reviewer-gh"));

        Assert.Equal("author", receipt.AuthorWorkerId);
        Assert.Equal("reviewer", receipt.ReviewerWorkerId);
        Assert.Contains("review-command", receipt.EvidenceJson);
        Assert.Equal(1, await fixture.App.Store.Read(db => db.GitHubReviewReceipts.CountAsync()));
    }

    [Fact]
    public async Task LiveTargetBranchAdvancementInvalidatesIntentBase()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.TargetSha = new string('d', 40);

        var observation = await fixture.Service.Observe("intent");

        Assert.Equal("Blocked", observation.State);
        Assert.Contains("target branch or base changed", observation.Detail);
    }

    [Fact]
    public async Task ExecutionRequestIdMismatchCannotReachGitHubPut()
    {
        await using var fixture = await Fixture.Create();

        await Assert.ThrowsAsync<ControlException>(() => fixture.Service.Merge("intent", new("different-intent", 0)));

        Assert.Equal(0, fixture.Remote.MergeCalls);
    }

    [Fact]
    public async Task UnavailablePrerequisiteExplicitlyHoldsWithoutAttemptOrLease()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.FailReviews = true;

        await Assert.ThrowsAsync<ControlException>(() => fixture.Service.Merge("intent", new("intent", 0)));

        var intent = await fixture.App.Store.Read(db => db.GitHubMergeIntents.AsNoTracking().SingleAsync(x => x.Id == "intent"));
        Assert.Equal("Blocked", intent.State);
        Assert.Contains("prerequisites", intent.Detail);
        Assert.False(await fixture.App.Store.Read(db => db.GitHubMergeAttempts.AnyAsync()));
        Assert.False(await fixture.App.Store.Read(db => db.GitHubMergeLeases.AnyAsync()));
    }

    [Fact]
    public async Task GraphQlPartialDataWithErrorsExplicitlyHoldsExecution()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.GraphqlErrors = true;

        await Assert.ThrowsAsync<ControlException>(() => fixture.Service.Merge("intent", new("intent", 0)));

        var intent = await fixture.App.Store.Read(db => db.GitHubMergeIntents.AsNoTracking().SingleAsync(x => x.Id == "intent"));
        Assert.Equal("Blocked", intent.State);
        Assert.Equal(0, fixture.Remote.MergeCalls);
        Assert.False(await fixture.App.Store.Read(db => db.GitHubMergeLeases.AnyAsync()));
    }

    [Fact]
    public async Task LostSuccessReconcilesAfterStoreRestartWithoutAnotherPut()
    {
        var fixture = await Fixture.Create();
        var dataPath = fixture.App.DataPath;
        var secretPath = fixture.App.SecretPath;
        fixture.Remote.LoseMergeResponse = true;

        await Assert.ThrowsAsync<ControlException>(() => fixture.Service.Merge("intent", new("intent", 0)));
        Assert.Equal(1, fixture.Remote.MergeCalls);
        Assert.Equal(new[] { "Attempted", "Unknown" }, await AttemptStates(fixture.App.Store));
        await fixture.DisposeAsync();

        await using var restarted = new TestApp(dataPath, secretPath);
        var remote = new Remote { Merged = true, TargetSha = new string('c', 40) };
        var service = Fixture.ServiceFor(restarted, remote);
        await restarted.Store.Write(db =>
        {
            db.GitHubReviewReceipts.Add(Fixture.Receipt("receipt-2", 13));
            db.GitHubMergeIntents.Add(Fixture.Intent("intent-2", "receipt-2", 13));
            return Task.FromResult(true);
        });
        await Assert.ThrowsAsync<ControlException>(() => service.Merge("intent-2", new("intent-2", 0)));
        var reconciled = await service.Merge("intent", new("intent", 0));

        Assert.Equal("Merged", reconciled.State);
        Assert.Equal(0, remote.MergeCalls);
        Assert.Equal(new[] { "Attempted", "Merged", "Unknown" }, await AttemptStates(restarted.Store));
        Assert.False(await restarted.Store.Read(db => db.GitHubMergeLeases.AnyAsync()));
    }

    [Fact]
    public async Task DefiniteGitHubRejectionReleasesLeaseWithoutRetryingRequest()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.MergeStatus = HttpStatusCode.UnprocessableEntity;

        var rejected = await fixture.Service.Merge("intent", new("intent", 0));
        var replay = await fixture.Service.Merge("intent", new("intent", 0));

        Assert.Equal("Blocked", rejected.State);
        Assert.Equal("Blocked", replay.State);
        Assert.Equal(1, fixture.Remote.MergeCalls);
        Assert.Equal(new[] { "Attempted", "Rejected" }, await AttemptStates(fixture.App.Store));
        Assert.False(await fixture.App.Store.Read(db => db.GitHubMergeLeases.AnyAsync()));
    }

    [Fact]
    public async Task LaterMergedObservationsDoNotRewriteFirstConfirmedReceipt()
    {
        await using var fixture = await Fixture.Create();
        var merged = await fixture.Service.Merge("intent", new("intent", 0));
        fixture.Remote.MergeSha = new string('d', 40);
        fixture.Remote.TargetSha = fixture.Remote.MergeSha;

        await fixture.Service.Observe("intent");
        var retained = await fixture.App.Store.Read(db => db.GitHubMergeIntents.AsNoTracking().SingleAsync(x => x.Id == "intent"));

        Assert.Equal(new string('c', 40), merged.MergeCommitSha);
        Assert.Equal(merged.MergeCommitSha, retained.MergeCommitSha);
        Assert.Equal(merged.ReceiptJson, retained.ReceiptJson);
        Assert.Equal(1, await fixture.App.Store.Read(db => db.GitHubMergeAttempts.CountAsync(x => x.IntentId == "intent" && x.State == "Merged")));
    }

    [Fact]
    public async Task SeparateServiceInstancesSerializeByRepositoryTargetBranch()
    {
        await using var fixture = await Fixture.Create();
        await fixture.App.Store.Write(db =>
        {
            db.GitHubReviewReceipts.Add(Fixture.Receipt("receipt-2", 13));
            db.GitHubMergeIntents.Add(Fixture.Intent("intent-2", "receipt-2", 13));
            return Task.FromResult(true);
        });
        fixture.Remote.HoldMerge = true;
        var factory = fixture.App.Services.GetRequiredService<IDbContextFactory<ControlDb>>();
        var options = fixture.App.Services.GetRequiredService<IOptions<ControlOptions>>();
        var secrets = fixture.App.Services.GetRequiredService<Secrets>();
        var secondStore = new ControlStore(factory, options, secrets);
        var secondService = new GitHubMergeService(secondStore, secrets, fixture.Client);

        var first = fixture.Service.Merge("intent", new("intent", 0));
        await fixture.Remote.MergeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var conflict = await Assert.ThrowsAsync<ControlException>(() => secondService.Merge("intent-2", new("intent-2", 0)));
        fixture.Remote.ReleaseMerge.TrySetResult();
        await first;

        Assert.Contains("target-branch lease", conflict.Message);
        Assert.Equal(1, fixture.Remote.MergeCalls);
    }

    [Fact]
    public async Task ConcurrentSameIntentReplayDoesNotCreateFalseUnknownOrSecondPut()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.HoldMerge = true;
        var secondService = SecondService(fixture);

        var first = fixture.Service.Merge("intent", new("intent", 0));
        await fixture.Remote.MergeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replay = await secondService.Merge("intent", new("intent", 0));

        Assert.Equal("Attempted", replay.State);
        Assert.Equal(new[] { "Attempted" }, await AttemptStates(fixture.App.Store));
        fixture.Remote.ReleaseMerge.TrySetResult();
        await first;
        Assert.Equal(1, fixture.Remote.MergeCalls);
    }

    [Fact]
    public async Task PolicyChangedAfterObservationFencesFinalEffectAdmission()
    {
        await using var fixture = await Fixture.Create();
        var tokenRequests = 0;
        fixture.Remote.Intercept = async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal) &&
                Interlocked.Increment(ref tokenRequests) == 2)
                await fixture.Service.ConfigurePolicy(new("policy", Fixture.Repository, "main", ["build"], 1, false));
            return null;
        };

        await Assert.ThrowsAsync<ControlException>(() => fixture.Service.Merge("intent", new("intent", 0)));

        Assert.Equal(0, fixture.Remote.MergeCalls);
        Assert.False(await fixture.App.Store.Read(db => db.GitHubMergeAttempts.AnyAsync()));
        Assert.False(await fixture.App.Store.Read(db => db.GitHubMergeLeases.AnyAsync()));
    }

    [Fact]
    public async Task LatePreflightFailureCannotRemoveReplacementExecutionLease()
    {
        await using var fixture = await Fixture.Create();
        var reviewEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Remote.Intercept = async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/reviews", StringComparison.Ordinal))
            {
                reviewEntered.TrySetResult();
                await releaseReview.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return null;
        };
        var delayed = fixture.Service.Merge("intent", new("intent", 0));
        await reviewEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string replacementId = Guid.NewGuid().ToString("N");
        await fixture.App.Store.Write(async db =>
        {
            var original = await db.GitHubMergeLeases.SingleAsync();
            db.GitHubMergeLeases.Remove(original);
            await db.SaveChangesAsync();
            db.GitHubMergeLeases.Add(new GitHubMergeLease
            {
                Id = replacementId,
                Repository = original.Repository,
                BaseBranch = original.BaseBranch,
                IntentId = original.IntentId,
                AcquiredAt = ControlStore.Now
            });
            return true;
        });
        releaseReview.TrySetResult();

        await Assert.ThrowsAsync<ControlException>(() => delayed);

        var retained = await fixture.App.Store.Read(db => db.GitHubMergeLeases.AsNoTracking().SingleAsync());
        Assert.Equal(replacementId, retained.Id);
        Assert.Equal("Requested", (await fixture.Service.ListIntents()).Single().State);
    }

    [Fact]
    public async Task LateLostResponseCannotOverwriteConfirmedMergedProjection()
    {
        await using var fixture = await Fixture.Create();
        fixture.Remote.HoldMerge = true;
        fixture.Remote.LoseMergeResponse = true;
        var merge = fixture.Service.Merge("intent", new("intent", 0));
        await fixture.Remote.MergeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Remote.Merged = true;
        fixture.Remote.TargetSha = fixture.Remote.MergeSha;
        var observed = await fixture.Service.Observe("intent");
        fixture.Remote.ReleaseMerge.TrySetResult();

        await Assert.ThrowsAsync<ControlException>(() => merge);
        var retained = (await fixture.Service.ListIntents()).Single();

        Assert.Equal("Merged", observed.State);
        Assert.Equal("Merged", retained.State);
        Assert.Equal(fixture.Remote.MergeSha, retained.MergeCommitSha);
        Assert.Equal(new[] { "Attempted", "Merged", "Unknown" }, await AttemptStates(fixture.App.Store));
    }

    [Fact]
    public async Task LiteralPromptRoleAndTargetTextCannotMintTypedReviewAuthority()
    {
        await using var fixture = await Fixture.Create(seedIntent: false);
        await fixture.App.Store.Write(async db =>
        {
            db.GitHubReviewReceipts.RemoveRange(db.GitHubReviewReceipts);
            var command = await db.Commands.SingleAsync(x => x.Id == "review-command");
            var assignment = await db.Assignments.SingleAsync(x => x.Id == "review-command");
            var text = $"Merge role: reviewer\nReport the time. Background: {Fixture.Repository} #12 {Fixture.Head}. Do not review repository content.";
            command.Payload = text;
            assignment.Prompt = text;
            assignment.Evidence = "Verified clock report; no repository inspection performed.";
            assignment.GitHubAuthorityJson = "{}";
            return true;
        });

        await Assert.ThrowsAsync<ControlException>(() => fixture.Service.RecordReview(new(
            "text-only-review", Fixture.Repository, 12, Fixture.Head, "author", "reviewer",
            "author-command", "review-command", 1, "reviewer-gh")));

        Assert.False(await fixture.App.Store.Read(db => db.GitHubReviewReceipts.AnyAsync()));
    }

    private static GitHubMergeService SecondService(Fixture fixture)
    {
        var factory = fixture.App.Services.GetRequiredService<IDbContextFactory<ControlDb>>();
        var options = fixture.App.Services.GetRequiredService<IOptions<ControlOptions>>();
        var secrets = fixture.App.Services.GetRequiredService<Secrets>();
        return new GitHubMergeService(new ControlStore(factory, options, secrets), secrets, fixture.Client);
    }

    private static Task<string[]> AttemptStates(ControlStore store) => store.Read(async db =>
        await db.GitHubMergeAttempts.OrderBy(x => x.State).Select(x => x.State).ToArrayAsync());

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Repository = "Owner/Repo";
        public static readonly string Head = new('a', 40);
        public static readonly string Base = new('b', 40);
        public TestApp App { get; }
        public Remote Remote { get; }
        public GitHubAppClient Client { get; }
        public GitHubMergeService Service { get; }

        private Fixture(TestApp app, Remote remote)
        {
            App = app;
            Remote = remote;
            Client = new GitHubAppClient(new HttpClient(remote), TimeProvider.System);
            Service = new GitHubMergeService(app.Store, app.Services.GetRequiredService<Secrets>(), Client);
        }

        public static async Task<Fixture> Create(bool seedIntent = true)
        {
            var app = new TestApp();
            var remote = new Remote();
            var fixture = new Fixture(app, remote);
            using var rsa = RSA.Create(2048);
            var secrets = app.Services.GetRequiredService<Secrets>();
            var key = secrets.StoreEncrypted(rsa.ExportRSAPrivateKeyPem());
            var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile());
            await app.Store.Write(db =>
            {
                db.GitHubAccess.Add(new GitHubAccess
                {
                    Id = runtime.Id,
                    AppId = 123,
                    InstallationId = 456,
                    PrivateKeyReference = key,
                    RepositoriesJson = Json.Write(new[] { Repository }),
                    State = "Ready",
                    CredentialState = GitHubCredentialState.Delivered,
                    CredentialConfigurationFingerprint = new string('b', 64),
                    ExpiresAt = ControlStore.Now + 3600000,
                    ChecksPermission = GitHubPermissionState.Granted,
                    CommitStatusesPermission = GitHubPermissionState.Granted,
                    ActionsPermission = GitHubPermissionState.Granted,
                    EnvironmentPolicyVersion = GitHubProcessEnvironment.CurrentPolicyVersion,
                    EnvironmentPolicyFingerprint = new string('c', 64),
                    EnvironmentProcessId = 42,
                    EnvironmentProcessIncarnation = "boot:42",
                    EnvironmentVerifiedAt = ControlStore.Now
                });
                db.Workers.AddRange(
                    new WorkerRecord { Id = "author", RuntimeId = runtime.Id, ManagedServerId = "server-a", NativeSessionId = "session-a", Directory = "/work/a" },
                    new WorkerRecord { Id = "reviewer", RuntimeId = runtime.Id, ManagedServerId = "server-b", NativeSessionId = "session-b", Directory = "/work/b" });
                var authorCommand = Command("author-command", "author", runtime.Id, GitHubMergeTaskKinds.Author);
                var reviewCommand = Command("review-command", "reviewer", runtime.Id, GitHubMergeTaskKinds.Reviewer);
                var authorAssignment = Assignment(authorCommand, "author", GitHubMergeTaskKinds.Author);
                var reviewAssignment = Assignment(reviewCommand, "reviewer", GitHubMergeTaskKinds.Reviewer);
                db.Commands.AddRange(authorCommand, reviewCommand);
                db.Assignments.AddRange(authorAssignment, reviewAssignment);
                db.GitHubMergePolicies.Add(new GitHubMergePolicy
                {
                    Id = "policy",
                    Repository = Repository,
                    BaseBranch = "main",
                    RequiredChecksJson = Json.Write(new[] { "build" }),
                    State = "Active",
                    Revision = 1
                });
                db.GitHubReviewReceipts.Add(Receipt("receipt", 12));
                if (seedIntent) db.GitHubMergeIntents.Add(Intent("intent", "receipt", 12));
                return Task.FromResult(true);
            });
            return fixture;
        }

        public static GitHubMergeService ServiceFor(TestApp app, Remote remote)
        {
            var client = new GitHubAppClient(new HttpClient(remote), TimeProvider.System);
            return new GitHubMergeService(app.Store, app.Services.GetRequiredService<Secrets>(), client);
        }

        public static CreateGitHubMergeIntentInput IntentInput(string id) =>
            new(id, Repository, 12, Head, "main", Base, "receipt", "policy", 1);

        public static GitHubReviewReceipt Receipt(string id, int number) => new()
        {
            Id = id,
            Repository = Repository,
            PullRequestNumber = number,
            HeadSha = Head,
            AuthorWorkerId = "author",
            ReviewerWorkerId = "reviewer",
            AuthorCommandId = "author-command",
            ReviewCommandId = "review-command",
            GitHubReviewId = 1,
            GitHubReviewerIdentity = "reviewer-gh",
            EvidenceJson = Json.Write(new GitHubReviewAuthoritySnapshot(GitHubMergeTaskKinds.Version,
                Authority("author-command", "author", GitHubMergeTaskKinds.Author),
                Authority("review-command", "reviewer", GitHubMergeTaskKinds.Reviewer)))
        };

        public static GitHubMergeIntent Intent(string id, string receiptId, int number) => new()
        {
            Id = id,
            Repository = Repository,
            PullRequestNumber = number,
            ExpectedHeadSha = Head,
            BaseBranch = "main",
            BaseSha = Base,
            ReviewReceiptId = receiptId,
            PolicyId = "policy",
            PolicyRevision = 1,
            RequiredChecksJson = Json.Write(new[] { "build" }),
            State = "Requested"
        };

        private static CommandRecord Command(string id, string workerId, string runtimeId, string role) => new()
        {
            Id = id,
            RuntimeId = runtimeId,
            WorkerId = workerId,
            Kind = "Prompt",
            State = Delivery.Finished,
            Payload = Json.Write(new PromptInput(id, Evidence(role), 0, GitHubMergeScope: PromptScope(role))),
            ResultId = id + "-result",
            ResultJson = Result(id)
        };

        private static AssignmentRecord Assignment(CommandRecord command, string workerId, string role) => new()
        {
            Id = command.Id,
            WorkerId = workerId,
            Prompt = Evidence(role),
            Evidence = Evidence(role),
            Outcome = "VerifiedComplete",
            GitHubAuthorityJson = Json.Write(Authority(command.Id, workerId, role)),
            GitHubAuthorProvenanceJson = role == GitHubMergeTaskKinds.Author
                ? Json.Write(Authority(command.Id, workerId, role)) : "{}"
        };

        public static GitHubMergeOutcomeAuthority Authority(string commandId, string workerId, string role) => new(
            GitHubMergeTaskKinds.Version, Scope(role), role == GitHubMergeTaskKinds.Author
                ? GitHubMergeTaskKinds.PublishedExactHead : GitHubMergeTaskKinds.ApprovedExactHead,
            workerId, commandId, commandId + "-result", GitHubMergeTaskAuthority.Hash(Result(commandId)),
            GitHubMergeTaskAuthority.Hash(Evidence(role)), 1);

        private static GitHubMergeTaskScope Scope(string role) => GitHubMergeTaskAuthority.Scope(role, Repository, 12, Head);
        private static GitHubMergeTaskScope PromptScope(string role) => role == GitHubMergeTaskKinds.Author
            ? new(GitHubMergeTaskKinds.Version, GitHubMergeTaskKinds.Purpose, role, Repository, 0, "")
            : Scope(role);
        private static string Result(string commandId) => Json.Write(new { commandId, completed = true });
        private static string Evidence(string role) => $"Typed {role} task completed for the exact head.";
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private sealed class Remote : HttpMessageHandler
    {
        public List<object[]> ReviewPages { get; set; } = [[Review(1, "reviewer-gh", "APPROVED", "2026-09-08T10:00:00Z")]];
        public List<object[]> CheckPages { get; set; } = [[new { name = "build", status = "completed", conclusion = "success" }]];
        public List<object[]> StatusPages { get; set; } = [[]];
        public List<(bool Resolved, bool Outdated)[]> ThreadPages { get; set; } = [[]];
        public bool NeverEndingReviews { get; set; }
        public bool NeverEndingThreads { get; set; }
        public bool FailReviews { get; set; }
        public bool GraphqlErrors { get; set; }
        public bool LoseMergeResponse { get; set; }
        public bool HoldMerge { get; set; }
        public bool Merged { get; set; }
        public string TargetSha { get; set; } = Fixture.Base;
        public string MergeSha { get; set; } = new('c', 40);
        public HttpStatusCode? MergeStatus { get; set; }
        public int ReviewCalls { get; private set; }
        public int CheckCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int GraphqlCalls { get; private set; }
        public int MergeCalls { get; private set; }
        public TaskCompletionSource MergeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMerge { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Task>? BeforeReviewResponse { get; set; }
        public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? Intercept { get; set; }

        public static object Review(long id, string user, string state, string submittedAt) => new
        {
            id,
            user = new { login = user },
            state,
            commit_id = Fixture.Head,
            submitted_at = submittedAt
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Intercept is not null && await Intercept(request) is { } intercepted) return intercepted;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/app/installations/456") return Response(new
            {
                account = new { login = "Owner" },
                app_slug = "agentcontrol-test",
                permissions = Permissions()
            });
            if (path == "/app/installations/456/access_tokens") return Response(new
            {
                token = "installation-secret",
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
                repository_selection = "selected",
                repositories = new[] { new { full_name = Fixture.Repository } },
                permissions = Permissions()
            });
            if (path.EndsWith("/reviews", StringComparison.Ordinal))
            {
                ReviewCalls++;
                if (FailReviews) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (BeforeReviewResponse is { } before)
                {
                    BeforeReviewResponse = null;
                    await before();
                }
                var page = Page(request.RequestUri.Query);
                var response = Response(ReviewPages[Math.Min(page - 1, ReviewPages.Count - 1)]);
                if (NeverEndingReviews || page < ReviewPages.Count)
                    response.Headers.TryAddWithoutValidation("Link", $"<https://api.github.com/repos/{Fixture.Repository}/pulls/12/reviews?per_page=100&page={page + 1}>; rel=\"next\"");
                return response;
            }
            if (path.EndsWith("/check-runs", StringComparison.Ordinal))
            {
                CheckCalls++;
                var page = Page(request.RequestUri.Query);
                var response = Response(new { check_runs = CheckPages[Math.Min(page - 1, CheckPages.Count - 1)] });
                if (page < CheckPages.Count)
                    response.Headers.TryAddWithoutValidation("Link", $"<https://api.github.com/repos/{Fixture.Repository}/commits/{Fixture.Head}/check-runs?per_page=100&filter=latest&page={page + 1}>; rel=\"next\"");
                return response;
            }
            if (path.EndsWith("/statuses", StringComparison.Ordinal))
            {
                StatusCalls++;
                var page = Page(request.RequestUri.Query);
                var response = Response(StatusPages[Math.Min(page - 1, StatusPages.Count - 1)]);
                if (page < StatusPages.Count)
                    response.Headers.TryAddWithoutValidation("Link", $"<https://api.github.com/repos/{Fixture.Repository}/commits/{Fixture.Head}/statuses?per_page=100&page={page + 1}>; rel=\"next\"");
                return response;
            }
            if (path.Contains("/git/ref/heads/", StringComparison.Ordinal)) return Response(new
            {
                @ref = "refs/heads/main",
                @object = new { sha = TargetSha, type = "commit" }
            });
            if (path == "/graphql")
            {
                GraphqlCalls++;
                var page = Math.Min(GraphqlCalls - 1, ThreadPages.Count - 1);
                var hasNext = NeverEndingThreads || GraphqlCalls < ThreadPages.Count;
                return Response(new
                {
                    errors = GraphqlErrors ? new[] { new { message = "partial result" } } : Array.Empty<object>(),
                    data = new
                    {
                        repository = new
                        {
                            pullRequest = new
                            {
                                reviewThreads = new
                                {
                                    nodes = ThreadPages[page].Select(x => new { isResolved = x.Resolved, isOutdated = x.Outdated }),
                                    pageInfo = new { hasNextPage = hasNext, endCursor = hasNext ? "next-cursor" : null }
                                }
                            }
                        }
                    }
                });
            }
            if (path.EndsWith("/merge", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                MergeCalls++;
                MergeEntered.TrySetResult();
                if (HoldMerge) await ReleaseMerge.Task.WaitAsync(cancellationToken);
                if (MergeStatus is { } status) return new HttpResponseMessage(status);
                if (LoseMergeResponse)
                {
                    Merged = true;
                    TargetSha = MergeSha;
                    throw new HttpRequestException("simulated lost response");
                }
                Merged = true;
                TargetSha = MergeSha;
                return Response(new { merged = true, sha = MergeSha });
            }
            if (path.Contains("/pulls/", StringComparison.Ordinal)) return Response(new
            {
                head = new { sha = Fixture.Head },
                @base = new { sha = Fixture.Base, @ref = "main" },
                state = Merged ? "closed" : "open",
                mergeable = true,
                merged = Merged,
                merge_commit_sha = Merged ? MergeSha : null,
                user = new { login = "author-gh" }
            });
            throw new InvalidOperationException("Unexpected GitHub request: " + request.Method + " " + request.RequestUri);
        }

        private static int Page(string query)
        {
            var match = System.Text.RegularExpressions.Regex.Match(query, "(?:[?&])page=(\\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        }

        private static Dictionary<string, string> Permissions() => new()
        {
            ["contents"] = "write",
            ["issues"] = "write",
            ["pull_requests"] = "write",
            ["checks"] = "read",
            ["statuses"] = "read",
            ["actions"] = "read",
            ["metadata"] = "read"
        };

        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(Json.Write(value))
        };
    }
}
