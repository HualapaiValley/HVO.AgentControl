using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using HVO.AgentControl.OpenCode;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WorkerSlotCapabilityProbeTests
{
    [Fact]
    public void ProbeUsesVersionedSchemaAndRegisteredTypedResults()
    {
        var snapshot = CapabilityProbe.Parse("os\tLinux\narchitecture\tx86_64\ntool.git\tpresent\ntool.node\tabsent\ndockerDaemonAccess\tunknown", "/work");

        Assert.Equal(CapabilityProbeCatalog.SchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(CapabilityProbeCatalog.CatalogVersion, snapshot.CatalogVersion);
        Assert.Equal(CapabilityProbeStatus.Available, Result(snapshot, "environment.os").Status);
        Assert.Equal(CapabilityProbeStatus.Available, Result(snapshot, "tool.git").Status);
        Assert.Equal(CapabilityProbeStatus.Unavailable, Result(snapshot, "tool.node").Status);
        Assert.Equal(CapabilityProbeStatus.Unknown, Result(snapshot, "access.docker-daemon").Status);
        Assert.Throws<ArgumentException>(() => CapabilityProbeCatalog.Default.NormalizeRequirements(["tool.not-registered"]));

        var legacy = CapabilityProbeCatalog.Default.Normalize(Json.Read<CapabilitySnapshot>(
            "{\"observedAt\":1,\"source\":\"probe\",\"scope\":\"/work\",\"facts\":{\"tool.git\":\"present\"}}"));
        Assert.Equal(CapabilityProbeStatus.Available, Result(legacy, "tool.git").Status);
        Assert.Equal(["tool.git"], CapabilityProbeCatalog.Default.Missing(Json.Write(snapshot with { SchemaVersion = 2 }), ["tool.git"]));
    }

    [Fact]
    public async Task CatalogAndPerRuntimeReportExposeSlotEligibilityThroughOwnerApi()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, ["tool.git"], "tool.git\tpresent\ntool.node\tabsent");
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/worker-slot-probes")).StatusCode);
        using var owner = await app.SignIn();

        var catalog = await owner.GetFromJsonAsync<CapabilityProbeCatalogContract>("/api/v1/worker-slot-probes");
        var report = await owner.GetFromJsonAsync<RuntimeCapabilityReport>($"/api/v1/runtimes/{setup.Runtime.Id}/capabilities");

        Assert.Equal(CapabilityProbeCatalog.SchemaVersion, catalog!.SchemaVersion);
        Assert.Contains(catalog.Probes, x => x.Id == "tool.git" && x.Version == 1);
        Assert.Equal(setup.Runtime.Id, report!.RuntimeId);
        Assert.Equal(CapabilityProbeStatus.Available, report.Results.Single(x => x.Id == "tool.git").Status);
        var slot = Assert.Single(report.WorkerSlots);
        Assert.Equal(setup.Slot.Id, slot.WorkerSlotId);
        Assert.True(slot.CapabilityRequirementsSatisfied);
        Assert.Empty(slot.MissingProbeIds);
    }

    [Fact]
    public async Task TaskBindingRejectsMissingCapabilityWithoutOwnershipOrReceipt()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, ["tool.git"], "tool.git\tabsent");
        var input = Binding(setup);

        var error = await Assert.ThrowsAsync<InventoryException>(() => app.Store.CreateTaskBinding(input));

        Assert.Equal("capability_unavailable", error.Code);
        Assert.Equal(0, await app.Store.Read(db => db.TaskBindings.CountAsync()));
        Assert.Equal(0, await app.Store.Read(db => db.InventoryMutations.CountAsync(x => x.RequestId == input.RequestId)));
        var report = await app.Store.RuntimeCapabilityReport(setup.Runtime.Id);
        Assert.Equal(["tool.git"], Assert.Single(report.WorkerSlots).MissingProbeIds);
    }

    [Fact]
    public async Task SupervisorRechecksBoundSlotCapabilitiesBeforeClaimingPrompt()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, ["tool.git"], "tool.git\tpresent");
        await app.Store.CreateTaskBinding(Binding(setup));
        var command = await app.Store.Prompt(setup.Worker.Id,
            new(Guid.NewGuid().ToString(), "run the task", setup.Worker.Revision));
        await SetCapabilities(app, setup.Runtime.Id, "tool.git\tabsent");
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);

        Assert.Null(await Claim(supervisor, setup.Runtime.Id));
        Assert.Equal(Delivery.Queued, (await app.Store.Detail(setup.Worker.Id)).Commands.Single(x => x.Id == command.Id).State);

        await SetCapabilities(app, setup.Runtime.Id, "tool.git\tpresent");
        Assert.Equal(command.Id, (await Claim(supervisor, setup.Runtime.Id))!.Id);
    }

    [Fact]
    public async Task SupervisorRequeuesClaimedPromptWhenCapabilityChangesBeforeNativeMutation()
    {
        await using var app = new TestApp();
        var setup = await Seed(app, ["tool.git"], "tool.git\tpresent");
        await app.Store.CreateTaskBinding(Binding(setup));
        var command = await app.Store.Prompt(setup.Worker.Id,
            new(Guid.NewGuid().ToString(), "run the task", setup.Worker.Revision));
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        command = (await Claim(supervisor, setup.Runtime.Id))!;
        await SetCapabilities(app, setup.Runtime.Id, "tool.git\tabsent");
        var handler = new PromptHandler(setup.Worker);

        await Dispatch(supervisor, command, setup.Runtime, new PromptTransport(handler));

        Assert.Equal(0, handler.Posts);
        var retained = (await app.Store.Detail(setup.Worker.Id)).Commands.Single(x => x.Id == command.Id);
        Assert.Equal(Delivery.Queued, retained.State);
        Assert.Contains("tool.git", retained.Detail);
    }

    [Fact]
    public async Task SlotRegistrationNormalizesRequirementsAndRejectsUnknownCatalogEntry()
    {
        await using var app = new TestApp();
        var profile = PersistenceTests.Profile(); profile.Id = Id();
        var runtimeCommand = await app.Store.SaveRuntimeForSetup(profile, Id());
        var runtime = await app.Store.Read(db => db.Runtimes.SingleAsync(x => x.Id == runtimeCommand.ResultId));
        var slot = await app.Store.CreateWorkerSlot(new(Id(), Id(), runtime.Id, "probe-slot",
            CapabilityProbeIds: ["tool.node", "tool.git", "tool.node"]));
        Assert.Equal(["tool.git", "tool.node"], slot.CapabilityProbeIds);
        Assert.Equal(slot.CapabilityProbeIds, (await app.Store.WorkerSlot(slot.Id)).CapabilityProbeIds);

        var error = await Assert.ThrowsAsync<InventoryException>(() => app.Store.CreateWorkerSlot(
            new(Id(), Id(), runtime.Id, "invalid-slot", CapabilityProbeIds: ["tool.unregistered"])));
        Assert.Equal("validation", error.Code);
    }

    private static CapabilityProbeResult Result(CapabilitySnapshot snapshot, string id) => snapshot.Results.Single(x => x.Id == id);
    private static string Id() => Guid.NewGuid().ToString("N");

    private static Task<CommandRecord?> Claim(RuntimeSupervisor supervisor, string runtimeId) =>
        (Task<CommandRecord?>)typeof(RuntimeSupervisor).GetMethod("Claim", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(supervisor, [runtimeId, null, CancellationToken.None, null])!;

    private static Task Dispatch(RuntimeSupervisor supervisor, CommandRecord command, RuntimeRecord runtime, IRuntimeTransport transport) =>
        (Task)typeof(RuntimeSupervisor).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(supervisor, [command, runtime, transport, CancellationToken.None])!;

    private static Task SetCapabilities(TestApp app, string runtimeId, string output) => app.Store.Write(async db =>
    {
        (await db.Runtimes.FindAsync(runtimeId))!.CapabilitiesJson = Json.Write(CapabilityProbe.Parse(output, "/work"));
        return true;
    });

    private static CreateTaskBindingInput Binding(Setup setup) => new(Id(), Id(), setup.Work.Id, setup.Project.Id,
        setup.Slot.Id, Id(), Id(), setup.Worker.Directory, setup.Work.Branch, setup.Work.Revision, setup.Project.Revision,
        setup.Slot.Revision, setup.Worker.Id, setup.Worker.NativeSessionId);

    private static async Task<Setup> Seed(TestApp app, string[] required, string output)
    {
        var profile = PersistenceTests.Profile(); profile.Id = Id();
        var runtimeCommand = await app.Store.SaveRuntimeForSetup(profile, Id());
        var runtime = await app.Store.Read(db => db.Runtimes.SingleAsync(x => x.Id == runtimeCommand.ResultId));
        await SetCapabilities(app, runtime.Id, output);
        var host = await app.Store.CreateHost(new(Id(), Id(), "probe-host"));
        await app.Store.ConfigureRuntimeEnvironment(runtime.Id,
            new(Id(), 0, runtime.Revision, host.Id, RuntimeEnvironmentKind.ExistingMachine));
        var project = await app.Store.CreateProject(new(Id(), Id(), "Probe project", "https://github.com/RoySalisbury/HVO.Probe.git"));
        var worker = new WorkerRecord
        {
            Id = Id(),
            RuntimeId = runtime.Id,
            ManagedServerId = runtime.ManagedServerId,
            NativeSessionId = "native-" + Id(),
            Name = "probe-worker",
            Project = project.Name,
            Directory = "/work/probe",
            Branch = "feature/probe",
            ProviderId = "provider",
            ModelId = "model",
            ModelsJson = "[]",
            Activity = "Idle",
            Stale = false,
            Revision = 7
        };
        await app.Store.Write(db => { db.Workers.Add(worker); return Task.FromResult(true); });
        var work = await app.Store.CreateWorkItem(new("probe-" + Id(), null, "Probe task", worker.Branch, project.RepositoryUrl, worker.Id));
        var slot = await app.Store.CreateWorkerSlot(new(Id(), Id(), runtime.Id, "probe-slot", CapabilityProbeIds: required));
        return new(runtime, project, worker, work, slot);
    }

    private sealed record Setup(RuntimeRecord Runtime, ProjectRecord Project, WorkerRecord Worker, WorkItem Work, WorkerSlotRecord Slot);

    private sealed class PromptTransport(HttpMessageHandler handler) : IRuntimeTransport
    {
        public OpenCodeClient Api { get; } = new(new HttpClient(handler) { BaseAddress = new Uri("http://native.test") });
        public bool Connected => true;
        public string Platform => "linux";
        public Task<WorkspaceIdentity> Workspace(RuntimeRecord runtime, CreateWorkerInput input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopOwnedServer(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PromptHandler(WorkerRecord worker) : HttpMessageHandler
    {
        public int Posts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Post) Posts++;
            var path = request.RequestUri!.AbsolutePath;
            object body = path == "/provider"
                ? new { connected = new[] { worker.ProviderId }, all = new[] { new { id = worker.ProviderId, models = new Dictionary<string, object> { [worker.ModelId] = new { name = worker.ModelId } } } } }
                : path.EndsWith("/message", StringComparison.Ordinal) ? Array.Empty<object>()
                : path == "/session/status" ? new { }
                : new { id = worker.NativeSessionId, directory = worker.Directory, time = new { created = 1L } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}
