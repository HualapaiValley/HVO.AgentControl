using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    // This timer is independent of native model turns. No model call or owner renewal
    // is required to monitor a run or reopen its next configured budget window.
    public async Task<bool> CoordinationSupervisionTick(long? observedAt = null)
    {
        var now = observedAt ?? Now;
        var due = await Read(db => db.CoordinationRuns.AsNoTracking().AnyAsync(x =>
            x.State != "Completed" && x.State != "Stopped" &&
            (x.LastSupervisorAt <= now - 30000 || x.ContinuousSupervision && x.State != "Paused" && x.BudgetWindowEndsAt <= now)));
        if (!due) return false;
        return await Write(async db =>
        {
            var run = await db.CoordinationRuns.FirstOrDefaultAsync(x => x.State != "Completed" && x.State != "Stopped");
            if (run is null) return false;
            var changed = now - run.LastSupervisorAt >= 30000;
            if (changed) run.LastSupervisorAt = now;
            // An owner's pause is never an automatic recovery condition.
            if (!run.ContinuousSupervision || run.State == "Paused" || now < run.BudgetWindowEndsAt) return changed;
            if (run.Round > int.MaxValue - run.TurnsPerWindow)
            { PauseCoordination(run, "Coordinator lifetime turn counter needs maintenance; existing work remains recorded."); return true; }
            // Missed windows do not accumulate grants after downtime.
            run.MaxRounds = run.Round + run.TurnsPerWindow;
            run.BudgetWindowEndsAt = now + run.TurnWindowMinutes * 60000L;
            if (run.State == "WaitingBudget")
            {
                run.State = run.DecisionCommandId is null ? "Ready" : "Deciding";
                run.LastObservation = "";
                run.Detail = "The service opened the next coordinator budget window. Existing assignments and receipts are retained.";
            }
            run.Revision++;
            Event(db, "CoordinationBudgetWindowOpened", payload: new { run.Id, run.Round, run.MaxRounds, run.BudgetWindowEndsAt }, provenance: "service");
            return true;
        });
    }

    private static void AwaitCoordinationBudget(ControlDb db, Core.CoordinationRun run, string boundedDetail)
    {
        if (!run.ContinuousSupervision) { PauseCoordination(run, boundedDetail); return; }
        run.State = "WaitingBudget";
        run.Revision++;
        run.Detail = $"The service is monitoring workers. Coordinator model calls resume after {DateTimeOffset.FromUnixTimeMilliseconds(run.BudgetWindowEndsAt):u}; this window's {run.TurnsPerWindow}-turn allowance is used.";
        Event(db, "CoordinationBudgetWindowExhausted", payload: new { run.Id, run.Round, run.MaxRounds, run.BudgetWindowEndsAt }, provenance: "service");
    }
}
