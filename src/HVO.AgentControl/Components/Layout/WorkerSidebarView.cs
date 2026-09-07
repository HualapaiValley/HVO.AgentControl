using HVO.AgentControl.Core;

namespace HVO.AgentControl.Components.Layout;

public sealed record WorkerSidebarGroup(string RuntimeId, string RuntimeName, IReadOnlyList<WorkerRecord> Workers);

public static class WorkerSidebarView
{
    public static IReadOnlyList<WorkerSidebarGroup> TaskWorkerGroups(ControlSnapshot snapshot, string search, string? selectedWorkerId)
    {
        var runtimes = snapshot.Runtimes.ToDictionary(x => x.Id);
        return snapshot.Workers
            .Where(x => x.Role == SessionRoles.Worker && (!x.Archived || x.Id == selectedWorkerId))
            .GroupBy(x => x.RuntimeId)
            .Select(group =>
            {
                var name = runtimes.GetValueOrDefault(group.Key)?.Name ?? "Unassigned runtime";
                var runtimeMatch = Matches(search, name);
                var workers = group.Where(x => runtimeMatch || Matches(search, x.Name, x.Project, x.Description, x.Id))
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                return new WorkerSidebarGroup(group.Key, name, workers);
            })
            .Where(x => x.Workers.Count > 0)
            .OrderBy(x => x.RuntimeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<WorkerRecord> Coordinators(ControlSnapshot snapshot, string search, string? selectedWorkerId) => snapshot.Workers
        .Where(x => x.Role == SessionRoles.Coordinator && (!x.Archived || x.Id == selectedWorkerId) && Matches(search, x.Name, x.Project, x.Description, x.Id))
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static string ConversationUrl(string id) => "/?worker=" + Uri.EscapeDataString(id);

    private static bool Matches(string search, params string[] values) => string.IsNullOrWhiteSpace(search) ||
        values.Any(x => x.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
}
