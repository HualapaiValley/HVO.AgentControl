using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.AgentControl.Components.Pages;

public partial class Workers
{
    [SupplyParameterFromQuery(Name = "runtime")] public string? SetupRuntimeId { get; set; }
    [SupplyParameterFromQuery(Name = "new")] public bool StartNew { get; set; }
    [SupplyParameterFromQuery(Name = "role")] public string? RequestedRole { get; set; }
    private string? appliedSetup, pendingCreationId;
    [SupplyParameterFromQuery(Name = "edit")] public string? EditWorkerId { get; set; }
    private WorkerRecord? editing;
    private string editModel = "";
    private bool showArchived;
    private string? deletingWorkerId;
    private string? editRequestId;
    private string? appliedEdit;
    private bool creatingWorker;
    private string newRole = SessionRoles.Worker;
    private bool discoverCapabilities = true;
    private string newRuntimeId = "", newWorkerName = "", newProject = "", newDirectory = "", newModel = "", newRepository = "", newBranch = "", newBaseRef = "HEAD";
    private string runtimeFilter = "", activityFilter = "", projectFilter = "";
    private string? workerRequestId, inspectionId, inspectedDirectory, inspectedRuntime;
    private List<ModelChoice>? inspectedModels;

    protected override void OnParametersSet()
    {
        if (EditWorkerId is not null && appliedEdit != EditWorkerId && snapshot?.Workers.FirstOrDefault(x => x.Id == EditWorkerId) is { } requested)
        { appliedEdit = EditWorkerId; BeginEdit(requested); }
        var key = SetupRuntimeId + ":" + StartNew;
        if (appliedSetup == key) return;
        appliedSetup = key;
        if (StartNew) BeginWorkerSetup(SetupRuntimeId);
    }
    private void NewWorker() => BeginWorkerSetup(null);
    private void BeginWorkerSetup(string? runtimeId)
    {
        var runtime = snapshot?.Runtimes.FirstOrDefault(x => x.ConnectionKind == RuntimeConnections.Ssh && x.Transport == "Connected" && (runtimeId is null || x.Id == runtimeId));
        if (runtime is null) { error = "Connect the runtime before setting up a worker."; return; }
        newRole = RequestedRole == SessionRoles.Coordinator ? SessionRoles.Coordinator : SessionRoles.Worker;
        editing = null; creatingWorker = true; workerRequestId = null; newRuntimeId = runtime.Id;
        newWorkerName = ""; newProject = ""; newDirectory = ControlStore.Roots(runtime)[0]; newModel = ""; newRepository = ""; newBranch = ""; newBaseRef = "HEAD";
        inspectionId = null; inspectedModels = null;
    }
    protected override Task SnapshotChanged()
    {
        if (editing is not null && snapshot!.Workers.FirstOrDefault(x => x.Id == editing.Id) is { } current)
            editing.ModelsJson = current.ModelsJson;
        if (inspectionId is not null && snapshot!.Commands.FirstOrDefault(x => x.Id == inspectionId && x.State == Delivery.Finished) is { } inspected)
        {
            using var result = JsonDocument.Parse(inspected.ResultJson);
            inspectedModels = Json.Read<List<ModelChoice>>(result.RootElement.GetProperty("models").GetRawText());
            inspectedDirectory = result.RootElement.GetProperty("directory").GetString(); inspectedRuntime = inspected.RuntimeId;
            inspectionId = null; notice = inspectedModels.Count == 0 ? "No connected models in this workspace. Configure its remote provider first." : "Workspace-specific models are ready to select.";
        }
        if (pendingCreationId is not null && snapshot!.Commands.FirstOrDefault(x => x.Id == pendingCreationId) is { } created)
        {
            if (created.State == Delivery.Finished && created.ResultId is not null)
            {
                pendingCreationId = null; Navigation.NavigateTo(ConversationUrl(created.ResultId));
            }
            else if (created.State is Delivery.Failed or Delivery.Unknown or Delivery.Cancelled)
            {
                pendingCreationId = null; error = "Worker setup " + created.State + ". " + created.Detail;
            }
        }
        return Task.CompletedTask;
    }
    private List<ModelChoice> Models(string id) => id == inspectedRuntime && (newDirectory == inspectedDirectory || newRepository == inspectedDirectory) && inspectedModels is not null
        ? inspectedModels : Json.Read<List<ModelChoice>>(snapshot?.Runtimes.FirstOrDefault(x => x.Id == id)?.ModelsJson ?? "[]");
    private Task InspectWorkspaceModels() => Execute(async () =>
    {
        var command = await Store.InspectWorkspace(new(Guid.NewGuid().ToString(), newRuntimeId, string.IsNullOrWhiteSpace(newRepository) ? newDirectory : newRepository));
        inspectionId = command.Id; notice = "Workspace inspection recorded.";
    });
    private Task CreateWorker() => Execute(async () =>
    {
        var model = Models(newRuntimeId).FirstOrDefault(x => x.ProviderId + "/" + x.ModelId == newModel) ?? throw new ControlException("Choose an available provider/model.");
        workerRequestId ??= Guid.NewGuid().ToString();
        var command = await Store.CreateWorker(new(workerRequestId, newRuntimeId, newWorkerName, newProject, newDirectory,
            model.ProviderId, model.ModelId, string.IsNullOrWhiteSpace(newRepository) ? null : newRepository, newBranch, newBaseRef, newRole, discoverCapabilities));
        creatingWorker = false; workerRequestId = null; pendingCreationId = command.Id; notice = "Creating the worker. Its conversation will open when ready.";
    });
    private void BeginEdit(WorkerRecord worker)
    {
        editing = Json.Read<WorkerRecord>(Json.Write(worker)); editModel = worker.ProviderId + "/" + worker.ModelId;
        editRequestId = null; creatingWorker = false;
    }
    private List<ModelChoice> EditModels => Json.Read<List<ModelChoice>>(editing?.ModelsJson ?? "[]");
    private ModelChoice? EditChoice => EditModels.FirstOrDefault(x => x.ProviderId + "/" + x.ModelId == editModel);
    private void EditModelChanged() { editing!.Agent = ""; editing.Variant = ""; }
    private Task RefreshEditModels() => Execute(async () =>
    {
        await Store.InspectWorkspace(new(Guid.NewGuid().ToString(), editing!.RuntimeId, editing.Directory));
        notice = "Refreshing models available in this workspace.";
    });
    private Task SaveEdit() => Execute(async () =>
    {
        var split = editModel.IndexOf('/');
        if (split < 1) throw new ControlException("Select a model.");
        editRequestId ??= Guid.NewGuid().ToString();
        await Store.UpdateWorker(editing!.Id, new(editRequestId, editing.SettingsRevision, editing.Name, editing.Project,
            editing.Description, editModel[..split], editModel[(split + 1)..], editing.Agent, editing.Variant));
        editing = null; editRequestId = null; notice = "Worker updated. New instructions use the saved settings; the conversation continues.";
    });
    private Task SetArchived(WorkerRecord worker, bool archived) => Execute(async () =>
    {
        await Store.ArchiveWorker(worker.Id, new(Guid.NewGuid().ToString(), worker.SettingsRevision, archived));
        notice = archived ? "Worker archived. Find it using Show archived workers." : "Worker restored with its original conversation.";
    });
    private Task ReserveCoordinator() => Execute(async () =>
    {
        await Store.ReserveCoordinator(editing!.Id, new(Guid.NewGuid().ToString(), editing.SettingsRevision));
        editing = null; Navigation.NavigateTo("/coordination");
    });
    private Task Discover(WorkerRecord worker) => Execute(async () =>
    {
        await Store.DiscoverCapabilities(worker.Id, new(Guid.NewGuid().ToString()));
        notice = "Capability inquiry queued; active work continues. Results will appear here and in the conversation.";
    });
    private WorkerRecord? WorkspaceOwner(string runtimeId, string directory)
    {
        var runtime = snapshot?.Runtimes.FirstOrDefault(x => x.Id == runtimeId);
        if (runtime is null) return null;
        var ids = snapshot!.Runtimes.Where(x => x.Host == runtime.Host && x.Port == runtime.Port && x.Username == runtime.Username).Select(x => x.Id).ToHashSet();
        return snapshot.Workers.FirstOrDefault(x => ids.Contains(x.RuntimeId) && x.Directory.TrimEnd('/') == directory.TrimEnd('/'));
    }
    private IEnumerable<CommandRecord> CreationCards => snapshot!.Commands.Where(x => x.Kind == "CreateWorker" && !x.Dismissed && x.State != Delivery.Finished &&
        (runtimeFilter == "" || x.RuntimeId == runtimeFilter)).OrderByDescending(x => x.CreatedAt);
    private bool PastSetup(CommandRecord command)
    {
        if (command.State is not (Delivery.Failed or Delivery.Cancelled)) return false;
        var input = Json.Read<CreateWorkerInput>(command.Payload);
        return WorkspaceOwner(command.RuntimeId, input.Directory) is not null;
    }
    private static string CreationStatus(CommandRecord command) => command.State switch
    {
        Delivery.Queued => "Queued",
        Delivery.Dispatching => "Creating",
        Delivery.Unknown => "Needs reconciliation",
        Delivery.Failed => "Failed",
        Delivery.Cancelled => "Cancelled",
        _ => command.State
    };
    private Task DismissSetup(string id) => Execute(async () => { await Store.DismissCreation(id); notice = "Setup card dismissed; its audit record remains."; });
    private void EditSetup(CommandRecord command)
    {
        var input = Json.Read<CreateWorkerInput>(command.Payload);
        creatingWorker = true; workerRequestId = null; newRuntimeId = input.RuntimeId; newWorkerName = input.Name;
        newProject = input.Project; newDirectory = input.Directory; newModel = input.ProviderId + "/" + input.ModelId;
        newRepository = input.Repository ?? ""; newBranch = input.Branch ?? ""; newBaseRef = input.BaseRef ?? "HEAD";
        newRole = input.Role; discoverCapabilities = input.DiscoverCapabilities; inspectedModels = null; inspectionId = null;
    }
    private Task DeleteWorker(WorkerRecord worker) => Execute(async () =>
    {
        await Store.DeleteWorker(worker.Id, new(Guid.NewGuid().ToString(), worker.SettingsRevision));
        deletingWorkerId = null; editing = null; notice = "Worker registration deleted. Remote files and native conversation retained.";
    });
    private IEnumerable<WorkerRecord> FilteredWorkers() => snapshot!.Workers.Where(x =>
        x.Role == SessionRoles.Worker && (showArchived || !x.Archived) && (runtimeFilter == "" || x.RuntimeId == runtimeFilter) && x.Project.Contains(projectFilter, StringComparison.OrdinalIgnoreCase) &&
        (activityFilter == "" || x.Activity == activityFilter || activityFilter == "Waiting" && x.Activity.StartsWith("Waiting", StringComparison.Ordinal) || activityFilter == "Error" && (x.Outcome == "Failed" || x.Stale)));
}
