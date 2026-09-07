using System.Security.Cryptography;
using System.Text;

namespace HVO.AgentControl.Core;

public static class CoordinationObservation
{
    public static string Fingerprint(IEnumerable<CommandRecord> commands, IEnumerable<PendingRequest> requests) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(new
        {
            commands = commands.Select(x => new { x.Id, x.State, x.ProgressText }),
            questions = requests.Select(x => new { x.Id, x.State })
        }))));
}
