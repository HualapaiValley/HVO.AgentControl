using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    private readonly Dictionary<string, int> activeTerminals = [];

    public async Task<RuntimeRecord> AcquireTerminalRuntime(string id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var runtime = await Read(async db => await db.Runtimes.FindAsync(id)) ?? throw new ControlException("Runtime not found.", 404);
            activeTerminals[id] = activeTerminals.GetValueOrDefault(id) + 1;
            return runtime;
        }
        finally { gate.Release(); }
    }
    public async Task ReleaseTerminalRuntime(string id)
    {
        await gate.WaitAsync();
        try { if (activeTerminals.GetValueOrDefault(id) <= 1) activeTerminals.Remove(id); else activeTerminals[id]--; }
        finally { gate.Release(); }
    }

    public Task<CommandRecord> DeleteRuntime(string id, DeleteRegistrationInput input) => Write(async db =>
    {
        var payload = Json.Write(input);
        if (await db.Commands.FindAsync(input.Id) is { } prior) return Same(prior, id, null, "DeleteRuntime", payload);
        var runtime = await db.Runtimes.FindAsync(id) ?? throw new ControlException("Runtime not found.", 404);
        if (runtime.Revision != input.ExpectedRevision) throw new ControlException("Runtime changed; refresh before deleting.");
        var count = await db.Workers.CountAsync(x => x.RuntimeId == id);
        if (count > 0) throw new ControlException($"Runtime is in use by {count} worker/coordinator registration(s), including archived workers. Delete those registrations first.");
        if (activeTerminals.GetValueOrDefault(id) > 0) throw new ControlException("Close this runtime's admin terminals before deleting it.");
        if (runtime.DesiredConnected || runtime.Transport != "Disconnected") throw new ControlException("Disconnect this runtime before deleting its registration.");
        if (await Unresolved(db, id, null) || await db.WorkspaceClaims.AnyAsync(x => x.RuntimeId == id))
            throw new ControlException("Resolve pending operations and workspace claims before deleting this runtime.");
        var command = await Record(db, input.Id, id, null, "DeleteRuntime", payload);
        command.State = Delivery.Finished; command.Detail = "Runtime registration deleted. Remote processes, files, credentials and audit records retained.";
        db.Runtimes.Remove(runtime);
        return command;
    });

    public Task<CommandRecord> DeleteWorker(string id, DeleteRegistrationInput input) => Write(async db =>
    {
        var payload = Json.Write(input);
        if (await db.Commands.FindAsync(input.Id) is { } prior)
        { if (prior.WorkerId != id) throw new ControlException("Request ID belongs to another worker."); return Same(prior, prior.RuntimeId, id, "DeleteWorker", payload); }
        var worker = await db.Workers.FindAsync(id) ?? throw new ControlException("Worker not found.", 404);
        if (worker.SettingsRevision != input.ExpectedRevision) throw new ControlException("Worker settings changed; refresh before deleting.");
        if (worker.Stale || worker.Activity != "Idle" || worker.LastObservedAt is null || worker.LastObservedAt < Now - 15000)
            throw new ControlException("A fresh idle observation is required. Reconnect and resolve active work before deleting.");
        if (await Unresolved(db, worker.RuntimeId, id) || await db.Requests.AnyAsync(x => x.WorkerId == id && (x.State == "Pending" || x.State == "ReplyUnknown")))
            throw new ControlException("Resolve queued work, uncertain delivery and pending requests before deleting.");
        var runs = await db.CoordinationRuns.Where(x => x.State != "Completed" && x.State != "Stopped").ToListAsync();
        if (runs.Any(x => x.CoordinatorWorkerId == id || Json.Read<string[]>(x.WorkerIdsJson).Contains(id)))
            throw new ControlException("This session belongs to an unfinished coordination. Stop or finish the run before deleting.");
        var command = await Record(db, input.Id, worker.RuntimeId, id, "DeleteWorker", payload);
        command.State = Delivery.Finished; command.Detail = "Worker registration deleted and workspace claim released. Remote files, native conversation and command audit retained.";
        await db.WorkspaceClaims.Where(x => x.WorkerId == id).ExecuteDeleteAsync();
        await db.Messages.Where(x => x.WorkerId == id).ExecuteDeleteAsync();
        await db.Assignments.Where(x => x.WorkerId == id).ExecuteDeleteAsync();
        await db.Requests.Where(x => x.WorkerId == id).ExecuteDeleteAsync();
        db.Workers.Remove(worker);
        return command;
    });

    public Task<CommandRecord> DismissCreation(string id) => Write(async db =>
    {
        var command = await db.Commands.FindAsync(id) ?? throw new ControlException("Setup not found.", 404);
        if (command.Kind != "CreateWorker" || command.State is not (Delivery.Failed or Delivery.Cancelled))
            throw new ControlException("Only failed or cancelled setup cards can be dismissed. Resolve uncertain delivery before removing its card.");
        command.Dismissed = true;
        await db.WorkspaceClaims.Where(x => x.CommandId == id && x.WorkerId == null).ExecuteDeleteAsync();
        Event(db, "WorkerSetupDismissed", command.RuntimeId, commandId: id, provenance: "user");
        return command;
    });

    private static Task<bool> Unresolved(ControlDb db, string runtimeId, string? workerId) => db.Commands.AnyAsync(x =>
        x.RuntimeId == runtimeId && (workerId == null || x.WorkerId == workerId) &&
        (x.State == Delivery.Queued || x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown));
}
