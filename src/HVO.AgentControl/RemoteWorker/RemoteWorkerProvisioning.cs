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
    Task<HostProbePayload> ProbeAsync(ApprovedExecutionHost host, CancellationToken token);
    Task<string> CreateVolumeAsync(ApprovedExecutionHost host, VolumeCreateSpec spec, CancellationToken token);
    Task<RemoteResourceInspection> InspectVolumeAsync(ApprovedExecutionHost host, string name, CancellationToken token);
    Task<string> CreateContainerAsync(ApprovedExecutionHost host, ContainerCreateSpec spec, CancellationToken token);
    Task<RemoteResourceInspection> InspectContainerAsync(ApprovedExecutionHost host, string name, CancellationToken token);
    Task BootstrapAsync(ApprovedExecutionHost host, BootstrapSpec spec, byte[] key, CancellationToken token);
    Task StartAsync(ApprovedExecutionHost host, string container, CancellationToken token);
    Task StopAsync(ApprovedExecutionHost host, string container, CancellationToken token);
    Task RemoveContainerAsync(ApprovedExecutionHost host, string container, CancellationToken token);
    Task RemoveVolumeAsync(ApprovedExecutionHost host, string volume, CancellationToken token);
}

public sealed class RemoteWorkerProvisionerAdapter(IRemoteWorkerOperations operations) : IRemoteWorkerProvisioner
{
    public Task<HostProbePayload> ProbeAsync(ApprovedExecutionHost host, CancellationToken token) => operations.ProbeHostAsync(host, token);
    public async Task<string> CreateVolumeAsync(ApprovedExecutionHost host, VolumeCreateSpec spec, CancellationToken token) => Require(await operations.CreateVolumeAsync(host, spec, token));
    public async Task<RemoteResourceInspection> InspectVolumeAsync(ApprovedExecutionHost host, string name, CancellationToken token) => Inspect(await operations.ExecuteAsync(host, RemoteDockerOperation.VolumeInspect, [name], null, token));
    public async Task<string> CreateContainerAsync(ApprovedExecutionHost host, ContainerCreateSpec spec, CancellationToken token) => Require(await operations.CreateContainerAsync(host, spec, token));
    public async Task<RemoteResourceInspection> InspectContainerAsync(ApprovedExecutionHost host, string name, CancellationToken token) => Inspect(await operations.ExecuteAsync(host, RemoteDockerOperation.ContainerInspect, [name], null, token));

    /// <summary>
    /// Runs the ephemeral bootstrap container with the controller-encoded key on
    /// standard input. The encoded buffer is zeroed on every path, and so is the
    /// caller's raw key.
    /// </summary>
    public async Task BootstrapAsync(ApprovedExecutionHost host, BootstrapSpec spec, byte[] key, CancellationToken token)
    {
        byte[]? encoded = null;
        try
        {
            encoded = WorkerBootstrapEncoding.Encode(key);
            Require(await operations.BootstrapAsync(host, spec, encoded, token));
        }
        finally
        {
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task StartAsync(ApprovedExecutionHost host, string container, CancellationToken token) => _ = Require(await operations.ExecuteAsync(host, RemoteDockerOperation.ContainerStart, [container], null, token));
    public async Task StopAsync(ApprovedExecutionHost host, string container, CancellationToken token) => _ = Require(await operations.ExecuteAsync(host, RemoteDockerOperation.ContainerStop, [container], null, token));
    public async Task RemoveContainerAsync(ApprovedExecutionHost host, string container, CancellationToken token) => _ = Require(await operations.ExecuteAsync(host, RemoteDockerOperation.ContainerRemove, [container], null, token));
    public async Task RemoveVolumeAsync(ApprovedExecutionHost host, string volume, CancellationToken token) => _ = Require(await operations.ExecuteAsync(host, RemoteDockerOperation.VolumeRemove, [volume], null, token));

    private static string Require(RemoteOperationResult result)
    {
        if (result.ExitCode == 0) return result.StandardOutput.Trim();
        throw new RemoteWorkerUnavailableException("Remote provisioning operation failed.", result.ErrorCategory == "transport");
    }

    private static RemoteResourceInspection Inspect(RemoteOperationResult result)
    {
        if (result.ExitCode != 0)
        {
            // Only the exact Docker "no such ..." answer proves absence. A transport
            // failure or any other error leaves existence unknown and fails closed.
            if (result.ErrorCategory == "not-found") return new(false, null, new Dictionary<string, string>(), "absent");
            throw new RemoteWorkerUnavailableException("Remote resource inspection was unavailable.", result.ErrorCategory == "transport");
        }
        JsonDocument d;
        try { d = JsonDocument.Parse(result.StandardOutput); }
        catch (JsonException exception) { throw new RemoteWorkerUnavailableException("Remote resource inspection returned unparsable output.", transport: false, exception); }
        using (d)
        {
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
}

public sealed class RemoteWorkerProvisioningCoordinator
{
    /// <summary>The number of ordered steps in one fixed plan: key, four volumes, bootstrap, container, start.</summary>
    private const int PlanStepCount = 8;

    private readonly AcpControlHost _control; private readonly IRemoteWorkerProvisioner _remote; private readonly WorkerControlOptions _options; private readonly IWorkerBridgeSessionFactory _verification;
    public RemoteWorkerProvisioningCoordinator(AcpControlHost control, IRemoteWorkerProvisioner remote, IOptions<WorkerControlOptions> configured, IWorkerBridgeSessionFactory verification) { _control = control; _remote = remote; _options = configured.Value; _verification = verification; }

    /// <summary>
    /// Plans an enrollment. <paramref name="imageDigest"/> is the configured base
    /// when null; otherwise it must be a verified profile build digest for this
    /// host (the host's approved set), and it is frozen into the enrollment so a
    /// later change of the approved base or a new build never moves an existing
    /// plan.
    /// </summary>
    public Task<WorkerEnrollmentRecord> PlanAsync(string bindingId, string hostId, CancellationToken token = default) => PlanAsync(bindingId, hostId, null, token);

    public Task<WorkerEnrollmentRecord> PlanAsync(string bindingId, string hostId, string? imageDigest, CancellationToken token = default)
    {
        RequireEnabled();
        var store = Store();
        var host = Approved(hostId);
        var digest = imageDigest ?? _options.ApprovedImageDigest;
        if (!store.ListApprovedImageDigests(host.Id, _options.ApprovedImageDigest).Contains(digest, StringComparer.Ordinal))
            throw new WorkerControlConfigurationException("The requested image is not the approved base or a verified profile build for this host.");
        var existing = store.ListWorkerEnrollments().SingleOrDefault(x => x.RuntimeBindingId == bindingId);
        if (existing is not null)
        {
            if (existing.HostId != hostId || existing.ExpectedImageDigest != digest || existing.ExpectedPlatform != _options.ApprovedImagePlatform) throw new OrganizationConcurrencyException("The binding already has a different worker plan.");
            _ = ControllerPrivateFile.ReadExact(existing.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32);
            return Task.FromResult(existing);
        }

        var worker = DeterministicWorkerId(store.GetOverview().Id, bindingId, hostId, _options.ControllerId);
        var keyDirectory = Path.Combine(_control.PrivateDataDirectory, "worker-keys");
        ControllerPrivateFile.EnsurePrivateDirectory(keyDirectory, _options.ExpectedControllerUid);
        var keyPath = Path.Combine(keyDirectory, worker + ".key");
        var key = RandomNumberGenerator.GetBytes(32);
        var keyId = HVO.AgentControl.Worker.WorkerProtocol.KeyId(key);
        try
        {
            if (File.Exists(keyPath))
            {
                CryptographicOperations.ZeroMemory(key);
                key = ControllerPrivateFile.ReadExact(keyPath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32);
                keyId = HVO.AgentControl.Worker.WorkerProtocol.KeyId(key);
            }
            else ControllerPrivateFile.PublishExclusive(keyPath, key, _options.ExpectedControllerUid);

            var enrollment = store.CreateWorkerEnrollmentForPlan(bindingId, host.Id, _options.ControllerId, digest, _options.ApprovedImagePlatform, keyPath, keyId, worker);
            store.AddProvisioningIntent(worker, host.Id, bindingId, "enroll-key", Hash(keyId));

            // The control volume and the ephemeral key bootstrap are applied before
            // the other volumes and the long-lived container, so the enrolled key is
            // already in the control volume before any supervisor can start.
            foreach (var volume in new[] { enrollment.ControlVolumeName, enrollment.HomeVolumeName, enrollment.WorkspaceVolumeName, enrollment.SessionVolumeName })
            {
                var op = store.AddProvisioningIntent(worker, host.Id, bindingId, "volume-create", Hash(volume));
                store.AddResourceIntent(op.Id, host.Id, worker, "volume", volume, LabelsHash(Identity(enrollment, op.Id)));
            }
            store.AddProvisioningIntent(worker, host.Id, bindingId, "bootstrap", Hash(keyId + enrollment.ControlVolumeName));

            var containerOp = store.AddProvisioningIntent(worker, host.Id, bindingId, "container-create", Hash(enrollment.ContainerName));
            store.AddResourceIntent(containerOp.Id, host.Id, worker, "container", enrollment.ContainerName, LabelsHash(Identity(enrollment, containerOp.Id)));
            store.AddProvisioningIntent(worker, host.Id, bindingId, "start", Hash(enrollment.ContainerName));
            return Task.FromResult(enrollment);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<WorkerEnrollmentRecord> ApplyNextAsync(string workerId, CancellationToken token)
    {
        RequireEnabled();
        var store = Store();
        var enrollment = store.GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker enrollment not found.");
        if (enrollment.LifecycleStatus == "planned") enrollment = store.UpdateEnrollmentLifecycle(workerId, enrollment.Revision, "planned", "provisioning");
        var operation = NextPending(store, enrollment);
        if (operation is null) return enrollment;
        var host = Approved(enrollment.HostId);

        if (operation.State == "Uncertain")
        {
            await ReconcileUncertain(store, enrollment, operation, host, token).ConfigureAwait(false);
            return store.GetWorkerEnrollment(workerId)!;
        }

        operation = store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Intent", "Applying");
        try
        {
            await ApplyEffect(store, enrollment, operation, host, token).ConfigureAwait(false);
            store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Applying", "Applied", "verified");
        }
        catch
        {
            store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Applying", "Uncertain", error: "effect-unknown");
            throw;
        }
        return store.GetWorkerEnrollment(workerId)!;
    }

    public async Task<WorkerEnrollmentRecord> ApplyAllAsync(string workerId, CancellationToken token)
    {
        RequireEnabled();
        // Bounded by the fixed plan size plus one reconciliation attempt each, so a
        // step that neither applies nor holds can never spin.
        var remaining = PlanStepCount * 2;
        while (Store().GetWorkerEnrollment(workerId) is { } pending && NextPending(Store(), pending) is not null)
        {
            if (remaining-- <= 0) throw new WorkerRecoveryRequiredException("Provisioning did not converge and requires operator reconciliation.", "provisioning-stalled");
            await ApplyNextAsync(workerId, token).ConfigureAwait(false);
            var blocked = Store().ListProvisioningOperations(workerId).FirstOrDefault(x => x.State is "Held" or "Failed");
            if (blocked is not null) throw new WorkerRecoveryRequiredException($"Provisioning is {blocked.State.ToLowerInvariant()} at {blocked.Kind}.", blocked.ErrorCategory ?? "reconciliation-required");
        }
        var enrollment = Store().GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker not found.");
        await using var session = await _verification.ConnectAsync(enrollment, token).ConfigureAwait(false);
        var statusResult = await session.InvokeAsync("status", new { operation = "status" }, false, token).ConfigureAwait(false);
        var status = JsonSerializer.Deserialize<BridgeWorkerStatus>(statusResult.Result.GetRawText(), HVO.AgentControl.Worker.WorkerProtocol.JsonOptions) ?? throw new HVO.AgentControl.Worker.WorkerProtocolException("Worker status is invalid.");
        status = await EnsureProvisionedSessionAsync(Store(), enrollment, session, status, token).ConfigureAwait(false);
        var authoritative = Store().GetRemoteBindingSession(enrollment.RuntimeBindingId);
        if (authoritative.NativeSessionId is not { } nativeSessionId) throw new OrganizationConcurrencyException("Provisioning did not establish an authoritative worker session.");
        WorkerConnectionManager.EnsureSessionReadyForWork(Store(), workerId, nativeSessionId, status);
        if (enrollment.LifecycleStatus == "provisioning") enrollment = Store().UpdateEnrollmentLifecycle(workerId, enrollment.Revision, "provisioning", "enrolled");
        Store().SetExecutionHostEnrolled(enrollment.HostId, true);
        return enrollment;
    }

    private static async Task<BridgeWorkerStatus> EnsureProvisionedSessionAsync(OrganizationStore store, WorkerEnrollmentRecord enrollment, IWorkerBridgeSession session, BridgeWorkerStatus status, CancellationToken token)
    {
        if (status.ProcessState != "running" || !status.AcpInitialized) throw new OrganizationConcurrencyException("Provisioning verification requires an initialized running ACP process.");
        if (status.SessionOperationState == "uncertain") throw new WorkerRecoveryRequiredException("ACP session creation is uncertain; replace and re-enroll the worker container.", "session-create-uncertain");
        var binding = store.GetRemoteBindingSession(enrollment.RuntimeBindingId);
        if (binding.Placement != "DeveloperContainer") throw new OrganizationConcurrencyException("Worker enrollment runtime binding is not a DeveloperContainer placement.");
        if (binding.NativeSessionId is { } recorded)
        {
            if (status.SessionId is null)
            {
                await session.InvokeAsync("load-session", session.Mutation("load-session", new Dictionary<string, object?> { ["sessionId"] = recorded }), true, token).ConfigureAwait(false);
                status = await ReadStatusAsync(session, token).ConfigureAwait(false);
            }
            if (status.SessionId != recorded) throw new OrganizationConcurrencyException("Worker session does not match the authoritative provisioning session.");
            return status;
        }
        var nativeSessionId = status.SessionId;
        if (nativeSessionId is null)
        {
            var created = await session.InvokeAsync("new-session", session.Mutation("new-session", new Dictionary<string, object?>()), true, token).ConfigureAwait(false);
            if (created.Result.ValueKind != JsonValueKind.String || created.Result.GetString() is not { } value) throw new HVO.AgentControl.Worker.WorkerProtocolException("Worker session creation result is invalid.");
            HVO.AgentControl.Worker.WorkerProtocol.ValidateIdentifier(value, HVO.AgentControl.Worker.WorkerProtocol.MaxIdentifierLength, "session id");
            nativeSessionId = value;
            status = await ReadStatusAsync(session, token).ConfigureAwait(false);
            if (status.SessionId != nativeSessionId) throw new OrganizationConcurrencyException("Worker did not retain the newly created session.");
        }
        store.RecordRemoteWorkerSession(enrollment.RuntimeBindingId, enrollment.WorkerId, nativeSessionId, "Remote worker");
        return status;
    }

    private static async Task<BridgeWorkerStatus> ReadStatusAsync(IWorkerBridgeSession session, CancellationToken token)
    {
        var result = await session.InvokeAsync("status", new { operation = "status" }, false, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BridgeWorkerStatus>(result.Result.GetRawText(), HVO.AgentControl.Worker.WorkerProtocol.JsonOptions) ?? throw new HVO.AgentControl.Worker.WorkerProtocolException("Worker status is invalid.");
    }

    /// <summary>
    /// Removes exactly the resources this controller owns on this host. Absence is
    /// only ever concluded from the exact Docker "no such" answer; when the host or
    /// its transport is unavailable the resource is recorded uncertain, cleanup
    /// stops, and the enrolled key file is never deleted.
    /// </summary>
    public async Task CleanupAsync(string workerId, CancellationToken token)
    {
        RequireEnabled();
        var store = Store();
        var enrollment = store.GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker not found.");
        var host = Approved(enrollment.HostId);
        foreach (var resource in store.ListWorkerResources(workerId).OrderBy(x => x.ResourceKind == "container" ? 0 : 1).ThenByDescending(x => x.ResourceName, StringComparer.Ordinal))
        {
            if (!resource.Disposable) continue;
            RemoteResourceInspection inspection;
            try
            {
                inspection = resource.ResourceKind == "container"
                    ? await _remote.InspectContainerAsync(host, resource.ResourceName, token).ConfigureAwait(false)
                    : await _remote.InspectVolumeAsync(host, resource.ResourceName, token).ConfigureAwait(false);
            }
            catch (RemoteWorkerUnavailableException)
            {
                if (resource.State != "uncertain") store.TransitionResource(resource.Id, resource.Revision, resource.State, "uncertain");
                throw;
            }

            if (!inspection.Exists)
            {
                if (resource.State != "absent") store.TransitionResource(resource.Id, resource.Revision, resource.State, "absent");
                continue;
            }

            // The persisted enrollment carries the organization that owns it, so a
            // renamed or replaced current organization cannot widen this match.
            var identity = Identity(enrollment, resource.OperationId);
            try { RemoteWorkerCommandBuilder.RequireOwnedLabels(inspection.Labels, identity); }
            catch (ForeignResourceException) { store.TransitionResource(resource.Id, resource.Revision, resource.State, "foreign"); continue; }

            try
            {
                if (resource.ResourceKind == "container")
                {
                    await _remote.StopAsync(host, resource.ResourceName, token).ConfigureAwait(false);
                    await _remote.RemoveContainerAsync(host, resource.ResourceName, token).ConfigureAwait(false);
                }
                else await _remote.RemoveVolumeAsync(host, resource.ResourceName, token).ConfigureAwait(false);
            }
            catch (RemoteWorkerUnavailableException)
            {
                if (resource.State != "uncertain") store.TransitionResource(resource.Id, resource.Revision, resource.State, "uncertain");
                throw;
            }
            store.TransitionResource(resource.Id, resource.Revision, resource.State, "absent");
        }

        var resources = store.ListWorkerResources(workerId);
        if (resources.Any(x => x.State is "foreign" or "uncertain" or "present" or "planned")) throw new WorkerRecoveryRequiredException("Worker cleanup is held until every owned resource is verified absent and no foreign resource is present.", "cleanup-unverified");
        ControllerPrivateFile.SecureUnlink(enrollment.KeyFilePath, _options.ExpectedControllerUid, allowAbsent: true);
        if (enrollment.LifecycleStatus == "enrolled") store.UpdateEnrollmentLifecycle(workerId, store.GetWorkerEnrollment(workerId)!.Revision, "enrolled", "stopped", false);
        if (!store.ListWorkerEnrollments().Any(x => x.HostId == enrollment.HostId && x.Enabled && x.LifecycleStatus is not ("stopped" or "failed"))) store.SetExecutionHostEnrolled(enrollment.HostId, false);
    }

    /// <summary>
    /// Selects the next pending step in the fixed plan order rather than in
    /// insertion order, so ordering is a property of the plan and not of how the
    /// rows happen to be stored.
    /// </summary>
    private static ProvisioningOperationRecord? NextPending(OrganizationStore store, WorkerEnrollmentRecord enrollment)
    {
        var resources = store.ListWorkerResources(enrollment.WorkerId).ToDictionary(x => x.OperationId, x => x.ResourceName, StringComparer.Ordinal);
        return store.ListProvisioningOperations(enrollment.WorkerId)
            .Where(x => x.State is "Intent" or "Uncertain")
            .OrderBy(x => Rank(x, enrollment, resources))
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int Rank(ProvisioningOperationRecord operation, WorkerEnrollmentRecord enrollment, IReadOnlyDictionary<string, string> resources) => operation.Kind switch
    {
        "enroll-key" => 0,
        "volume-create" when resources.TryGetValue(operation.Id, out var name) && name == enrollment.ControlVolumeName => 1,
        "bootstrap" => 2,
        "volume-create" => 3,
        "container-create" => 4,
        "start" => 5,
        _ => 6,
    };

    private async Task ApplyEffect(OrganizationStore store, WorkerEnrollmentRecord enrollment, ProvisioningOperationRecord operation, ApprovedExecutionHost host, CancellationToken token)
    {
        var identity = Identity(enrollment, operation.Id);
        switch (operation.Kind)
        {
            case "enroll-key":
                _ = ControllerPrivateFile.ReadExact(enrollment.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32);
                break;
            case "volume-create":
                {
                    var resource = store.ListWorkerResources(enrollment.WorkerId).Single(x => x.OperationId == operation.Id);
                    var reference = await _remote.CreateVolumeAsync(host, new(resource.ResourceName, identity), token).ConfigureAwait(false);
                    store.TransitionResource(resource.Id, resource.Revision, "planned", "present", reference);
                    store.SetEnrollmentResourceReference(enrollment.WorkerId, VolumeKind(enrollment, resource.ResourceName), reference);
                    break;
                }
            case "bootstrap":
                await _remote.BootstrapAsync(host, BootstrapFor(enrollment, operation), Key(enrollment), token).ConfigureAwait(false);
                break;
            case "container-create":
                {
                    var resource = store.ListWorkerResources(enrollment.WorkerId).Single(x => x.OperationId == operation.Id);
                    var mounts = new[] { new NamedVolumeMount(enrollment.ControlVolumeName, "/control"), new(enrollment.HomeVolumeName, "/home/worker"), new(enrollment.WorkspaceVolumeName, "/workspace"), new(enrollment.SessionVolumeName, "/session") };
                    var approved = store.ListApprovedImageDigests(host.Id, _options.ApprovedImageDigest);
                    var reference = await _remote.CreateContainerAsync(host, new(enrollment.ContainerName, enrollment.ExpectedImageDigest, enrollment.ExpectedPlatform, identity, mounts, _options.MemoryBytes, _options.CpuLimit, _options.PidsLimit, approved), token).ConfigureAwait(false);
                    store.TransitionResource(resource.Id, resource.Revision, "planned", "present", reference);
                    store.SetEnrollmentResourceReference(enrollment.WorkerId, "container", reference);
                    break;
                }
            case "start":
                {
                    await _remote.StartAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
                    var inspect = await _remote.InspectContainerAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
                    if (!inspect.Exists || !inspect.State.Contains("running", StringComparison.OrdinalIgnoreCase)) throw new RemoteWorkerUnavailableException("Container did not reach running state.");
                    break;
                }
            default: throw new WorkerControlConfigurationException("Unsupported provisioning operation.");
        }
    }

    private async Task ReconcileUncertain(OrganizationStore store, WorkerEnrollmentRecord enrollment, ProvisioningOperationRecord operation, ApprovedExecutionHost host, CancellationToken token)
    {
        try
        {
            switch (operation.Kind)
            {
                case "enroll-key":
                    _ = ControllerPrivateFile.ReadExact(enrollment.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32);
                    store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Applied", "key-present");
                    return;
                case "bootstrap":
                    // Repeating the bootstrap is safe: the worker verifies the enrolled
                    // key and treats an identical key as a no-op, so a repeat converges
                    // and a different key fails closed rather than overwriting.
                    await _remote.BootstrapAsync(host, BootstrapFor(enrollment, operation), Key(enrollment), token).ConfigureAwait(false);
                    store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Applied", "bootstrap-idempotent");
                    return;
                case "start":
                    {
                        var running = await _remote.InspectContainerAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
                        if (!running.Exists) { store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Held", error: "container-absent"); return; }
                        if (!running.State.Contains("running", StringComparison.OrdinalIgnoreCase)) await _remote.StartAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
                        var verified = await _remote.InspectContainerAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
                        if (!verified.State.Contains("running", StringComparison.OrdinalIgnoreCase)) { store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Held", error: "start-unverified"); return; }
                        store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Applied", "running");
                        return;
                    }
                case "volume-create":
                case "container-create":
                    {
                        var resource = store.ListWorkerResources(enrollment.WorkerId).Single(x => x.OperationId == operation.Id);
                        var inspection = resource.ResourceKind == "volume"
                            ? await _remote.InspectVolumeAsync(host, resource.ResourceName, token).ConfigureAwait(false)
                            : await _remote.InspectContainerAsync(host, resource.ResourceName, token).ConfigureAwait(false);
                        if (!inspection.Exists) { store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Held", error: "effect-absent"); return; }
                        try { RemoteWorkerCommandBuilder.RequireOwnedLabels(inspection.Labels, Identity(enrollment, operation.Id)); }
                        catch (ForeignResourceException)
                        {
                            store.TransitionResource(resource.Id, resource.Revision, resource.State, "foreign");
                            store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Held", error: "foreign-resource");
                            return;
                        }
                        store.TransitionResource(resource.Id, resource.Revision, resource.State, "present", inspection.Reference);
                        store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Applied", "inspected-present");
                        return;
                    }
                default:
                    store.TransitionProvisioningOperation(operation.Id, operation.Revision, "Uncertain", "Held", error: "manual-reconciliation-required");
                    return;
            }
        }
        catch (Exception exception) when (exception is RemoteWorkerUnavailableException or WorkerControlConfigurationException or ForeignResourceException)
        {
            // Reconciliation itself failed, so the effect stays unknown. Hold the step
            // instead of leaving it pending, which would re-enter this path forever.
            var current = store.GetProvisioningOperation(operation.Id);
            if (current is { State: "Uncertain" }) store.TransitionProvisioningOperation(current.Id, current.Revision, "Uncertain", "Held", error: "reconciliation-unavailable");
            throw;
        }
    }

    private BootstrapSpec BootstrapFor(WorkerEnrollmentRecord enrollment, ProvisioningOperationRecord operation) =>
        new(enrollment.ControlVolumeName, enrollment.ExpectedImageDigest, enrollment.ExpectedPlatform, Identity(enrollment, operation.Id));

    private byte[] Key(WorkerEnrollmentRecord enrollment) =>
        ControllerPrivateFile.ReadExact(enrollment.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32);

    private static WorkerResourceIdentity Identity(WorkerEnrollmentRecord e, string operation) => new(e.OrganizationId, e.ControllerId, e.HostId, e.WorkerId, e.RuntimeBindingId, operation);
    private static string LabelsHash(WorkerResourceIdentity identity) => Hash(string.Join('\n', identity.Labels.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)));
    private static string VolumeKind(WorkerEnrollmentRecord e, string name) => name == e.ControlVolumeName ? "control" : name == e.HomeVolumeName ? "home" : name == e.WorkspaceVolumeName ? "workspace" : "session";
    private ApprovedExecutionHost Approved(string id) => _options.ApprovedHosts.SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Host is not approved.");
    private void RequireEnabled()
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        if (_options.Validate().Count != 0) throw new WorkerControlConfigurationException("Remote worker configuration is invalid.");
    }
    private OrganizationStore Store() => _control.Organization ?? throw new OrganizationStoreException("Organization store unavailable.");
    private static string DeterministicWorkerId(string organizationId, string bindingId, string hostId, string controllerId) => "wrk-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', organizationId, bindingId, hostId, controllerId)))).ToLowerInvariant()[..24];
    private static string Hash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
