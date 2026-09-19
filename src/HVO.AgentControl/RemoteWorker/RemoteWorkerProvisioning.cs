using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

public sealed record RemoteResourceInspection(bool Exists, string? Reference, IReadOnlyDictionary<string, string> Labels, string State);

/// <summary>
/// The durable outcome of replacing a worker container to pick up a newly
/// installed orientation. It carries the exact enrollment, the fresh process
/// status, the advanced process generation and the authoritative native session
/// that was loaded in the new process.
/// </summary>
public sealed record ContainerReplacementResult(
    WorkerEnrollmentRecord Enrollment,
    BridgeWorkerStatus Status,
    long ProcessGeneration,
    string NativeSessionId);

/// <summary>
/// Provisioning operations addressed by an <see cref="ExecutionTarget"/>. The
/// real adapter refuses a controller-local target in this build; the SSH target
/// carries its configured approved host.
/// </summary>
public interface IRemoteWorkerProvisioner
{
    Task<HostProbePayload> ProbeAsync(ExecutionTarget target, CancellationToken token);
    Task<string> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec spec, CancellationToken token);
    Task<RemoteResourceInspection> InspectVolumeAsync(ExecutionTarget target, string name, CancellationToken token);
    Task<string> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec spec, CancellationToken token);
    Task<RemoteResourceInspection> InspectContainerAsync(ExecutionTarget target, string name, CancellationToken token);
    Task BootstrapAsync(ExecutionTarget target, BootstrapSpec spec, byte[] key, CancellationToken token);
    Task StartAsync(ExecutionTarget target, string container, CancellationToken token);
    Task StopAsync(ExecutionTarget target, string container, CancellationToken token);
    Task RemoveContainerAsync(ExecutionTarget target, string container, CancellationToken token);
    Task RemoveVolumeAsync(ExecutionTarget target, string volume, CancellationToken token);
}

public sealed class RemoteWorkerProvisionerAdapter(IRemoteWorkerOperations operations) : IRemoteWorkerProvisioner
{
    public Task<HostProbePayload> ProbeAsync(ExecutionTarget target, CancellationToken token) => operations.ProbeHostAsync(RequireSsh(target), token);
    public async Task<string> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec spec, CancellationToken token) => Require(await operations.CreateVolumeAsync(RequireSsh(target), spec, token));
    public async Task<RemoteResourceInspection> InspectVolumeAsync(ExecutionTarget target, string name, CancellationToken token) => Inspect(await operations.ExecuteAsync(RequireSsh(target), RemoteDockerOperation.VolumeInspect, [name], null, token));
    public async Task<string> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec spec, CancellationToken token) => Require(await operations.CreateContainerAsync(RequireSsh(target), spec, token));
    public async Task<RemoteResourceInspection> InspectContainerAsync(ExecutionTarget target, string name, CancellationToken token) => Inspect(await operations.ExecuteAsync(RequireSsh(target), RemoteDockerOperation.ContainerInspect, [name], null, token));

    /// <summary>
    /// Runs the ephemeral bootstrap container with the controller-encoded key on
    /// standard input. The encoded buffer is zeroed on every path, and so is the
    /// caller's raw key.
    /// </summary>
    public async Task BootstrapAsync(ExecutionTarget target, BootstrapSpec spec, byte[] key, CancellationToken token)
    {
        byte[]? encoded = null;
        try
        {
            encoded = WorkerBootstrapEncoding.Encode(key);
            Require(await operations.BootstrapAsync(RequireSsh(target), spec, encoded, token));
        }
        finally
        {
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task StartAsync(ExecutionTarget target, string container, CancellationToken token) => _ = Require(await operations.ExecuteAsync(RequireSsh(target), RemoteDockerOperation.ContainerStart, [container], null, token));
    public async Task StopAsync(ExecutionTarget target, string container, CancellationToken token) => _ = Require(await operations.ExecuteAsync(RequireSsh(target), RemoteDockerOperation.ContainerStop, [container], null, token));
    public async Task RemoveContainerAsync(ExecutionTarget target, string container, CancellationToken token) => _ = Require(await operations.ExecuteAsync(RequireSsh(target), RemoteDockerOperation.ContainerRemove, [container], null, token));
    public async Task RemoveVolumeAsync(ExecutionTarget target, string volume, CancellationToken token) => _ = Require(await operations.ExecuteAsync(RequireSsh(target), RemoteDockerOperation.VolumeRemove, [volume], null, token));

    /// <summary>
    /// Routes a controller-local target to the fail-closed implementation. The
    /// local execution path is not implemented in this build, so the call must
    /// throw rather than fall through to an SSH operation.
    /// </summary>
    public static ApprovedExecutionHost RequireSsh(ExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Ssh ?? throw new WorkerControlConfigurationException(LocalDockerUnavailableOperations.Message);
    }

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

    private readonly AcpControlHost _control; private readonly IRemoteWorkerProvisioner _remote; private readonly WorkerControlOptions _options; private readonly IWorkerBridgeSessionFactory _verification; private readonly ExecutionTargetResolver _targets;
    public RemoteWorkerProvisioningCoordinator(AcpControlHost control, IRemoteWorkerProvisioner remote, IOptions<WorkerControlOptions> configured, IWorkerBridgeSessionFactory verification) { _control = control; _remote = remote; _options = configured.Value; _verification = verification; _targets = new ExecutionTargetResolver(control, configured); }

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

        // A managed binding carries owner-frozen resources. It may only be planned
        // through the managed path, never by a caller naming a host or digest: a
        // manual API must not be able to redirect an owner-approved hire.
        var managed = store.GetManagedEnrollmentResources(bindingId);
        if (managed is not null)
            throw new OrganizationValidationException("A managed enrollment is provisioned from its frozen owner approval and cannot be planned by host or digest.");

        var host = _targets.Resolve(hostId).Ssh ?? throw new WorkerControlConfigurationException(LocalDockerUnavailableOperations.Message);
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

    /// <summary>
    /// Plans a managed binding from its frozen owner approval with no host or
    /// digest argument: both are derived from the managed resources. Managed
    /// hiring in this step provisions only on the controller-local Docker target,
    /// so any other frozen host is refused. The frozen limits must fit under the
    /// current global ceilings. After the enrollment exists the approval is linked
    /// to the exact worker identity under its own revision, and an exact replay is
    /// idempotent.
    /// </summary>
    public Task<WorkerEnrollmentRecord> PlanManagedAsync(string bindingId, CancellationToken token = default)
    {
        RequireEnabled();
        var store = Store();
        var managed = store.GetManagedEnrollmentResources(bindingId)
            ?? throw new OrganizationValidationException("The binding has no frozen managed resources.");
        return Task.FromResult(PlanManaged(store, managed, bindingId));
    }

    private WorkerEnrollmentRecord PlanManaged(OrganizationStore store, ManagedEnrollmentResourcesRecord managed, string bindingId)
    {
        var hostId = managed.ApprovedHostId;
        if (!string.Equals(hostId, ExecutionHosts.LocalDockerId, StringComparison.Ordinal))
            throw new WorkerControlConfigurationException("Managed hires provision only on the controller-local Docker target.");
        if (managed.MemoryLimitMiB > _options.MemoryBytes / (1024 * 1024)
            || managed.CpuLimit > _options.CpuLimit
            || managed.PidsLimit > _options.PidsLimit)
            throw new WorkerControlConfigurationException("The frozen managed resources exceed the controller's global ceilings.");
        if (!string.Equals(managed.Platform, _options.ApprovedImagePlatform, StringComparison.Ordinal))
            throw new WorkerControlConfigurationException("The frozen managed platform differs from controller policy.");

        // The frozen local target must still be present and usable; approval does
        // not bypass the live host gate. CreateWorkerEnrollmentForPlan re-checks
        // that the host is enabled and ready in the authoritative store.
        _ = _targets.Resolve(managed.ApprovedHostId);
        var approval = store.GetHireRequestApprovalByBinding(bindingId)
            ?? throw new OrganizationStoreCorruptException("A managed binding has frozen resources but no owner approval.");

        var existing = store.ListWorkerEnrollments().SingleOrDefault(x => x.RuntimeBindingId == bindingId);
        if (existing is not null)
        {
            if (existing.HostId != managed.ApprovedHostId
                || existing.ExpectedImageDigest != managed.ApprovedImageDigest
                || existing.ExpectedPlatform != managed.Platform)
                throw new OrganizationConcurrencyException("The binding already has a different worker plan.");
            _ = ControllerPrivateFile.ReadExact(existing.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32);
            LinkManagedWorker(store, approval, bindingId, existing.WorkerId);
            return existing;
        }

        var worker = DeterministicWorkerId(store.GetOverview().Id, bindingId, managed.ApprovedHostId, _options.ControllerId);
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

            var enrollment = store.CreateWorkerEnrollmentForPlan(bindingId, managed.ApprovedHostId, _options.ControllerId, managed.ApprovedImageDigest, managed.Platform, keyPath, keyId, worker);
            store.AddProvisioningIntent(worker, managed.ApprovedHostId, bindingId, "enroll-key", Hash(keyId));
            foreach (var volume in new[] { enrollment.ControlVolumeName, enrollment.HomeVolumeName, enrollment.WorkspaceVolumeName, enrollment.SessionVolumeName })
            {
                var op = store.AddProvisioningIntent(worker, managed.ApprovedHostId, bindingId, "volume-create", Hash(volume));
                store.AddResourceIntent(op.Id, managed.ApprovedHostId, worker, "volume", volume, LabelsHash(Identity(enrollment, op.Id)));
            }
            store.AddProvisioningIntent(worker, managed.ApprovedHostId, bindingId, "bootstrap", Hash(keyId + enrollment.ControlVolumeName));
            var containerOp = store.AddProvisioningIntent(worker, managed.ApprovedHostId, bindingId, "container-create", Hash(enrollment.ContainerName));
            store.AddResourceIntent(containerOp.Id, managed.ApprovedHostId, worker, "container", enrollment.ContainerName, LabelsHash(Identity(enrollment, containerOp.Id)));
            store.AddProvisioningIntent(worker, managed.ApprovedHostId, bindingId, "start", Hash(enrollment.ContainerName));

            LinkManagedWorker(store, approval, bindingId, worker);
            return enrollment;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void LinkManagedWorker(OrganizationStore store, HireRequestApprovalRecord approval, string bindingId, string workerId)
    {
        if (approval.EmployeeId is null || approval.RuntimeBindingId is null)
            throw new OrganizationStoreCorruptException("A managed binding's owner approval has no employee or binding link.");
        if (!string.Equals(approval.RuntimeBindingId, bindingId, StringComparison.Ordinal))
            throw new OrganizationStoreCorruptException("A managed binding's frozen resources and owner approval disagree.");
        // LinkHireWorker is revision-bound and idempotent for an identical worker.
        _ = store.LinkHireWorker(approval.HireRequestId, approval.EmployeeId, approval.RuntimeBindingId, workerId, approval.Revision);
    }

    public async Task<WorkerEnrollmentRecord> ApplyNextAsync(string workerId, CancellationToken token)
    {
        RequireEnabled();
        var store = Store();
        var enrollment = store.GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker enrollment not found.");
        if (enrollment.LifecycleStatus == "planned") enrollment = store.UpdateEnrollmentLifecycle(workerId, enrollment.Revision, "planned", "provisioning");
        var operation = NextPending(store, enrollment);
        if (operation is null) return enrollment;
        var host = _targets.Resolve(enrollment.HostId);

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
        var host = _targets.Resolve(enrollment.HostId);
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

    private async Task ApplyEffect(OrganizationStore store, WorkerEnrollmentRecord enrollment, ProvisioningOperationRecord operation, ExecutionTarget host, CancellationToken token)
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
                    var reference = await _remote.CreateContainerAsync(host, ContainerSpecFor(store, enrollment, host, identity), token).ConfigureAwait(false);
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

    /// <summary>
    /// Replaces the enrolled worker's container so a freshly installed orientation
    /// artifact is loaded by a new process, preserving the four volumes and the
    /// authoritative native session. This is a deliberate replacement, not cleanup:
    /// the original owned container is removed after verifying its exact identity
    /// labels, and every volume is left untouched. It is owner-orchestration and
    /// safe to repeat: an absent owned container is created, a stopped owned
    /// container is started, a foreign object at the name is refused, and a crash
    /// after removal, creation, or start converges on the next call by inspection.
    /// </summary>
    public async Task<ContainerReplacementResult> ReplaceContainerForOrientationAsync(string workerId, CancellationToken token)
    {
        RequireEnabled();
        var store = Store();
        var enrollment = store.GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker enrollment not found.");

        // The expected lifecycle is enrolled: the container has already gone through
        // the fixed plan and the authoritative session exists. A stopped or failed
        // enrollment cannot be replaced into service, and a planned one has no
        // container-create operation to derive an exact owned identity from.
        if (enrollment.LifecycleStatus != "enrolled")
            throw new OrganizationConcurrencyException("Container replacement requires an enrolled worker.");

        var resource = store.ListWorkerResources(workerId).SingleOrDefault(x => x.ResourceKind == "container")
            ?? throw new OrganizationConcurrencyException("The worker has no owned container resource to replace.");
        var host = _targets.Resolve(enrollment.HostId);
        var identity = Identity(enrollment, resource.OperationId);

        // Inspect before touching anything. Absence is only concluded from the exact
        // Docker "no such" answer; a foreign object at the name fails closed.
        var needsCreate = false;
        var inspection = await _remote.InspectContainerAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
        if (inspection.Exists)
        {
            RemoteWorkerCommandBuilder.RequireOwnedLabels(inspection.Labels, identity);
            if (inspection.State.Contains("running", StringComparison.OrdinalIgnoreCase))
            {
                // Present and running: stop, then remove, the deliberate replacement.
                await _remote.StopAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
                await _remote.RemoveContainerAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
                needsCreate = true;
            }
            else
            {
                // Present and stopped is the crash-after-create (or crash-after-stop)
                // case: start the owned container rather than removing and recreating.
                await _remote.StartAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
            }
        }
        else
        {
            // Absent, including crash-after-remove: nothing to stop or remove. A
            // repeated call never removes blindly; it recreates the owned container.
            needsCreate = true;
        }

        if (needsCreate)
        {
            var reference = await _remote.CreateContainerAsync(host, ContainerSpecFor(store, enrollment, host, identity), token).ConfigureAwait(false);
            store.TransitionResource(resource.Id, resource.Revision, resource.State, "present", reference);
            store.ReplaceEnrollmentContainerReference(workerId, reference);
            await _remote.StartAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
        }

        var started = await _remote.InspectContainerAsync(host, enrollment.ContainerName, token).ConfigureAwait(false);
        if (!started.Exists || !started.State.Contains("running", StringComparison.OrdinalIgnoreCase))
            throw new RemoteWorkerUnavailableException("The replacement worker container did not reach running state.", transport: false);

        // A fresh process has no in-memory session. Load the authoritative native
        // session through the verification bridge so the replacement continues the
        // employee's history rather than starting a new one, then require the
        // session to be ready for work before returning.
        await using var session = await _verification.ConnectAsync(store.GetWorkerEnrollment(workerId)!, token).ConfigureAwait(false);
        var status = await ReadStatusAsync(session, token).ConfigureAwait(false);
        status = await EnsureProvisionedSessionAsync(store, store.GetWorkerEnrollment(workerId)!, session, status, token).ConfigureAwait(false);
        var authoritative = store.GetRemoteBindingSession(enrollment.RuntimeBindingId);
        if (authoritative.NativeSessionId is not { } nativeSessionId) throw new OrganizationConcurrencyException("Replacement did not establish an authoritative worker session.");
        WorkerConnectionManager.EnsureSessionReadyForWork(store, workerId, nativeSessionId, status);

        return new ContainerReplacementResult(
            store.GetWorkerEnrollment(workerId)!,
            status,
            status.ProcessGeneration,
            nativeSessionId);
    }

    /// <summary>
    /// Builds the exact container create spec for an enrollment. It is shared by
    /// first provisioning and replacement so a replacement reproduces the same
    /// name, image, platform, four volumes, frozen limits, approved digest set,
    /// profile environment and operation identity as the original.
    /// </summary>
    private ContainerCreateSpec ContainerSpecFor(OrganizationStore store, WorkerEnrollmentRecord enrollment, ExecutionTarget host, WorkerResourceIdentity identity)
    {
        var mounts = new[] { new NamedVolumeMount(enrollment.ControlVolumeName, "/control"), new(enrollment.HomeVolumeName, "/home/worker"), new(enrollment.WorkspaceVolumeName, "/workspace"), new(enrollment.SessionVolumeName, "/session") };
        var approved = store.ListApprovedImageDigests(host.Id, _options.ApprovedImageDigest);
        var limits = ContainerLimitsFor(store, enrollment);
        return new(enrollment.ContainerName, enrollment.ExpectedImageDigest, enrollment.ExpectedPlatform, identity, mounts, limits.MemoryBytes, limits.CpuLimit, limits.PidsLimit, approved, ProfileEnvironmentFor(enrollment));
    }

    private async Task ReconcileUncertain(OrganizationStore store, WorkerEnrollmentRecord enrollment, ProvisioningOperationRecord operation, ExecutionTarget host, CancellationToken token)
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

    // The key bootstrap only writes the enrolled key into the control volume, so it
    // always runs the configured base regardless of the image the long-lived
    // container will use; a profile digest is never given root-adjacent bootstrap work.
    private BootstrapSpec BootstrapFor(WorkerEnrollmentRecord enrollment, ProvisioningOperationRecord operation) =>
        new(enrollment.ControlVolumeName, _options.ApprovedImageDigest, enrollment.ExpectedPlatform, Identity(enrollment, operation.Id));

    private byte[] Key(WorkerEnrollmentRecord enrollment) =>
        ControllerPrivateFile.ReadExact(enrollment.KeyFilePath, _options.ExpectedControllerUid, ControllerFileModes.Private0600, 32);

    // A profile enrollment's digest is exactly one verified build on its host, so
    // the revision id is recovered from the frozen digest rather than stored twice.
    private WorkerResourceIdentity Identity(WorkerEnrollmentRecord e, string operation) =>
        new(e.OrganizationId, e.ControllerId, e.HostId, e.WorkerId, e.RuntimeBindingId, operation,
            e.ExpectedImageDigest == _options.ApprovedImageDigest ? null : ProfileRevisionFor(e));

    private string ProfileRevisionFor(WorkerEnrollmentRecord e) =>
        Store().ListProfileBuilds(hostId: e.HostId).SingleOrDefault(b => b.State == ProfileBuildStates.Built && b.Verified && b.ImageDigest == e.ExpectedImageDigest)?.ProfileRevisionId
        ?? throw new WorkerControlConfigurationException("The enrollment's image digest is not a verified profile build on its host.");

    // A managed enrollment consumes the owner-frozen limits recorded at approval
    // time (MiB to bytes, integer CPU to the decimal Docker expects); every other
    // enrollment uses the controller's global policy. Either way the command
    // builder verifies the values are positive and do not exceed the ceilings.
    private (long MemoryBytes, decimal CpuLimit, int PidsLimit) ContainerLimitsFor(OrganizationStore store, WorkerEnrollmentRecord enrollment)
    {
        var managed = store.GetManagedEnrollmentResources(enrollment.RuntimeBindingId);
        if (managed is null) return (_options.MemoryBytes, _options.CpuLimit, _options.PidsLimit);
        return ((long)managed.MemoryLimitMiB * 1024 * 1024, managed.CpuLimit, managed.PidsLimit);
    }

    private IReadOnlyDictionary<string, string>? ProfileEnvironmentFor(WorkerEnrollmentRecord enrollment)
    {
        if (enrollment.ExpectedImageDigest == _options.ApprovedImageDigest) return null;
        var revisionId = ProfileRevisionFor(enrollment);
        var revision = Store().ListContainerProfiles()
            .SelectMany(profile => Store().ListContainerProfileRevisions(profile.Id) ?? [])
            .SingleOrDefault(item => item.Id == revisionId)
            ?? throw new OrganizationStoreCorruptException("The enrollment's verified profile revision is missing.");
        using var document = System.Text.Json.JsonDocument.Parse(revision.Definition);
        if (!document.RootElement.TryGetProperty("containerEnv", out var environment)) return null;
        return environment.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }
    private static string LabelsHash(WorkerResourceIdentity identity) => Hash(string.Join('\n', identity.Labels.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)));
    private static string VolumeKind(WorkerEnrollmentRecord e, string name) => name == e.ControlVolumeName ? "control" : name == e.HomeVolumeName ? "home" : name == e.WorkspaceVolumeName ? "workspace" : "session";
    private void RequireEnabled()
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        if (_options.Validate().Count != 0) throw new WorkerControlConfigurationException("Remote worker configuration is invalid.");
    }
    private OrganizationStore Store() => _control.Organization ?? throw new OrganizationStoreException("Organization store unavailable.");
    private static string DeterministicWorkerId(string organizationId, string bindingId, string hostId, string controllerId) => "wrk-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', organizationId, bindingId, hostId, controllerId)))).ToLowerInvariant()[..24];
    private static string Hash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
