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
}
