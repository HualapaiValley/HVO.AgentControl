using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<OperatorUpdateSchedule> ConfigureOperatorUpdates(string runId, ConfigureOperatorUpdatesInput input, long? observedAt = null) => Write(async db =>
    {
        ValidateRequestId(input.Id);
        if (input.IntervalMinutes is < 1 or > 1440)
            throw new ControlException("Operator update interval must be 1-1440 minutes.", 400);
        var run = await db.CoordinationRuns.FindAsync(runId) ?? throw new ControlException("Coordination not found.", 404);
        if (await db.OperatorUpdateSchedules.FindAsync(input.Id) is { } prior)
        {
            if (prior.CoordinationRunId != runId || prior.IntervalMinutes != input.IntervalMinutes)
                throw new ControlException("Request ID already belongs to a different operator update schedule.");
            return prior;
        }
        if (await db.OperatorUpdateSchedules.AnyAsync(x => x.CoordinationRunId == runId))
            throw new ControlException("This coordination already has an operator update schedule.");

        var now = observedAt ?? Now;
        var intervalMs = checked(input.IntervalMinutes * 60_000L);
        var terminal = run.State is "Completed" or "Stopped";
        var schedule = new OperatorUpdateSchedule
        {
            Id = input.Id,
            CoordinationRunId = runId,
            IntervalMinutes = input.IntervalMinutes,
            Enabled = !terminal,
            NextDueAt = checked(now + intervalMs),
            CreatedAt = now,
            Revision = 1
        };
        db.OperatorUpdateSchedules.Add(schedule);
        var kind = terminal ? "Terminal" : "Baseline";
        await PublishOperatorUpdate(db, schedule, run, kind, now, now, missedIntervals: 0, input.Id + ":" + kind.ToLowerInvariant());
        return schedule;
    });

    public async Task<int> OperatorUpdateTick(long? observedAt = null)
    {
        var now = observedAt ?? Now;
        var shouldWrite = await Read(async db =>
        {
            var schedules = await db.OperatorUpdateSchedules.AsNoTracking().Where(x => x.Enabled).ToListAsync();
            var runIds = schedules.Select(x => x.CoordinationRunId).ToArray();
            var states = await db.CoordinationRuns.AsNoTracking().Where(x => runIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.State);
            return schedules.Any(x => x.NextDueAt <= now || !states.TryGetValue(x.CoordinationRunId, out var state) || state is "Completed" or "Stopped");
        });
        if (!shouldWrite) return 0;
        return await Write(async db =>
        {
            var schedules = await db.OperatorUpdateSchedules.Where(x => x.Enabled).OrderBy(x => x.NextDueAt).ToListAsync();
            var published = 0;
            foreach (var schedule in schedules)
            {
                var run = await db.CoordinationRuns.FindAsync(schedule.CoordinationRunId);
                if (run is null)
                {
                    schedule.Enabled = false;
                    schedule.Revision++;
                    continue;
                }
                if (run.State is "Completed" or "Stopped")
                {
                    var terminalId = schedule.Id + ":terminal";
                    if (!await db.OperatorStatusUpdates.AnyAsync(x => x.Id == terminalId))
                    {
                        await PublishOperatorUpdate(db, schedule, run, "Terminal", now, now, 0, terminalId);
                        published++;
                    }
                    schedule.Enabled = false;
                    schedule.Revision++;
                    continue;
                }
                if (now < schedule.NextDueAt) continue;

                var dueAt = schedule.NextDueAt;
                var intervalMs = checked(schedule.IntervalMinutes * 60_000L);
                var intervals = checked((now - dueAt) / intervalMs + 1);
                var kind = intervals == 1 ? "Scheduled" : "CatchUp";
                var dueId = schedule.Id + ":" + dueAt;
                if (!await db.OperatorStatusUpdates.AnyAsync(x => x.Id == dueId))
                {
                    await PublishOperatorUpdate(db, schedule, run, kind, dueAt, now, intervals - 1, dueId);
                    published++;
                }
                schedule.LastDueAt = dueAt;
                schedule.NextDueAt = checked(dueAt + intervals * intervalMs);
                schedule.Revision++;
            }
            return published;
        });
    }

    public Task<List<OperatorStatusUpdate>> OperatorUpdates(long after = 0, int take = 50) => Read(db =>
    {
        if (after < 0 || take is < 1 or > 100) throw new ControlException("Choose a non-negative cursor and a page size from 1-100.", 400);
        return db.OperatorStatusUpdates.AsNoTracking().Where(x => x.Sequence > after).OrderBy(x => x.Sequence).Take(take).ToListAsync();
    });

    public Task<OperatorStatusUpdate> AcknowledgeOperatorUpdate(string id, long? observedAt = null) => Write(async db =>
    {
        var update = await db.OperatorStatusUpdates.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new ControlException("Operator update not found.", 404);
        update.AcknowledgedAt ??= observedAt ?? Now;
        return update;
    });

    public Task<int> PublishOperatorMilestone(string runId, string milestoneKind, long? observedAt = null) =>
        Write(async db =>
        {
            var schedule = await db.OperatorUpdateSchedules.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CoordinationRunId == runId && x.Enabled);
            if (schedule is null) return 0;
            var run = await db.CoordinationRuns.FindAsync(runId);
            if (run is null) return 0;
            return await PublishMilestoneInternal(db, schedule, run, milestoneKind, observedAt ?? Now);
        });

    internal async Task<int> PublishMilestoneInternal(ControlDb db, OperatorUpdateSchedule schedule, CoordinationRun run, string milestoneKind, long now)
    {
        var transitionKey = run.Id + ":" + milestoneKind + ":" + now;
        var milestoneId = schedule.Id + ":milestone:" + transitionKey;

        if (await db.OperatorStatusUpdates.AnyAsync(x => x.Id == milestoneId)) return 0;

        var summary = await BuildOperatorSummary(db, run, now);
        db.OperatorStatusUpdates.Add(new OperatorStatusUpdate
        {
            Id = milestoneId,
            ScheduleId = schedule.Id,
            CoordinationRunId = run.Id,
            Kind = milestoneKind,
            DueAt = now,
            PublishedAt = now,
            SourceEventSequence = 0,
            ActiveSetRevision = schedule.ActiveSetRevision,
            MissedIntervals = 0,
            SummaryJson = Json.Write(summary)
        });
        Event(db, "OperatorStatusUpdatePublished", payload: new { scheduleId = schedule.Id, runId = run.Id, kind = milestoneKind, milestoneId });
        return 1;
    }

    public async Task<int> PruneAcknowledgedOperatorUpdates(int maxAcknowledgedPerSchedule = 1000, long? observedAt = null)
    {
        var now = observedAt ?? Now;
        return await Write(async db =>
        {
            var schedules = await db.OperatorUpdateSchedules.AsNoTracking().Select(x => x.Id).ToListAsync();
            var deleted = 0;
            foreach (var scheduleId in schedules)
            {
                var acknowledged = await db.OperatorStatusUpdates
                    .Where(x => x.ScheduleId == scheduleId && x.AcknowledgedAt != null)
                    .OrderByDescending(x => x.AcknowledgedAt)
                    .Skip(maxAcknowledgedPerSchedule)
                    .Select(x => x.Sequence)
                    .ToListAsync();
                if (acknowledged.Count > 0)
                    deleted += await db.OperatorStatusUpdates.Where(x => acknowledged.Contains(x.Sequence)).ExecuteDeleteAsync();
            }
            return deleted;
        });
    }

    private static async Task PublishOperatorUpdate(ControlDb db, OperatorUpdateSchedule schedule, CoordinationRun run,
        string kind, long dueAt, long publishedAt, long missedIntervals, string id)
    {
        var sourceSequence = await db.Events.OrderByDescending(x => x.Sequence).Select(x => x.Sequence).FirstOrDefaultAsync();
        var summary = await BuildOperatorSummary(db, run, publishedAt);
        db.OperatorStatusUpdates.Add(new OperatorStatusUpdate
        {
            Id = id,
            ScheduleId = schedule.Id,
            CoordinationRunId = run.Id,
            Kind = kind,
            DueAt = dueAt,
            PublishedAt = publishedAt,
            SourceEventSequence = sourceSequence,
            ActiveSetRevision = schedule.ActiveSetRevision,
            MissedIntervals = missedIntervals,
            SummaryJson = Json.Write(summary)
        });
        schedule.LastEmittedEventSequence = sourceSequence;
        Event(db, "OperatorStatusUpdatePublished", payload: new { scheduleId = schedule.Id, runId = run.Id, kind, dueAt, missedIntervals });
    }

    private static async Task<OperatorStatusSummary> BuildOperatorSummary(ControlDb db, CoordinationRun run, long generatedAt)
    {
        var ids = Json.Read<string[]>(run.WorkerIdsJson);
        var workers = await db.Workers.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var commands = await db.Commands.Where(x => x.WorkerId != null && ids.Contains(x.WorkerId))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync();
        var requests = await db.Requests.Where(x => ids.Contains(x.WorkerId) && (x.State == "Pending" || x.State == "ReplyUnknown")).ToListAsync();
        var summaries = ids.Select(id => SummarizeParticipant(run.Id, run.State, id, workers.GetValueOrDefault(id), commands, requests)).ToArray();
        return new(run.Id, run.State, generatedAt,
            summaries.Count(x => x.Phase is "Active" or "Waiting" or "Uncertain"),
            summaries.Count(x => x.Phase == "Available"),
            summaries.Count(x => x.Phase == "Queued"),
            summaries.Count(x => x.Phase is "Stale" or "Failed" or "Cancelled"), summaries);
    }

    private static OperatorParticipantSummary SummarizeParticipant(string runId, string runState, string id, WorkerRecord? worker,
        List<CommandRecord> commands, List<PendingRequest> requests)
    {
        var global = commands.Where(x => x.WorkerId == id).ToList();
        var runCommands = global.Where(x => x.Origin == "coordinator:" + runId).ToList();
        var pending = requests.Where(x => x.WorkerId == id).ToList();
        var permission = pending.Any(x => x.Kind == "permission" && x.State == "Pending" && x.ReplyCommandId is null);
        var question = pending.Any(x => x.Kind == "question" && x.State == "Pending" && x.ReplyCommandId is null);
        var uncertain = pending.Any(x => x.State == "ReplyUnknown") || global.Any(x => x.State == Delivery.Unknown);
        var runCurrent = runCommands.FirstOrDefault(x => x.State is Delivery.Dispatching or Delivery.Accepted or Delivery.Running)
            ?? runCommands.FirstOrDefault(x => x.State == Delivery.Queued);
        var globalCurrent = global.FirstOrDefault(x => x.State is Delivery.Dispatching or Delivery.Accepted or Delivery.Running)
            ?? global.FirstOrDefault(x => x.State == Delivery.Queued);
        var latestGlobal = global.LastOrDefault();
        var assignment = PromptSummary(runCurrent);
        var progressAt = runCommands.Where(x => x.LastProgressAt is not null).Select(x => x.LastProgressAt).Max();
        var receiptAt = runCommands.Select(x => new long?[] { x.LastProgressAt, x.AcceptedAt,
            x.State is Delivery.Finished or Delivery.Failed or Delivery.Cancelled ? x.UpdatedAt : null }.Max()).Max();
        string phase, blocker, next;

        if (worker is null || worker.Archived || worker.Stale || worker.LastObservedAt is null || worker.Activity == "MissingSession")
        { phase = "Stale"; blocker = "Session unavailable or not observed."; next = "Await a trusted session observation."; }
        else if (permission)
        { phase = "Waiting"; blocker = "Owner tool approval required."; next = question ? "Owner approval and a coordinator or owner answer." : "Owner approval in the worker conversation."; }
        else if (question)
        { phase = "Waiting"; blocker = "Task question pending."; next = "Coordinator or owner answer."; }
        else if (uncertain)
        { phase = "Uncertain"; blocker = "Delivery outcome is unconfirmed."; next = "Reconcile delivery before assigning more work."; }
        else if (runCurrent is { State: Delivery.Dispatching or Delivery.Accepted or Delivery.Running })
        { phase = "Active"; blocker = ""; next = "Progress update or completion receipt."; }
        else if (globalCurrent is { State: Delivery.Dispatching or Delivery.Accepted or Delivery.Running })
        { phase = "Active"; blocker = "Busy on work outside this run."; next = runCurrent?.State == Delivery.Queued ? "Other work completes, then queued run work dispatches." : "Observe completion before a run assignment."; }
        else if (globalCurrent?.State == Delivery.Queued)
        { phase = "Queued"; blocker = "Queued work is not dispatched."; next = "Dispatch when runtime capacity is available."; }
        else if (latestGlobal?.State is Delivery.Failed or Delivery.Cancelled)
        { phase = latestGlobal.State; blocker = "Explicit command outcome requires review."; next = "Review the recorded outcome."; }
        else if (worker.Activity == "Unknown")
        { phase = "Uncertain"; blocker = "Native activity is unknown."; next = "Await a trusted native observation."; }
        else if (worker.Activity is "Active" or "Retrying" or "WaitingPermission" or "WaitingQuestion")
        { phase = "Active"; blocker = "Native activity has no current run command receipt."; next = "Await the next native observation."; }
        else
        { phase = "Available"; blocker = ""; next = "No current run work; available for a future dispatch."; }

        if (runState is "Completed" or "Stopped")
            next = "Run " + runState.ToLowerInvariant() + "; no new run assignments. Already dispatched work continues independently. " +
                (phase is "Waiting" or "Uncertain" or "Stale" ? next : "");

        return new(id, worker?.Name ?? "Unknown participant", phase, assignment, worker?.LastObservedAt,
            progressAt, receiptAt, blocker.Length == 0 ? null : blocker, next);
    }

    private static string? PromptSummary(CommandRecord? command)
    {
        if (command?.Kind != "Prompt") return null;
        try
        {
            var text = Json.Read<PromptInput>(command.Payload).Text;
            if (string.IsNullOrWhiteSpace(text)) return null;
            var plain = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return plain.Length <= 160 ? plain : plain[..160] + "...";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        { return null; }
    }
}
