using System.Collections;
using System.Reflection;
using HVO.AgentControl.Components.Pages;
using HVO.AgentControl.Core;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class CoordinationStatusTests
{
    [Theory]
    [InlineData("permission", Delivery.Running, "Running", false, false, "Waiting on permission", false, false)]
    [InlineData("question", Delivery.Running, "Running", false, false, "Waiting on a question", false, false)]
    [InlineData(null, Delivery.Unknown, "Unknown", true, false, "Uncertain", false, false)]
    [InlineData(null, Delivery.Queued, "None", true, false, "Queued", false, false)]
    [InlineData(null, Delivery.Finished, "NeedsReview", false, true, "Idle", true, false)]
    [InlineData(null, Delivery.Finished, "Cancelled", false, false, "Cancelled", false, false)]
    [InlineData("both", Delivery.Running, "Running", false, false, "Waiting on permission and a question", false, false)]
    [InlineData(null, Delivery.Unknown, "Running", true, false, "Uncertain", false, true)]
    public void CurrentBlockersAndOutcomesDetermineAvailability(string? request, string delivery,
        string outcome, bool ownerOperation, bool oldFailure, string expectedLabel, bool available, bool anotherRunning)
    {
        var worker = new WorkerRecord
        {
            Id = "worker",
            RuntimeId = "runtime",
            Name = "Worker",
            Activity = "Idle",
            Stale = false,
            LastObservedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Outcome = outcome
        };
        var run = new CoordinationRun { Id = "run", WorkerIdsJson = Json.Write(new[] { worker.Id }) };
        List<CommandRecord> commands = [];
        if (oldFailure) commands.Add(new()
        {
            Id = "old",
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:run",
            State = Delivery.Failed,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        commands.Add(new()
        {
            Id = "current",
            WorkerId = worker.Id,
            State = delivery,
            Kind = ownerOperation && delivery == Delivery.Queued ? "Abort" : "Prompt",
            Origin = ownerOperation ? "owner" : "coordinator:run",
            CreatedAt = 2,
            UpdatedAt = 2
        });
        if (anotherRunning) commands.Add(new()
        {
            Id = "another",
            WorkerId = worker.Id,
            State = Delivery.Running,
            Kind = "Prompt",
            Origin = "owner",
            CreatedAt = 3,
            UpdatedAt = 3
        });
        string[] requestKinds = request is null ? [] : request == "both" ? ["question", "permission"] : [request];
        var requests = requestKinds.Select(kind => new PendingRequest { WorkerId = worker.Id, Kind = kind, State = "Pending" }).ToList();
        var page = new Coordination();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ControlPage).GetField("snapshot", flags)!.SetValue(page,
            new ControlSnapshot(1, [], [worker], commands, requests));
        var statuses = (IEnumerable)typeof(Coordination).GetMethod("ParticipantStatuses", flags)!.Invoke(page, [run])!;
        var status = Assert.Single(statuses.Cast<object>());
        Assert.Equal(expectedLabel, status.GetType().GetProperty("StateLabel")!.GetValue(status));
        var aggregate = (string)typeof(Coordination).GetMethod("Aggregate", flags)!.Invoke(page, [run])!;
        Assert.Contains(available ? "Available 1" : "Available 0", aggregate);
        if (oldFailure) Assert.Null(status.GetType().GetProperty("Outcome")!.GetValue(status));
    }

    [Theory]
    [InlineData(Delivery.Running)]
    [InlineData(Delivery.Queued)]
    public void OtherWorkDoesNotInventCoordinationQueue(string state)
    {
        var worker = Observe(new WorkerRecord { Id = "worker", Activity = "Idle" });
        var run = RunWith(worker);
        var command = new CommandRecord { WorkerId = worker.Id, Kind = "Abort", Origin = "owner", State = state };
        var page = Page(new ControlSnapshot(1, [], [worker], [command], []));
        var status = Assert.Single(Statuses(page, run));
        Assert.Contains(state == Delivery.Running ? "no instruction from this run is queued" : "0 belong to this run", Property(status, "Assignment"));
    }

    [Theory]
    [InlineData("permission", "owner must review")]
    [InlineData("question", "coordinator or owner")]
    public void WaitingPreservesActualProgressAndRawAssignment(string kind, string expected)
    {
        var worker = Observe(new WorkerRecord { Id = "worker", Activity = "Idle" });
        var run = RunWith(worker);
        var command = new CommandRecord
        {
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:run",
            State = Delivery.Running,
            LastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = Json.Write(new PromptInput("request", "Review PR 123", 1)),
            ExecutionPayload = Json.Write(new PromptInput("request", "GUIDANCE BOILERPLATE Review PR 123", 1))
        };
        var page = Page(new ControlSnapshot(1, [], [worker], [command], [new PendingRequest { WorkerId = worker.Id, Kind = kind, State = "Pending" }]));
        var status = Assert.Single(Statuses(page, run));
        Assert.Equal("Review PR 123", Property(status, "Assignment"));
        Assert.Contains("latest progress", Property(status, "Receipt"));
        Assert.Contains(expected, Property(status, "Next"));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Stopped")]
    public void TerminalRunDoesNotPromiseAnotherDispatch(string state)
    {
        var worker = Observe(new WorkerRecord { Id = "worker", Activity = "Idle" });
        var run = RunWith(worker); run.State = state;
        var page = Page(new ControlSnapshot(1, [], [worker], [], []));
        Assert.Contains("no further routing", Property(Assert.Single(Statuses(page, run)), "Next"));
    }

    [Fact]
    public void ProviderRetryIsNotPresentedAsNewWorkProgressOrAvailability()
    {
        var worker = Observe(new WorkerRecord { Id = "worker", Activity = "Retrying" });
        var run = RunWith(worker);
        var command = new CommandRecord { WorkerId = worker.Id, Kind = "Prompt", Origin = "coordinator:run", State = Delivery.Running };
        var page = Page(new ControlSnapshot(1, [], [worker], [command], []));
        var status = Assert.Single(Statuses(page, run));
        Assert.Equal("Provider retry", Property(status, "StateLabel"));
        Assert.Contains("not new work progress", Property(status, "StatusLine"));
        Assert.Contains("do not resend", Property(status, "Next"));
        Assert.Contains("Available 0", Invoke(page, "Aggregate", run));
    }

    private static readonly BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    private static Coordination Page(ControlSnapshot snapshot)
    {
        var page = new Coordination();
        typeof(ControlPage).GetField("snapshot", InstancePrivate)!.SetValue(page, snapshot);
        return page;
    }

    private static object[] Statuses(Coordination page, CoordinationRun run) =>
        ((IEnumerable)typeof(Coordination).GetMethod("ParticipantStatuses", InstancePrivate)!.Invoke(page, [run])!).Cast<object>().ToArray();

    private static string Property(object status, string name) => (string)status.GetType().GetProperty(name)!.GetValue(status)!;

    private static string Invoke(Coordination page, string method, CoordinationRun run) =>
        (string)typeof(Coordination).GetMethod(method, InstancePrivate)!.Invoke(page, [run])!;

    private static string? SchedulerReason(Coordination page, CoordinationRun run) =>
        (string?)typeof(Coordination).GetMethod("SchedulerWaitReason", InstancePrivate)!.Invoke(page, [run]);

    private static string? LastSummary(Coordination page, CoordinationRun run) =>
        (string?)typeof(Coordination).GetMethod("LastCoordinatorSummary", InstancePrivate)!.Invoke(page, [run]);

    private static WorkerRecord Observe(WorkerRecord worker) { worker.Stale = false; worker.LastObservedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); return worker; }

    private static CoordinationRun RunWith(WorkerRecord worker) => new() { Id = "run", WorkerIdsJson = Json.Write(new[] { worker.Id }) };

    [Fact]
    public void QuietRunningWorkIsNotLabeledFailed()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Name = "Worker", Activity = "Idle" });
        var run = RunWith(worker);
        var command = new CommandRecord
        {
            Id = "current",
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:run",
            State = Delivery.Running,
            CreatedAt = now - 30_000,
            UpdatedAt = now - 30_000,
            AcceptedAt = now - 30_000
        };
        var status = Assert.Single(Statuses(Page(new ControlSnapshot(1, [], [worker], [command], [])), run));
        Assert.Equal("Active", Property(status, "StateLabel"));
        Assert.Contains("no progress text yet", Property(status, "Receipt"));
        Assert.DoesNotContain("failed", Property(status, "Receipt"));
        Assert.DoesNotContain("failed", Property(status, "Next"));
        Assert.Contains("Next:", Property(status, "Next"));
    }

    [Fact]
    public void ProgressEvidenceShowsLatestProgressAge()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Name = "Worker", Activity = "Idle" });
        var run = RunWith(worker);
        var command = new CommandRecord
        {
            Id = "current",
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:run",
            State = Delivery.Running,
            CreatedAt = now - 60_000,
            UpdatedAt = now - 120_000,
            AcceptedAt = now - 120_000,
            LastProgressAt = now - 60_000,
            ProgressText = "Compiled"
        };
        var status = Assert.Single(Statuses(Page(new ControlSnapshot(1, [], [worker], [command], [])), run));
        Assert.Contains("latest progress 1m ago", Property(status, "Receipt"));
        Assert.Contains("recorded 1m ago", Property(status, "Assignment"));
    }

    [Fact]
    public void WaitingOnQuestionAllowsCoordinatorOrOwnerReply()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Name = "Worker", Activity = "Idle" });
        var run = RunWith(worker);
        var command = new CommandRecord
        {
            Id = "current",
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:run",
            State = Delivery.Running,
            CreatedAt = now - 30_000,
            UpdatedAt = now - 30_000,
            AcceptedAt = now - 30_000
        };
        var question = new PendingRequest { WorkerId = worker.Id, Kind = "question", State = "Pending" };
        var status = Assert.Single(Statuses(Page(new ControlSnapshot(1, [], [worker], [command], [question])), run));
        Assert.Equal("Waiting on a question", Property(status, "StateLabel"));
        Assert.Contains("waits on a task answer", Property(status, "Assignment"));
        Assert.Contains("coordinator or owner", Property(status, "Next"));
    }

    [Fact]
    public void QueuedInstructionShowsDispatchAsNextEvent()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Name = "Worker", Activity = "Idle" });
        var run = RunWith(worker);
        var command = new CommandRecord
        {
            Id = "current",
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:run",
            State = Delivery.Queued,
            CreatedAt = now - 30_000,
            UpdatedAt = now - 30_000
        };
        var status = Assert.Single(Statuses(Page(new ControlSnapshot(1, [], [worker], [command], [])), run));
        Assert.Equal("Queued", Property(status, "StateLabel"));
        Assert.Contains("1 belong to this run", Property(status, "Assignment"));
        Assert.Contains("queued 30s ago", Property(status, "Receipt"));
        Assert.Contains("when a slot frees", Property(status, "Next"));
    }

    [Fact]
    public void UnobservedSessionIsNotTrusted()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var worker = new WorkerRecord { Id = "worker", RuntimeId = "runtime", Name = "Worker", Activity = "Idle", Stale = true, LastObservedAt = null };
        var run = RunWith(worker);
        var command = new CommandRecord
        {
            Id = "current",
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:run",
            State = Delivery.Queued,
            CreatedAt = now - 30_000,
            UpdatedAt = now - 30_000
        };
        var status = Assert.Single(Statuses(Page(new ControlSnapshot(1, [], [worker], [command], [])), run));
        Assert.Equal("Stale", Property(status, "StateLabel"));
        Assert.Contains("not trusted", Property(status, "Assignment"));
        Assert.Equal("never observed", Property(status, "Receipt"));
        Assert.Contains("reachable and observed", Property(status, "Next"));
    }

    [Fact]
    public void IdleParticipantShowsReadyForNextDispatch()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Name = "Worker", Activity = "Idle" });
        var run = RunWith(worker);
        var status = Assert.Single(Statuses(Page(new ControlSnapshot(1, [], [worker], [], [])), run));
        Assert.Equal("Idle", Property(status, "StateLabel"));
        Assert.Contains("No coordination instruction recorded", Property(status, "Assignment"));
        Assert.Contains("ready for the coordinator's next dispatch", Property(status, "Next"));
    }

    [Fact]
    public void FailedOutcomeGivesReviewAsNextEvent()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Name = "Worker", Activity = "Idle" });
        var run = RunWith(worker);
        var command = new CommandRecord
        {
            Id = "current",
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:run",
            State = Delivery.Failed,
            CreatedAt = now - 30_000,
            UpdatedAt = now - 30_000
        };
        var status = Assert.Single(Statuses(Page(new ControlSnapshot(1, [], [worker], [command], [])), run));
        Assert.Equal("Failed", Property(status, "StateLabel"));
        Assert.Contains("ended failed", Property(status, "Assignment"));
        Assert.Contains("review the failed result", Property(status, "Next"));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Stopped")]
    public void EndedRunsHaveNoFurtherEvents(string state)
    {
        var run = new CoordinationRun { Id = "run", State = state, WorkerIdsJson = Json.Write(Array.Empty<string>()) };
        var page = Page(new ControlSnapshot(1, [], [], [], []));
        Assert.Contains("no further events", Invoke(page, "RunNextEvent", run));
    }

    [Fact]
    public void PausedRunWaitsForResume()
    {
        var run = new CoordinationRun { Id = "run", State = "Paused", WorkerIdsJson = Json.Write(Array.Empty<string>()) };
        var page = Page(new ControlSnapshot(1, [], [], [], []));
        Assert.Contains("until resumed", Invoke(page, "RunNextEvent", run));
    }

    [Fact]
    public void FreshRunShowsStartedEvidenceAndFirstDispatch()
    {
        var run = new CoordinationRun { Id = "run", State = "Ready", WorkerIdsJson = Json.Write(Array.Empty<string>()) };
        var page = Page(new ControlSnapshot(1, [], [], [], []));
        Assert.Contains("started", Invoke(page, "RunLatestEvidence", run));
        Assert.Contains("coordinator is available", Invoke(page, "RunNextEvent", run));
    }

    [Fact]
    public void RunShowsLatestReceiptAge()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Name = "Worker", Activity = "Idle" });
        var run = new CoordinationRun { Id = "run", WorkerIdsJson = Json.Write(new[] { worker.Id }) };
        var command = new CommandRecord
        {
            Id = "current",
            WorkerId = worker.Id,
            Kind = "Prompt",
            Origin = "coordinator:run",
            State = Delivery.Running,
            CreatedAt = now - 120_000,
            UpdatedAt = now - 90_000,
            AcceptedAt = now - 120_000
        };
        Assert.Contains("receipt 1m ago", Invoke(Page(new ControlSnapshot(1, [], [worker], [command], [])), "RunLatestEvidence", run));
    }

    [Theory]
    [InlineData("Paused", "owner pause")]
    [InlineData("Recovering", "recovery backoff")]
    public void SchedulerReasonReportsOwnerAndRecoveryBlocks(string state, string expected)
    {
        var run = new CoordinationRun { Id = "run", State = state, WorkerIdsJson = Json.Write(Array.Empty<string>()), Detail = "Coordination paused. Already dispatched worker instructions remain independent." };
        Assert.Contains(expected, SchedulerReason(Page(new ControlSnapshot(1, [], [], [], [])), run));
    }

    [Theory]
    [InlineData("Configured coordinator turn limit reached after a failed decision.", "turn budget")]
    [InlineData("Coordinator delivery needs attention: DeliveryUnknown.", "delivery outcome")]
    [InlineData("The configured coordinator turn limit was reached during format recovery.", "output could not be used")]
    [InlineData("A participant is unavailable or is no longer a task worker.", "participant is unavailable")]
    public void SchedulerReasonDistinguishesSystemPauses(string detail, string expected)
    {
        var run = new CoordinationRun { Id = "run", State = "Paused", Detail = detail, WorkerIdsJson = Json.Write(Array.Empty<string>()) };
        Assert.Contains(expected, SchedulerReason(Page(new ControlSnapshot(1, [], [], [], [])), run));
    }

    [Fact]
    public void SchedulerReasonReportsTurnBudgetAndCoordinatorAvailability()
    {
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Worker });
        var run = new CoordinationRun { Id = "run", CoordinatorWorkerId = "coordinator", WorkerIdsJson = Json.Write(new[] { worker.Id }), Round = 1, MaxRounds = 1 };
        var coordinator = Observe(new WorkerRecord { Id = "coordinator", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Coordinator });
        var page = Page(new ControlSnapshot(1, [], [coordinator, worker], [], []));
        Assert.Contains("turn budget", SchedulerReason(page, run));

        run.Round = 0;
        coordinator.Stale = true;
        Assert.Contains("stale", SchedulerReason(page, run));

        coordinator.Stale = false; coordinator.Activity = "Active";
        Assert.Contains("coordinator is busy", SchedulerReason(page, run));
    }

    [Theory]
    [InlineData("stale", "awaits scheduler reconciliation")]
    [InlineData("active", "awaits scheduler reconciliation")]
    [InlineData("in-flight", "awaits scheduler reconciliation")]
    public void FinishedDecisionIsReconciledBeforeParticipantAvailability(string availability, string expected)
    {
        var coordinator = Observe(new WorkerRecord { Id = "coordinator", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Coordinator });
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Worker });
        var decision = new CommandRecord { Id = "decision", WorkerId = coordinator.Id, State = Delivery.Finished };
        var commands = new List<CommandRecord> { decision };
        switch (availability)
        {
            case "stale": worker.Stale = true; break;
            case "active": worker.Activity = "Active"; break;
            case "in-flight": commands.Add(new CommandRecord { Id = "work", WorkerId = worker.Id, Origin = "coordinator:run", State = Delivery.Running }); break;
        }
        var run = new CoordinationRun { Id = "run", CoordinatorWorkerId = coordinator.Id, DecisionCommandId = decision.Id, WorkerIdsJson = Json.Write(new[] { worker.Id }) };

        Assert.Contains(expected, SchedulerReason(Page(new ControlSnapshot(1, [], [coordinator, worker], commands, [])), run));
    }

    [Fact]
    public void SchedulerReasonDistinguishesUnresolvedWorkAndUnchangedEvidence()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var coordinator = Observe(new WorkerRecord { Id = "coordinator", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Coordinator });
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Worker });
        var run = new CoordinationRun { Id = "run", CoordinatorWorkerId = coordinator.Id, WorkerIdsJson = Json.Write(new[] { worker.Id }), LastDecisionAt = now };
        var command = new CommandRecord { Id = "work", WorkerId = worker.Id, Origin = "coordinator:run", State = Delivery.Running, CreatedAt = now, UpdatedAt = now };
        var page = Page(new ControlSnapshot(1, [], [coordinator, worker], [command], []));
        Assert.Contains("worker work is still unresolved", SchedulerReason(page, run));

        command.State = Delivery.Finished;
        run.LastObservation = CoordinationObservation.Fingerprint([command], []);
        Assert.Contains("evidence is unchanged", SchedulerReason(page, run));
    }

    [Fact]
    public void OwnerFollowupIsNotReportedBlockedByEarlierWork()
    {
        var coordinator = Observe(new WorkerRecord { Id = "coordinator", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Coordinator });
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Activity = "Active", Role = SessionRoles.Worker });
        var command = new CommandRecord { Id = "work", WorkerId = worker.Id, Origin = "coordinator:run", State = Delivery.Running };
        var run = new CoordinationRun
        {
            Id = "run",
            CoordinatorWorkerId = coordinator.Id,
            Instruction = "Original\n\nOwner follow-up:\nUse the correction.",
            WorkerIdsJson = Json.Write(new[] { worker.Id }),
            InputJson = Json.Write(new CoordinatorContext("Original", [], [], []))
        };

        Assert.Null(SchedulerReason(Page(new ControlSnapshot(1, [], [coordinator, worker], [command], [])), run));
    }

    [Fact]
    public void SchedulerReasonDoesNotReplaceLastCoordinatorSummary()
    {
        var run = new CoordinationRun
        {
            Id = "run",
            State = "Paused",
            WorkerIdsJson = Json.Write(Array.Empty<string>()),
            Detail = "Coordination paused. Already dispatched worker instructions remain independent.",
            DecisionJson = Json.Write(new CoordinatorDecision("Workers are done.", [], true))
        };
        var page = Page(new ControlSnapshot(1, [], [], [], []));

        Assert.Equal("Workers are done.", LastSummary(page, run));
        Assert.Contains("owner pause", SchedulerReason(page, run));
    }

    [Fact]
    public void SchedulerReasonKeepsPendingRecoveryAndRepairDistinctFromUnchangedEvidence()
    {
        var coordinator = Observe(new WorkerRecord { Id = "coordinator", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Coordinator });
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Worker });
        var run = new CoordinationRun
        {
            Id = "run",
            CoordinatorWorkerId = coordinator.Id,
            WorkerIdsJson = Json.Write(new[] { worker.Id }),
            LastObservation = CoordinationObservation.Fingerprint([], []),
            InputJson = Json.Write(new CoordinatorContext("Task", [], [], [], Repair: new(1, "rejected")))
        };
        var page = Page(new ControlSnapshot(1, [], [coordinator, worker], [], []));
        Assert.Contains("evidence is unchanged", SchedulerReason(page, run));

        run.InputJson = Json.Write(new CoordinatorContext("Task", [], [], [], Recovery: new(1, 1234, "retry")));
        Assert.Contains("evidence is unchanged", SchedulerReason(page, run));
    }

    [Fact]
    public void MissingDecisionFromBoundedSnapshotIsNotReportedComplete()
    {
        var coordinator = Observe(new WorkerRecord { Id = "coordinator", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Coordinator });
        var worker = Observe(new WorkerRecord { Id = "worker", RuntimeId = "runtime", Activity = "Idle", Role = SessionRoles.Worker });
        var run = new CoordinationRun { Id = "run", CoordinatorWorkerId = coordinator.Id, DecisionCommandId = "trimmed", WorkerIdsJson = Json.Write(new[] { worker.Id }) };

        var reason = SchedulerReason(Page(new ControlSnapshot(1, [], [coordinator, worker], [], [])), run);

        Assert.Contains("bounded snapshot", reason);
        Assert.Contains("unavailable", reason);
    }
    [Theory]
    [InlineData("progress")]
    [InlineData("completion")]
    [InlineData("question")]
    [InlineData("repair")]
    public void NewEvidenceCanWakeSchedulerWhileAnotherWorkerRuns(string signal)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var coordinator = Observe(new WorkerRecord { Id = "coordinator", Activity = "Idle", Role = SessionRoles.Coordinator });
        var worker = Observe(new WorkerRecord { Id = "worker", Activity = "Active", Role = SessionRoles.Worker });
        var run = new CoordinationRun { Id = "run", CoordinatorWorkerId = coordinator.Id,
            WorkerIdsJson = Json.Write(new[] { worker.Id }), LastDecisionAt = now - 120_000 };
        var command = new CommandRecord { Id = "work", WorkerId = worker.Id, Origin = "coordinator:run", State = Delivery.Running };
        var commands = new List<CommandRecord> { command };
        var requests = new List<PendingRequest>();
        if (signal == "progress") command.LastProgressAt = now;
        if (signal == "completion") commands.Add(new CommandRecord { Id = "finished", WorkerId = worker.Id,
            Origin = "coordinator:run", State = Delivery.Finished, UpdatedAt = now });
        if (signal == "question") requests.Add(new PendingRequest { WorkerId = worker.Id, Kind = "question", State = "Pending" });
        if (signal == "repair") run.InputJson = Json.Write(new CoordinatorContext(run.Instruction, [], [], [], Repair: new(1, "rejected")));
        Assert.Null(SchedulerReason(Page(new ControlSnapshot(1, [], [coordinator, worker], commands, requests)), run));
    }

}
