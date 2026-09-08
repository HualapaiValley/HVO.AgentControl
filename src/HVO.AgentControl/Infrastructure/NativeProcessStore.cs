using HVO.AgentControl.Core;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    private const long NativeProcessFreshnessMilliseconds = 60000;
    internal const string NativeProcessInterruptedDetail = "A proven native-process replacement crossed this unresolved prompt. Delivery is uncertain; inspect retained artifacts and choose an explicit linked recovery. The original prompt was not replayed.";

    public Task<NativeProcessObservationResult> ObserveNativeProcess(string runtimeId, NativeProcessObservation observation) => Write<NativeProcessObservationResult>(async db =>
    {
        var runtime = await db.Runtimes.FindAsync(runtimeId) ?? throw new ControlException("Runtime not found.", 404);
        ValidateNativeProcessObservation(runtime, observation);
        var now = Now;
        var freshness = observation.ObservedAt <= now + 5000 && observation.ObservedAt >= now - NativeProcessFreshnessMilliseconds ? "Fresh" : "Stale";
        var priorEvent = await db.Events.AsNoTracking().Where(x => x.RuntimeId == runtimeId && x.Type == "NativeProcessObserved")
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync();
        NativeProcessObservationEvidence? prior = priorEvent is null ? null : Json.Read<NativeProcessObservationEvidence>(priorEvent.Payload);

        var evidence = new NativeProcessObservationEvidence(runtimeId, observation.ManagedServerId, observation.State, observation.Platform,
            observation.ProcessId, observation.Incarnation, observation.ObservedAt, observation.Provenance, observation.Detail, freshness);
        var observed = Event(db, "NativeProcessObserved", runtimeId, payload: evidence, provenance: observation.Provenance,
            nativeId: observation.Incarnation.Length > 0 ? observation.Incarnation : null);
        observed.ObservedAt = observation.ObservedAt;

        var unseen = new List<NativeProcessReplacementReceipt>();
        foreach (var replacement in observation.Replacements)
        {
            if (await db.Events.AnyAsync(x => x.RuntimeId == runtimeId && x.NativeId == replacement.Id &&
                (x.Type == "NativeProcessReplaced" || x.Type == "NativeProcessReplacementUnverified"))) continue;
            if (unseen.Any(x => x.Id == replacement.Id)) continue;
            unseen.Add(replacement);
        }
        if (unseen.Count == 0) return new(freshness, 0, 0);

        var marker = prior is { State: NativeProcessObservationState.Observed, Freshness: "Fresh" } ? prior.Incarnation : "";
        var processId = prior is { State: NativeProcessObservationState.Observed, Freshness: "Fresh" } ? prior.ProcessId : null;
        var causal = freshness == "Fresh" && observation.State == NativeProcessObservationState.Observed && marker.Length > 0 && processId is not null;
        foreach (var replacement in unseen)
        {
            if (!causal || replacement.PreviousIncarnation != marker || replacement.PreviousProcessId != processId) { causal = false; break; }
            marker = replacement.CurrentIncarnation;
            processId = replacement.CurrentProcessId;
        }
        causal = causal && marker == observation.Incarnation && processId == observation.ProcessId;
        if (!causal)
        {
            foreach (var replacement in unseen)
                Event(db, "NativeProcessReplacementUnverified", runtimeId, payload: new { replacement, freshness, reason = "Missing fresh causal incarnation chain" },
                    provenance: observation.Provenance, nativeId: replacement.Id);
            return new(freshness, 0, 0);
        }

        foreach (var replacement in unseen)
            Event(db, "NativeProcessReplaced", runtimeId, payload: replacement, provenance: observation.Provenance, nativeId: replacement.Id);

        var interrupted = 0;
        var commands = await db.Commands.Where(x => x.RuntimeId == runtimeId && x.Kind == "Prompt" &&
            (x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)).ToListAsync();
        foreach (var command in commands)
        {
            command.State = Delivery.Unknown;
            command.Detail = NativeProcessInterruptedDetail;
            command.UpdatedAt = now;
            var assignment = await db.Assignments.FindAsync(command.Id);
            var worker = command.WorkerId is null ? null : await db.Workers.FindAsync(command.WorkerId);
            var preserveRecordedOutcome = assignment is not null && IsRecordedTerminalOutcome(assignment.Outcome) && worker?.Outcome == assignment.Outcome;
            if (assignment is not null && !IsRecordedTerminalOutcome(assignment.Outcome)) assignment.Outcome = "Interrupted";
            if (worker is not null)
            {
                if (!preserveRecordedOutcome) worker.Outcome = "Interrupted";
                worker.CurrentAction = NativeProcessInterruptedDetail;
            }
            Event(db, "NativeProcessCommandInterrupted", runtimeId, command.WorkerId, command.Id,
                new { replacementReceiptId = unseen[^1].Id, nativeSessionId = worker?.NativeSessionId, command.NativeMessageId },
                provenance: observation.Provenance, nativeId: command.NativeMessageId);
            interrupted++;
        }
        return new(freshness, unseen.Count, interrupted);
    });

    public Task<List<NativeProcessObservationEvidence>> NativeProcessObservations(string runtimeId, int take = 20) => Read(async db =>
    {
        if (take is < 1 or > 100) throw new ControlException("Native process observation limit must be between 1 and 100.", 400);
        if (await db.Runtimes.FindAsync(runtimeId) is null) throw new ControlException("Runtime not found.", 404);
        var events = await db.Events.AsNoTracking().Where(x => x.RuntimeId == runtimeId && x.Type == "NativeProcessObserved")
            .OrderByDescending(x => x.Sequence).Take(take).ToListAsync();
        return events.Select(x => Json.Read<NativeProcessObservationEvidence>(x.Payload)).ToList();
    });

    private static void ValidateNativeProcessObservation(RuntimeRecord runtime, NativeProcessObservation observation)
    {
        if (observation.ManagedServerId != runtime.ManagedServerId) throw new ControlException("Native process observation belongs to a different managed server.", 409);
        if (observation.Provenance is not (NativeProcessProbe.Provenance or "TransportUnsupported" or "SyntheticTest")) throw new ControlException("Native process observation provenance is unsupported.", 400);
        if (observation.State is not (NativeProcessObservationState.Observed or NativeProcessObservationState.Unknown or
            NativeProcessObservationState.Unsupported or NativeProcessObservationState.Unavailable) || observation.ObservedAt < 1 ||
            observation.Platform.Length is < 1 or > 80 || observation.Platform.Any(char.IsControl) ||
            observation.Detail.Length > 1000 || observation.Detail.Any(char.IsControl) || observation.Replacements.Length > 32)
            throw new ControlException("Native process observation is invalid.", 400);
        if (observation.State == NativeProcessObservationState.Observed != (observation.ProcessId is > 0 && NativeProcessProbe.ValidMarker(observation.Incarnation)))
            throw new ControlException("Observed native processes require a PID and incarnation; unknown states cannot assert them.", 400);
        foreach (var replacement in observation.Replacements)
        {
            if (replacement.Id != NativeProcessProbe.ReceiptId(observation.ManagedServerId, replacement.PreviousProcessId, replacement.PreviousIncarnation,
                    replacement.CurrentProcessId, replacement.CurrentIncarnation) || replacement.PreviousProcessId < 1 || replacement.CurrentProcessId < 1 ||
                !NativeProcessProbe.ValidMarker(replacement.PreviousIncarnation) || !NativeProcessProbe.ValidMarker(replacement.CurrentIncarnation) ||
                replacement.PreviousProcessId == replacement.CurrentProcessId && replacement.PreviousIncarnation == replacement.CurrentIncarnation ||
                replacement.Reason is not ("DeadPaneRespawn" or "IncarnationChanged") ||
                replacement.OomEvidence != "Unknown" || replacement.EnvironmentRestartEvidence != "Unknown" ||
                replacement.ProcessExitEvidence is not (NativeProcessExitEvidence.Unknown or NativeProcessExitEvidence.ExitStatus or NativeProcessExitEvidence.Signal) ||
                (replacement.ProcessExitEvidence == NativeProcessExitEvidence.Unknown) != (replacement.ProcessExitCode is null) ||
                replacement.ProcessExitCode is < 0 or > 255 || replacement is { ProcessExitEvidence: NativeProcessExitEvidence.Signal, ProcessExitCode: 0 })
                throw new ControlException("Native process replacement evidence is invalid or overclaims its cause.", 400);
        }
    }

    internal static bool IsRecordedTerminalOutcome(string outcome) =>
        outcome is "ReportedComplete" or "VerifiedComplete" or "Blocked" or "Failed" or "Cancelled";
}
