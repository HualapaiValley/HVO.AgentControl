using System.Security.Cryptography;
using System.Text;

namespace HVO.AgentControl.Core;

public static class CoordinationObservation
{
    // Progress prose changes frequently. A scheduling review is bounded by actual
    // task/capacity/policy changes, not by another token or native retry observation.
    public static string PlanningKey(string instruction, IEnumerable<string> availableWorkers,
        IEnumerable<CommandRecord> commands, IEnumerable<PendingRequest> requests, IEnumerable<CoordinatorGitHubAccess> github) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(new
        {
            instruction,
            availableWorkers = availableWorkers.Order(),
            commands = commands.OrderBy(x => x.Id).Select(x => new { x.Id, x.State }),
            requests = requests.OrderBy(x => x.Id).Select(x => new { x.Id, x.State }),
            github = GitHubKey(github)
        }))));

    public static string GitHubKey(IEnumerable<CoordinatorGitHubAccess> github) => Json.Write(github.OrderBy(x => x.RuntimeId)
        .Select(x => new { x.RuntimeId, x.CiInspectionState, x.ChecksPermission, x.CommitStatusesPermission, x.ActionsPermission }));

    public static string Fingerprint(IEnumerable<CommandRecord> commands, IEnumerable<PendingRequest> requests) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(new
        {
            commands = commands.Select(x => new { x.Id, x.State, x.ProgressText }),
            questions = requests.Select(x => new { x.Id, x.State })
        }))));
}
