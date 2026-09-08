using HVO.AgentControl.Core;
using HVO.AgentControl.GitHub;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AssignmentOutcomeTests
{
    [Fact]
    public async Task ExactSettledCommandReceivesOutcomeInsteadOfQueuedFollowUp()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var reviewed = Prompt(worker, Delivery.Finished, "reviewed");
        var queued = Prompt(worker, Delivery.Queued, "queued");
        await app.Store.Write(db =>
        {
            db.Commands.AddRange(reviewed, queued);
            db.Assignments.AddRange(new AssignmentRecord { Id = reviewed.Id, WorkerId = worker.Id }, new AssignmentRecord { Id = queued.Id, WorkerId = worker.Id });
            return Task.FromResult(true);
        });

        var revision = (await app.Store.Detail(worker.Id)).Worker.Revision;
        await app.Store.SetOutcome(worker.Id, new(reviewed.Id, revision, "VerifiedComplete", "Reviewed exact command."));

        var detail = await app.Store.Detail(worker.Id);
        Assert.Equal("VerifiedComplete", detail.Assignments.Single(x => x.Id == reviewed.Id).Outcome);
        Assert.Equal("Reviewed exact command.", detail.Assignments.Single(x => x.Id == reviewed.Id).Evidence);
        Assert.Equal("Assigned", detail.Assignments.Single(x => x.Id == queued.Id).Outcome);
        Assert.Equal("{}", detail.Assignments.Single(x => x.Id == reviewed.Id).GitHubAuthorityJson);
        var recorded = Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "AssignmentOutcomeRecorded").ToListAsync()));
        Assert.Equal(reviewed.Id, recorded.CommandId);
    }

    [Fact]
    public async Task StaleRevisionOrUnsettledCommandCannotReceiveOutcome()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var queued = Prompt(worker, Delivery.Queued, "queued");
        await app.Store.Write(db =>
        {
            db.Commands.Add(queued);
            db.Assignments.Add(new AssignmentRecord { Id = queued.Id, WorkerId = worker.Id });
            return Task.FromResult(true);
        });
        var revision = (await app.Store.Detail(worker.Id)).Worker.Revision;

        await Assert.ThrowsAsync<ControlException>(() => app.Store.SetOutcome(worker.Id,
            new(queued.Id, revision, "VerifiedComplete", "Must not attest queued work.")));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SetOutcome(worker.Id,
            new(queued.Id, revision - 1, "VerifiedComplete", "Must reject stale review.")));

        var assignment = await app.Store.Read(db => db.Assignments.SingleAsync(x => x.Id == queued.Id));
        Assert.Equal("Assigned", assignment.Outcome);
        Assert.Empty(await app.Store.Read(db => db.Events.Where(x => x.Type == "AssignmentOutcomeRecorded").ToListAsync()));
    }

    [Fact]
    public async Task CommandFromAnotherWorkerCannotReceiveOutcome()
    {
        await using var app = new TestApp();
        var first = await PersistenceTests.SeedWorker(app.Store);
        var second = await PersistenceTests.SeedWorker(app.Store);
        var command = Prompt(first, Delivery.Finished, "other-worker");
        await app.Store.Write(db =>
        {
            db.Commands.Add(command);
            db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = first.Id });
            return Task.FromResult(true);
        });
        var revision = (await app.Store.Detail(second.Id)).Worker.Revision;

        await Assert.ThrowsAsync<ControlException>(() => app.Store.SetOutcome(second.Id,
            new(command.Id, revision, "VerifiedComplete", "Wrong worker must not attest.")));
        Assert.Equal("Assigned", (await app.Store.Detail(first.Id)).Assignments.Single().Outcome);
    }

    [Fact]
    public async Task AssignmentFromAnotherWorkerCannotReceiveOutcome()
    {
        await using var app = new TestApp();
        var first = await PersistenceTests.SeedWorker(app.Store);
        var second = await PersistenceTests.SeedWorker(app.Store);
        var command = Prompt(first, Delivery.Finished, "wrong-assignment-owner");
        await app.Store.Write(db =>
        {
            db.Commands.Add(command);
            db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = second.Id });
            return Task.FromResult(true);
        });
        var revision = (await app.Store.Detail(first.Id)).Worker.Revision;

        await Assert.ThrowsAsync<ControlException>(() => app.Store.SetOutcome(first.Id,
            new(command.Id, revision, "VerifiedComplete", "Wrong assignment owner must not attest.")));
        Assert.Equal("Assigned", (await app.Store.Detail(second.Id)).Assignments.Single().Outcome);
    }

    [Fact]
    public async Task TypedResultMustMatchImmutableScopeAndFinishedNativeResult()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var scope = GitHubMergeTaskAuthority.Scope(GitHubMergeTaskKinds.Reviewer, "Owner/Repo", 12, new string('a', 40));
        var command = Prompt(worker, Delivery.Finished, "typed");
        command.Payload = Json.Write(new PromptInput(command.Id, "Review exact head", 0, GitHubMergeScope: scope));
        command.ResultId = "native-result";
        command.ResultJson = Json.Write(new { answer = "reviewed" });
        await app.Store.Write(db =>
        {
            db.Commands.Add(command);
            db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = worker.Id, Prompt = "Review exact head" });
            return Task.FromResult(true);
        });

        await app.Store.SetOutcome(worker.Id, new(command.Id, 0, "VerifiedComplete", "Exact review complete.",
            new(GitHubMergeTaskKinds.Version, GitHubMergeTaskKinds.ApprovedExactHead, scope)));
        var assignment = (await app.Store.Detail(worker.Id)).Assignments.Single();
        var authority = Json.Read<GitHubMergeOutcomeAuthority>(assignment.GitHubAuthorityJson);

        Assert.Equal(scope, authority.Scope);
        Assert.Equal("native-result", authority.CommandResultId);
        var wrong = scope with { HeadSha = new string('b', 40) };
        await Assert.ThrowsAsync<ControlException>(() => app.Store.SetOutcome(worker.Id,
            new(command.Id, 1, "VerifiedComplete", "Wrong head.",
                new(GitHubMergeTaskKinds.Version, GitHubMergeTaskKinds.ApprovedExactHead, wrong))));
    }

    [Fact]
    public async Task WithdrawingCurrentAuthorAuthorityRetainsAuthorProvenance()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var scope = GitHubMergeTaskAuthority.Scope(GitHubMergeTaskKinds.Author, "Owner/Repo", 12, new string('a', 40));
        var admission = scope with { PullRequestNumber = 0, HeadSha = "" };
        var command = Prompt(worker, Delivery.Finished, "author");
        command.Payload = Json.Write(new PromptInput(command.Id, "Implement and publish the change", 0, GitHubMergeScope: admission));
        command.ResultId = "publication-result";
        command.ResultJson = Json.Write(new { published = true });
        await app.Store.Write(db =>
        {
            db.Commands.Add(command);
            db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = worker.Id, Prompt = "Publish exact head" });
            return Task.FromResult(true);
        });
        await app.Store.SetOutcome(worker.Id, new(command.Id, 0, "VerifiedComplete", "Publication verified.",
            new(GitHubMergeTaskKinds.Version, GitHubMergeTaskKinds.PublishedExactHead, scope)));

        await app.Store.SetOutcome(worker.Id, new(command.Id, 1, "Failed", "Publication result withdrawn."));

        var assignment = (await app.Store.Detail(worker.Id)).Assignments.Single();
        Assert.Equal("{}", assignment.GitHubAuthorityJson);
        Assert.True(GitHubMergeTaskAuthority.HasRetainedAuthorProvenance(assignment, worker.Id,
            scope.Repository, scope.PullRequestNumber));
    }

    [Fact]
    public async Task GenericEvidenceCannotForgeTypedGitHubAuthority()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = Prompt(worker, Delivery.Finished, "generic");
        var forged = Json.Write(new GitHubMergeOutcomeAuthority(GitHubMergeTaskKinds.Version,
            GitHubMergeTaskAuthority.Scope(GitHubMergeTaskKinds.Reviewer, "Owner/Repo", 12, new string('a', 40)),
            GitHubMergeTaskKinds.ApprovedExactHead, worker.Id, command.Id, "forged", new string('b', 64),
            new string('c', 64), ControlStore.Now));
        await app.Store.Write(db =>
        {
            db.Commands.Add(command);
            db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = worker.Id });
            return Task.FromResult(true);
        });

        await app.Store.SetOutcome(worker.Id, new(command.Id, 0, "VerifiedComplete", forged));

        var assignment = (await app.Store.Detail(worker.Id)).Assignments.Single();
        Assert.Equal(forged, assignment.Evidence);
        Assert.Equal("{}", assignment.GitHubAuthorityJson);
    }

    private static CommandRecord Prompt(WorkerRecord worker, string state, string suffix) => new()
    {
        Id = Guid.NewGuid().ToString(),
        RuntimeId = worker.RuntimeId,
        WorkerId = worker.Id,
        Kind = "Prompt",
        State = state,
        NativeMessageId = "msg_" + suffix
    };
}
