using HVO.AgentControl.Organization;
using HVO.AgentControl.Worker;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// The durable outcome of delivering the current orientation to one managed
/// employee and confirming a fresh process has loaded it.
/// </summary>
public sealed record EmployeeOrientationDeliveryResult(
    OrientationArtifact Artifact,
    OrientationStatus Status,
    WorkerEnrollmentRecord Enrollment,
    ContainerReplacementResult Replacement);

/// <summary>
/// Owner-triggered orientation delivery and comprehension for an already
/// provisioned managed employee. It is the single place that composes the
/// current orientation, installs it over the authenticated bridge, marks the
/// assignment <c>Delivered</c> with the next required process generation,
/// deliberately replaces the running container so a fresh process loads the
/// artifact while preserving the four volumes and the authoritative native
/// session, confirms the new generation loaded it, and runs the bounded tool-free
/// comprehension operation.
/// </summary>
/// <remarks>
/// This is the same machinery <see cref="HireProvisioningCoordinator"/> drives for
/// a hire. It is factored here so an owner can re-deliver orientation and run
/// comprehension on an existing managed employee without a new hire and without
/// an image rebuild. It never builds an image, never provisions resources and
/// never cleans up on failure; the methods are idempotent and safe to repeat.
/// </remarks>
public sealed class EmployeeOrientationCoordinator(RemoteWorkerProvisioningCoordinator provisioning, RemoteOrientationCoordinator orientation)
{
    /// <summary>
    /// Composes the current orientation for one managed employee and installs it
    /// over the bridge, then replaces the container so a fresh process loads it.
    /// The caller supplies the employee revision it acted on; the exact binding,
    /// worker, session and enrollment are resolved server-side from persisted
    /// state. Returns the delivered status and the confirmed load.
    /// </summary>
    public async Task<EmployeeOrientationDeliveryResult> DeliverAsync(OrganizationStore store, string employeeId, int expectedEmployeeRevision, CancellationToken cancellationToken)
    {
        var employee = store.GetOverview().Employees.SingleOrDefault(item => string.Equals(item.Id, employeeId, StringComparison.Ordinal))
            ?? throw new OrganizationNotFoundException($"Employee '{employeeId}' does not exist.");
        if (employee.Revision != expectedEmployeeRevision)
            throw new OrganizationConcurrencyException("The employee changed; reload and retry with its current revision.");

        var enrollment = ResolveManagedEnrollment(store, employeeId);
        if (enrollment.LifecycleStatus != "enrolled")
            throw new OrganizationConcurrencyException("Only an enrolled managed worker can be oriented.");

        var artifact = store.ComposeAndAssignCurrentOrientation(employeeId);
        var delivery = await InstallAndReplaceAsync(store, enrollment, artifact, cancellationToken).ConfigureAwait(false);
        var replaced = store.GetWorkerEnrollment(enrollment.WorkerId) ?? enrollment;
        return new EmployeeOrientationDeliveryResult(artifact, delivery.Status, replaced, delivery.Replacement);
    }

    /// <summary>
    /// Runs the bounded, tool-free comprehension turn for the employee's current
    /// delivered and loaded orientation over the authenticated bridge, validates
    /// the returned structured evidence through the authoritative store as
    /// <c>live-model</c> evidence, and returns the resulting status. A stale or
    /// non-loaded orientation is a conflict; the existing holds continue to block
    /// dispatch.
    /// </summary>
    public async Task<OrientationStatus> RunComprehensionAsync(OrganizationStore store, string employeeId, int expectedOrientationRevision, CancellationToken cancellationToken)
    {
        var status = store.GetOrientationStatus(employeeId);
        if (status.Revision != expectedOrientationRevision)
            throw new OrganizationConcurrencyException("The orientation changed; reload and retry with its current revision.");
        if (status.State != OrientationStates.Delivered || status.RestartRequired)
            throw new OrganizationConcurrencyException("A current delivered orientation loaded by the live runtime is required.");

        var enrollment = ResolveManagedEnrollment(store, employeeId);
        var evidence = await orientation.RunComprehensionAsync(enrollment, status, cancellationToken).ConfigureAwait(false);
        return store.ValidateAndRecordComprehension(evidence, OrientationEvidenceSource.LiveModel);
    }

    /// <summary>
    /// Installs an already-composed artifact, marks it delivered with the next
    /// required generation, replaces the container preserving the volumes and the
    /// authoritative session, and confirms the replacement loaded it. This is the
    /// shared step the hire coordinator drives, so a hire and an owner re-delivery
    /// reach the same confirmed state through exactly one implementation.
    /// </summary>
    public async Task<(OrientationStatus Status, ContainerReplacementResult Replacement)> InstallAndReplaceAsync(OrganizationStore store, WorkerEnrollmentRecord enrollment, OrientationArtifact artifact, CancellationToken cancellationToken)
    {
        var sessionId = artifact.SessionId
            ?? throw new OrganizationConcurrencyException("The current orientation has no authoritative session to deliver against.");

        // Read the exact process generation that must be restarted to load the
        // artifact before installing it, so the required generation is the next
        // process generation rather than a value inferred after the fact.
        var preStatus = await orientation.ReadStatusAsync(enrollment, cancellationToken).ConfigureAwait(false);
        _ = await orientation.DeliverAsync(enrollment, artifact, cancellationToken).ConfigureAwait(false);

        var requiredGeneration = preStatus.ProcessGeneration + 1;
        _ = store.MarkOrientationDelivered(
            artifact.AssignmentId,
            artifact.OrientationVersion,
            sessionId,
            artifact.AssignmentRevision,
            requiredRuntimeGeneration: requiredGeneration);

        var replacement = await provisioning.ReplaceContainerForOrientationAsync(enrollment.WorkerId, cancellationToken).ConfigureAwait(false);
        ValidateReplacementStatus(replacement, artifact, sessionId, requiredGeneration);

        var status = store.ConfirmOrientationLoaded(
            artifact.EmployeeId,
            artifact.AssignmentId,
            artifact.OrientationVersion,
            sessionId,
            replacement.ProcessGeneration);
        return (status, replacement);
    }

    /// <summary>
    /// Resolves the exact managed enrollment for one employee: a DeveloperContainer
    /// binding with an enrolled, enabled enrollment. A non-managed or internal
    /// employee is a validation error rather than a silent no-op.
    /// </summary>
    private static WorkerEnrollmentRecord ResolveManagedEnrollment(OrganizationStore store, string employeeId)
    {
        var resources = store.GetManagedEnrollmentResourcesForEmployee(employeeId)
            ?? throw new OrganizationValidationException("Only a managed DeveloperContainer employee has remote orientation resources.");
        return store.ListWorkerEnrollments().SingleOrDefault(x => x.RuntimeBindingId == resources.RuntimeBindingId)
            ?? throw new OrganizationConcurrencyException("The managed employee has no worker enrollment to orient.");
    }

    internal static void ValidateReplacementStatus(ContainerReplacementResult replacement, OrientationArtifact artifact, string nativeSessionId, long requiredGeneration)
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
}
