using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Core;

namespace HVO.AgentControl.Ssh;

public static class NativeProcessProbe
{
    public const string Provenance = "SshBootstrap";

    public static string ReadScript(RuntimeRecord runtime) => $$"""
        test "$(cat {{BootstrapScript.Quote(runtime.StateDirectory + "/owner")}})" = {{BootstrapScript.Quote(runtime.ManagedServerId + ":" + runtime.ApiPort)}} || { echo OWNERSHIP_CONFLICT; exit 33; }
        if test -r {{BootstrapScript.Quote(runtime.StateDirectory + "/native-process-current")}}; then
          printf 'OBSERVATION\t'; cat {{BootstrapScript.Quote(runtime.StateDirectory + "/native-process-current")}}
        else
          printf 'OBSERVATION\tUnknown\tUnknown\t\t\n'
        fi
        if test -r {{BootstrapScript.Quote(runtime.StateDirectory + "/native-process-replacements")}}; then
          while IFS= read -r replacement; do printf 'REPLACEMENT\t%s\n' "$replacement"; done < {{BootstrapScript.Quote(runtime.StateDirectory + "/native-process-replacements")}}
        fi
        """;

    public static NativeProcessObservation Parse(string managedServerId, string output, long observedAt)
    {
        try
        {
            var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var current = lines.Single(x => x.StartsWith("OBSERVATION\t", StringComparison.Ordinal)).Split('\t');
            if (current.Length != 5) throw new FormatException();
            var state = current[1];
            if (state is not (NativeProcessObservationState.Observed or NativeProcessObservationState.Unknown or
                NativeProcessObservationState.Unsupported or NativeProcessObservationState.Unavailable)) throw new FormatException();
            int? pid = null;
            if (current[3].Length > 0)
            {
                if (!int.TryParse(current[3], out var parsed) || parsed < 1) throw new FormatException();
                pid = parsed;
            }
            var incarnation = current[4];
            if (state == NativeProcessObservationState.Observed && (pid is null || !ValidMarker(incarnation))) throw new FormatException();
            if (state != NativeProcessObservationState.Observed && incarnation.Length > 0) throw new FormatException();

            var replacements = lines.Where(x => x.StartsWith("REPLACEMENT\t", StringComparison.Ordinal)).Select(line =>
            {
                var fields = line.Split('\t');
                if (fields.Length != 9 || !int.TryParse(fields[1], out var previousPid) || previousPid < 1 || !ValidMarker(fields[2]) ||
                    !int.TryParse(fields[3], out var currentPid) || currentPid < 1 || !ValidMarker(fields[4]) ||
                    previousPid == currentPid && fields[2] == fields[4] || fields[5] is not ("DeadPaneRespawn" or "IncarnationChanged") ||
                    fields[6] is not (NativeProcessExitEvidence.Unknown or NativeProcessExitEvidence.ExitStatus or NativeProcessExitEvidence.Signal) || fields[8] != "Unknown")
                    throw new FormatException();
                int? exitCode = null;
                if (fields[7].Length > 0)
                {
                    if (!int.TryParse(fields[7], out var parsed) || parsed is < 0 or > 255 ||
                        fields[6] == NativeProcessExitEvidence.Signal && parsed == 0) throw new FormatException();
                    exitCode = parsed;
                }
                if ((fields[6] == NativeProcessExitEvidence.Unknown) != (exitCode is null)) throw new FormatException();
                return new NativeProcessReplacementReceipt(ReceiptId(managedServerId, previousPid, fields[2], currentPid, fields[4]),
                    previousPid, fields[2], currentPid, fields[4], fields[5], fields[6], exitCode, fields[8], "Unknown");
            }).ToArray();
            return new(managedServerId, state, current[2], pid, incarnation, observedAt, Provenance,
                state == NativeProcessObservationState.Observed ? "Owned native process identity observed after bootstrap." :
                "Native process identity was not available; no process replacement is inferred.", replacements);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return new(managedServerId, NativeProcessObservationState.Unavailable, "Unknown", null, "", observedAt, Provenance,
                "Native process probe output was unavailable or invalid; no process replacement is inferred.", []);
        }
    }

    public static string ReceiptId(string managedServerId, int previousPid, string previous, int currentPid, string current) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{managedServerId}\n{previousPid}\n{previous}\n{currentPid}\n{current}")))).ToLowerInvariant();

    internal static bool ValidMarker(string value) => value.Length is > 0 and <= 200 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_' or '.');
}
