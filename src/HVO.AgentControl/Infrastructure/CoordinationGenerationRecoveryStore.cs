using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    private async Task<(CoordinatorGenerationRecovery? Recovery, string? Hold)> RequestGenerationRecovery(
        ControlDb db, CoordinationRun run, CommandRecord command, CoordinatorDecisionCheckpoint checkpoint,
        string phase, long phaseElapsed, long totalElapsed)
    {
        if (!await DecisionFenceMatches(db, run, command, checkpoint))
            return (null, "Automatic recovery is held: the coordinator, session, runtime, or native caller fence changed.");
        if (!options.Value.EnableCoordinatorGenerationRecovery)
            return (null, "Automatic successor recovery is disabled until its pinned-native canary has passed.");
        var coordinator = await db.Workers.FindAsync(checkpoint.CoordinatorWorkerId);
        var runtime = await db.Runtimes.FindAsync(checkpoint.RuntimeId);
        var binding = await db.ControlSessions.SingleOrDefaultAsync(x => x.WorkerId == checkpoint.CoordinatorWorkerId && x.IsCurrent);
        var nativeObservation = await db.CoordinatorNativeObservations.FindAsync(command.Id);
        if (coordinator is null || runtime is null || binding is null || runtime.ConnectionKind != RuntimeConnections.ControlHttp)
            return (null, "Automatic recovery is held: the native transport cannot prove caller-attributed cancellation and process incarnation.");
        if (binding.ScopeKind != "Workgroup" || binding.State != "Ready" || binding.NativeSessionId.Length == 0 ||
            !runtime.DesiredConnected || runtime.Transport != "Connected" || runtime.Health != "Healthy")
            return (null, "Automatic recovery is held: the current workgroup control session is not healthy and ready.");
        var recoveryLimit = Math.Clamp(options.Value.CoordinatorGenerationRecoveryLimit, 0, 100);
        if (await db.ControlSessions.CountAsync(x => x.ScopeKind == binding.ScopeKind && x.ScopeId == binding.ScopeId &&
            x.GenerationReason == "DecisionBudgetRecovery") >= recoveryLimit)
            return (null, "Automatic recovery is held: the configured successor-generation limit is exhausted.");
        if (coordinator.HistoryGap || !ExactNativeObservation(nativeObservation, command, checkpoint) ||
            nativeObservation!.ObservedAt < Now - 15000 || coordinator.LastObservedAt is null ||
            coordinator.LastObservedAt < nativeObservation.ObservedAt || nativeObservation.ChildSessionCount != 0 ||
            coordinator.LastToolAt >= checkpoint.StartedAt ||
            await db.Requests.AnyAsync(x => x.WorkerId == coordinator.Id && (x.State == "Pending" || x.State == "ReplyUnknown")))
            return (null, "Automatic recovery is held: exact tool-free proposal ownership cannot be proven.");
        if (await db.Commands.AnyAsync(x => x.WorkerId == coordinator.Id && x.Id != command.Id &&
            (x.State == Delivery.Queued || x.State == Delivery.Dispatching || x.State == Delivery.Accepted ||
             x.State == Delivery.Running || x.State == Delivery.Unknown)))
            return (null, "Automatic recovery is held: another command shares the coordinator session.");
        if (await db.ControlSessions.AnyAsync(x => x.PredecessorId == binding.Id && x.State != Delivery.Failed && x.State != Delivery.Cancelled))
            return (null, "Automatic recovery is held: this generation already has a successor.");
        var service = await db.ControlServices.FindAsync(binding.ControlServiceId);
        if (service is null)
            return (null, "Automatic recovery is held: the control service identity is missing.");

        var intentId = Guid.NewGuid().ToString("N");
        var instructionHash = InstructionHash(run.Instruction);
        var measuredUsage = (await db.ModelUsage.AsNoTracking().Where(x => x.CommandId == command.Id).ToListAsync())
            .Select(x => new { Usage = x, EffectiveInput = SumKnown(x.InputTokens, x.CacheReadTokens, x.CacheWriteTokens) })
            .Where(x => x.EffectiveInput.HasValue).OrderByDescending(x => x.EffectiveInput).ThenByDescending(x => x.Usage.ObservedAt)
            .FirstOrDefault();
        var usage = measuredUsage?.Usage;
        var effectiveInput = measuredUsage?.EffectiveInput;
        var spendLimit = Math.Max(0, options.Value.CoordinatorGenerationRecoveryEffectiveInputTokenLimit);
        if (effectiveInput > spendLimit)
            return (null, "Automatic recovery is held: the configured effective-input spend limit is exhausted.");
        var successor = await QueueSuccessorGeneration(db, service, binding, coordinator, "DecisionBudgetRecovery",
            intentId, run.Id, command.Id, run.OwnerPolicyRevision, instructionHash);
        var recovery = new CoordinatorGenerationRecovery(intentId, "Provisioning", "DecisionBudgetExceeded", command.Id,
            binding.Id, binding.Generation, successor.Id, successor.Generation, successor.CreationCommandId,
            run.OwnerPolicyRevision, instructionHash, Now, phase, phaseElapsed, totalElapsed,
            usage?.NativeMessageId, effectiveInput);
        Event(db, "CoordinatorGenerationRecoveryRequested", runtime.Id, coordinator.Id, command.Id,
            new
            {
                run.Id,
                recovery.IntentId,
                recovery.SourceControlSessionId,
                recovery.SuccessorControlSessionId,
                recovery.CreationCommandId,
                recovery.Phase,
                recovery.PhaseElapsedMilliseconds,
                recovery.TotalElapsedMilliseconds,
                recovery.SourceUsageMessageId,
                recovery.EffectiveInputTokens
            }, generation: successor.Generation);
        return (recovery, null);
    }

    private async Task<bool> AdvanceGenerationRecovery(ControlDb db, CoordinationRun run, CoordinatorContext context)
    {
        var recovery = context.GenerationRecovery;
        if (recovery is null || recovery.State is "Cutover" or "ReplacementQueued" or "OriginalCompleted" or "Held") return false;
        var sourceCommand = await db.Commands.FindAsync(recovery.SourceDecisionCommandId);
        if (sourceCommand?.State == Delivery.Finished) return false;
        var successor = await db.ControlSessions.FindAsync(recovery.SuccessorControlSessionId);
        var creation = successor is null ? null : await db.Commands.FindAsync(successor.CreationCommandId);
        if (successor is null || creation is null)
            return HoldGenerationRecovery(db, run, context, recovery, "Successor generation metadata is missing.");
        if (creation.State is Delivery.Failed or Delivery.Cancelled)
            return HoldGenerationRecovery(db, run, context, recovery, "Successor generation creation did not complete; the original proposal remains isolated and auditable.");
        if (Now - recovery.RequestedAt >= BudgetMilliseconds(options.Value.CoordinatorGenerationRecoveryMinutes))
            return HoldGenerationRecovery(db, run, context, recovery, "The successor-generation recovery elapsed-time budget is exhausted.");
        if (successor.State != "Ready") return false;
        if (run.State is "Paused" or "Stopped" or "Completed" || run.OwnerPolicyRevision != recovery.OwnerPolicyRevision ||
            InstructionHash(run.Instruction) != recovery.InstructionHash || run.DecisionCommandId != recovery.SourceDecisionCommandId)
            return HoldGenerationRecovery(db, run, context, recovery, "Owner policy or proposal authority changed before generation cutover.");
        var checkpoint = context.DecisionCheckpoint;
        if (sourceCommand is null || checkpoint is null || !await GenerationCutoverFenceMatches(db, run, sourceCommand, checkpoint))
            return HoldGenerationRecovery(db, run, context, recovery, "The source decision fence changed before generation cutover.");
        var sourceBinding = await db.ControlSessions.FindAsync(recovery.SourceControlSessionId);
        var source = sourceBinding is null ? null : await db.Workers.FindAsync(sourceBinding.WorkerId);
        var sourceObservation = await db.CoordinatorNativeObservations.FindAsync(recovery.SourceDecisionCommandId);
        var target = await db.Workers.FindAsync(successor.WorkerId);
        var runtime = target is null ? null : await db.Runtimes.FindAsync(target.RuntimeId);
        if (sourceBinding is null || source is null || target is null || runtime is null || !sourceBinding.IsCurrent || successor.IsCurrent ||
            successor.PredecessorId != sourceBinding.Id || runtime.ConnectionKind != RuntimeConnections.ControlHttp ||
            !runtime.DesiredConnected || runtime.Transport != "Connected" || runtime.Health != "Healthy" ||
            target.Stale || target.Activity != "Idle" || target.LastObservedAt is null || target.LastObservedAt < Now - 15000)
            return false;
        var workerIds = new[] { source.Id, target.Id };
        if (await db.Requests.AnyAsync(x => workerIds.Contains(x.WorkerId) && (x.State == "Pending" || x.State == "ReplyUnknown")) ||
            await db.Commands.AnyAsync(x => x.WorkerId == target.Id && (x.State == Delivery.Queued || x.State == Delivery.Dispatching ||
                x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)) ||
            source.HistoryGap || !ExactNativeObservation(sourceObservation, sourceCommand, checkpoint) ||
            sourceObservation!.ObservedAt < Now - 15000 || source.LastObservedAt is null ||
            source.LastObservedAt < sourceObservation.ObservedAt || sourceObservation.ChildSessionCount != 0 ||
            source.LastToolAt >= checkpoint.StartedAt)
            return HoldGenerationRecovery(db, run, context, recovery, "Fresh quiescent tool-free generation ownership cannot be proven for cutover.");

        sourceBinding.IsCurrent = false;
        sourceBinding.Detail = "Predecessor generation retained with unfinished native delivery after fenced automatic authority retirement.";
        sourceBinding.Revision++;
        source.Archived = true;
        source.Revision++;
        await db.SaveChangesAsync();
        successor.IsCurrent = true;
        successor.Detail = "Current persistent control conversation is bound after fenced decision-budget recovery.";
        successor.Revision++;
        target.Archived = false;
        target.Revision++;
        var cutover = recovery with { State = "Cutover", CutoverAt = Now };
        run.CoordinatorWorkerId = target.Id;
        run.DecisionCommandId = null;
        run.LastObservation = "";
        run.State = "Ready";
        run.Detail = "The unfinished proposal's routing authority was retired. Preparing one linked decision on the isolated successor generation.";
        run.InputJson = Json.Write(context with { DecisionCheckpoint = null, GenerationRecovery = cutover });
        run.Revision++;
        Event(db, "ControlDecisionAuthorityRetired", runtime.Id, source.Id, sourceCommand.Id,
            new
            {
                run.Id,
                recovery.IntentId,
                nativeDelivery = sourceCommand.State,
                actionDispatch = "None",
                predecessorId = sourceBinding.Id,
                successorId = successor.Id,
                successor.Generation
            });
        Event(db, "CoordinatorGenerationCutover", runtime.Id, target.Id, sourceCommand.Id,
            new
            {
                run.Id,
                recovery.IntentId,
                sourceWorkerId = source.Id,
                targetWorkerId = target.Id,
                predecessorId = sourceBinding.Id,
                successorId = successor.Id
            }, generation: successor.Generation);
        return true;
    }

    private static bool HoldGenerationRecovery(ControlDb db, CoordinationRun run, CoordinatorContext context,
        CoordinatorGenerationRecovery recovery, string detail)
    {
        var held = recovery with { State = "Held", Hold = detail };
        run.InputJson = Json.Write(context with { GenerationRecovery = held });
        run.Detail = "Automatic generation recovery is held: " + detail;
        run.Revision++;
        Event(db, "CoordinatorGenerationRecoveryHeld", commandId: recovery.SourceDecisionCommandId,
            payload: new { run.Id, recovery.IntentId, detail });
        return true;
    }

    private static async Task<bool> GenerationCutoverFenceMatches(ControlDb db, CoordinationRun run, CommandRecord command,
        CoordinatorDecisionCheckpoint checkpoint)
    {
        if (run.DecisionCommandId != checkpoint.CommandId || command.Id != checkpoint.CommandId ||
            command.WorkerId != checkpoint.CoordinatorWorkerId || checkpoint.NativeCallerId != command.NativeMessageId)
            return false;
        var coordinator = await db.Workers.FindAsync(checkpoint.CoordinatorWorkerId);
        if (coordinator is null || coordinator.SettingsRevision != checkpoint.CoordinatorSettingsRevision ||
            coordinator.RuntimeId != checkpoint.RuntimeId || coordinator.NativeSessionId != checkpoint.NativeSessionId ||
            coordinator.Directory != checkpoint.Directory)
            return false;
        var binding = await db.ControlSessions.SingleOrDefaultAsync(x => x.WorkerId == checkpoint.CoordinatorWorkerId && x.IsCurrent);
        if (binding?.Id != checkpoint.ControlSessionId || binding?.Generation != checkpoint.ControlSessionGeneration) return false;
        var process = binding is null ? null : await db.ControlServices.Where(x => x.Id == binding.ControlServiceId)
            .Select(x => x.IncarnationId).SingleOrDefaultAsync();
        // A backend reconnect advances RuntimeRecord.Generation. Once creation was authorized,
        // the sidecar process incarnation and exact native session remain the effect fence.
        return process == checkpoint.ControlProcessIncarnation;
    }

    private async Task<CoordinatorContext> CompleteGenerationRecoveryFromOriginal(ControlDb db, CoordinationRun run,
        CoordinatorContext context)
    {
        var recovery = context.GenerationRecovery;
        if (recovery is null || recovery.State is "OriginalCompleted" or "Cutover" or "ReplacementQueued") return context;
        var successor = await db.ControlSessions.FindAsync(recovery.SuccessorControlSessionId);
        var creation = successor is null ? null : await db.Commands.FindAsync(successor.CreationCommandId);
        if (successor is not null && creation is not null && creation.State == Delivery.Queued && successor.CreationAuthorizedAt is null)
        {
            creation.State = Delivery.Cancelled;
            creation.Detail = "The original proposal completed before successor creation was authorized; no native session was created.";
            creation.UpdatedAt = Now;
            successor.State = Delivery.Cancelled;
            successor.Detail = creation.Detail;
            successor.Revision++;
        }
        var completed = recovery with { State = "OriginalCompleted" };
        Event(db, "CoordinatorGenerationRecoverySuperseded", commandId: recovery.SourceDecisionCommandId,
            payload: new { run.Id, recovery.IntentId, creationState = creation?.State, successorState = successor?.State });
        return context with { GenerationRecovery = completed };
    }

    internal Task<bool> AuthorizeAutomaticControlSessionCreation(string commandId) => Write(async db =>
    {
        var command = await db.Commands.FindAsync(commandId) ?? throw new ControlException("Creation command not found.", 404);
        var successor = await db.ControlSessions.SingleAsync(x => x.CreationCommandId == commandId);
        if (successor.RecoveryIntentId is null) return true;
        var run = successor.RecoveryRunId is null ? null : await db.CoordinationRuns.FindAsync(successor.RecoveryRunId);
        var source = successor.RecoverySourceCommandId is null ? null : await db.Commands.FindAsync(successor.RecoverySourceCommandId);
        var sourceBinding = successor.PredecessorId is null ? null : await db.ControlSessions.FindAsync(successor.PredecessorId);
        var sourceWorker = sourceBinding is null ? null : await db.Workers.FindAsync(sourceBinding.WorkerId);
        var nativeObservation = source is null ? null : await db.CoordinatorNativeObservations.FindAsync(source.Id);
        var sourceCommandId = source?.Id;
        var context = run is null ? null : ReadRecoveryContext(run);
        var checkpoint = context?.DecisionCheckpoint;
        var recovery = context?.GenerationRecovery;
        var callerFresh = source is not null && checkpoint is not null && ExactNativeObservation(nativeObservation, source, checkpoint) &&
            nativeObservation!.ObservedAt >= Now - 15000 && sourceWorker?.LastObservedAt is not null &&
            sourceWorker.LastObservedAt >= nativeObservation.ObservedAt;
        var childrenClear = callerFresh && nativeObservation!.ChildSessionCount == 0;
        var requestsClear = sourceWorker is not null && !await db.Requests.AnyAsync(x => x.WorkerId == sourceWorker.Id &&
            (x.State == "Pending" || x.State == "ReplyUnknown"));
        var commandsClear = sourceWorker is not null && !await db.Commands.AnyAsync(x => x.WorkerId == sourceWorker.Id && x.Id != sourceCommandId &&
                (x.State == Delivery.Queued || x.State == Delivery.Dispatching || x.State == Delivery.Accepted ||
                 x.State == Delivery.Running || x.State == Delivery.Unknown));
        var sourceExclusive = sourceWorker is not null && checkpoint is not null && source is not null && !sourceWorker.HistoryGap &&
            callerFresh && childrenClear && (sourceWorker.LastToolAt is null || sourceWorker.LastToolAt < checkpoint.StartedAt) &&
            requestsClear && commandsClear;
        var activeAuthority = run is not null && source is not null && sourceBinding is not null && checkpoint is not null && recovery is not null &&
            command.State == Delivery.Dispatching && recovery.IntentId == successor.RecoveryIntentId && recovery.State == "Provisioning" &&
            Now - recovery.RequestedAt < BudgetMilliseconds(options.Value.CoordinatorGenerationRecoveryMinutes) &&
            source.State is (Delivery.Dispatching or Delivery.Accepted or Delivery.Running or Delivery.Unknown) &&
            run.State is ("Ready" or "Waiting" or "Deciding" or "Recovering") &&
            run.OwnerPolicyRevision == successor.RecoveryOwnerPolicyRevision && InstructionHash(run.Instruction) == successor.RecoveryInstructionHash &&
            run.DecisionCommandId == source.Id && sourceBinding.IsCurrent && sourceBinding.State == "Ready" &&
            sourceBinding.Generation < successor.Generation;
        var fenceMatches = activeAuthority && await GenerationCutoverFenceMatches(db, run!, source!, checkpoint!);
        var authorized = activeAuthority && sourceExclusive && fenceMatches;
        if (!authorized)
        {
            var reason = !activeAuthority ? "owner or proposal authority changed" : !callerFresh ? "fresh exact caller evidence is unavailable" :
                !childrenClear ? "fresh child-session evidence is unavailable" : sourceWorker?.HistoryGap != false ? "native history is incomplete" :
                !requestsClear ? "an interactive request is pending" : !commandsClear ? "another command shares the session" :
                sourceWorker?.LastToolAt >= checkpoint?.StartedAt ? "a tool effect was observed" : "native session or process fence changed";
            if (command.State is Delivery.Queued or Delivery.Dispatching)
            {
                command.State = Delivery.Cancelled;
                command.Detail = "Automatic successor creation was superseded before its native effect (" + reason + "); no session was created.";
                command.UpdatedAt = Now;
                successor.State = Delivery.Cancelled;
                successor.Detail = command.Detail;
                successor.Revision++;
            }
            Event(db, "CoordinatorGenerationCreationSuperseded", commandId: command.Id,
                payload: new { successor.RecoveryRunId, successor.RecoveryIntentId, successor.Id });
            return false;
        }
        if (successor.CreationAuthorizedAt is null)
        {
            successor.CreationAuthorizedAt = Now;
            successor.Revision++;
            Event(db, "CoordinatorGenerationCreationAuthorized", command.RuntimeId, commandId: command.Id,
                payload: new { successor.RecoveryRunId, successor.RecoveryIntentId, successor.Id }, generation: successor.Generation);
        }
        return true;
    });

    private static long? SumKnown(params long?[] values) => values.Any(x => x.HasValue) ? values.Sum(x => x ?? 0) : null;

    private static bool ExactNativeObservation(CoordinatorNativeObservation? observation, CommandRecord command,
        CoordinatorDecisionCheckpoint checkpoint) => observation is not null && observation.CommandId == command.Id &&
        observation.WorkerId == checkpoint.CoordinatorWorkerId && observation.RuntimeId == checkpoint.RuntimeId &&
        observation.NativeSessionId == checkpoint.NativeSessionId && observation.NativeCallerId == checkpoint.NativeCallerId &&
        observation.NativeCallerId == command.NativeMessageId;

    private static string InstructionHash(string instruction) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instruction))).ToLowerInvariant();
}
