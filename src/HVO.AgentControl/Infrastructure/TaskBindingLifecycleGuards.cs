using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

internal static class TaskBindingLifecycleGuards
{
    public static async Task RequireRuntimeDeletionAllowed(ControlDb db, string runtimeId)
    {
        if (await db.WorkerSlots.AnyAsync(x => x.RuntimeId == runtimeId && !x.Archived) ||
            await db.TaskBindings.AnyAsync(x => x.RuntimeId == runtimeId && x.State == TaskBindingState.Active))
            throw new ControlException("Archive worker slots and release active task bindings before deleting this runtime; released binding history is preserved.", 409);
    }

    public static async Task RequireWorkerDeletionAllowed(ControlDb db, string workerId)
    {
        if (await db.TaskSessionBindings.AnyAsync(x => x.LegacyWorkerId == workerId && x.State == TaskSessionBindingState.Bound))
            throw new ControlException("Release the active task session binding before deleting this legacy worker.", 409);
    }
}
