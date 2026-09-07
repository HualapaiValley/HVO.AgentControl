using HVO.AgentControl.Core;

namespace HVO.AgentControl.Components.Pages;

public partial class Coordination
{
    private List<CoordinationRun> runs = [];
    private readonly HashSet<string> selected = [];
    private string coordinatorId = "", instruction = "";
    private int maxRounds = 20;
    private bool includeGuidance = true;
    private int progressMinutes = 5;
    private string? requestId;
    private readonly Dictionary<string, string> followups = [];
    private readonly Dictionary<string, string> followupIds = [];
    private Task SendFollowup(CoordinationRun run) => Execute(async () =>
    {
        if (!followupIds.TryGetValue(run.Id, out var id)) followupIds[run.Id] = id = Guid.NewGuid().ToString();
        await Store.PromptCoordination(run.Id, new(id, run.Revision, followups.GetValueOrDefault(run.Id, "")));
        followupIds.Remove(run.Id); followups.Remove(run.Id); notice = "Follow-up recorded for the coordinator.";
    });
    protected override async Task SnapshotChanged() => runs = await Store.Coordinations();
    private void Select(string id, bool include) { if (include) selected.Add(id); else selected.Remove(id); }
    private Task Start() => Execute(async () =>
    {
        requestId ??= Guid.NewGuid().ToString();
        await Store.StartCoordination(new(requestId, coordinatorId, instruction, selected.Where(x => x != coordinatorId).Order().ToArray(), maxRounds, includeGuidance, includeGuidance && progressMinutes > 0 ? progressMinutes : null));
        requestId = null; notice = "Coordination started. Its conversation and delivery log remain available here.";
    });
    private Task Control(CoordinationRun run, string action) => Execute(async () => { await Store.ControlCoordination(run.Id, new(run.Revision, action)); });

    private sealed record ParticipantStatus(string WorkerId, string Name, string Runtime, string State, string StateLabel,
        string StatusLine, string ProgressAge, int QueuedBacklog, string? Outcome);

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
            return Describe(id, worker, commands, pending, global);
        });
    }

    private ParticipantStatus Describe(string id, WorkerRecord? worker, List<CommandRecord> commands,
        List<PendingRequest> pending, List<CommandRecord> global)
    {
        var name = worker?.Name ?? "Unknown participant";
        var runtime = worker is null ? "" : RuntimeName(worker.RuntimeId);
        var uncertainReply = pending.FirstOrDefault(x => x.State == "ReplyUnknown");
        var waiting = pending.FirstOrDefault(x => x.Kind is "permission" or "question" && x.State == "Pending" && x.ReplyCommandId is null);
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
        string state, stateLabel, line; long? ageAt;
        if (worker is null || worker.Stale || worker.LastObservedAt is null || worker.Activity == "MissingSession")
        { state = "stale"; stateLabel = "Stale"; line = "Session is unavailable or was never observed; command and result state are not trusted."; ageAt = worker?.LastObservedAt; }
        else if (waiting is { } waitingFor)
        { state = "waiting"; stateLabel = waitingFor.Kind == "permission" ? "Waiting on permission" : "Waiting on a question"; line = waitingFor.Kind == "permission" ? "Waiting on a tool-permission approval; this is not an unanswered task question." : "Waiting on an unanswered task question; this is not a tool-permission request."; ageAt = latest?.UpdatedAt ?? worker.LastObservedAt; }
        else if (runRunning is { } running)
        { state = "active"; stateLabel = "Active"; var progress = Clipped(running.ProgressText, 100); line = StateWord(running.State) + (progress.Length == 0 ? " with no progress text yet." : ": \"" + progress + "\"") + QueuedSuffix(queuedBacklog); ageAt = running.LastProgressAt ?? running.UpdatedAt; }
        else if (globalRunning is { } gRunning)
        { state = "active"; stateLabel = "Active"; var progress = Clipped(gRunning.ProgressText, 100); line = "Busy on another task; last " + StateWord(gRunning.State).ToLowerInvariant() + (progress.Length == 0 ? " with no progress text yet." : ": \"" + progress + "\"") + QueuedSuffix(queuedBacklog); ageAt = gRunning.LastProgressAt ?? gRunning.UpdatedAt; }
        else if (nativeBusy)
        { state = "active"; stateLabel = "Active"; line = "Native session reports " + worker!.Activity.ToLowerInvariant() + " without a dispatched run command recorded; not idle."; ageAt = worker.LastObservedAt; }
        else if (hasUnknown || uncertainReply is { } unknownQuestion)
        { state = "uncertain"; stateLabel = "Uncertain"; line = "Delivery has no confirmed outcome (unresolved delivery or question); resolve it before treating the work as done."; ageAt = (global.FirstOrDefault(x => x.State == Delivery.Unknown)?.UpdatedAt) ?? latest?.UpdatedAt ?? worker.LastObservedAt; }
        else if (queuedBacklog > 0)
        { state = "queued"; stateLabel = "Queued"; line = queuedBacklog == 1 ? "One instruction or operation is recorded and queued; not dispatched to the runtime yet." : queuedBacklog + " instructions or operations are recorded and queued; not dispatched to the runtime yet."; ageAt = global.Where(x => x.State == Delivery.Queued).Min(x => x.CreatedAt); }
        else if (latestFailedCancelled is { } outcome || nativeOutcome.Length > 0 || worker!.Activity == "Unknown")
        {
            if (latestFailedCancelled is { } o)
            { state = "outcome"; stateLabel = o.State == Delivery.Failed ? "Failed" : "Cancelled"; line = "The latest dispatched instruction " + o.State.ToLowerInvariant() + "; a separate fact from availability, not verified completion."; ageAt = o.UpdatedAt; }
            else if (nativeOutcome.Length > 0)
            { state = "outcome"; stateLabel = nativeOutcome; line = "The native turn " + nativeOutcome.ToLowerInvariant() + "; a separate fact from availability, not verified completion."; ageAt = latest?.UpdatedAt ?? worker.LastObservedAt; }
            else
            { state = "uncertain"; stateLabel = "Uncertain activity"; line = "Native status is unknown; the participant is not treated as idle or available."; ageAt = worker.LastObservedAt; }
        }
        else
        {
            state = "idle"; stateLabel = "Idle";
            line = "Native reports no active task or pending work; idle means available, not that the assigned work is verified complete.";
            ageAt = latest?.UpdatedAt ?? worker.LastObservedAt;
        }
        if (line.Length > 160) line = line[..160] + "…";
        var outcomeChip = (latestFailedCancelled?.State ?? nativeOutcome) is { } chip && chip is Delivery.Failed or Delivery.Cancelled && state is not ("stale" or "outcome") ? chip : null;
        return new(id, name, runtime, state, stateLabel, line, Age(ageAt), queuedBacklog, outcomeChip);
    }

    private static string QueuedSuffix(int queued) => queued > 0 ? " · " + queued + " queued ahead" : "";

    private string Aggregate(CoordinationRun run)
    {
        var statuses = ParticipantStatuses(run).ToList();
        var parts = new List<string>
        {
            "Busy " + statuses.Count(x => x.State is "active" or "waiting" or "uncertain"),
            "Available " + statuses.Count(x => x.State == "idle"),
            "Queued " + statuses.Count(x => x.State == "queued" || x.QueuedBacklog > 0)
        };
        var stale = statuses.Count(x => x.State == "stale");
        if (stale > 0) parts.Add("Stale " + stale);
        var failed = statuses.Count(x => x.State == "outcome" || x.Outcome is not null);
        if (failed > 0) parts.Add("Failed/Cancelled " + failed);
        return string.Join(" · ", parts);
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
