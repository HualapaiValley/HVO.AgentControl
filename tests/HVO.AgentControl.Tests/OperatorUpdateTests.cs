using System.Net;
using System.Net.Http.Json;
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
}
