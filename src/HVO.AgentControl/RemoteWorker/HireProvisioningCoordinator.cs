using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Worker;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// The durable outcome of driving one approved managed hire from its owner
/// approval through provisioning and remote orientation delivery to a replaced
/// process that has loaded and comprehended the assigned orientation.
/// </summary>
public sealed record HireProvisioningResult(
    HireRequestSummary Hire,
    ManagedEmployeeCreation Employee,
    WorkerEnrollmentRecord Enrollment,
    OrientationStatus Orientation);

/// <summary>
/// Drives one approved managed hire through the provisioning and orientation
/// delivery slice: Approved → Provisioning → Orienting → Ready. It composes the current
/// orientation for the managed employee, installs the artifact over the
/// authenticated bridge, records the exact delivery, replaces the running
/// container so a fresh process picks up the artifact while keeping the four
/// volumes and the authoritative native session, and confirms the new process
/// generation loaded that session, then runs the bounded tool-free comprehension
/// operation inside that exact worker and records Ready only when the returned
/// structured evidence passes the organization store's exact fact validation.
/// </summary>
/// <remarks>
/// The method is owner-triggered (the approval endpoint) and safe to repeat. A
/// restart resumes the same call: the plan, employee/binding allocation, worker
/// link, container replacement and orientation delivery are each idempotent, and
/// state transitions re-read the current revision so a concurrent advance is a
/// conflict rather than a silent overwrite. It never cleans up resources on
/// failure; uncertainty is recorded for operator reconciliation.
/// </remarks>
public sealed class HireProvisioningCoordinator(
    AcpControlHost control,
    RemoteWorkerProvisioningCoordinator provisioning,
    RemoteOrientationCoordinator orientation,
    IOptions<WorkerControlOptions> configured,
    ILogger<HireProvisioningCoordinator> logger)
{
    private readonly WorkerControlOptions _options = configured.Value;

    /// <summary>
    /// Resumes one hire from wherever it lies. This is the same idempotent path
    /// the approval endpoint triggers and the hosted service uses on restart; it is
    /// named separately so a resume is explicit at the call site.
    /// </summary>
    public Task<HireProvisioningResult> ResumeAsync(string hireId, CancellationToken cancellationToken) =>
        ProvisionToOrientingAsync(hireId, cancellationToken);

    public async Task<HireProvisioningResult> ProvisionToOrientingAsync(string hireId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        var store = Store();
        var hire = store.GetHireRequest(hireId) ?? throw new OrganizationNotFoundException($"Hire request '{hireId}' does not exist.");
        var approval = store.GetHireRequestApproval(hireId) ?? throw new OrganizationConcurrencyException("The hire request has no owner approval.");

        // Resolve (or allocate, idempotently) the managed employee and binding.
        // The approval must already carry the employee/binding links for the
        // frozen resources to be meaningful; an approved request without them is
        // completed here so a restart after approval but before allocation resumes.
        var creation = store.CreateManagedEmployeeFromHire(hireId);

        // Re-read so the current state and revision are authoritative.
        hire = store.GetHireRequest(hireId)!;

        // A Ready hire is the completed outcome: return it without contacting the
        // host, so a repeated approval, resume or restart never re-prompts the model
        // or replaces a working container.
        if (hire.State == HireRequestStates.Ready)
        {
            var enrollment = ResolveEnrollment(store, creation);
            return new HireProvisioningResult(hire, creation, enrollment, store.GetOrientationStatus(creation.EmployeeId));
        }

        if (hire.State is not (HireRequestStates.Approved or HireRequestStates.Provisioning
            or HireRequestStates.Interrupted or HireRequestStates.Uncertain or HireRequestStates.Orienting))
        {
            throw new OrganizationConcurrencyException($"A hire request in state {hire.State} cannot be provisioned.");
        }

        // An already-orienting hire may have completed comprehension before the
        // controller committed the hire transition. Finish that one durable step
        // without touching the container again.
        if (hire.State == HireRequestStates.Orienting && IsOrientationReady(store, creation.EmployeeId))
        {
            var enrollment = ResolveEnrollment(store, creation);
            var ready = store.TransitionHireRequestState(hireId, hire.Revision, HireRequestStates.Orienting, HireRequestStates.Ready);
            return new HireProvisioningResult(ready, creation, enrollment, store.GetOrientationStatus(creation.EmployeeId));
        }

        try
        {
            return await ProvisionCoreAsync(hireId, creation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A caller cancellation before a mutation is clean; one during a mutation
            // may leave the effect unknown. Either way record the intent so a repeat
            // resumes deterministically rather than silently repeating an effect.
            TransitionUncertainOrInterrupted(store, hireId, cancellationToken, "caller-canceled");
            throw;
        }
        catch (Exception exception) when (IsEffectUncertain(exception))
        {
            TransitionTo(store, hireId, HireRequestStates.Uncertain, "The remote effect is uncertain; reconcile before retrying.");
            logger.LogWarning(exception, "Hire {HireId} provisioning is uncertain and requires reconciliation.", hireId);
            throw;
        }
        catch (Exception exception) when (exception is OrganizationValidationException or OrganizationNotFoundException
            or WorkerControlConfigurationException or ForeignResourceException or WorkerReconciliationInvalidException or WorkerProtocolException)
        {
            // Deterministic validation, policy or protocol errors are a Failed
            // outcome. Resources are never auto-cleaned up.
            TransitionTo(store, hireId, HireRequestStates.Failed, "Provisioning failed deterministic validation.");
            logger.LogWarning(exception, "Hire {HireId} provisioning failed validation.", hireId);
            throw;
        }
        catch (Exception exception) when (exception is WorkerRecoveryRequiredException or RemoteWorkerUnavailableException or OrganizationConcurrencyException or OrganizationStoreException)
        {
            TransitionTo(store, hireId, HireRequestStates.Uncertain, "Provisioning could not reach a durable outcome.");
            logger.LogWarning(exception, "Hire {HireId} provisioning could not reach a durable outcome.", hireId);
            throw;
        }
    }

    private async Task<HireProvisioningResult> ProvisionCoreAsync(string hireId, ManagedEmployeeCreation creation, CancellationToken cancellationToken)
    {
        var store = Store();

        // Move to Provisioning from the resumable pre-provisioning states, re-reading
        // first so a concurrent advance is a conflict. An already-Orienting hire is
        // past this point and resumes the delivery/replacement directly.
        var current = store.GetHireRequest(hireId)!;
        if (current.State is HireRequestStates.Approved or HireRequestStates.Interrupted or HireRequestStates.Uncertain)
        {
            current = store.TransitionHireRequestState(hireId, current.Revision, current.State, HireRequestStates.Provisioning, "Provisioning managed worker.");
        }

        var enrollment = await provisioning.PlanAsync(creation.RuntimeBindingId, creation.ApprovedHostId, creation.ApprovedImageDigest, cancellationToken).ConfigureAwait(false);
        enrollment = await provisioning.ApplyAllAsync(enrollment.WorkerId, cancellationToken).ConfigureAwait(false);

        var afterProvision = store.GetHireRequest(hireId)!;
        if (afterProvision.State != HireRequestStates.Orienting)
        {
            afterProvision = store.TransitionHireRequestState(hireId, afterProvision.Revision, HireRequestStates.Provisioning, HireRequestStates.Orienting, "Provisioned; delivering orientation.");
        }

        // Compose the current orientation for the managed employee. The artifact
        // carries the exact assignment, version, content and authoritative session.
        var artifact = store.ComposeAndAssignCurrentOrientation(creation.EmployeeId);

        // Deliver the artifact over the authenticated bridge and read the exact
        // process generation that will require a restart to load it.
        var preStatus = await orientation.ReadStatusAsync(enrollment, cancellationToken).ConfigureAwait(false);
        _ = await orientation.DeliverAsync(enrollment, artifact, cancellationToken).ConfigureAwait(false);

        var sessionId = artifact.SessionId;
        if (sessionId is null) throw new OrganizationConcurrencyException("The current orientation has no authoritative session to deliver against.");
        // The artifact must be loaded by a process strictly newer than the one that
        // was running when it was installed: the restart requirement is the next
        // process generation.
        var requiredGeneration = preStatus.ProcessGeneration + 1;
        store.MarkOrientationDelivered(
            artifact.AssignmentId,
            artifact.OrientationVersion,
            sessionId,
            artifact.AssignmentRevision,
            requiredRuntimeGeneration: requiredGeneration);

        // Replace the container so a fresh process loads the installed artifact and
        // the authoritative session. Volumes are preserved and the session is loaded
        // by the replacement verification.
        var replacement = await provisioning.ReplaceContainerForOrientationAsync(enrollment.WorkerId, cancellationToken).ConfigureAwait(false);
        ValidateReplacementStatus(replacement, artifact, sessionId, requiredGeneration);

        // Confirm the replacement process generation loaded the delivered
        // assignment, then re-read the exact orientation status.
        var orientationStatus = store.ConfirmOrientationLoaded(
            creation.EmployeeId,
            artifact.AssignmentId,
            artifact.OrientationVersion,
            sessionId,
            replacement.ProcessGeneration);

        // The worker returns only one bounded structured evidence object, never a
        // transcript. The store remains the authority over every fact and rejects
        // any mismatch before Comprehended/Ready.
        var replaced = store.GetWorkerEnrollment(enrollment.WorkerId)!;
        var evidence = await orientation.RunComprehensionAsync(replaced, orientationStatus, cancellationToken).ConfigureAwait(false);
        orientationStatus = store.ValidateAndRecordComprehension(evidence, OrientationEvidenceSource.LiveModel);
        if (!orientationStatus.Ready) throw new OrganizationConcurrencyException("The managed employee did not reach orientation readiness.");

        var orientingHire = store.GetHireRequest(hireId)!;
        var finalHire = store.TransitionHireRequestState(hireId, orientingHire.Revision, HireRequestStates.Orienting, HireRequestStates.Ready);
        var finalEnrollment = store.GetWorkerEnrollment(enrollment.WorkerId)!;
        return new HireProvisioningResult(finalHire, creation, finalEnrollment, orientationStatus);
    }

    /// <summary>
    /// Requires the replacement status to prove the artifact was installed in the
    /// new process, the generation advanced past the delivery, and the exact
    /// authoritative native session was loaded.
    /// </summary>
    private static void ValidateReplacementStatus(ContainerReplacementResult replacement, OrientationArtifact artifact, string nativeSessionId, long requiredGeneration)
    {
        var status = replacement.Status;
        if (status.ProcessState != "running" || !status.AcpInitialized)
            throw new OrganizationConcurrencyException("The replacement worker process is not running and initialized.");
        if (status.ProcessGeneration <= requiredGeneration - 1)
            throw new OrganizationConcurrencyException("The replacement worker process generation did not advance.");
        if (!string.Equals(status.OrientationAssignmentId, artifact.AssignmentId, StringComparison.Ordinal)
            || !string.Equals(status.OrientationVersion, artifact.OrientationVersion, StringComparison.Ordinal))
            throw new OrganizationConcurrencyException("The replacement worker did not report the delivered orientation artifact.");
        if (!string.Equals(status.SessionId, nativeSessionId, StringComparison.Ordinal)
            || !string.Equals(replacement.NativeSessionId, nativeSessionId, StringComparison.Ordinal))
            throw new OrganizationConcurrencyException("The replacement worker did not load the authoritative native session.");
    }

    /// <summary>
    /// True when the current orientation assignment is delivered (or beyond) and
    /// the loaded generation satisfies the required restart generation.
    /// </summary>
    private static bool IsOrientationReady(OrganizationStore store, string employeeId)
    {
        OrientationStatus status;
        try { status = store.GetOrientationStatus(employeeId); }
        catch (OrganizationNotFoundException) { return false; }
        return status.Ready;
    }

    private WorkerEnrollmentRecord ResolveEnrollment(OrganizationStore store, ManagedEmployeeCreation creation)
    {
        var enrollment = creation.WorkerId is null
            ? store.ListWorkerEnrollments().SingleOrDefault(x => x.RuntimeBindingId == creation.RuntimeBindingId)
            : store.GetWorkerEnrollment(creation.WorkerId);
        return enrollment ?? throw new OrganizationConcurrencyException("The managed hire has no worker enrollment.");
    }

    private void TransitionUncertainOrInterrupted(OrganizationStore store, string hireId, CancellationToken cancellationToken, string detail)
    {
        // A caller cancellation while an effect may be in flight is uncertain; a
        // clean cancellation before any effect is Interrupted. Either way the
        // transition re-reads the current state and never repeats an effect.
        var current = store.GetHireRequest(hireId);
        if (current is null) return;
        var target = cancellationToken.IsCancellationRequested ? HireRequestStates.Interrupted : HireRequestStates.Uncertain;
        TransitionTo(store, hireId, target, detail);
    }

    private void TransitionTo(OrganizationStore store, string hireId, string target, string detail)
    {
        var current = store.GetHireRequest(hireId);
        if (current is null || current.State == target) return;
        if (current.State is not (HireRequestStates.Provisioning or HireRequestStates.Orienting)) return;
        try
        {
            store.TransitionHireRequestState(hireId, current.Revision, current.State, target, detail);
        }
        catch (OrganizationConcurrencyException)
        {
            // A concurrent transition already moved the request; leave it to its
            // owner rather than overwriting with a stale revision.
        }
    }

    private static bool IsEffectUncertain(Exception exception) => exception switch
    {
        WorkerWriteUncertainException => true,
        WorkerReadUncertainException => true,
        WorkerOperationUncertainException => true,
        WorkerRemoteException remote when remote.Code == "worker-operation-uncertain" => true,
        _ => false,
    };

    private OrganizationStore Store() => control.Organization ?? throw new OrganizationStoreException("Organization store unavailable.");
}
