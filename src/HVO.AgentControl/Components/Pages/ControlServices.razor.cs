using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Components.Pages;

public partial class ControlServices
{
    [Inject] private ControlServiceRegistration Registration { get; set; } = default!;
    [Inject] private ProviderKeyService Keys { get; set; } = default!;
    [Inject] private ProviderLoginService Logins { get; set; } = default!;
    private List<ControlServiceView> services = [];
    private Dictionary<string, CommandRecord> creations = [];
    private ProviderKeyStatus? keyStatus;
    private List<ProviderLogin> logins = [];
    private bool registering;
    private string registrationId = "", serviceName = "", endpoint = "", expectedInstance = "", passwordReference = "";
    private string? creatingServiceId, creationId;
    private string scopeId = "", sessionName = "", sessionModel = "", sessionVariant = "";
    private WorkerRecord? editing;
    private string editModel = "";
    private string? editId;

    protected override async Task SnapshotChanged()
    {
        services = await Store.ControlServices();
        var commandIds = services.SelectMany(x => x.Sessions).Select(x => x.CreationCommandId).ToArray();
        creations = await Store.Read(db => db.Commands.AsNoTracking().Where(x => commandIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id));
        keyStatus = await Keys.Status();
        logins = Logins.List();
        if (editing is not null && snapshot!.Workers.FirstOrDefault(x => x.Id == editing.Id) is { } current)
            editing.ModelsJson = current.ModelsJson;
    }

    private void BeginRegistration()
    {
        registering = true; registrationId = Guid.NewGuid().ToString("N");
        serviceName = "Control service"; endpoint = "http://opencode-control:4096"; expectedInstance = ""; passwordReference = "";
    }
    private Task Register() => Execute(async () =>
    {
        await Registration.Register(new(registrationId, serviceName.Trim(), endpoint.Trim(), expectedInstance.Trim(), passwordReference.Trim()), lifetime.Token);
        registering = false; passwordReference = "";
        notice = "Control service registered. Its host conversation is being prepared; status remains available here.";
    });

    private void BeginSession(string serviceId)
    {
        creatingServiceId = serviceId; creationId = Guid.NewGuid().ToString();
        scopeId = ""; sessionName = ""; sessionModel = ""; sessionVariant = "";
    }
    private static List<ModelChoice> Models(string json) => Json.Read<List<ModelChoice>>(json);
    private List<ModelChoice> SessionModels => Models(services.FirstOrDefault(x => x.Service.Id == creatingServiceId)?.Connection.ModelsJson ?? "[]");
    private ModelChoice? SessionChoice => SessionModels.FirstOrDefault(x => ModelKey(x) == sessionModel);
    private static string ModelKey(ModelChoice choice) => choice.ProviderId + "/" + choice.ModelId;
    private Task CreateSession() => Execute(async () =>
    {
        var model = SessionChoice ?? throw new ControlException("Choose an available model.", 400);
        await Store.CreateControlSession(creatingServiceId!, new(creationId!, "Workgroup", scopeId.Trim(), sessionName.Trim(), model.ProviderId, model.ModelId, sessionVariant));
        creatingServiceId = null;
        notice = "Workgroup conversation requested. Its creation status is saved and survives navigation or refresh.";
    });

    private void BeginEdit(WorkerRecord worker)
    {
        editing = Json.Read<WorkerRecord>(Json.Write(worker));
        editModel = worker.ProviderId + "/" + worker.ModelId; editId = Guid.NewGuid().ToString();
    }
    private List<ModelChoice> EditModels => Models(editing?.ModelsJson ?? "[]");
    private ModelChoice? EditChoice => EditModels.FirstOrDefault(x => ModelKey(x) == editModel);
    private void EditModelChanged() { editing!.Agent = ""; editing.Variant = ""; }
    private Task SaveEdit() => Execute(async () =>
    {
        var model = EditChoice ?? throw new ControlException("Refresh models and choose an available model.", 400);
        await Store.UpdateWorker(editing!.Id, new(editId!, editing.SettingsRevision, editing.Name, editing.Project,
            editing.Description, model.ProviderId, model.ModelId, editing.Agent, editing.Variant));
        editing = null;
        notice = "Session settings saved for future instructions. Its conversation and existing instructions are preserved.";
    });

    private Task ConnectionAction(string id, string kind) => Execute(async () =>
    {
        await Store.RuntimeCommand(id, kind, Guid.NewGuid().ToString());
        notice = kind == "DisconnectRuntime" ? "Host connection will close. OpenCode and its conversations keep running."
            : "Connection refresh requested; available models and conversation state will be checked.";
    });
    private Task ApplyKey(string id) => Execute(async () =>
    {
        keyStatus = await Keys.Apply(id, new(keyStatus!.Revision), lifetime.Token);
        notice = "Go key delivery status updated. Refresh connection and models before choosing a newly available model.";
    });
    private Task StartLogin(string id) => Execute(async () => { await Logins.Start(id, Guid.NewGuid().ToString()); });
    private static string Status(ControlSessionBinding binding, CommandRecord? command) => binding.State == "Ready" ? "Ready" : command?.State switch
    {
        Delivery.Dispatching => "Creating",
        Delivery.Unknown => "Needs reconciliation",
        null => binding.State,
        var state => state
    };
}
