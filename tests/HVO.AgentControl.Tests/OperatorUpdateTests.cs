using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class OperatorUpdateTests
{
    [Fact]
    public async Task BaselineAndScheduledUpdatesUseDatabaseStateWithoutCreatingCommands()
    {
        await using var app = new TestApp();
        var (run, worker) = await Seed(app.Store);
        const long start = 1_000_000;
        var schedule = await app.Store.ConfigureOperatorUpdates(run.Id,
            new(Guid.NewGuid().ToString(), IntervalMinutes: 1), start);

        var baseline = Assert.Single(await app.Store.OperatorUpdates());
        Assert.Equal("Baseline", baseline.Kind);
        var summary = Json.Read<OperatorStatusSummary>(baseline.SummaryJson);
        var participant = Assert.Single(summary.Participants);
        Assert.Equal("Active", participant.Phase);
        Assert.Contains("Review the exact commit", participant.Assignment);
        Assert.Equal(900_000, participant.LastProgressAt);
        Assert.Equal(1, summary.Busy);
        Assert.Equal(0, await app.Store.OperatorUpdateTick(start + 59_999));
        Assert.Equal(1, await app.Store.OperatorUpdateTick(start + 60_000));
        Assert.Equal(0, await app.Store.OperatorUpdateTick(start + 60_000));

        var updates = await app.Store.OperatorUpdates();
        Assert.Equal(["Baseline", "Scheduled"], updates.Select(x => x.Kind));
        Assert.Equal(2, updates.Select(x => x.Id).Distinct().Count());
        Assert.Equal(start + 120_000, (await ReadSchedule(app.Store, schedule.Id)).NextDueAt);
        Assert.Equal(1, await app.Store.Read(db => db.Commands.CountAsync(x => x.WorkerId == worker.Id)));
    }

    [Fact]
    public async Task TwentyUnchangedIntervalsProduceTwentyDistinctDurableOccurrences()
    {
        await using var app = new TestApp();
        var (run, _) = await Seed(app.Store);
        const long start = 2_000_000;
        await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 1), start);

        for (var interval = 1; interval <= 20; interval++)
            Assert.Equal(1, await app.Store.OperatorUpdateTick(start + interval * 60_000L));

        var updates = await app.Store.OperatorUpdates(take: 100);
        Assert.Equal(21, updates.Count);
        Assert.Single(updates, x => x.Kind == "Baseline");
        Assert.Equal(20, updates.Count(x => x.Kind == "Scheduled"));
        Assert.Equal(21, updates.Select(x => x.Id).Distinct().Count());
        Assert.All(updates.Where(x => x.Kind == "Scheduled"), x => Assert.Equal(0, x.MissedIntervals));
    }

    [Fact]
    public async Task RestartProducesOneCatchUpAndPreservesCadence()
    {
        var data = Path.Combine(Path.GetTempPath(), "hvo-operator-updates-" + Guid.NewGuid().ToString("N"));
        string runId, scheduleId;
        const long start = 3_000_000;
        await using (var first = new TestApp(data))
        {
            var (run, _) = await Seed(first.Store);
            runId = run.Id;
            scheduleId = (await first.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 1), start)).Id;
        }

        await using var restarted = new TestApp(data);
        Assert.Equal(1, await restarted.Store.OperatorUpdateTick(start + 210_000));
        Assert.Equal(0, await restarted.Store.OperatorUpdateTick(start + 210_000));
        var updates = await restarted.Store.OperatorUpdates(take: 100);
        Assert.Equal(2, updates.Count);
        var catchUp = Assert.Single(updates, x => x.Kind == "CatchUp");
        Assert.Equal(2, catchUp.MissedIntervals);
        Assert.Equal(start + 60_000, catchUp.DueAt);
        Assert.Equal(start + 240_000, (await ReadSchedule(restarted.Store, scheduleId)).NextDueAt);
        Assert.Equal(runId, Json.Read<OperatorStatusSummary>(catchUp.SummaryJson).CoordinationRunId);
    }

    [Fact]
    public async Task ConcurrentDuplicateWakesPublishOnce()
    {
        await using var app = new TestApp();
        var (run, _) = await Seed(app.Store);
        const long start = 4_000_000;
        await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 1), start);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => app.Store.OperatorUpdateTick(start + 60_000)));
        Assert.Equal(1, results.Sum());
        Assert.Equal(2, (await app.Store.OperatorUpdates()).Count);
    }

    [Fact]
    public async Task QueuedCreationTimeIsNotReportedAsADeliveryReceipt()
    {
        await using var app = new TestApp();
        var (run, worker) = await Seed(app.Store);
        await app.Store.Write(async db =>
        {
            var command = await db.Commands.SingleAsync(x => x.WorkerId == worker.Id);
            command.State = Delivery.Queued;
            command.LastProgressAt = null;
            command.AcceptedAt = null;
            return true;
        });

        await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 1), 4_500_000);
        var summary = Json.Read<OperatorStatusSummary>((await app.Store.OperatorUpdates()).Single().SummaryJson);
        var participant = Assert.Single(summary.Participants);
        Assert.Equal("Queued", participant.Phase);
        Assert.Null(participant.LastReceiptAt);
    }

    [Fact]
    public async Task UnknownNativeActivityIsNotReportedAsAvailable()
    {
        await using var app = new TestApp();
        var (run, worker) = await Seed(app.Store);
        await app.Store.Write(async db =>
        {
            (await db.Workers.SingleAsync(x => x.Id == worker.Id)).Activity = "Unknown";
            (await db.Commands.SingleAsync(x => x.WorkerId == worker.Id)).State = Delivery.Finished;
            return true;
        });

        await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 1), 4_750_000);
        var summary = Json.Read<OperatorStatusSummary>((await app.Store.OperatorUpdates()).Single().SummaryJson);
        var participant = Assert.Single(summary.Participants);
        Assert.Equal("Uncertain", participant.Phase);
        Assert.Equal("Native activity is unknown.", participant.Blocker);
    }

    [Fact]
    public async Task TerminalTickPublishesOnceAndDisablesSchedule()
    {
        await using var app = new TestApp();
        var (run, _) = await Seed(app.Store);
        const long start = 5_000_000;
        var schedule = await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 60), start);
        await app.Store.Write(async db =>
        {
            (await db.CoordinationRuns.FindAsync(run.Id))!.State = "Completed";
            return true;
        });

        Assert.Equal(1, await app.Store.OperatorUpdateTick(start + 1));
        Assert.Equal(0, await app.Store.OperatorUpdateTick(start + 2));
        Assert.Equal(["Baseline", "Terminal"], (await app.Store.OperatorUpdates()).Select(x => x.Kind));
        Assert.False((await ReadSchedule(app.Store, schedule.Id)).Enabled);
    }

    [Fact]
    public async Task RetrievalAndAcknowledgementAreAuthorizedBoundedAndIdempotent()
    {
        await using var app = new TestApp();
        var (run, _) = await Seed(app.Store);
        var update = Assert.Single(await Configure(app.Store, run.Id));
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/operator-updates")).StatusCode);

        using var owner = await app.SignIn();
        var fetched = await owner.GetFromJsonAsync<List<OperatorStatusUpdate>>("/api/v1/operator-updates?after=0&take=1");
        Assert.Single(fetched!);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/operator-updates?take=101")).StatusCode);
        var first = await owner.PostAsync("/api/v1/operator-updates/" + Uri.EscapeDataString(update.Id) + "/ack", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var acknowledgedAt = (await app.Store.OperatorUpdates()).Single().AcknowledgedAt;
        Assert.NotNull(acknowledgedAt);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/v1/operator-updates/" + Uri.EscapeDataString(update.Id) + "/ack", null)).StatusCode);
        Assert.Equal(acknowledgedAt, (await app.Store.OperatorUpdates()).Single().AcknowledgedAt);
    }

    [Fact]
    public async Task ScheduleRequestIsIdempotentAndRejectsConflictingOrInvalidInput()
    {
        await using var app = new TestApp();
        var (run, _) = await Seed(app.Store);
        var id = Guid.NewGuid().ToString();
        var first = await app.Store.ConfigureOperatorUpdates(run.Id, new(id, 2), 6_000_000);
        Assert.Equal(first.Id, (await app.Store.ConfigureOperatorUpdates(run.Id, new(id, 2), 6_000_001)).Id);
        Assert.Single(await app.Store.OperatorUpdates());
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ConfigureOperatorUpdates(run.Id, new(id, 3), 6_000_000));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 2), 6_000_000));
    }


    [Theory]
    [InlineData("Completed")]
    [InlineData("Stopped")]
    public async Task TerminalSummaryDoesNotSuggestFutureRunDispatch(string state)
    {
        await using var app = new TestApp();
        var (run, _) = await Seed(app.Store);
        await app.Store.Write(async db => { (await db.CoordinationRuns.FindAsync(run.Id))!.State = state; return true; });
        var summary = Json.Read<OperatorStatusSummary>((await Configure(app.Store, run.Id)).Single().SummaryJson);
        Assert.Contains("no new run assignments", Assert.Single(summary.Participants).NextEvent);
    }

    [Fact]
    public async Task ArchivedParticipantAndTerminalReceiptAreReportedTruthfully()
    {
        await using var app = new TestApp();
        var (run, worker) = await Seed(app.Store);
        await app.Store.Write(async db =>
        {
            var saved = (await db.Workers.FindAsync(worker.Id))!;
            saved.Archived = true; saved.Activity = "Idle";
            var command = await db.Commands.SingleAsync(x => x.WorkerId == worker.Id);
            command.State = Delivery.Finished; command.UpdatedAt = 1_000_000;
            return true;
        });
        var summary = Json.Read<OperatorStatusSummary>((await Configure(app.Store, run.Id)).Single().SummaryJson);
        var participant = Assert.Single(summary.Participants);
        Assert.Equal("Stale", participant.Phase);
        Assert.Equal(1_000_000, participant.LastReceiptAt);
        Assert.Equal(900_000, participant.LastProgressAt);
    }

    private static async Task<(CoordinationRun Run, WorkerRecord Worker)> Seed(ControlStore store)
    {
        var worker = new WorkerRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            RuntimeId = Guid.NewGuid().ToString("N"),
            ManagedServerId = "server",
            NativeSessionId = Guid.NewGuid().ToString("N"),
            Name = "Worker",
            Activity = "Active",
            Stale = false,
            LastObservedAt = 950_000
        };
        var run = new CoordinationRun
        {
            Id = Guid.NewGuid().ToString(),
            CoordinatorWorkerId = Guid.NewGuid().ToString("N"),
            WorkerIdsJson = Json.Write(new[] { worker.Id }),
            State = "Waiting",
            Instruction = "Review work"
        };
        var command = new CommandRecord
        {
            Id = Guid.NewGuid().ToString(),
            RuntimeId = worker.RuntimeId,
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:" + run.Id,
            State = Delivery.Running,
            Payload = Json.Write(new PromptInput(Guid.NewGuid().ToString(), "Review the exact commit and report blockers.", 0)),
            ProgressText = "Reading tests",
            LastProgressAt = 900_000,
            CreatedAt = 800_000,
            UpdatedAt = 850_000
        };
        await store.Write(db =>
        {
            db.Workers.Add(worker); db.CoordinationRuns.Add(run); db.Commands.Add(command);
            return Task.FromResult(true);
        });
        return (run, worker);
    }

    private static Task<OperatorUpdateSchedule> ReadSchedule(ControlStore store, string id) =>
        store.Read(async db => await db.OperatorUpdateSchedules.AsNoTracking().SingleAsync(x => x.Id == id));

    private static async Task<List<OperatorStatusUpdate>> Configure(ControlStore store, string runId)
    {
        await store.ConfigureOperatorUpdates(runId, new(Guid.NewGuid().ToString(), 1), 7_000_000);
        return await store.OperatorUpdates();
    }

    [Fact]
    public async Task MilestonePublishedWhenCoordinationStopped()
    {
        await using var app = new TestApp();
        var (run, _) = await Seed(app.Store);
        await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 60), 9_000_000);
        var initial = await app.Store.OperatorUpdates();
        Assert.Single(initial);

        await app.Store.ControlCoordination(run.Id, new(run.Revision, "stop"));
        var updates = await app.Store.OperatorUpdates();
        var milestone = updates.LastOrDefault();
        Assert.NotNull(milestone);
        Assert.Equal("Stopped", milestone.Kind);
        Assert.Equal(run.Id, milestone.CoordinationRunId);
    }

    [Fact]
    public async Task MilestoneUsesPersistedSourceEventAndIsIdempotent()
    {
        await using var app = new TestApp();
        var (run, _) = await Seed(app.Store);
        const long start = 10_000_000;
        var scheduleId = (await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 60), start)).Id;

        var firstResult = await app.Store.Write(async db =>
        {
            var schedule = (await db.OperatorUpdateSchedules.FindAsync(scheduleId))!;
            var sourceEvent = ControlStore.Event(db, "CoordinationChanged", payload: new { run.Id, state = "Paused" }, provenance: "user");
            await db.SaveChangesAsync();
            var savedRun = (await db.CoordinationRuns.FindAsync(run.Id))!;
            return await app.Store.PublishMilestoneInternal(db, schedule, savedRun, "Paused", start + 1, sourceEvent);
        });
        Assert.Equal(1, firstResult);

        var secondResult = await app.Store.Write(async db =>
        {
            var schedule = (await db.OperatorUpdateSchedules.FindAsync(scheduleId))!;
            var sourceEvent = await db.Events.SingleAsync(e => e.Type == "CoordinationChanged" && e.Payload.Contains(run.Id));
            var savedRun = (await db.CoordinationRuns.FindAsync(run.Id))!;
            return await app.Store.PublishMilestoneInternal(db, schedule, savedRun, "Paused", start + 1, sourceEvent);
        });
        Assert.Equal(0, secondResult);

        var milestone = Assert.Single(await app.Store.OperatorUpdates(), x => x.Kind == "Paused");
        var transitionEvent = await app.Store.Read(db => db.Events.SingleAsync(e => e.Type == "CoordinationChanged" && e.Payload.Contains(run.Id)));
        Assert.Equal(transitionEvent.Sequence, milestone.SourceEventSequence);
    }

    [Fact]
    public async Task PruneAcknowledgedDeletesOldRowsButKeepsRecentAndUnacknowledged()
    {
        await using var app = new TestApp();
        var (run, _) = await Seed(app.Store);
        var scheduleId = (await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 1), 11_000_000)).Id;

        await app.Store.Write(async db =>
        {
            for (var i = 0; i < 10; i++)
            {
                db.OperatorStatusUpdates.Add(new OperatorStatusUpdate
                {
                    Id = scheduleId + ":prune:" + i,
                    ScheduleId = scheduleId,
                    CoordinationRunId = run.Id,
                    Kind = "Scheduled",
                    DueAt = 11_000_000L + i,
                    PublishedAt = 11_000_000L + i,
                    SourceEventSequence = 11_000_000L + i,
                    ActiveSetRevision = 1,
                    SummaryJson = "{}",
                    AcknowledgedAt = i < 3 ? 12_000_000L : 11_000_000L
                });
            }
            return Task.FromResult(true);
        });

        var before = await app.Store.Read(db => db.OperatorStatusUpdates.CountAsync(x => x.ScheduleId == scheduleId));
        Assert.Equal(11, before);

        var pruned = await app.Store.PruneAcknowledgedOperatorUpdates(maxAcknowledgedPerSchedule: 3);
        Assert.Equal(7, pruned);

        var remaining = await app.Store.Read(db => db.OperatorStatusUpdates.Where(x => x.ScheduleId == scheduleId).ToListAsync());
        Assert.Equal(4, remaining.Count);
        Assert.Equal(3, remaining.Count(u => u.AcknowledgedAt == 12_000_000L));
        Assert.Single(remaining, u => u.AcknowledgedAt == null);
    }

    private static async Task<(WorkerRecord Coordinator, WorkerRecord Participant)> SeedCoordinatorParticipant(ControlStore store)
    {
        var runtime = await store.SaveRuntime(PersistenceTests.Profile());
        var coordinator = new WorkerRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            RuntimeId = runtime.Id,
            ManagedServerId = "server",
            NativeSessionId = Guid.NewGuid().ToString("N"),
            Name = "Coordinator",
            Activity = "Idle",
            Stale = false,
            Role = SessionRoles.Coordinator,
            Directory = "/home/agent/workspaces/a",
            LastObservedAt = 950_000
        };
        var participant = new WorkerRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            RuntimeId = coordinator.RuntimeId,
            ManagedServerId = "server",
            NativeSessionId = Guid.NewGuid().ToString("N"),
            Name = "Participant",
            Activity = "Idle",
            Stale = false,
            Directory = "/home/agent/workspaces/b",
            LastObservedAt = 950_000
        };
        await store.Write(async db =>
        {
            db.Workers.Add(coordinator);
            db.Workers.Add(participant);
            return true;
        });
        return (coordinator, participant);
    }

    [Fact]
    public async Task MilestonePublishedOnceWhenDecisionAppliedViaCoordinationTick()
    {
        await using var app = new TestApp();
        var (coordinator, participant) = await SeedCoordinatorParticipant(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Review blockers.", [participant.Id]));
        await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 60), 9_000_000);
        var initial = await app.Store.OperatorUpdates();
        Assert.Single(initial);

        await app.Store.CoordinationTick();
        var decisionCommandId = (await app.Store.Coordinations()).Single().DecisionCommandId;
        Assert.NotNull(decisionCommandId);

        await FinishDecision(app.Store, run.Id);

        await app.Store.CoordinationTick();
        var afterApply = await app.Store.OperatorUpdates();
        var milestones = afterApply.Where(u => u.Kind is "DecisionApplied" or "Completed").ToList();
        Assert.Single(milestones);
        Assert.Equal(run.Id, milestones[0].CoordinationRunId);
        Assert.True(milestones[0].SourceEventSequence > 0);

        var transitionEventSeq = await app.Store.Read(db =>
            db.Events.AsNoTracking().Where(e => e.Type == "CoordinatorDecisionApplied").OrderByDescending(e => e.Sequence).Select(e => e.Sequence).FirstOrDefaultAsync());
        Assert.Equal(transitionEventSeq, milestones[0].SourceEventSequence);
    }

    [Fact]
    public async Task ContinuousSupervisionPublishesDecisionAppliedForCompletedDecision()
    {
        await using var app = new TestApp();
        var (coordinator, participant) = await SeedCoordinatorParticipant(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Review blockers.", [participant.Id], ContinuousSupervision: true));
        await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 60), 9_000_000);

        await app.Store.CoordinationTick();
        await FinishDecision(app.Store, run.Id, complete: true);
        await app.Store.CoordinationTick();

        Assert.Equal("Waiting", (await app.Store.Coordinations()).Single().State);
        var milestone = Assert.Single(await app.Store.OperatorUpdates(), x => x.Kind == "DecisionApplied");
        Assert.Equal(run.Id, milestone.CoordinationRunId);
    }

    [Fact]
    public async Task FailedMilestonePublicationRollsBackTransitionEvidence()
    {
        await using var app = new TestApp();
        var (run, _) = await Seed(app.Store);
        await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 60), 9_000_000);
        await app.Store.Write(async db =>
        {
            (await db.CoordinationRuns.FindAsync(run.Id))!.WorkerIdsJson = "not-json";
            return true;
        });

        await Assert.ThrowsAsync<JsonException>(() => app.Store.ControlCoordination(run.Id, new(run.Revision, "stop")));

        var evidence = await app.Store.Read(async db => new
        {
            State = (await db.CoordinationRuns.FindAsync(run.Id))!.State,
            Events = await db.Events.Where(e => e.Type == "CoordinationChanged").ToListAsync(),
            Updates = await db.OperatorStatusUpdates.Where(u => u.CoordinationRunId == run.Id && u.Kind == "Stopped").ToListAsync()
        });
        Assert.Equal("Waiting", evidence.State);
        Assert.DoesNotContain(evidence.Events, e => e.Payload.Contains(run.Id));
        Assert.Empty(evidence.Updates);
    }

    [Fact]
    public async Task MilestonePublishedOnceWhenCoordinationStoppedByOwner()
    {
        await using var app = new TestApp();
        var (coordinator, participant) = await SeedCoordinatorParticipant(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Review blockers.", [participant.Id]));
        await app.Store.ConfigureOperatorUpdates(run.Id, new(Guid.NewGuid().ToString(), 60), 9_000_000);
        var initial = await app.Store.OperatorUpdates();
        Assert.Single(initial);

        await app.Store.ControlCoordination(run.Id, new(run.Revision, "stop"));
        var updates = await app.Store.OperatorUpdates();
        var milestones = updates.Where(u => u.Kind == "Stopped").ToList();
        Assert.Single(milestones);
        Assert.Equal(run.Id, milestones[0].CoordinationRunId);
        Assert.True(milestones[0].SourceEventSequence > 0);

        var transitionEventSeq = await app.Store.Read(db =>
            db.Events.AsNoTracking().Where(e => e.Type == "CoordinationChanged").OrderByDescending(e => e.Sequence).Select(e => e.Sequence).FirstOrDefaultAsync());
        Assert.Equal(transitionEventSeq, milestones[0].SourceEventSequence);
    }

    private static async Task FinishDecision(ControlStore store, string runId, bool complete = false)
    {
        var run = (await store.Coordinations()).Single(x => x.Id == runId);
        var commandId = run.DecisionCommandId ?? throw new InvalidOperationException("No decision command id");
        await store.Write(async db =>
        {
            var command = (await db.Commands.FindAsync(commandId))!;
            command.State = Delivery.Finished;
            var decision = new CoordinatorDecision("Assign", Array.Empty<CoordinatorAction>(), complete);
            var text = Json.Write(decision);
            command.ResultJson = Json.Write(new { messages = new[] { new { parts = new[] { new { type = "text", text } } } } });
            return true;
        });
    }
}
