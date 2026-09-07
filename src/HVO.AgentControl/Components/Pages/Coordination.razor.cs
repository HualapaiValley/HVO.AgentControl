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

    private sealed record ParticipantStatus(string WorkerId, string Name, string Runtime, string State, string StateLabel, string StatusLine, string ProgressAge);

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
            return Describe(id, worker, commands, pending);
        });
    }

    private ParticipantStatus Describe(string id, WorkerRecord? worker, List<CommandRecord> commands, List<PendingRequest> pending)
    {
        var name = worker?.Name ?? "Unknown participant";
        var runtime = worker is null ? "" : RuntimeName(worker.RuntimeId);
        var uncertain = commands.FirstOrDefault(x => x.State == Delivery.Unknown);
        var uncertainReply = pending.FirstOrDefault(x => x.State == "ReplyUnknown");
        var waiting = pending.FirstOrDefault(x => x.Kind is "permission" or "question" && x.State == "Pending" && x.ReplyCommandId is null);
        var queued = commands.FirstOrDefault(x => x.State == Delivery.Queued);
        var inFlight = commands.FirstOrDefault(x => Delivery.InFlight(x.State));
        var latest = commands.LastOrDefault();
        string state, stateLabel, line; long? ageAt;
        if (worker is null || worker.Stale || worker.LastObservedAt is null || worker.Activity == "MissingSession")
        { state = "stale"; stateLabel = "Stale"; line = "Session is unavailable or was never observed; command and result state are not trusted."; ageAt = worker?.LastObservedAt; }
        else if (uncertain is { } unknownCommand || uncertainReply is { } unknownQuestion)
        { state = "uncertain"; stateLabel = "Uncertain"; line = "Delivery has no confirmed outcome (" + (uncertain?.State ?? "unresolved question") + "); resolve it before treating the work as done."; ageAt = uncertain?.UpdatedAt ?? latest?.UpdatedAt ?? worker.LastObservedAt; }
        else if (waiting is { } waitingFor)
        { state = "waiting"; stateLabel = "Waiting for approval"; line = waitingFor.Kind == "permission" ? "Waiting on a tool-permission approval." : "Waiting on an unanswered task question."; ageAt = latest?.UpdatedAt ?? worker.LastObservedAt; }
        else if (queued is { } queuedCommand)
        { state = "queued"; stateLabel = "Queued"; line = "Instruction is recorded and queued; it has not been dispatched to the runtime yet."; ageAt = queuedCommand.CreatedAt; }
        else if (inFlight is { } running)
        { state = "active"; stateLabel = "Active"; var progress = Clipped(running.ProgressText, 100); line = StateWord(running.State) + (progress.Length == 0 ? " with no progress text yet." : ": \"" + progress + "\""); ageAt = running.LastProgressAt ?? running.UpdatedAt; }
        else if (worker.Activity is "Active" or "Retrying")
        { state = "active"; stateLabel = "Active"; line = "Native session reports " + worker.Activity.ToLowerInvariant() + " without a run command recorded yet."; ageAt = worker.LastObservedAt; }
        else
        {
            state = "idle"; stateLabel = "Idle";
            line = latest?.State is Delivery.Failed or Delivery.Cancelled
                ? "Native reports idle but the last dispatched instruction " + latest.State.ToLowerInvariant() + "; review its conversation before treating the task as complete."
                : "Native reports no active task; this is not confirmation the assigned work completed.";
            ageAt = latest?.UpdatedAt ?? worker.LastObservedAt;
        }
        if (line.Length > 160) line = line[..160] + "…";
        return new(id, name, runtime, state, stateLabel, line, Age(ageAt));
    }

    private string Aggregate(CoordinationRun run)
    {
        var statuses = ParticipantStatuses(run).ToList();
        var parts = new List<string>
        {
            "Busy " + statuses.Count(x => x.State is "active" or "waiting" or "uncertain"),
            "Available " + statuses.Count(x => x.State == "idle"),
            "Queued " + statuses.Count(x => x.State == "queued")
        };
        var stale = statuses.Count(x => x.State == "stale");
        if (stale > 0) parts.Add("Stale " + stale);
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
