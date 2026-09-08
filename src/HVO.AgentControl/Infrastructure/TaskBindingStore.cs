using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<WorkerSlotPage> WorkerSlots(long after = 0, int take = 50, bool includeArchived = false) => Read(async db =>
    {
        ValidatePage(after, take);
        var rows = await db.WorkerSlots.AsNoTracking()
            .Where(x => x.Sequence > after && (includeArchived || !x.Archived))
            .OrderBy(x => x.Sequence).Take(take + 1).ToListAsync();
        return new WorkerSlotPage(rows.Take(take).ToList(), rows.Count > take ? rows[take - 1].Sequence : null);
    });

    public Task<WorkerSlotRecord> WorkerSlot(string id) => Read(async db =>
        await db.WorkerSlots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == BindingId(id))
        ?? throw new InventoryException("not_found", "Worker slot not found.", 404));

    public Task<WorkerSlotRecord> CreateWorkerSlot(CreateWorkerSlotInput input) => Write(async db =>
    {
        var id = BindingId(input.Id);
        var runtimeId = RuntimeId(input.RuntimeId);
        var name = BindingText(input.Name, "slot name", 120, true);
        var role = input.Role is SessionRoles.Worker or SessionRoles.Coordinator ? input.Role : throw Validation("Role must be Worker or Coordinator.");
        var provider = BindingText(input.ProviderId, "provider", 120);
        var model = BindingText(input.ModelId, "model", 240);
        return await MutateBinding(db, input.RequestId, "WorkerSlot", id, "Create", new { id, runtimeId, name, role, provider, model }, async () =>
        {
            var runtime = await db.Runtimes.FindAsync(runtimeId) ?? throw new InventoryException("not_found", "Runtime not found.", 404);
            if (await db.WorkerSlots.AnyAsync(x => x.Id == id)) throw Conflict("identity_exists", "Worker slot identity already exists.");
            if (await db.WorkerSlots.AnyAsync(x => x.RuntimeId == runtimeId && x.Name == name)) throw Conflict("identity_exists", "A worker slot with this name already exists on the runtime.");
            var environment = await db.RuntimeEnvironments.FindAsync(runtimeId);
            if (environment?.Kind == RuntimeEnvironmentKind.ManagedDevcontainer &&
                (await db.WorkerSlots.AnyAsync(x => x.RuntimeId == runtimeId && !x.Archived) || await db.Workers.AnyAsync(x => x.RuntimeId == runtimeId)))
                throw Conflict("worker_limit", "A managed devcontainer permits one reusable worker slot or legacy worker registration.");
            var slot = new WorkerSlotRecord
            {
                Id = id,
                RuntimeId = runtime.Id,
                Name = name,
                Role = role,
                ProviderId = provider,
                ModelId = model,
                CreatedAt = Now,
                UpdatedAt = Now
            };
            db.WorkerSlots.Add(slot);
            Event(db, "WorkerSlotCreated", runtime.Id, payload: new { slot.Id, slot.RuntimeId, slot.Role }, provenance: "user");
            return slot;
        });
    });

    public Task<TaskBindingPage> TaskBindings(long after = 0, int take = 50, bool includeReleased = false) => Read(async db =>
    {
        ValidatePage(after, take);
        var rows = await db.TaskBindings.AsNoTracking()
            .Where(x => x.Sequence > after && (includeReleased || x.State == TaskBindingState.Active))
            .OrderBy(x => x.Sequence).Take(take + 1).ToListAsync();
        var page = rows.Take(take).Select(x => LoadView(db, x)).ToArray();
        return new TaskBindingPage((await Task.WhenAll(page)).ToList(), rows.Count > take ? rows[take - 1].Sequence : null);
    });

    public Task<TaskBindingView> TaskBinding(string id) => Read(async db =>
    {
        var binding = await db.TaskBindings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == BindingId(id))
            ?? throw new InventoryException("not_found", "Task binding not found.", 404);
        return await LoadView(db, binding);
    });

    public Task<TaskBindingView> CreateTaskBinding(CreateTaskBindingInput input) => Write(async db =>
    {
        var id = BindingId(input.Id);
        var workItemId = BindingKey(input.WorkItemId, "work item ID", 200);
        var projectId = InventoryId(input.ProjectId);
        var slotId = BindingId(input.WorkerSlotId);
        var workspaceId = BindingId(input.WorkspaceId);
        var sessionId = BindingId(input.SessionBindingId);
        var directory = BindingText(input.Directory, "workspace directory", 2000, true);
        var branch = BindingText(input.Branch, "workspace branch", 240, true);
        var nativeSession = BindingText(input.NativeSessionId ?? "", "native session ID", 300);
        return await MutateBinding(db, input.RequestId, "TaskBinding", id, "Create",
            new
            {
                id,
                workItemId,
                projectId,
                slotId,
                workspaceId,
                sessionId,
                directory,
                branch,
                input.ExpectedWorkItemRevision,
                input.ExpectedProjectRevision,
                input.ExpectedSlotRevision,
                input.LegacyWorkerId,
                nativeSession
            }, async () =>
        {
            if (await db.TaskBindings.AnyAsync(x => x.Id == id)) throw Conflict("identity_exists", "Task binding identity already exists.");
            var workItem = await db.WorkItems.FindAsync(workItemId) ?? throw new InventoryException("not_found", "Work item not found.", 404);
            var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId) ?? throw new InventoryException("not_found", "Project not found.", 404);
            var slot = await db.WorkerSlots.SingleOrDefaultAsync(x => x.Id == slotId) ?? throw new InventoryException("not_found", "Worker slot not found.", 404);
            var runtime = await db.Runtimes.FindAsync(slot.RuntimeId) ?? throw new InventoryException("not_found", "Runtime not found.", 404);
            var environment = await db.RuntimeEnvironments.FindAsync(runtime.Id) ?? throw Conflict("runtime_environment_required", "Configure the runtime environment before creating a task binding.");
            if (project.Archived) throw Conflict("resource_archived", "The project is archived.");
            if (workItem.State is WorkItemState.Released or WorkItemState.Abandoned) throw Conflict("work_item_closed", "The work item is no longer active.");
            if (workItem.Revision != input.ExpectedWorkItemRevision || project.Revision != input.ExpectedProjectRevision || slot.Revision != input.ExpectedSlotRevision)
                throw Conflict("revision_conflict", "Work item, project or worker slot changed; refresh all three before binding.");
            if (!string.Equals(CanonicalProjectRepository(workItem.Repository), project.RepositoryUrl, StringComparison.Ordinal))
                throw Validation("Work item repository does not identify the selected project unambiguously.");
            if (workItem.Branch != branch) throw Validation("Workspace branch must match the work item branch.");
            if (slot.RuntimeId != runtime.Id || slot.Archived) throw Conflict("slot_unavailable", "Worker slot is not available on its runtime.");
            if (environment.Kind == RuntimeEnvironmentKind.ManagedDevcontainer && await db.WorkerSlots.CountAsync(x => x.RuntimeId == runtime.Id && !x.Archived) > 1)
                throw Conflict("worker_limit", "Managed devcontainers do not support multiple worker slots in this slice.");
            if (await db.TaskBindings.AnyAsync(x => x.WorkItemId == workItemId && x.State == TaskBindingState.Active))
                throw Conflict("task_in_use", "The work item already has an active task binding.");
            if (await db.TaskBindings.AnyAsync(x => x.WorkerSlotId == slotId && x.State == TaskBindingState.Active))
                throw Conflict("slot_in_use", "The worker slot already has an active task binding.");
            if (await db.TaskWorkspaces.AnyAsync(x => x.RuntimeId == runtime.Id && x.Directory == directory && x.State == TaskBindingState.Active))
                throw Conflict("workspace_in_use", "The workspace directory is already actively bound; use a fresh workspace for this task.");
            WorkerRecord? legacyWorker = null;
            if (input.LegacyWorkerId is not null || nativeSession.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(input.LegacyWorkerId) || nativeSession.Length == 0)
                    throw Validation("A legacy session binding requires both the legacy worker ID and exact native session ID.");
                legacyWorker = await db.Workers.FindAsync(input.LegacyWorkerId) ?? throw new InventoryException("not_found", "Legacy worker not found.", 404);
                if (legacyWorker.RuntimeId != runtime.Id || legacyWorker.NativeSessionId != nativeSession)
                    throw Conflict("legacy_session_mismatch", "The supplied native session is not the exact session recorded for the legacy worker.");
                if (await db.TaskSessionBindings.AnyAsync(x => x.NativeSessionId == nativeSession))
                    throw Conflict("session_in_use", "The native session is already explicitly bound to another task.");
            }
            var now = Now;
            var workspace = new TaskWorkspaceRecord
            {
                Id = workspaceId,
                RuntimeId = runtime.Id,
                WorkerSlotId = slot.Id,
                ProjectId = project.Id,
                WorkItemId = workItem.Id,
                Directory = directory,
                Branch = branch,
                CreatedAt = now,
                UpdatedAt = now
            };
            var session = new TaskSessionBindingRecord
            {
                Id = sessionId,
                TaskBindingId = id,
                WorkerSlotId = slot.Id,
                LegacyWorkerId = legacyWorker?.Id,
                NativeSessionId = nativeSession,
                Generation = 0,
                State = nativeSession.Length == 0 ? TaskSessionBindingState.Unbound : TaskSessionBindingState.Bound,
                CreatedAt = now,
                UpdatedAt = now
            };
            var binding = new TaskBindingRecord
            {
                Id = id,
                WorkItemId = workItem.Id,
                ProjectId = project.Id,
                RuntimeId = runtime.Id,
                WorkerSlotId = slot.Id,
                WorkspaceId = workspace.Id,
                SessionBindingId = session.Id,
                PlacementVerified = false,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.TaskWorkspaces.Add(workspace); db.TaskSessionBindings.Add(session); db.TaskBindings.Add(binding);
            Event(db, "TaskBindingCreated", runtime.Id, payload: new { binding.Id, binding.WorkItemId, binding.ProjectId, binding.WorkerSlotId, binding.WorkspaceId, binding.SessionBindingId, placementVerified = false }, provenance: "user");
            await db.SaveChangesAsync();
            return await LoadView(db, binding);
        });
    });

    public Task<TaskBindingView> ReleaseTaskBinding(string id, ReleaseTaskBindingInput input) => Write(async db =>
    {
        var bindingId = BindingId(id);
        return await MutateBinding(db, input.RequestId, "TaskBinding", bindingId, "Release", new { input.ExpectedRevision }, async () =>
        {
            var binding = await db.TaskBindings.SingleOrDefaultAsync(x => x.Id == bindingId) ?? throw new InventoryException("not_found", "Task binding not found.", 404);
            if (binding.Revision != input.ExpectedRevision) throw Conflict("revision_conflict", "Task binding changed; refresh before releasing it.");
            if (binding.State == TaskBindingState.Released) throw Conflict("already_released", "Task binding is already released.");
            var workspace = (await db.TaskWorkspaces.SingleOrDefaultAsync(x => x.Id == binding.WorkspaceId))!;
            var session = (await db.TaskSessionBindings.SingleOrDefaultAsync(x => x.Id == binding.SessionBindingId))!;
            binding.State = TaskBindingState.Released; binding.Revision++; binding.UpdatedAt = Now;
            workspace!.State = TaskBindingState.Released; workspace.Revision++; workspace.UpdatedAt = Now;
            session!.State = TaskSessionBindingState.Released; session.UpdatedAt = Now;
            Event(db, "TaskBindingReleased", binding.RuntimeId, payload: new { binding.Id }, provenance: "user");
            await db.SaveChangesAsync();
            return await LoadView(db, binding);
        });
    });

    private static async Task<TaskBindingView> LoadView(ControlDb db, TaskBindingRecord binding)
    {
        var workspace = await db.TaskWorkspaces.AsNoTracking().SingleAsync(x => x.Id == binding.WorkspaceId);
        var session = await db.TaskSessionBindings.AsNoTracking().SingleAsync(x => x.Id == binding.SessionBindingId);
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == binding.ProjectId);
        var slot = await db.WorkerSlots.AsNoTracking().SingleAsync(x => x.Id == binding.WorkerSlotId);
        return new(binding, workspace, session, project, slot);
    }

    private static async Task<T> MutateBinding<T>(ControlDb db, string requestId, string kind, string id, string action, object intent, Func<Task<T>> mutate)
    {
        var request = BindingId(requestId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(intent))));
        if (await db.InventoryMutations.FindAsync(request) is { } prior)
        {
            if (prior.ResourceKind != kind || prior.ResourceId != id || prior.Action != action || prior.RequestHash != hash)
                throw Conflict("idempotency_conflict", "This request ID belongs to a different binding mutation.");
            return Json.Read<T>(prior.ResultJson);
        }
        var result = await mutate();
        await db.SaveChangesAsync();
        db.InventoryMutations.Add(new InventoryMutationReceipt
        {
            RequestId = request,
            ResourceKind = kind,
            ResourceId = id,
            Action = action,
            RequestHash = hash,
            ResultJson = Json.Write(result),
            CreatedAt = Now
        });
        return result;
    }

    private static string RuntimeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 36 || !Guid.TryParseExact(value, "N", out _))
            throw Validation("Runtime ID must be the exact N-format runtime identity.");
        return value;
    }

    private static string BindingId(string? value)
    {
        if (value is null || value.Length > 36 || !Guid.TryParse(value, out var id) || id == Guid.Empty)
            throw Validation("A non-empty UUID binding identity is required.");
        return id.ToString("N");
    }

    private static string BindingKey(string? value, string field, int max)
    {
        if (value is null || value.Length > max || value.Any(char.IsControl) || string.IsNullOrWhiteSpace(value))
            throw Validation($"Invalid {field}; maximum {max} characters and no control characters.");
        return value.Trim();
    }

    private static string BindingText(string? value, string field, int max, bool required = false)
    {
        if (value is null || value.Length > max || value.Any(char.IsControl) || (required && string.IsNullOrWhiteSpace(value)))
            throw Validation($"Invalid {field}; maximum {max} characters and no control characters.");
        return value.Trim();
    }

    private static void ValidatePage(long after, int take)
    {
        if (after < 0 || take is < 1 or > 100) throw Validation("Use a non-negative after cursor and take between 1 and 100.");
    }

    private static InventoryException Validation(string message) => new("validation", message);
    private static InventoryException Conflict(string code, string message) => new(code, message, 409);
}
