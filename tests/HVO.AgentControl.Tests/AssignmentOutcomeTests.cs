using HVO.AgentControl.Core;
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
