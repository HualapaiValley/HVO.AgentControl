using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public static void ValidateModelOptions(List<ModelChoice> models, string provider, string model, string agent, string variant)
    {
        var choice = models.FirstOrDefault(x => x.ProviderId == provider && x.ModelId == model)
            ?? throw new ControlException("Refresh workspace models and choose an available model.", 400);
        if (agent.Length > 0 && choice.Agents?.Contains(agent) != true)
            throw new ControlException("Choose an available agent mode.", 400);
        if (variant.Length > 0 && choice.Variants?.Contains(variant) != true)
            throw new ControlException("Choose a reasoning variant supported by this model.", 400);
    }

    public Task<WorkerRecord> UpdateWorker(string id, UpdateWorkerInput input) => Write(async db =>
    {
        var worker = await db.Workers.FindAsync(id) ?? throw new ControlException("Worker not found.", 404);
        if (await db.ControlSessions.SingleOrDefaultAsync(x => x.WorkerId == id) is { } binding && input.Project != binding.ScopeId)
            throw new ControlException("A control session scope cannot be changed through worker settings.");
        var payload = Json.Write(input);
        if (await db.Commands.FindAsync(input.Id) is { } prior)
        { _ = Same(prior, worker.RuntimeId, id, "UpdateWorker", payload); return worker; }
        if (worker.SettingsRevision != input.ExpectedRevision) throw new ControlException("Worker settings changed; reopen the editor.");
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 120 || input.Project.Length > 120 || input.Description.Length > 4000)
            throw new ControlException("Enter a name (up to 120 characters), project (120), and description (4000).", 400);
        // Permit metadata edits even when a previously selected model has become unavailable.
        if (worker.ProviderId != input.ProviderId || worker.ModelId != input.ModelId || worker.Agent != input.Agent || worker.Variant != input.Variant)
            ValidateModelOptions(Json.Read<List<ModelChoice>>(worker.ModelsJson), input.ProviderId, input.ModelId, input.Agent, input.Variant);
        // Upgrade queued commands from releases that resolved defaults only at dispatch time.
        foreach (var queued in await db.Commands.Where(x => x.WorkerId == id && x.Kind == "Prompt" && x.ExecutionPayload == "").ToListAsync())
        {
            var prompt = Json.Read<PromptInput>(queued.Payload);
            queued.ExecutionPayload = Json.Write(prompt with
            {
                ProviderId = prompt.ProviderId ?? worker.ProviderId,
                ModelId = prompt.ModelId ?? worker.ModelId,
                Agent = prompt.Agent ?? worker.Agent,
                Variant = prompt.Variant ?? worker.Variant
            });
        }
        worker.Name = input.Name.Trim(); worker.Project = input.Project.Trim(); worker.Description = input.Description.Trim();
        worker.ProviderId = input.ProviderId; worker.ModelId = input.ModelId; worker.Agent = input.Agent; worker.Variant = input.Variant;
        worker.SettingsRevision++; worker.Revision++;
        var command = await Record(db, input.Id, worker.RuntimeId, id, "UpdateWorker", payload);
        command.State = Delivery.Finished; command.Detail = "Settings saved for future instructions; existing session and queued instructions preserved.";
        return worker;
    });

    public Task<WorkerRecord> ArchiveWorker(string id, WorkerArchiveInput input) => Write(async db =>
    {
        var worker = await db.Workers.FindAsync(id) ?? throw new ControlException("Worker not found.", 404);
        if (await db.ControlSessions.AnyAsync(x => x.WorkerId == id)) throw new ControlException("Host-owned control sessions retain their scope. Disconnect the control service to suspend it.");
        var payload = Json.Write(input);
        if (await db.Commands.FindAsync(input.Id) is { } prior)
        { _ = Same(prior, worker.RuntimeId, id, "ArchiveWorker", payload); return worker; }
        if (worker.SettingsRevision != input.ExpectedRevision) throw new ControlException("Worker settings changed; refresh before archiving or restoring.");
        if (input.Archived && !worker.Archived)
        {
            if (await db.CoordinationRuns.AnyAsync(x => x.CoordinatorWorkerId == id && x.State != "Completed" && x.State != "Stopped"))
                throw new ControlException("Stop the unfinished coordination before archiving its coordinator.");
            if (worker.Stale || worker.Activity != "Idle" || worker.LastObservedAt is null || worker.LastObservedAt < Now - 15000)
                throw new ControlException("Wait for a fresh idle observation before archiving. Active work is never stopped by archive.");
            if (await db.Commands.AnyAsync(x => x.WorkerId == id && (x.State == Delivery.Queued || x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)) ||
                await db.Requests.AnyAsync(x => x.WorkerId == id && (x.State == "Pending" || x.State == "ReplyUnknown")))
                throw new ControlException("Resolve queued work, uncertain delivery, and pending requests before archiving.");
        }
        worker.Archived = input.Archived; worker.SettingsRevision++; worker.Revision++;
        var command = await Record(db, input.Id, worker.RuntimeId, id, "ArchiveWorker", payload);
        command.State = Delivery.Finished; command.Detail = input.Archived ? "Archived; conversation and workspace ownership retained." : "Restored with the original conversation.";
        return worker;
    });
}
