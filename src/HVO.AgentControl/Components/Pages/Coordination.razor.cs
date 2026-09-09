using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;

namespace HVO.AgentControl.Components.Pages;

public partial class Coordination
{
    private List<CoordinationRun> runs = [];
    private HashSet<string> hostOperationsIds = [];
    private readonly HashSet<string> selected = [];
    private string coordinatorId = "", instruction = "";
    private int maxRounds = 20;
    private bool continuousSupervision = true;
    private bool includeGuidance = true;
    private int progressMinutes = 5;
    private string? requestId;
    private readonly Dictionary<string, string> followups = [];
    private readonly Dictionary<string, string> followupIds = [];
    private readonly Dictionary<string, string> renewalInstructions = [];
    private readonly Dictionary<string, int> renewalRounds = [];
    private readonly Dictionary<string, CoordinationRenewalInput> renewalRequests = [];
    private readonly Dictionary<string, bool> renewalSupervision = [];
    private Task Renew(CoordinationRun run) => Execute(async () =>
    {
        var instruction = renewalInstructions.GetValueOrDefault(run.Id, run.Instruction);
        var supervise = renewalSupervision.GetValueOrDefault(run.Id, true);
        var rounds = supervise ? 0 : renewalRounds.GetValueOrDefault(run.Id, Math.Min(100, Math.Max(1, 100 - (run.MaxRounds - run.Round))));
        if (!renewalRequests.TryGetValue(run.Id, out var input) || input.Instruction != instruction || input.AdditionalRounds != rounds || input.ExpectedRevision != run.Revision || input.ContinuousSupervision != supervise)
            renewalRequests[run.Id] = input = new(Guid.NewGuid().ToString(), run.Revision, rounds, instruction, supervise);
        await Store.RenewCoordination(run.Id, input);
        renewalRequests.Remove(run.Id); renewalInstructions.Remove(run.Id); renewalRounds.Remove(run.Id);
        renewalSupervision.Remove(run.Id);
        notice = "Coordination renewed. Existing assignments, sessions and receipts are retained.";
    });
    private Task SendFollowup(CoordinationRun run) => Execute(async () =>
    {
        if (!followupIds.TryGetValue(run.Id, out var id)) followupIds[run.Id] = id = Guid.NewGuid().ToString();
        await Store.PromptCoordination(run.Id, new(id, run.Revision, followups.GetValueOrDefault(run.Id, "")));
        followupIds.Remove(run.Id); followups.Remove(run.Id); notice = "Follow-up recorded for the coordinator.";
    });
    protected override async Task SnapshotChanged()
    {
        runs = await Store.Coordinations();
        var summaries = snapshot!.Commands.Where(x => x.Origin.StartsWith("coordinator:", StringComparison.Ordinal) &&
            (x.State == Delivery.Queued || Delivery.InFlight(x.State) || x.State == Delivery.Unknown))
            .OrderBy(x => x.QueueOrder).Take(100).ToArray();
        var missing = summaries.Where(x => !commandBodies.TryGetValue(x.Id, out var body) || body.UpdatedAt != x.UpdatedAt).Select(x => x.Id);
        foreach (var body in await Store.CommandBodies(missing)) commandBodies[body.Id] = body;
        foreach (var summary in snapshot.Commands)
        {
            if (!commandBodies.TryGetValue(summary.Id, out var body)) continue;
            ApplyCommandBody(summary, body);
        }
        hostOperationsIds = (await Store.ControlServices()).SelectMany(x => x.Sessions)
            .Where(x => x.ScopeKind == "HostOperations").Select(x => x.WorkerId).ToHashSet();
        if (hostOperationsIds.Contains(coordinatorId)) coordinatorId = "";
    }
    private Task LoadCommandBody(CommandRecord command) => Execute(async () =>
    {
        var body = await Store.Command(command.Id);
        commandBodies[body.Id] = body;
        var index = snapshot!.Commands.FindIndex(x => x.Id == body.Id);
        if (index >= 0) ApplyCommandBody(snapshot.Commands[index], body);
    });
    private void Select(string id, bool include) { if (include) selected.Add(id); else selected.Remove(id); }
    private Task Start() => Execute(async () =>
    {
        requestId ??= Guid.NewGuid().ToString();
        await Store.StartCoordination(new(requestId, coordinatorId, instruction, selected.Where(x => x != coordinatorId).Order().ToArray(), maxRounds, includeGuidance, includeGuidance && progressMinutes > 0 ? progressMinutes : null, continuousSupervision));
        requestId = null; notice = "Coordination started. Its conversation and delivery log remain available here.";
    });
    private Task Control(CoordinationRun run, string action) => Execute(async () => { await Store.ControlCoordination(run.Id, new(run.Revision, action)); });

    private sealed record ParticipantStatus(string WorkerId, string Name, string Runtime, string State, string StateLabel,
        string StatusLine, string ProgressAge, int QueuedBacklog, string? Outcome, string Assignment = "", string Receipt = "", string Next = "");

    private static string WaitingWords(bool permission, bool question) => permission && question ? "a tool approval and a task answer" : permission ? "a tool approval" : "a task answer";

    private IEnumerable<ParticipantStatus> ParticipantStatuses(CoordinationRun run)
    {
        if (snapshot is null) return [];
        var runCommands = snapshot.Commands.Where(x => x.Origin == "coordinator:" + run.Id).ToList();
        var ids = run.WorkerIdsJson is { Length: > 0 } json ? Json.Read<string[]>(json) : [];
        return ids.Select(id =>
        {
            var worker = snapshot.Workers.FirstOrDefault(x => x.Id == id);
            var commands = runCommands.Where(x => x.WorkerId == id).OrderBy(x => x.CreatedAt).ToList();
            var pending = snapshot.Requests.Where(x => x.WorkerId == id && x.State is "Pending" or "ReplyUnknown").ToList();
            var global = snapshot.Commands.Where(x => x.WorkerId == id).OrderBy(x => x.CreatedAt).ToList();
            var status = Describe(id, worker, commands, pending, global);
            return run.State is "Completed" or "Stopped" && status.State == "idle"
                ? status with { Next = "Next: no further routing; run " + run.State.ToLowerInvariant() + "." }
                : status;
        });
    }

    private ParticipantStatus Describe(string id, WorkerRecord? worker, List<CommandRecord> commands,
        List<PendingRequest> pending, List<CommandRecord> global)
    {
        var name = worker?.Name ?? "Unknown participant";
        var runtime = worker is null ? "" : RuntimeName(worker.RuntimeId);
        var uncertainReply = pending.FirstOrDefault(x => x.State == "ReplyUnknown");
        var waiting = pending.Where(x => x.Kind is "permission" or "question" && x.State == "Pending" && x.ReplyCommandId is null).ToList();
        var waitingPermission = waiting.Any(x => x.Kind == "permission");
        var waitingQuestion = waiting.Any(x => x.Kind == "question");
        var globalRunning = global.FirstOrDefault(x => x.State is Delivery.Dispatching or Delivery.Accepted or Delivery.Running);
        var runRunning = commands.FirstOrDefault(x => x.State is Delivery.Dispatching or Delivery.Accepted or Delivery.Running);
        var hasUnknown = global.Any(x => x.State == Delivery.Unknown);
        var queuedBacklog = global.Count(x => x.State == Delivery.Queued);
        var latest = commands.LastOrDefault();
        var latestGlobal = global.LastOrDefault();
        var latestFailedCancelled = latestGlobal?.State is Delivery.Failed or Delivery.Cancelled ? latestGlobal : null;
        var nativeOutcome = worker is { Outcome: Delivery.Failed or Delivery.Cancelled } w
            ? w.Outcome == Delivery.Failed ? "Failed" : "Cancelled"
            : "";
        var nativeBusy = worker is not null && worker.Activity is "Active" or "Retrying" or "WaitingPermission" or "WaitingQuestion";
        string state, stateLabel, line, assignment, receipt, next; long? ageAt;
        if (worker is null || worker.Stale || worker.LastObservedAt is null || worker.Activity == "MissingSession")
        {
            state = "stale"; stateLabel = "Stale"; line = "Session is unavailable or was never observed; command and result state are not trusted."; ageAt = worker?.LastObservedAt;
            assignment = commands.Count == 0 ? "No coordination instruction recorded." : "The recorded coordination instruction is not trusted until the session is observed.";
            receipt = worker?.LastObservedAt is null ? "never observed" : "last observed " + Age(worker.LastObservedAt);
            next = "Next: the session must become reachable and observed before assignment state is trusted.";
        }
        else if (waitingPermission || waitingQuestion)
        {
            state = "waiting";
            stateLabel = waitingPermission && waitingQuestion ? "Waiting on permission and a question" : waitingPermission ? "Waiting on permission" : "Waiting on a question";
            line = waitingPermission && waitingQuestion ? "A tool approval and a task answer are needed. Open the conversation to respond." : waitingPermission ? "A tool approval is needed. Open the conversation to review it." : "A task answer is needed. Open the conversation to respond.";
            ageAt = latest?.UpdatedAt ?? worker.LastObservedAt;
            assignment = "This session waits on " + WaitingWords(waitingPermission, waitingQuestion) + ".";
            var evidence = globalRunning ?? latestGlobal;
            receipt = evidence?.LastProgressAt is { } progressAt ? "latest progress " + Age(progressAt) : "last observed " + Age(worker.LastObservedAt);
            next = waitingPermission ? "Next: the owner must review the tool approval." : "Next: a task answer from the coordinator or owner.";
        }
        else if (hasUnknown || uncertainReply is not null)
        {
            state = "uncertain"; stateLabel = "Uncertain"; line = "A delivery has no confirmed outcome. Resolve it before assigning more work; another task may still be running."; ageAt = global.FirstOrDefault(x => x.State == Delivery.Unknown)?.UpdatedAt ?? latest?.UpdatedAt ?? worker.LastObservedAt;
            assignment = "The current delivery has no confirmed outcome; assignment state is not trusted yet.";
            receipt = "last receipt " + Age(ageAt);
            next = "Next: confirm the delivery outcome before assigning more work.";
        }
        else if (worker.Activity == "Retrying")
        {
            state = "retrying"; stateLabel = "Provider retry";
            line = "The native provider reports a retry; the instruction remains outstanding. This is not new work progress.";
            ageAt = worker.LastObservedAt;
            assignment = runRunning is null ? "The session is retrying other work." : "The current coordination instruction is waiting for the provider.";
            var evidence = runRunning ?? globalRunning;
            receipt = evidence?.LastProgressAt is { } progressAt ? "latest progress " + Age(progressAt) : "last observed " + Age(worker.LastObservedAt);
            next = "Next: provider recovery or owner intervention; do not resend the running instruction.";
        }
        else if (runRunning is { } running)
        {
            state = "active"; stateLabel = "Active"; var progress = Clipped(running.ProgressText, 100); line = StateWord(running.State) + (progress.Length == 0 ? " with no progress text yet." : ": \"" + progress + "\"") + QueuedSuffix(queuedBacklog); ageAt = running.LastProgressAt ?? running.UpdatedAt;
            assignment = "Current coordination instruction " + StateWord(running.State).ToLowerInvariant() + "; recorded " + Age(running.CreatedAt) + ".";
            receipt = running.LastProgressAt is null ? "no progress text yet; last receipt " + Age(running.UpdatedAt) : "latest progress " + Age(running.LastProgressAt);
            next = ProgressNext(running);
        }
        else if (globalRunning is { } gRunning)
        {
            state = "active"; stateLabel = "Active"; var progress = Clipped(gRunning.ProgressText, 100); line = "Busy on another task; last " + StateWord(gRunning.State).ToLowerInvariant() + (progress.Length == 0 ? " with no progress text yet." : ": \"" + progress + "\"") + QueuedSuffix(queuedBacklog); ageAt = gRunning.LastProgressAt ?? gRunning.UpdatedAt;
            assignment = commands.Any(x => x.State == Delivery.Queued) ? "Busy on another task; this run has a queued instruction." : "Busy on another task; no instruction from this run is queued.";
            receipt = gRunning.LastProgressAt is null ? "no progress text yet; last receipt " + Age(gRunning.UpdatedAt) : "latest progress " + Age(gRunning.LastProgressAt);
            next = ProgressNext(gRunning);
        }
        else if (nativeBusy)
        {
            state = "active"; stateLabel = "Active"; line = "Native session reports " + worker!.Activity.ToLowerInvariant() + " without a dispatched run command recorded; not idle."; ageAt = worker.LastObservedAt;
            assignment = "Native activity is underway without a recorded coordination instruction.";
            receipt = "last observed " + Age(worker.LastObservedAt);
            next = "Next: await the native session's next observed report.";
        }
        else if (queuedBacklog > 0)
        {
            state = "queued"; stateLabel = "Queued"; line = queuedBacklog == 1 ? "One instruction or operation is recorded and queued; not dispatched to the runtime yet." : queuedBacklog + " instructions or operations are recorded and queued; not dispatched to the runtime yet."; ageAt = global.Where(x => x.State == Delivery.Queued).Min(x => x.CreatedAt);
            assignment = queuedBacklog + " queued instruction(s) or operation(s) across all work; " + commands.Count(x => x.State == Delivery.Queued) + " belong to this run.";
            receipt = "queued " + Age(global.Where(x => x.State == Delivery.Queued).Max(x => x.CreatedAt));
            next = "Next: dispatch to the runtime when a slot frees.";
        }
        else if (latestFailedCancelled is { } outcome || nativeOutcome.Length > 0 || worker!.Activity == "Unknown")
        {
            if (latestFailedCancelled is { } o)
            {
                state = "outcome"; stateLabel = o.State == Delivery.Failed ? "Failed" : "Cancelled"; line = "The latest dispatched instruction " + o.State.ToLowerInvariant() + "; a separate fact from availability, not verified completion."; ageAt = o.UpdatedAt;
                assignment = "The latest coordination instruction ended " + o.State.ToLowerInvariant() + ".";
                receipt = "receipt " + Age(o.UpdatedAt);
                next = "Next: review the " + o.State.ToLowerInvariant() + " result; no further work is scheduled for this participant.";
            }
            else if (nativeOutcome.Length > 0)
            {
                state = "outcome"; stateLabel = nativeOutcome; line = "The native turn " + nativeOutcome.ToLowerInvariant() + "; a separate fact from availability, not verified completion."; ageAt = latest?.UpdatedAt ?? worker.LastObservedAt;
                assignment = "The native turn ended " + nativeOutcome.ToLowerInvariant() + "; the coordination instruction state is unverified.";
                receipt = "last receipt " + Age(ageAt);
                next = "Next: review the " + nativeOutcome.ToLowerInvariant() + " turn; no further work is scheduled for this participant.";
            }
            else
            {
                state = "uncertain"; stateLabel = "Uncertain activity"; line = "Native status is unknown; the participant is not treated as idle or available."; ageAt = worker.LastObservedAt;
                assignment = "No reliable current coordination assignment; native status is unknown.";
                receipt = "last observed " + Age(worker.LastObservedAt);
                next = "Next: confirm native status in the conversation before assigning work.";
            }
        }
        else
        {
            state = "idle"; stateLabel = "Idle";
            line = "Native reports no active task or pending work; idle means available, not that the assigned work is verified complete.";
            ageAt = latest?.UpdatedAt ?? worker.LastObservedAt;
            assignment = latest is null ? "No coordination instruction recorded for this participant." : "No current coordination instruction; last recorded " + latest.Kind.ToLowerInvariant() + " " + Age(latest.UpdatedAt) + ".";
            receipt = latest?.UpdatedAt is { } updated ? "last receipt " + Age(updated) : "last observed " + Age(worker!.LastObservedAt);
            next = "Next: ready for the coordinator's next dispatch when the run decides.";
        }
        if (line.Length > 160) line = line[..160] + "…";
        var outcomeChip = (latestFailedCancelled?.State ?? nativeOutcome) is { } chip && chip is Delivery.Failed or Delivery.Cancelled && state is not ("stale" or "outcome") ? chip : null;
        var currentPrompt = commands.FirstOrDefault(x => x.Kind == "Prompt" && (Delivery.InFlight(x.State) || x.State == Delivery.Queued));
        if (state is not ("stale" or "uncertain") && currentPrompt is not null)
        {
            var summary = PromptSummary(currentPrompt);
            if (summary.Length > 0) assignment = summary;
        }
        return new(id, name, runtime, state, stateLabel, line, Age(ageAt), queuedBacklog, outcomeChip, assignment, receipt, next);
    }

    private static string PromptSummary(CommandRecord command)
    {
        try { return Clipped(Json.Read<PromptInput>(command.Payload).Text, 160); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ControlException) { return ""; }
    }

    private static string ProgressNext(CommandRecord command)
    {
        var requested = AssignmentGuidance.ProgressStatus(command);
        return requested.Length == 0 ? "Next: await a progress update or completion of the running turn." : "Next: " + requested;
    }

    private static string QueuedSuffix(int queued) => queued > 0 ? " · " + queued + " queued" : "";

    private string Aggregate(CoordinationRun run)
    {
        var statuses = ParticipantStatuses(run).ToList();
        var parts = new List<string>
        {
            "Busy " + statuses.Count(x => x.State is "active" or "retrying" or "waiting" or "uncertain"),
            "Available " + statuses.Count(x => x.State == "idle"),
            "Queued " + statuses.Count(x => x.State == "queued" || x.QueuedBacklog > 0)
        };
        var stale = statuses.Count(x => x.State == "stale");
        if (stale > 0) parts.Add("Stale " + stale);
        var failed = statuses.Count(x => x.State == "outcome" || x.Outcome is not null);
        if (failed > 0) parts.Add("Failed/Cancelled " + failed);
        return string.Join(" · ", parts);
    }

    private string RunLatestEvidence(CoordinationRun run)
    {
        if (snapshot is null) return "not available";
        var commands = snapshot.Commands.Where(x => x.Origin == "coordinator:" + run.Id || x.Origin == "coordinator-decision:" + run.Id).ToList();
        long? progressAt = commands.Where(x => x.LastProgressAt is not null).Max(x => x.LastProgressAt);
        long? updateAt = commands.Count > 0 ? commands.Max(x => x.UpdatedAt) : null;
        long? dispatchAt = run.LastDecisionAt > 0 ? run.LastDecisionAt : null;
        long? newest = new long?[] { progressAt, updateAt, dispatchAt }.Where(x => x is not null).Max();
        if (newest is null) return "started " + Age(run.CreatedAt);
        var label = newest == progressAt ? "progress" : updateAt is not null && newest == updateAt ? "receipt" : "activity";
        return label + " " + Age(newest);
    }

    private string RunNextEvent(CoordinationRun run)
    {
        if (run.State is "Completed" or "Stopped") return "Next: no further events; run " + run.State.ToLowerInvariant() + ".";
        if (run.State == "Paused") return "Next: no routing decisions until resumed; already dispatched instructions continue independently.";
        if (run.State == "Recovering") return "Next: automatic coordinator retry after backoff; worker monitoring continues.";
        var waiting = ParticipantStatuses(run).Count(x => x.State == "waiting");
        var needsOwner = waiting > 0 ? " A participant needs a tool approval or task answer; inspect its conversation." : "";
        return run.State switch
        {
            "Deciding" => "Next: the coordinator's decision response." + needsOwner,
            "Waiting" => "Next: a participant result, question or progress update." + needsOwner,
            "Ready" => "Next: request a decision when the coordinator is available." + needsOwner,
            _ => "Next: awaiting the next observed decision or participant result." + needsOwner
        };
    }

    private string? LastCoordinatorSummary(CoordinationRun run)
    {
        try
        {
            var summary = Json.Read<CoordinatorDecision>(run.DecisionJson).Summary;
            return string.IsNullOrWhiteSpace(summary) ? null : Clipped(summary, 600);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ControlException)
        { return null; }
    }

    private string? SchedulerWaitReason(CoordinationRun run)
    {
        if (snapshot is null || run.State is "Completed" or "Stopped") return null;
        if (run.State == "Paused") return PauseReason(run);
        if (run.State == "Recovering") return "automatic recovery backoff is active; no new decision is sent until its retry is due.";
        if (!run.ContinuousSupervision && run.Round >= run.MaxRounds && run.DecisionCommandId is null) return "the configured coordinator turn budget is exhausted; no new decision is sent.";

        if (run.DecisionCommandId is { } decisionId)
        {
            var decision = snapshot.Commands.FirstOrDefault(x => x.Id == decisionId);
            if (decision is null)
                return "the recorded coordinator decision is not in this bounded snapshot; its completion is unavailable here.";
            return decision.State == Delivery.Finished
                ? "the recorded decision is finished and awaits scheduler reconciliation; its actions are not yet confirmed."
                : "a coordinator decision is already " + StateWord(decision.State).ToLowerInvariant() + "; awaiting scheduler reconciliation.";
        }

        var coordinator = snapshot.Workers.FirstOrDefault(x => x.Id == run.CoordinatorWorkerId);
        if (coordinator is null || coordinator.Archived || coordinator.Role != SessionRoles.Coordinator)
            return "the coordinator is unavailable; no new decision is sent.";
        if (coordinator.Stale) return "the coordinator session is stale; no new decision is sent until it is observed.";

        var coordinatorBusy = coordinator.Activity != "Idle" || snapshot.Commands.Any(x => x.WorkerId == coordinator.Id &&
            (x.State == Delivery.Queued || Delivery.InFlight(x.State)));
        if (coordinatorBusy) return "the coordinator is busy; no new decision is sent until it is idle.";

        var participantIds = run.WorkerIdsJson is { Length: > 0 } json ? Json.Read<string[]>(json) : [];
        var participants = snapshot.Workers.Where(x => participantIds.Contains(x.Id)).ToArray();
        if (participants.Length != participantIds.Length || participants.Any(x => x.Archived || x.Role != SessionRoles.Worker))
            return "a participant is unavailable; no new decision is sent.";

        var context = ReadContext(run);
        var ownerFollowup = context is not null && context.Instruction != run.Instruction;
        var commands = snapshot.Commands.Where(x => x.Origin == "coordinator:" + run.Id).OrderBy(x => x.CreatedAt).ToArray();
        var requests = snapshot.Requests.Where(x => participantIds.Contains(x.WorkerId) && x.State == "Pending" && x.ReplyCommandId is null).ToArray();
        if (commands.Any(x => x.State == Delivery.Unknown))
            return "a worker delivery is uncertain; reconcile it before further coordination.";
        var progressDue = commands.Any(x => x.LastProgressAt > run.LastDecisionAt) &&
            ControlStore.Now - run.LastDecisionAt >= (run.ProgressMinutes ?? 1) * 60000L;
        var completedSinceDecision = commands.Any(x => x.UpdatedAt > run.LastDecisionAt &&
            x.State is Delivery.Finished or Delivery.Failed or Delivery.Cancelled);
        if (!ownerFollowup && context?.Repair is null && context?.Recovery is null &&
            commands.Any(x => x.State == Delivery.Queued || Delivery.InFlight(x.State)) &&
            requests.All(x => x.Kind != "question") && !progressDue && !completedSinceDecision)
            return "assigned worker work is still unresolved; no new decision is sent.";

        if (CoordinationObservation.Fingerprint(commands, requests) == run.LastObservation)
            return "worker evidence is unchanged since the last coordinator observation; no new decision is sent.";
        return null;
    }

    private static string PauseReason(CoordinationRun run)
    {
        var context = ReadContext(run);
        if (context?.Repair is not null || run.Detail.Contains("format", StringComparison.OrdinalIgnoreCase))
            return "the coordinator output could not be used; no new decision is sent.";
        if (run.Detail.Contains("turn limit", StringComparison.OrdinalIgnoreCase) || run.Detail.Contains("round limit", StringComparison.OrdinalIgnoreCase))
            return "the configured coordinator turn budget is exhausted; no new decision is sent.";
        if (run.Detail.Contains("delivery", StringComparison.OrdinalIgnoreCase) || run.Detail.Contains("uncertain", StringComparison.OrdinalIgnoreCase))
            return "a delivery outcome needs review; no new decision is sent.";
        if (run.Detail.Contains("participant", StringComparison.OrdinalIgnoreCase))
            return "a participant is unavailable; no new decision is sent.";
        return run.Detail.StartsWith("Coordination paused.", StringComparison.Ordinal)
            ? "owner pause is active; no new decision is sent until the run is resumed."
            : "coordination is paused; inspect the current detail before resuming.";
    }

    private static CoordinatorContext? ReadContext(CoordinationRun run)
    {
        if (run.InputJson == "{}") return null;
        try { return Json.Read<CoordinatorContext>(run.InputJson); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ControlException)
        { return null; }
    }

    private static string StateWord(string state) => state switch
    {
        Delivery.Dispatching => "Dispatching",
        Delivery.Accepted => "Accepted and running",
        Delivery.Running => "Running",
        _ => state
    };

    private static string Clipped(string text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var cleaned = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length <= limit ? cleaned : cleaned[..limit] + "…";
    }
}
