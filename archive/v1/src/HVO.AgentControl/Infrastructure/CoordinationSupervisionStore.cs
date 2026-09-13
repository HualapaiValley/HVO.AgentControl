using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    // Record service liveness independently of model responses. The same hosted
    // timer invokes CoordinationTick to reassess work and viable capacity.
    public async Task<bool> CoordinationSupervisionTick(long? observedAt = null)
    {
        var now = observedAt ?? Now;
        var due = await Read(db => db.CoordinationRuns.AsNoTracking().AnyAsync(x =>
            x.State != "Completed" && x.State != "Stopped" && x.LastSupervisorAt <= now - 30000));
        if (!due) return false;
        return await Write(async db =>
        {
            var runs = await db.CoordinationRuns.Where(x =>
                x.State != "Completed" && x.State != "Stopped" && x.LastSupervisorAt <= now - 30000).ToListAsync();
            foreach (var run in runs) run.LastSupervisorAt = now;
            // An explicit pause never becomes an automatic resume.
            return runs.Count > 0;
        });
    }
}
