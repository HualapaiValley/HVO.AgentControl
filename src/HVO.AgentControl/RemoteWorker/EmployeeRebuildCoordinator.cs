using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Worker;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>The durable result of driving an employee rebuild to its current outcome.</summary>
public sealed record EmployeeRebuildResult(EmployeeRebuildRecord Rebuild, WorkerEnrollmentRecord Enrollment, BridgeWorkerStatus? Status);

/// <summary>
/// Coordinates one owner-authorized, data-preserving managed employee rebuild.
/// The enrollment and managed approval remain immutable: the rebuild row is the
/// target override, while the existing four volume names and native ACP session
/// preserve employee identity.
/// </summary>
public sealed class EmployeeRebuildCoordinator(
    AcpControlHost control,
    RemoteWorkerProvisioningCoordinator provisioning,
    IWorkerBridgeSessionFactory sessions,
    IOptions<WorkerControlOptions> configured,
    ILogger<EmployeeRebuildCoordinator> logger)
{
    private readonly WorkerControlOptions _options = configured.Value;

    public async Task<EmployeeRebuildRecord> RebuildAsync(
        string employeeId,
        string targetProfileRevisionId,
        bool resetWorkspace,
        bool resetHome,
        string? resetConfirmation,
        CancellationToken cancellationToken)
    {
        RequireEnabled();
        var store = Store();
        var status = store.GetEmployeeProfileStatus(employeeId)
            ?? throw new OrganizationNotFoundException($"Managed employee '{employeeId}' does not exist.");
        if (status.WorkerId is null) throw new OrganizationConcurrencyException("The managed employee has no worker enrollment.");
        if (status.ActiveRebuild is not null) throw new OrganizationConcurrencyException("The worker already has an active employee rebuild; resume it before starting another.");

        var requiredConfirmation = OrganizationStore.EmployeeRebuildResetConfirmation.Required(resetWorkspace, resetHome);
        if (!string.Equals(requiredConfirmation, resetConfirmation, StringComparison.Ordinal))
        {
            if (requiredConfirmation is null) throw new OrganizationValidationException("A rebuild that resets nothing must not carry a reset confirmation.");
            throw new OrganizationValidationException($"A reset rebuild requires the exact confirmation phrase '{requiredConfirmation}'.");
        }

        var enrollment = store.GetWorkerEnrollment(status.WorkerId)
            ?? throw new OrganizationConcurrencyException("The managed employee's worker enrollment is missing.");
        var targetRevision = FindRevision(store, targetProfileRevisionId)
            ?? throw new OrganizationNotFoundException($"Container profile revision '{targetProfileRevisionId}' does not exist.");
        if (!string.Equals(targetRevision.ProfileId, status.CurrentProfileId, StringComparison.Ordinal))
            throw new OrganizationValidationException("An employee rebuild cannot migrate the employee to a different container profile.");
        var targetBuild = store.GetVerifiedProfileBuild(targetProfileRevisionId, enrollment.HostId)
            ?? throw new OrganizationValidationException("The target profile revision has no verified build on the employee's host.");
        if (targetBuild.ImageDigest is null) throw new OrganizationStoreCorruptException("The verified target build has no image digest.");
        if (!string.Equals(targetBuild.Platform, enrollment.ExpectedPlatform, StringComparison.Ordinal)
            || !string.Equals(targetBuild.Platform, status.CurrentPlatform, StringComparison.Ordinal))
            throw new OrganizationValidationException("The target profile build platform differs from the employee's enrolled platform.");
        if (string.Equals(targetRevision.Id, status.CurrentProfileRevisionId, StringComparison.Ordinal)
            && string.Equals(targetBuild.ImageDigest, status.CurrentImageDigest, StringComparison.Ordinal))
            throw new OrganizationValidationException("The employee already runs the requested profile revision.");
        if (targetRevision.RevisionNumber <= status.CurrentRevisionNumber)
            throw new OrganizationValidationException("The target profile revision must be newer than the employee's current revision.");

        var rebuild = store.BeginEmployeeRebuild(new EmployeeRebuildCreate(
            employeeId,
            status.RuntimeBindingId,
            enrollment.WorkerId,
            enrollment.HostId,
            status.CurrentProfileRevisionId,
            status.CurrentImageDigest,
            status.CurrentRevisionNumber,
            targetRevision.Id,
            targetBuild.Id,
            targetBuild.ImageDigest,
            targetBuild.Platform,
            resetWorkspace,
            resetHome,
            resetConfirmation,
            enrollment.OwnershipEpoch));

        return (await ResumeAsync(rebuild.Id, cancellationToken).ConfigureAwait(false)).Rebuild;
    }

    public async Task<EmployeeRebuildResult> ResumeAsync(string rebuildId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        var store = Store();
        var rebuild = store.GetEmployeeRebuild(rebuildId)
            ?? throw new OrganizationNotFoundException($"Employee rebuild '{rebuildId}' does not exist.");
        if (rebuild.State is EmployeeRebuildStates.Applied or EmployeeRebuildStates.Failed)
            return new(rebuild, RequireEnrollment(store, rebuild), null);

        try
        {
            return await ResumeCoreAsync(rebuild, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TransitionToUncertain(store, rebuildId, "caller-canceled-during-rebuild");
            throw;
        }
        catch (Exception exception) when (IsEffectUncertain(exception))
        {
            TransitionToUncertain(store, rebuildId, "remote-effect-uncertain");
            logger.LogWarning(exception, "Employee rebuild {RebuildId} has an uncertain remote effect.", rebuildId);
            throw;
        }
        catch (Exception exception) when (IsDeterministic(exception))
        {
            await RecordDeterministicFailureAsync(store, rebuildId, cancellationToken).ConfigureAwait(false);
            logger.LogWarning(exception, "Employee rebuild {RebuildId} failed deterministic validation.", rebuildId);
            throw;
        }
        catch (Exception exception) when (exception is WorkerRecoveryRequiredException or OrganizationConcurrencyException or OrganizationStoreException or RemoteWorkerUnavailableException)
        {
            TransitionToUncertain(store, rebuildId, "rebuild-outcome-not-durable");
            logger.LogWarning(exception, "Employee rebuild {RebuildId} could not reach a durable outcome.", rebuildId);
            throw;
        }
    }

    /// <summary>
    /// Marks interrupted rows uncertain, then reconciles each by exact old/target
    /// labels. Cancellation is deliberately Uncertain: once stop/remove/create may
    /// have started, safety is more important than distinguishing a clean interrupt.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReconcileInterruptedOnStartup(CancellationToken cancellationToken = default)
    {
        RequireEnabled();
        var store = Store();
        _ = store.MarkInterruptedEmployeeRebuildsUncertain();
        var reconciled = new List<string>();
        foreach (var rebuild in store.ListEmployeeRebuilds().Where(x => x.State == EmployeeRebuildStates.Uncertain))
        {
            store.SetManualDispatchHold(rebuild.EmployeeId, true, "rebuild " + rebuild.Id);
            try
            {
                var target = Target(rebuild);
                var inspection = await provisioning.InspectRebuildContainerAsync(rebuild.WorkerId, target, cancellationToken).ConfigureAwait(false);
                if (inspection.IsTarget && inspection.Running)
                {
                    var result = await VerifyAndApplyAsync(store, rebuild, cancellationToken).ConfigureAwait(false);
                    reconciled.Add(result.Rebuild.Id);
                }
                else if (inspection.IsOriginal && inspection.Running)
                {
                    var current = store.GetEmployeeRebuild(rebuild.Id)!;
                    store.TransitionEmployeeRebuild(current.Id, current.Revision, EmployeeRebuildStates.Uncertain, EmployeeRebuildStates.Replacing, failureSummary: "original-container-intact-resume-rebuild");
                    reconciled.Add(rebuild.Id);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Employee rebuild {RebuildId} remains uncertain after startup reconciliation.", rebuild.Id);
            }
        }
        return reconciled;
    }

    private async Task<EmployeeRebuildResult> ResumeCoreAsync(EmployeeRebuildRecord rebuild, CancellationToken cancellationToken)
    {
        var store = Store();
        var current = store.GetEmployeeRebuild(rebuild.Id)!;
        if (current.State == EmployeeRebuildStates.Intent)
        {
            store.SetManualDispatchHold(current.EmployeeId, true, "rebuild " + current.Id);
            current = Transition(store, current.Id, EmployeeRebuildStates.Intent, EmployeeRebuildStates.Holding);
            if (WorkerConnectionManager.InstanceFor(control) is { } holdingManager)
                await holdingManager.InvalidateCachedSessionAsync(current.WorkerId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            store.SetManualDispatchHold(current.EmployeeId, true, "rebuild " + current.Id);
        }

        current = store.GetEmployeeRebuild(current.Id)!;
        if (current.State == EmployeeRebuildStates.Holding)
            current = Transition(store, current.Id, EmployeeRebuildStates.Holding, EmployeeRebuildStates.Replacing);
        else if (current.State == EmployeeRebuildStates.Uncertain)
            current = Transition(store, current.Id, EmployeeRebuildStates.Uncertain, EmployeeRebuildStates.Replacing);

        current = store.GetEmployeeRebuild(current.Id)!;
        if (current.State == EmployeeRebuildStates.Replacing)
        {
            _ = RequireEnrollment(store, current);
            await provisioning.ReplaceContainerAsync(current.WorkerId, Target(current), cancellationToken).ConfigureAwait(false);
            if (WorkerConnectionManager.InstanceFor(control) is { } manager)
                await manager.InvalidateCachedSessionAsync(current.WorkerId, cancellationToken).ConfigureAwait(false);
            current = Transition(store, current.Id, EmployeeRebuildStates.Replacing, EmployeeRebuildStates.Verifying);
        }

        return await VerifyAndApplyAsync(store, current, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmployeeRebuildResult> VerifyAndApplyAsync(OrganizationStore store, EmployeeRebuildRecord rebuild, CancellationToken cancellationToken)
    {
        var current = store.GetEmployeeRebuild(rebuild.Id)!;
        var enrollment = RequireEnrollment(store, current);
        await using var session = await sessions.ConnectAsync(enrollment, cancellationToken).ConfigureAwait(false);
        var status = await RemoteOrientationCoordinator.ReadStatusAsync(session, cancellationToken).ConfigureAwait(false);
        status = await WorkerConnectionManager.ReconcileRecordedSessionAsync(store, enrollment, session, status, cancellationToken).ConfigureAwait(false);
        var binding = store.GetRemoteBindingSession(enrollment.RuntimeBindingId);
        if (binding.NativeSessionId is not { } nativeSessionId)
            throw new OrganizationConcurrencyException("The rebuilt worker has no authoritative native session.");
        WorkerConnectionManager.EnsureSessionReadyForWork(store, enrollment.WorkerId, nativeSessionId, status);
        if (status.OwnershipEpoch != session.Lease.Epoch || status.OwnershipEpoch <= current.OwnershipEpochBefore)
            throw new OrganizationConcurrencyException("The rebuilt worker did not report a fresh ownership epoch.");
        store.RecordWorkerConnectionState(enrollment.WorkerId, "authenticated", status.OwnershipEpoch);
        store.RecordWorkerStatusAndEvents(enrollment.WorkerId, ToController(status), []);

        current = store.GetEmployeeRebuild(current.Id)!;
        if (current.State == EmployeeRebuildStates.Uncertain)
        {
            var inspection = await provisioning.InspectRebuildContainerAsync(current.WorkerId, Target(current), cancellationToken).ConfigureAwait(false);
            if (!inspection.IsTarget || !inspection.Running) throw new OrganizationConcurrencyException("The rebuild target container is not proven running.");
        }
        else if (current.State != EmployeeRebuildStates.Verifying)
        {
            throw new OrganizationConcurrencyException($"An employee rebuild in state {current.State} cannot be verified.");
        }

        var evidence = Hash(string.Join('\n', current.WorkerId, current.ToImageDigest, current.ToProfileRevisionId, status.OwnershipEpoch));
        var applied = store.TransitionEmployeeRebuild(current.Id, current.Revision, current.State, EmployeeRebuildStates.Applied, evidenceHash: evidence, ownershipEpochAfter: status.OwnershipEpoch);
        store.SetManualDispatchHold(applied.EmployeeId, false, null);
        return new(applied, store.GetWorkerEnrollment(enrollment.WorkerId)!, status);
    }

    private async Task RecordDeterministicFailureAsync(OrganizationStore store, string rebuildId, CancellationToken cancellationToken)
    {
        var current = store.GetEmployeeRebuild(rebuildId);
        if (current is null || current.State is EmployeeRebuildStates.Applied or EmployeeRebuildStates.Failed) return;
        try
        {
            var inspection = await provisioning.InspectRebuildContainerAsync(current.WorkerId, Target(current), cancellationToken).ConfigureAwait(false);
            if (!inspection.Exists || (!inspection.IsOriginal && !inspection.IsTarget))
            {
                TransitionToUncertain(store, rebuildId, "container-state-unknown-after-failure");
                return;
            }
            current = store.GetEmployeeRebuild(rebuildId)!;
            var failed = store.TransitionEmployeeRebuild(current.Id, current.Revision, current.State, EmployeeRebuildStates.Failed, failureSummary: "deterministic-rebuild-validation-failed");
            store.SetManualDispatchHold(failed.EmployeeId, false, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TransitionToUncertain(store, rebuildId, "container-state-unknown-after-failure");
        }
    }

    private static EmployeeRebuildRecord Transition(OrganizationStore store, string id, string from, string to)
    {
        var current = store.GetEmployeeRebuild(id) ?? throw new OrganizationNotFoundException($"Employee rebuild '{id}' does not exist.");
        if (current.State == to) return current;
        if (current.State != from) throw new OrganizationConcurrencyException($"The employee rebuild is not in state {from}.");
        return store.TransitionEmployeeRebuild(id, current.Revision, from, to);
    }

    private static void TransitionToUncertain(OrganizationStore store, string rebuildId, string summary)
    {
        var current = store.GetEmployeeRebuild(rebuildId);
        if (current is null || current.State is EmployeeRebuildStates.Uncertain or EmployeeRebuildStates.Applied or EmployeeRebuildStates.Failed or EmployeeRebuildStates.Intent) return;
        try { store.TransitionEmployeeRebuild(current.Id, current.Revision, current.State, EmployeeRebuildStates.Uncertain, failureSummary: summary); }
        catch (OrganizationConcurrencyException) { }
    }

    private static WorkerEnrollmentRecord RequireEnrollment(OrganizationStore store, EmployeeRebuildRecord rebuild)
    {
        var enrollment = store.GetWorkerEnrollment(rebuild.WorkerId)
            ?? throw new OrganizationConcurrencyException("The employee rebuild's worker enrollment is missing.");
        if (enrollment.RuntimeBindingId != rebuild.RuntimeBindingId || enrollment.HostId != rebuild.HostId || !enrollment.Enabled || enrollment.LifecycleStatus != "enrolled")
            throw new OrganizationConcurrencyException("The employee rebuild no longer matches an enabled enrolled worker on its frozen binding and host.");
        return enrollment;
    }

    private static ContainerTarget Target(EmployeeRebuildRecord rebuild) =>
        new(rebuild.ToImageDigest, rebuild.ToPlatform, rebuild.ToProfileRevisionId, rebuild.ResetWorkspace, rebuild.ResetHome, rebuild.FromProfileRevisionId);

    private static ContainerProfileRevisionSummary? FindRevision(OrganizationStore store, string revisionId) =>
        store.ListContainerProfiles().SelectMany(profile => store.ListContainerProfileRevisions(profile.Id) ?? []).SingleOrDefault(x => x.Id == revisionId);

    private static bool IsEffectUncertain(Exception exception) => exception switch
    {
        WorkerWriteUncertainException => true,
        WorkerReadUncertainException => true,
        WorkerOperationUncertainException => true,
        WorkerRemoteException remote when remote.Code == "worker-operation-uncertain" => true,
        RemoteWorkerUnavailableException remote when remote.Transport => true,
        IOException => true,
        ObjectDisposedException => true,
        _ => false,
    };

    private static bool IsDeterministic(Exception exception) => exception is OrganizationValidationException
        or OrganizationNotFoundException
        or WorkerControlConfigurationException
        or ForeignResourceException
        or WorkerReconciliationInvalidException
        or WorkerProtocolException;

    private static ControllerWorkerStatus ToController(BridgeWorkerStatus status) =>
        new(status.WorkerGeneration, status.ProcessGeneration, status.ProcessState, status.ActiveRequestId, status.PendingPermission?.PayloadHash, status.OwnershipEpoch, status.DispatchHeld, status.HoldReasons, status.AcknowledgedWorkerGeneration, status.AcknowledgedSequence, ViewerSupported: status.ViewerSupported, ViewerAvailable: status.ViewerAvailable);

    private void RequireEnabled()
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        if (_options.Validate().Count != 0) throw new WorkerControlConfigurationException("Remote worker configuration is invalid.");
    }

    private OrganizationStore Store() => control.Organization ?? throw new OrganizationStoreException("Organization store unavailable.");
    private static string Hash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
