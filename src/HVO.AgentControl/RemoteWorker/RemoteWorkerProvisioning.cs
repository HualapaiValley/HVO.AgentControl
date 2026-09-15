using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

public sealed record RemoteResourceInspection(bool Exists, string? Reference, IReadOnlyDictionary<string, string> Labels, string State);
public interface IRemoteWorkerProvisioner
{
    Task<RemoteOperationResult> ProbeAsync(ApprovedExecutionHost host, CancellationToken token);
    Task<string> CreateVolumeAsync(ApprovedExecutionHost host, VolumeCreateSpec spec, CancellationToken token);
    Task<RemoteResourceInspection> InspectVolumeAsync(ApprovedExecutionHost host, string name, CancellationToken token);
    Task<string> CreateContainerAsync(ApprovedExecutionHost host, ContainerCreateSpec spec, CancellationToken token);
    Task<RemoteResourceInspection> InspectContainerAsync(ApprovedExecutionHost host, string name, CancellationToken token);
    Task BootstrapAsync(ApprovedExecutionHost host, string container, byte[] key, CancellationToken token);
    Task StartAsync(ApprovedExecutionHost host, string container, CancellationToken token);
    Task StopAsync(ApprovedExecutionHost host, string container, CancellationToken token);
    Task RemoveContainerAsync(ApprovedExecutionHost host, string container, CancellationToken token);
    Task RemoveVolumeAsync(ApprovedExecutionHost host, string volume, CancellationToken token);
}

public sealed class RemoteWorkerProvisionerAdapter(IRemoteWorkerOperations operations) : IRemoteWorkerProvisioner
{
    public Task<RemoteOperationResult> ProbeAsync(ApprovedExecutionHost host, CancellationToken token) => operations.ExecuteAsync(host, RemoteDockerOperation.Probe, [], null, token);
    public async Task<string> CreateVolumeAsync(ApprovedExecutionHost host, VolumeCreateSpec spec, CancellationToken token) => Require(await operations.CreateVolumeAsync(host, spec, token));
    public async Task<RemoteResourceInspection> InspectVolumeAsync(ApprovedExecutionHost host, string name, CancellationToken token) => Inspect(await operations.ExecuteAsync(host, RemoteDockerOperation.VolumeInspect, [name], null, token));
    public async Task<string> CreateContainerAsync(ApprovedExecutionHost host, ContainerCreateSpec spec, CancellationToken token) => Require(await operations.CreateContainerAsync(host, spec, token));
    public async Task<RemoteResourceInspection> InspectContainerAsync(ApprovedExecutionHost host, string name, CancellationToken token) => Inspect(await operations.ExecuteAsync(host, RemoteDockerOperation.ContainerInspect, [name], null, token));
    public async Task BootstrapAsync(ApprovedExecutionHost host, string container, byte[] key, CancellationToken token) { try { Require(await operations.ExecuteAsync(host, RemoteDockerOperation.Bootstrap, [container], key, token)); } finally { CryptographicOperations.ZeroMemory(key); } }
    public async Task StartAsync(ApprovedExecutionHost host, string container, CancellationToken token) => _ = Require(await operations.ExecuteAsync(host, RemoteDockerOperation.ContainerStart, [container], null, token));
    public async Task StopAsync(ApprovedExecutionHost host, string container, CancellationToken token) => _ = Require(await operations.ExecuteAsync(host, RemoteDockerOperation.ContainerStop, [container], null, token));
    public async Task RemoveContainerAsync(ApprovedExecutionHost host, string container, CancellationToken token) => _ = Require(await operations.ExecuteAsync(host, RemoteDockerOperation.ContainerRemove, [container], null, token));
    public async Task RemoveVolumeAsync(ApprovedExecutionHost host, string volume, CancellationToken token) => _ = Require(await operations.ExecuteAsync(host, RemoteDockerOperation.VolumeRemove, [volume], null, token));
    private static string Require(RemoteOperationResult result) { if (result.ExitCode != 0) throw new InvalidOperationException("Remote provisioning operation failed."); return result.StandardOutput.Trim(); }
    private static RemoteResourceInspection Inspect(RemoteOperationResult result)
    {
        if (result.ExitCode != 0)
        {
            if (result.ErrorCategory == "not-found") return new(false, null, new Dictionary<string, string>(), "absent");
            throw new InvalidOperationException("Remote resource inspection was unavailable.");
        }
        using var d = JsonDocument.Parse(result.StandardOutput);
        var root = d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement[0] : d.RootElement;
        var labelElement = root.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Labels", out var containerLabels) ? containerLabels : root.TryGetProperty("Labels", out var volumeLabels) ? volumeLabels : default;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (labelElement.ValueKind == JsonValueKind.Object) foreach (var p in labelElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) labels[p.Name] = p.Value.GetString()!;
        var id = root.TryGetProperty("Id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : root.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var state = "present";
        if (root.TryGetProperty("State", out var stateElement) && stateElement.ValueKind == JsonValueKind.Object)
        {
            if (stateElement.TryGetProperty("Running", out var running) && running.ValueKind is JsonValueKind.True or JsonValueKind.False) state = running.GetBoolean() ? "running" : "stopped";
            else if (stateElement.TryGetProperty("Status", out var status) && status.ValueKind == JsonValueKind.String) state = status.GetString()!;
        }
        return new(true, id, labels, state);
    }
}

public sealed class RemoteWorkerProvisioningCoordinator
{
    private readonly AcpControlHost _control; private readonly IRemoteWorkerProvisioner _remote; private readonly WorkerControlOptions _options; private readonly IWorkerBridgeSessionFactory _verification;
    public RemoteWorkerProvisioningCoordinator(AcpControlHost control, IRemoteWorkerProvisioner remote, IOptions<WorkerControlOptions> configured, IWorkerBridgeSessionFactory verification) { _control = control; _remote = remote; _options = configured.Value; _verification = verification; }
    public Task<WorkerEnrollmentRecord> PlanAsync(string bindingId, string hostId, CancellationToken token = default)
    {
        RequireEnabled(); var store = Store(); var host = Approved(hostId); var existing = store.ListWorkerEnrollments().SingleOrDefault(x => x.RuntimeBindingId == bindingId); if (existing is not null) { if (existing.HostId != hostId || existing.ExpectedImageDigest != _options.ApprovedImageDigest || existing.ExpectedPlatform != _options.ApprovedImagePlatform) throw new OrganizationConcurrencyException("The binding already has a different worker plan."); _ = ControllerPrivateFile.ReadExact(existing.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32); return Task.FromResult(existing); }
        var worker = DeterministicWorkerId(store.GetOverview().Id, bindingId, hostId, _options.ControllerId); var keyDirectory = Path.Combine(_control.PrivateDataDirectory, "worker-keys"); ControllerPrivateFile.EnsurePrivateDirectory(keyDirectory, _options.ExpectedControllerUid); var keyPath = Path.Combine(keyDirectory, worker + ".key"); var key = RandomNumberGenerator.GetBytes(32); var keyId = HVO.AgentControl.Worker.WorkerProtocol.KeyId(key);
        try
        {
            if (File.Exists(keyPath))
            {
                CryptographicOperations.ZeroMemory(key);
                key = ControllerPrivateFile.ReadExact(keyPath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32);
                keyId = HVO.AgentControl.Worker.WorkerProtocol.KeyId(key);
            }
            else ControllerPrivateFile.PublishExclusive(keyPath, key, _options.ExpectedControllerUid);
            var enrollment = store.CreateWorkerEnrollmentForPlan(bindingId, host.Id, _options.ControllerId, _options.ApprovedImageDigest, _options.ApprovedImagePlatform, keyPath, keyId, worker); var keyOperation = store.AddProvisioningIntent(worker, host.Id, bindingId, "enroll-key", Hash(keyId)); foreach (var volume in new[] { enrollment.ControlVolumeName, enrollment.HomeVolumeName, enrollment.WorkspaceVolumeName, enrollment.SessionVolumeName }) { var op = store.AddProvisioningIntent(worker, host.Id, bindingId, "volume-create", Hash(volume)); store.AddResourceIntent(op.Id, host.Id, worker, "volume", volume, LabelsHash(Identity(enrollment, op.Id))); }
            var containerOp = store.AddProvisioningIntent(worker, host.Id, bindingId, "container-create", Hash(enrollment.ContainerName)); store.AddResourceIntent(containerOp.Id, host.Id, worker, "container", enrollment.ContainerName, LabelsHash(Identity(enrollment, containerOp.Id))); store.AddProvisioningIntent(worker, host.Id, bindingId, "bootstrap", Hash(keyId + enrollment.ContainerName)); store.AddProvisioningIntent(worker, host.Id, bindingId, "start", Hash(enrollment.ContainerName));
            return Task.FromResult(enrollment);
        }
        catch { throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<WorkerEnrollmentRecord> ApplyNextAsync(string workerId, CancellationToken token)
    {
        RequireEnabled(); var store = Store(); var enrollment = store.GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker enrollment not found."); if (enrollment.LifecycleStatus == "planned") enrollment = store.UpdateEnrollmentLifecycle(workerId, enrollment.Revision, "planned", "provisioning"); var operation = store.ListProvisioningOperations(workerId).FirstOrDefault(x => x.State is "Intent" or "Uncertain"); if (operation is null) return enrollment; var host = Approved(enrollment.HostId);
        if (operation.State == "Uncertain") { await ReconcileUncertain(store, enrollment, operation, host, token).ConfigureAwait(false); return store.GetWorkerEnrollment(workerId)!; }
        operation = store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Intent", "Applying"); try { await ApplyEffect(store, enrollment, operation, host, token).ConfigureAwait(false); store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Applying", "Applied", "verified"); } catch { store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Applying", "Uncertain", error: "effect-unknown"); throw; }
        return store.GetWorkerEnrollment(workerId)!;
    }

    public async Task<WorkerEnrollmentRecord> ApplyAllAsync(string workerId, CancellationToken token)
    {
        RequireEnabled();
        while (Store().ListProvisioningOperations(workerId).Any(x => x.State is "Intent" or "Uncertain")) { await ApplyNextAsync(workerId, token).ConfigureAwait(false); var blocked = Store().ListProvisioningOperations(workerId).FirstOrDefault(x => x.State is "Held" or "Failed"); if (blocked is not null) throw new InvalidOperationException($"Provisioning is {blocked.State.ToLowerInvariant()} at {blocked.Kind}: {blocked.ErrorCategory ?? "reconciliation-required"}."); }
        var enrollment = Store().GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker not found."); await using var session = await _verification.ConnectAsync(enrollment, token).ConfigureAwait(false); await session.InvokeAsync("status", new { operation = "status" }, false, token).ConfigureAwait(false); if (enrollment.LifecycleStatus == "provisioning") enrollment = Store().UpdateEnrollmentLifecycle(workerId, enrollment.Revision, "provisioning", "enrolled"); Store().SetExecutionHostEnrolled(enrollment.HostId, true); return enrollment;
    }

    public async Task CleanupAsync(string workerId, CancellationToken token)
    {
        RequireEnabled(); var store = Store(); var enrollment = store.GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker not found."); var host = Approved(enrollment.HostId); foreach (var resource in store.ListWorkerResources(workerId).OrderBy(x => x.ResourceKind == "container" ? 0 : 1).ThenByDescending(x => x.ResourceName, StringComparer.Ordinal)) { if (!resource.Disposable) continue; var inspection = resource.ResourceKind == "container" ? await _remote.InspectContainerAsync(host, resource.ResourceName, token) : await _remote.InspectVolumeAsync(host, resource.ResourceName, token); if (!inspection.Exists) { if (resource.State != "absent") store.TransitionResource(resource.Id, resource.Revision, resource.State, "absent"); continue; } var identity = Identity(enrollment, resource.OperationId); try { RemoteWorkerCommandBuilder.RequireOwnedLabels(inspection.Labels, identity); } catch { store.TransitionResource(resource.Id, resource.Revision, resource.State, "foreign"); continue; } if (resource.ResourceKind == "container") { await _remote.StopAsync(host, resource.ResourceName, token); await _remote.RemoveContainerAsync(host, resource.ResourceName, token); } else await _remote.RemoveVolumeAsync(host, resource.ResourceName, token); store.TransitionResource(resource.Id, resource.Revision, resource.State, "absent"); }
        var resources = store.ListWorkerResources(workerId); if (resources.Any(x => x.State is "foreign" or "uncertain" or "present" or "planned")) throw new InvalidOperationException("Worker cleanup is held until every owned resource is verified absent and no foreign resource is present."); ControllerPrivateFile.SecureUnlink(enrollment.KeyFilePath, _options.ExpectedControllerUid, allowAbsent: true); if (enrollment.LifecycleStatus == "enrolled") store.UpdateEnrollmentLifecycle(workerId, store.GetWorkerEnrollment(workerId)!.Revision, "enrolled", "stopped", false); if (!store.ListWorkerEnrollments().Any(x => x.HostId == enrollment.HostId && x.Enabled && x.LifecycleStatus is not ("stopped" or "failed"))) store.SetExecutionHostEnrolled(enrollment.HostId, false);
    }

    private async Task ApplyEffect(OrganizationStore store, WorkerEnrollmentRecord enrollment, ProvisioningOperationRecord operation, ApprovedExecutionHost host, CancellationToken token)
    {
        var identity = Identity(enrollment, operation.Id); switch (operation.Kind) { case "enroll-key": _ = ControllerPrivateFile.ReadExact(enrollment.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32); break; case "volume-create": { var resource = store.ListWorkerResources(enrollment.WorkerId).Single(x => x.OperationId == operation.Id); var reference = await _remote.CreateVolumeAsync(host, new(resource.ResourceName, identity), token); store.TransitionResource(resource.Id, resource.Revision, "planned", "present", reference); store.SetEnrollmentResourceReference(enrollment.WorkerId, VolumeKind(enrollment, resource.ResourceName), reference); break; } case "container-create": { var resource = store.ListWorkerResources(enrollment.WorkerId).Single(x => x.OperationId == operation.Id); var mounts = new[] { new NamedVolumeMount(enrollment.ControlVolumeName, "/control"), new(enrollment.HomeVolumeName, "/home/worker"), new(enrollment.WorkspaceVolumeName, "/workspace"), new(enrollment.SessionVolumeName, "/session") }; var reference = await _remote.CreateContainerAsync(host, new(enrollment.ContainerName, enrollment.ExpectedImageDigest, enrollment.ExpectedPlatform, identity, mounts, _options.MemoryBytes, _options.CpuLimit, _options.PidsLimit), token); store.TransitionResource(resource.Id, resource.Revision, "planned", "present", reference); store.SetEnrollmentResourceReference(enrollment.WorkerId, "container", reference); break; } case "bootstrap": await _remote.BootstrapAsync(host, enrollment.ContainerName, ControllerPrivateFile.ReadExact(enrollment.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32), token); break; case "start": await _remote.StartAsync(host, enrollment.ContainerName, token); var inspect = await _remote.InspectContainerAsync(host, enrollment.ContainerName, token); if (!inspect.Exists || !inspect.State.Contains("running", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Container did not reach running state."); break; default: throw new InvalidOperationException("Unsupported provisioning operation."); }
    }
    private async Task ReconcileUncertain(OrganizationStore store, WorkerEnrollmentRecord enrollment, ProvisioningOperationRecord operation, ApprovedExecutionHost host, CancellationToken token) { if (operation.Kind == "enroll-key") { _ = ControllerPrivateFile.ReadExact(enrollment.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32); store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Applied", "key-present"); return; } if (operation.Kind == "bootstrap") { await _remote.BootstrapAsync(host, enrollment.ContainerName, ControllerPrivateFile.ReadExact(enrollment.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32), token); store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Applied", "bootstrap-idempotent"); return; } if (operation.Kind == "start") { var running = await _remote.InspectContainerAsync(host, enrollment.ContainerName, token); if (!running.Exists) { store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Held", error: "container-absent"); return; } if (!running.State.Contains("running", StringComparison.OrdinalIgnoreCase)) await _remote.StartAsync(host, enrollment.ContainerName, token); var verified = await _remote.InspectContainerAsync(host, enrollment.ContainerName, token); if (!verified.State.Contains("running", StringComparison.OrdinalIgnoreCase)) { store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Held", error: "start-unverified"); return; } store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Applied", "running"); return; } if (operation.Kind is "volume-create" or "container-create") { var resource = store.ListWorkerResources(enrollment.WorkerId).Single(x => x.OperationId == operation.Id); var inspection = resource.ResourceKind == "volume" ? await _remote.InspectVolumeAsync(host, resource.ResourceName, token) : await _remote.InspectContainerAsync(host, resource.ResourceName, token); if (!inspection.Exists) { store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Held", error: "effect-absent"); return; } try { RemoteWorkerCommandBuilder.RequireOwnedLabels(inspection.Labels, Identity(enrollment, operation.Id)); } catch { store.TransitionResource(resource.Id, resource.Revision, resource.State, "foreign"); store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Held", error: "foreign-resource"); return; } store.TransitionResource(resource.Id, resource.Revision, resource.State, "present", inspection.Reference); store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Applied", "inspected-present"); return; } store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Held", error: "manual-reconciliation-required"); }
    private static WorkerResourceIdentity Identity(WorkerEnrollmentRecord e, string operation) => new(e.OrganizationId, e.ControllerId, e.HostId, e.WorkerId, e.RuntimeBindingId, operation); private static string LabelsHash(WorkerResourceIdentity identity) => Hash(string.Join('\n', identity.Labels.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value))); private static string VolumeKind(WorkerEnrollmentRecord e, string name) => name == e.ControlVolumeName ? "control" : name == e.HomeVolumeName ? "home" : name == e.WorkspaceVolumeName ? "workspace" : "session";
    private ApprovedExecutionHost Approved(string id) => _options.ApprovedHosts.SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Host is not approved."); private void RequireEnabled() { if (!_options.Enabled) throw new InvalidOperationException("Remote worker execution is disabled."); if (_options.Validate().Count != 0) throw new InvalidOperationException("Remote worker configuration is invalid."); }
    private OrganizationStore Store() => _control.Organization ?? throw new OrganizationStoreException("Organization store unavailable."); private static string DeterministicWorkerId(string organizationId, string bindingId, string hostId, string controllerId) => "wrk-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', organizationId, bindingId, hostId, controllerId)))).ToLowerInvariant()[..24]; private static string Hash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
