using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.GitHub;
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
            RequireDevelopmentRuntime(runtime);
            if (await db.WorkerSlots.AnyAsync(x => x.Id == id)) throw Conflict("identity_exists", "Worker slot identity already exists.");
            if (await db.WorkerSlots.AnyAsync(x => x.RuntimeId == runtimeId && x.Name == name)) throw Conflict("identity_exists", "A worker slot with this name already exists on the runtime.");
            var environment = await db.RuntimeEnvironments.FindAsync(runtimeId);
            if (environment?.Kind == RuntimeEnvironmentKind.ManagedDevcontainer &&
                (await db.WorkerSlots.AnyAsync(x => x.RuntimeId == runtimeId) || await db.Workers.AnyAsync(x => x.RuntimeId == runtimeId) ||
                 await db.WorkspaceClaims.AnyAsync(x => x.RuntimeId == runtimeId && x.WorkerId == null) ||
                 await db.Commands.AnyAsync(x => x.RuntimeId == runtimeId && x.Kind == "CreateWorker" &&
                     (x.State == Delivery.Queued || x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown))))
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
        var page = new List<TaskBindingView>();
        foreach (var row in rows.Take(take)) page.Add(await LoadView(db, row));
        return new TaskBindingPage(page, rows.Count > take ? rows[take - 1].Sequence : null);
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
            RequireDevelopmentRuntime(runtime);
            var environment = await db.RuntimeEnvironments.FindAsync(runtime.Id) ?? throw Conflict("runtime_environment_required", "Configure the runtime environment before creating a task binding.");
            if (project.Archived) throw Conflict("resource_archived", "The project is archived.");
            if (workItem.State is WorkItemState.Released or WorkItemState.Abandoned) throw Conflict("work_item_closed", "The work item is no longer active.");
            if (workItem.Revision != input.ExpectedWorkItemRevision || project.Revision != input.ExpectedProjectRevision || slot.Revision != input.ExpectedSlotRevision)
                throw Conflict("revision_conflict", "Work item, project or worker slot changed; refresh all three before binding.");
            if (workItem.OwnerWorkerSlotId is not null && workItem.OwnerWorkerSlotId != slot.Id)
                throw Conflict("work_item_slot_mismatch", "The work item belongs to a different reusable worker slot.");
            if (!string.Equals(CanonicalProjectRepository(workItem.Repository), project.RepositoryUrl, StringComparison.Ordinal))
                throw Validation("Work item repository does not identify the selected project unambiguously.");
            if (workItem.Branch != branch) throw Validation("Workspace branch must match the work item branch.");
            if (slot.RuntimeId != runtime.Id || slot.Archived) throw Conflict("slot_unavailable", "Worker slot is not available on its runtime.");
            if (environment.Kind == RuntimeEnvironmentKind.ManagedDevcontainer && await db.WorkerSlots.CountAsync(x => x.RuntimeId == runtime.Id) > 1)
                throw Conflict("worker_limit", "Managed devcontainers do not support multiple worker slots in this slice.");
            if (slot.Role == SessionRoles.Coordinator)
                throw Conflict("slot_role", "Coordinator slots cannot own development task bindings.");
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
                if (legacyWorker.Role != SessionRoles.Worker || legacyWorker.Project != project.Name ||
                    workItem.OwnerWorkerId != legacyWorker.Id || legacyWorker.RuntimeId != runtime.Id ||
                    legacyWorker.ManagedServerId != runtime.ManagedServerId || legacyWorker.NativeSessionId != nativeSession ||
                    legacyWorker.Directory != directory || legacyWorker.Branch != branch)
                    throw Conflict("legacy_session_mismatch", "The supplied legacy worker is not the exact project and task session association.");
                if (await db.TaskSessionBindings.AnyAsync(x => x.WorkerSlotId == slot.Id && x.NativeSessionId == nativeSession))
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

    public Task<CommandRecord> CreateTaskSession(string id, CreateTaskSessionInput input) => Write(async db =>
    {
        var bindingId = BindingId(id); var workerId = BindingId(input.WorkerId);
        ValidateRequestId(input.Id);
        if (input.ExpectedHead.Length is not (40 or 64) || input.ExpectedHead.Any(x => !char.IsAsciiHexDigit(x) || char.IsAsciiLetterUpper(x)))
            throw Validation("Expected HEAD must be an exact lowercase commit identity.");
        if (await db.Commands.FindAsync(input.Id) is { } prior) return SameTaskSession(prior, bindingId, workerId, input);
        var binding = await db.TaskBindings.SingleOrDefaultAsync(x => x.Id == bindingId) ?? throw new InventoryException("not_found", "Task binding not found.", 404);
        var workspace = await db.TaskWorkspaces.SingleAsync(x => x.Id == binding.WorkspaceId);
        var session = await db.TaskSessionBindings.SingleAsync(x => x.Id == binding.SessionBindingId);
        var work = await db.WorkItems.FindAsync(binding.WorkItemId) ?? throw new InventoryException("not_found", "Work item not found.", 404);
        var project = await db.Projects.SingleAsync(x => x.Id == binding.ProjectId);
        var slot = await db.WorkerSlots.SingleAsync(x => x.Id == binding.WorkerSlotId);
        var runtime = await db.Runtimes.FindAsync(binding.RuntimeId) ?? throw new InventoryException("not_found", "Runtime not found.", 404);
        var environment = await db.RuntimeEnvironments.FindAsync(runtime.Id) ?? throw Conflict("runtime_environment_required", "Configure the runtime environment before activating a task session.");
        if (runtime.ConnectionKind != RuntimeConnections.Ssh || environment.Kind != RuntimeEnvironmentKind.ExistingMachine)
            throw Conflict("unsupported_runtime_environment", "Fresh task sessions require a configured ExistingMachine SSH runtime.");
        if (binding.State != TaskBindingState.Active || workspace.State != TaskBindingState.Active || session.State != TaskSessionBindingState.Unbound ||
            work.State is WorkItemState.Completed or WorkItemState.Released or WorkItemState.Abandoned ||
            work.OwnerWorkerSlotId != slot.Id || !string.IsNullOrEmpty(work.OwnerWorkerId) || project.Archived || slot.Archived)
            throw Conflict("session_unavailable", "The exact slot-owned task binding is not available for activation.");
        if (binding.Revision != input.ExpectedBindingRevision || workspace.Revision != input.ExpectedWorkspaceRevision || session.Revision != input.ExpectedSessionRevision ||
            work.Revision != input.ExpectedWorkItemRevision || project.Revision != input.ExpectedProjectRevision || slot.Revision != input.ExpectedSlotRevision ||
            runtime.Revision != input.ExpectedRuntimeRevision || environment.Revision != input.ExpectedEnvironmentRevision)
            throw Conflict("revision_conflict", "Task-session activation inputs changed; refresh before retrying.");
        if (await db.Workers.AnyAsync(x => x.Id == workerId)) throw Conflict("identity_exists", "Task execution worker identity already exists.");
        var generation = session.Generation + 1;
        var intent = new TaskSessionCreationIntent(binding.Id, input with { WorkerId = workerId }, generation, $"HVO task {binding.Id} generation {generation}");
        var command = await Record(db, input.Id, runtime.Id, workerId, "CreateTaskSession", Json.Write(intent));
        db.Workers.Add(new WorkerRecord
        {
            Id = workerId,
            RuntimeId = runtime.Id,
            ManagedServerId = runtime.ManagedServerId,
            NativeSessionId = "pending:" + command.Id,
            Name = $"{slot.Name} task {generation}",
            Project = project.Name,
            Directory = workspace.Directory,
            Branch = workspace.Branch,
            ProviderId = slot.ProviderId,
            ModelId = slot.ModelId,
            Role = SessionRoles.Worker,
            Archived = true,
            Stale = true
        });
        session.WorkerId = workerId; session.CreationCommandId = command.Id; session.Generation = generation;
        session.State = TaskSessionBindingState.ActivationPending; session.Revision++; session.UpdatedAt = Now;
        binding.Revision++; binding.UpdatedAt = Now;
        Event(db, "TaskSessionActivationRecorded", runtime.Id, workerId, command.Id, new { bindingId = binding.Id, sessionId = session.Id, generation }, "user");
        return command;
    });

    public Task<VerifyPreparedCheckoutInput> TaskSessionCheckout(string commandId) => Read(async db =>
    {
        var command = await db.Commands.SingleAsync(x => x.Id == commandId && x.Kind == "CreateTaskSession");
        var intent = Json.Read<TaskSessionCreationIntent>(command.Payload);
        var binding = await db.TaskBindings.SingleAsync(x => x.Id == intent.TaskBindingId);
        var workspace = await db.TaskWorkspaces.SingleAsync(x => x.Id == binding.WorkspaceId);
        var project = await db.Projects.SingleAsync(x => x.Id == binding.ProjectId);
        return new VerifyPreparedCheckoutInput(command.Id, binding.RuntimeId, intent.Input.ExpectedRuntimeRevision, workspace.Directory, project.RepositoryUrl, workspace.Branch, intent.Input.ExpectedHead);
    });

    public Task<bool> BindTaskSession(string commandId, JsonElement nativeSession, List<ModelChoice> models) => Write(async db =>
    {
        var command = await db.Commands.SingleAsync(x => x.Id == commandId && x.Kind == "CreateTaskSession");
        var intent = Json.Read<TaskSessionCreationIntent>(command.Payload);
        var binding = await db.TaskBindings.SingleAsync(x => x.Id == intent.TaskBindingId);
        var workspace = await db.TaskWorkspaces.SingleAsync(x => x.Id == binding.WorkspaceId);
        var session = await db.TaskSessionBindings.SingleAsync(x => x.Id == binding.SessionBindingId);
        var work = await db.WorkItems.FindAsync(binding.WorkItemId) ?? throw new InventoryException("not_found", "Work item not found.", 404);
        var project = await db.Projects.SingleAsync(x => x.Id == binding.ProjectId);
        var slot = await db.WorkerSlots.SingleAsync(x => x.Id == binding.WorkerSlotId);
        var runtime = await db.Runtimes.FindAsync(binding.RuntimeId) ?? throw new InventoryException("not_found", "Runtime not found.", 404);
        var environment = await db.RuntimeEnvironments.FindAsync(runtime.Id);
        var worker = await db.Workers.FindAsync(command.WorkerId) ?? throw new ControlException("Reserved task worker is missing.");
        var nativeId = nativeSession.GetProperty("id").GetString();
        if (nativeSession.GetProperty("directory").GetString() != workspace.Directory || string.IsNullOrWhiteSpace(nativeId))
            throw Conflict("native_session_mismatch", "Created session directory or identity differs from the verified task workspace.");
        if (!ActivationCurrent(binding, workspace, session, work, project, slot, runtime, environment, worker, command, intent))
        {
            command.ResultId = nativeId;
            command.ResultJson = Json.Write(new { nativeSessionId = nativeId, directory = workspace.Directory });
            command.State = Delivery.Failed;
            command.Detail = "Native session receipt retained, but task activation authority was superseded; no session was bound.";
            command.UpdatedAt = Now;
            // A retained late receipt is terminal evidence, not an in-flight activation.
            session.State = TaskSessionBindingState.Superseded; session.Revision++; session.UpdatedAt = Now;
            Event(db, "TaskSessionActivationSuperseded", binding.RuntimeId, worker.Id, command.Id,
                new { bindingId = binding.Id, sessionId = session.Id, intent.Generation, nativeSessionId = nativeId }, "observed");
            return false;
        }
        worker.NativeSessionId = nativeId; worker.ModelsJson = Json.Write(models); worker.Archived = false; worker.Revision++;
        session.NativeSessionId = nativeId; session.State = TaskSessionBindingState.Bound; session.Revision++; session.UpdatedAt = Now;
        work.OwnerWorkerId = worker.Id; work.OwnerWorkerSlotId = null; work.Revision++; work.UpdatedAt = Now;
        foreach (var phase in await db.WorkItemPhases.Where(x => x.WorkItemId == work.Id && x.State == WorkItemPhaseState.Active).ToListAsync())
        { phase.OwnerWorkerId = worker.Id; phase.OwnerWorkerSlotId = null; }
        command.ResultId = worker.Id; command.State = Delivery.Finished; command.Detail = "Task session created and bound."; command.UpdatedAt = Now;
        return true;
    });

    internal static async Task<ProjectRecord?> RequireActiveTaskTuple(ControlDb db, WorkerRecord worker, bool allowTerminal = false)
    {
        var session = await db.TaskSessionBindings.SingleOrDefaultAsync(x => x.WorkerId == worker.Id);
        if (session is null) return null;
        var binding = await db.TaskBindings.SingleOrDefaultAsync(x => x.Id == session.TaskBindingId);
        var workspace = binding is null ? null : await db.TaskWorkspaces.SingleOrDefaultAsync(x => x.Id == binding.WorkspaceId);
        var work = binding is null ? null : await db.WorkItems.FindAsync(binding.WorkItemId);
        var project = binding is null ? null : await db.Projects.SingleOrDefaultAsync(x => x.Id == binding.ProjectId);
        if (binding is null || workspace is null || work is null || binding.State != TaskBindingState.Active || workspace.State != TaskBindingState.Active ||
            session.State != TaskSessionBindingState.Bound || session.NativeSessionId != worker.NativeSessionId || workspace.Directory != worker.Directory ||
            workspace.Branch != worker.Branch || work.OwnerWorkerId != worker.Id || work.OwnerWorkerSlotId is not null || project is null || project.Archived ||
            (!allowTerminal && (work.State is WorkItemState.Completed or WorkItemState.Released or WorkItemState.Abandoned)) ||
            !string.Equals(CanonicalProjectRepository(work.Repository), project.RepositoryUrl, StringComparison.Ordinal))
            throw new ControlException("Task execution worker is not the exact active binding, session and workspace owner.");
        return project;
    }

    internal static void RequireTaskPromptScope(ProjectRecord? project, GitHubMergeTaskScope? scope)
    {
        if (project is null || scope is null) return;
        var repository = CanonicalProjectRepository(project.RepositoryUrl)["https://github.com/".Length..];
        if (!GitHubMergeTaskAuthority.SameRepository(scope.Repository, repository))
            throw new ControlException("Typed GitHub merge task scope does not match the canonical repository of the bound task project.", 409);
    }

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
            if (session.State is TaskSessionBindingState.ActivationPending or TaskSessionBindingState.Creating)
                throw Conflict("session_in_flight", "Reconcile task-session activation before release.");
            if (session.WorkerId is not null && session.State != TaskSessionBindingState.Superseded)
            {
                var worker = await db.Workers.FindAsync(session.WorkerId) ?? throw Conflict("worker_missing", "Task worker projection is missing.");
                var work = await db.WorkItems.FindAsync(binding.WorkItemId) ?? throw new InventoryException("not_found", "Work item not found.", 404);
                await RequireActiveTaskTuple(db, worker, allowTerminal: true);
                if (work.State is not (WorkItemState.Completed or WorkItemState.Released or WorkItemState.Abandoned)) throw Conflict("task_not_terminal", "Task ownership must be terminal before release.");
                if (worker.Stale || worker.Activity != "Idle" || worker.LastObservedAt < Now - 60000) throw Conflict("worker_not_idle", "Fresh native idle evidence is required.");
                if (await db.Commands.AnyAsync(x => x.WorkerId == worker.Id && x.State != Delivery.Finished && x.State != Delivery.Failed && x.State != Delivery.Cancelled)) throw Conflict("commands_pending", "Drain task commands before release.");
                if (await db.Requests.AnyAsync(x => x.WorkerId == worker.Id && (x.State == "Pending" || x.State == "ReplyUnknown"))) throw Conflict("requests_pending", "Resolve task requests before release.");
                if ((await db.CoordinationRuns.Where(x => x.State != "Completed" && x.State != "Stopped").ToListAsync()).Any(x => x.CoordinatorWorkerId == worker.Id || Json.Read<string[]>(x.WorkerIdsJson).Contains(worker.Id))) throw Conflict("coordination_pending", "Finish coordination before release.");
                worker.Archived = true; worker.Revision++;
            }
            binding.State = TaskBindingState.Released; binding.Revision++; binding.UpdatedAt = Now;
            workspace!.State = TaskBindingState.Released; workspace.Revision++; workspace.UpdatedAt = Now;
            session!.State = TaskSessionBindingState.Released; session.Revision++; session.UpdatedAt = Now;
            Event(db, "TaskBindingReleased", binding.RuntimeId, payload: new { binding.Id }, provenance: "user");
            await db.SaveChangesAsync();
            return await LoadView(db, binding);
        });
    });

    public Task<WorkerSlotRecord> ArchiveWorkerSlot(string id, ArchiveInventoryInput input) => Write(async db =>
    {
        var slotId = BindingId(id);
        return await MutateBinding(db, input.RequestId, "WorkerSlot", slotId, "Archive", new { input.ExpectedRevision, input.Archived }, async () =>
        {
            var slot = await db.WorkerSlots.SingleOrDefaultAsync(x => x.Id == slotId) ?? throw new InventoryException("not_found", "Worker slot not found.", 404);
            if (slot.Revision != input.ExpectedRevision) throw Conflict("revision_conflict", "Worker slot changed; refresh before archiving it.");
            if (input.Archived && await db.TaskBindings.AnyAsync(x => x.WorkerSlotId == slot.Id && x.State == TaskBindingState.Active))
                throw Conflict("resource_in_use", "Release the active task binding before archiving this worker slot.");
            if (!input.Archived)
            {
                var runtime = await db.Runtimes.FindAsync(slot.RuntimeId);
                if (runtime is null) throw Conflict("runtime_unavailable", "Restore requires an existing development runtime parent.");
                RequireDevelopmentRuntime(runtime);
            }
            slot.Archived = input.Archived; slot.Revision++; slot.UpdatedAt = Now;
            Event(db, "WorkerSlotArchived", slot.RuntimeId, payload: new { slot.Id, slot.Archived }, provenance: "user");
            return slot;
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

    private static CommandRecord SameTaskSession(CommandRecord prior, string bindingId, string workerId, CreateTaskSessionInput input)
    {
        try
        {
            var intent = Json.Read<TaskSessionCreationIntent>(prior.Payload);
            if (prior.Kind == "CreateTaskSession" && prior.WorkerId == workerId && intent.TaskBindingId == bindingId && intent.Input == input)
                return prior;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }
        throw new ControlException("Request ID was already used for different task-session activation authority.", 409);
    }

    private static bool ActivationCurrent(TaskBindingRecord binding, TaskWorkspaceRecord workspace, TaskSessionBindingRecord session,
        WorkItem work, ProjectRecord project, WorkerSlotRecord slot, RuntimeRecord runtime, RuntimeEnvironmentRecord? environment,
        WorkerRecord worker, CommandRecord command, TaskSessionCreationIntent intent) =>
        binding.State == TaskBindingState.Active && workspace.State == TaskBindingState.Active &&
        session.State == TaskSessionBindingState.ActivationPending && session.CreationCommandId == command.Id &&
        session.WorkerId == worker.Id && session.Generation == intent.Generation &&
        binding.Revision == intent.Input.ExpectedBindingRevision + 1 && session.Revision == intent.Input.ExpectedSessionRevision + 1 &&
        work.Revision == intent.Input.ExpectedWorkItemRevision && project.Revision == intent.Input.ExpectedProjectRevision &&
        slot.Revision == intent.Input.ExpectedSlotRevision && runtime.Revision == intent.Input.ExpectedRuntimeRevision &&
        environment?.Revision == intent.Input.ExpectedEnvironmentRevision && !project.Archived && !slot.Archived && worker.Archived &&
        work.State is not (WorkItemState.Completed or WorkItemState.Released or WorkItemState.Abandoned) &&
        work.OwnerWorkerSlotId == slot.Id && string.IsNullOrEmpty(work.OwnerWorkerId) &&
        worker.RuntimeId == runtime.Id && worker.Directory == workspace.Directory && worker.Branch == workspace.Branch &&
        worker.NativeSessionId == "pending:" + command.Id &&
        string.Equals(CanonicalProjectRepository(work.Repository), project.RepositoryUrl, StringComparison.Ordinal);

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
