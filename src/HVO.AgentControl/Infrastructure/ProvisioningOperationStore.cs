using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<ProvisionOperationView> ApproveHostProvisionAuthority(HostExecutorPrincipal principal, string id,
        ApproveHostProvisionAuthorityInput input) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        var executor = await RequireProvisioningExecutor(db, principal, operation);
        if (input.Intent.AuthorityRevision != executor.AuthorityGeneration)
            throw new ProvisionIntentConflictException();
        if (operation.SelectedEnrollmentId.Length != 0 && (operation.SelectedEnrollmentId != executor.Id ||
            operation.SelectedAuthorityGeneration != executor.AuthorityGeneration || operation.SelectedIncarnationId != executor.IncarnationId))
            throw new ControlException("Provisioning authority is already bound to another executor claim.", 409);
        await ApproveProvisionAuthorityCore(db, operation, input.Intent);
        operation.SelectedEnrollmentId = executor.Id;
        operation.SelectedAuthorityGeneration = executor.AuthorityGeneration;
        operation.SelectedIncarnationId = executor.IncarnationId;
        return await ProvisionView(db, operation);
    });

    public Task<ProvisionOperationView> BindProvisionCapacity(string id, BindProvisionCapacityInput input) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        RequireProvisionRevision(operation, input.ExpectedRevision);
        var reservation = await db.HostResourceReservations.FindAsync(ResourceId(input.ReservationId))
            ?? throw new ControlException("Resource reservation not found.", 404);
        if (operation.ApprovedIntentJson.Length == 0 || operation.SelectedEnrollmentId.Length == 0 ||
            reservation.State != HostReservationState.Held || reservation.OperationId != operation.Id || reservation.HostId != operation.HostId ||
            reservation.WorkspaceId != operation.WorkspaceId || reservation.EnrollmentId != operation.SelectedEnrollmentId ||
            reservation.AuthorityGeneration != operation.SelectedAuthorityGeneration || reservation.IncarnationId != operation.SelectedIncarnationId ||
            !SameDigest(reservation.IntentDigest, operation.IntentDigest) || reservation.GrantExpiresAt <= Now || reservation.BuildSlots < 1 ||
            reservation.CpuMillis < checked(operation.RequestedBuildCpuMillis + operation.RequestedRuntimeCpuMillis) ||
            reservation.MemoryBytes < checked(operation.RequestedBuildMemoryBytes + operation.RequestedRuntimeMemoryBytes) ||
            reservation.CanonicalWorkspaceIdentity != ProvisionWorkspaceDigest(operation.ApprovedWorkspaceIdentity))
            throw new ProvisionAdmissionException("valid_physical_capacity_reservation_required");
        operation.CapacityReservationId = reservation.Id;
        operation.CapacityRevision = reservation.Revision;
        operation.CapacityValidUntil = reservation.GrantExpiresAt;
        operation.ReservedBuildCpuMillis = operation.RequestedBuildCpuMillis;
        operation.ReservedBuildMemoryBytes = operation.RequestedBuildMemoryBytes;
        operation.ReservedRuntimeCpuMillis = operation.RequestedRuntimeCpuMillis;
        operation.ReservedRuntimeMemoryBytes = operation.RequestedRuntimeMemoryBytes;
        operation.ReconcileRequested = false;
        operation.State = ProvisionOperationState.AwaitingExecution;
        operation.Code = "trusted_executor_may_claim";
        operation.Revision++;
        operation.UpdatedAt = Now;
        return await ProvisionView(db, operation);
    });

    public Task<HostProvisioningAssignment> ClaimHostProvisioningAssignment(HostExecutorPrincipal principal, string id,
        ClaimHostProvisioningInput input) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        var executor = await RequireProvisioningExecutor(db, principal, operation);
        var reservation = await RequireProvisionReservation(db, operation, input.ReservationId);
        var prior = await db.ProvisionAttempts.SingleOrDefaultAsync(x => x.OperationId == operation.Id);
        if (prior is not null)
        {
            RequireClaim(prior, executor, operation.ClaimGeneration);
            return await Assignment(db, operation, prior, reservation);
        }
        if (operation.State != ProvisionOperationState.AwaitingExecution || reservation.State != HostReservationState.Held ||
            reservation.GrantExpiresAt <= Now || operation.CapacityReservationId != reservation.Id ||
            operation.CapacityRevision != reservation.Revision)
            throw new ControlException("Provisioning operation is not claimable.", 409);
        operation.ClaimGeneration++;
        operation.Code = "trusted_executor_claimed";
        operation.Revision++;
        operation.UpdatedAt = Now;
        var attempt = new ProvisionAttemptRecord
        {
            OperationId = operation.Id,
            IntentDigest = operation.IntentDigest,
            IntentJson = operation.ApprovedIntentJson,
            HostId = operation.HostId,
            WorkspaceId = operation.WorkspaceId,
            WorkspaceIdentity = operation.ApprovedWorkspaceIdentity,
            AuthorityRevision = operation.AuthorityRevision!.Value,
            CapacityReservationId = reservation.Id,
            CapacityRevision = reservation.Revision,
            CapacityFingerprint = ProvisionCapacityFingerprint(operation),
            ClaimGeneration = operation.ClaimGeneration,
            EnrollmentId = executor.Id,
            ExecutorAuthorityGeneration = executor.AuthorityGeneration,
            IncarnationId = executor.IncarnationId,
            ReservationGrantGeneration = reservation.GrantGeneration,
            ClaimedAt = Now,
            Revision = 1,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.ProvisionAttempts.Add(attempt);
        return await Assignment(db, operation, attempt, reservation);
    });

    public Task<HostProvisioningAssignment> HostProvisioningAssignment(HostExecutorPrincipal principal, string id) => Read(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        var executor = await RequireProvisioningExecutor(db, principal, operation);
        var attempt = await db.ProvisionAttempts.SingleOrDefaultAsync(x => x.OperationId == operation.Id)
            ?? throw new ControlException("Provisioning assignment has not been claimed.", 409);
        RequireClaim(attempt, executor, operation.ClaimGeneration);
        var reservation = await RequireProvisionReservation(db, operation, attempt.CapacityReservationId);
        return await Assignment(db, operation, attempt, reservation);
    });

    public Task<HostProvisionEffectDecision> BeginHostProvisionEffect(HostExecutorPrincipal principal, string id,
        BeginHostProvisionEffectInput input) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        var executor = await RequireProvisioningExecutor(db, principal, operation);
        var attempt = await RequireProvisionClaim(db, operation, executor, input.ClaimGeneration);
        var reservation = await RequireProvisionReservation(db, operation, input.ReservationId);
        var observationId = ResourceId(input.ObservationId);
        var workspaceId = ResourceId(input.WorkspaceId);
        var intentDigest = Digest(input.IntentDigest, "intent digest");
        var existing = await db.ProvisionEffects.SingleOrDefaultAsync(x => x.OperationId == operation.Id && x.Effect == "up");
        if (existing is not null)
        {
            if (reservation.State is not (HostReservationState.EffectCommitted or HostReservationState.Unknown) ||
                existing.ReservationId != reservation.Id || existing.ClaimGeneration != attempt.ClaimGeneration ||
                existing.EnrollmentId != executor.Id || existing.AuthorityGeneration != executor.AuthorityGeneration ||
                existing.IncarnationId != executor.IncarnationId || !SameDigest(existing.IntentDigest, intentDigest) ||
                existing.WorkspaceId != workspaceId || existing.ObservationId != observationId)
                throw new ProvisionAdmissionException("inconsistent_committed_provision_effect");
            return new HostProvisionEffectDecision(false, await ProvisionView(db, operation));
        }
        if (reservation.State != HostReservationState.Held)
            throw new ProvisionAdmissionException("inconsistent_committed_provision_effect");
        if (operation.State != ProvisionOperationState.AwaitingExecution || operation.CancelRequested || operation.ReconcileRequested ||
            operation.CapacityReservationId != reservation.Id || operation.CapacityRevision != reservation.Revision ||
            attempt.CapacityReservationId != reservation.Id || attempt.CapacityRevision != reservation.Revision ||
            input.ExpectedReservationRevision != reservation.Revision || input.ReservationGrantGeneration != reservation.GrantGeneration ||
            attempt.ReservationGrantGeneration != reservation.GrantGeneration || reservation.GrantExpiresAt <= Now ||
            workspaceId != operation.WorkspaceId || !SameDigest(intentDigest, operation.IntentDigest) ||
            attempt.IntentJson != operation.ApprovedIntentJson || attempt.CapacityFingerprint != ProvisionCapacityFingerprint(operation))
            throw new ProvisionAdmissionException("fresh_capacity_reservation_required");
        var observation = await db.HostResourceObservations.FindAsync(observationId)
            ?? throw new ControlException("Effect-time observation not found.", 404);
        var policy = await db.HostResourcePolicies.Where(x => x.Id == reservation.PolicyId).OrderByDescending(x => x.Revision).FirstOrDefaultAsync();
        var host = await RequireHost(db, reservation.HostId);
        if (executor.State != HostExecutorState.Active || executor.AuthorityGeneration != reservation.AuthorityGeneration ||
            executor.IncarnationId != reservation.IncarnationId || policy is null || policy.State != "Active" || policy.Revision != reservation.PolicyRevision ||
            host.Archived || !ObservationMatches(reservation, observation) || !Fresh(observation, policy) ||
            observation.PhysicalRevision != await LatestPhysicalRevision(db, reservation.PhysicalHostId) ||
            observation.Sequence != await LatestWorkspaceSequence(db, reservation.EnrollmentId, reservation.WorkspaceId))
            throw new ProvisionAdmissionException("effect_time_authority_changed");
        var otherHeld = (await HeldReservations(db, reservation.PhysicalHostId)).Where(x => x.Id != reservation.Id).ToList();
        RequireCapacity(reservation.Kind, reservation.CpuMillis, reservation.MemoryBytes, reservation.DiskBytes,
            reservation.BuildSlots, observation, policy, otherHeld);
        var now = Now;
        reservation.State = HostReservationState.EffectCommitted;
        reservation.EffectCommittedAt = now;
        reservation.EffectObservationId = observation.Id;
        reservation.Revision++;
        db.ProvisionEffects.Add(new()
        {
            OperationId = operation.Id,
            Effect = "up",
            ResourceId = operation.WorkspaceId,
            ReservationId = reservation.Id,
            ReservationRevision = reservation.Revision,
            ReservationGrantGeneration = reservation.GrantGeneration,
            ClaimGeneration = attempt.ClaimGeneration,
            EnrollmentId = executor.Id,
            AuthorityGeneration = executor.AuthorityGeneration,
            IncarnationId = executor.IncarnationId,
            IntentDigest = operation.IntentDigest,
            WorkspaceId = operation.WorkspaceId,
            CanonicalWorkspaceIdentity = reservation.CanonicalWorkspaceIdentity,
            ObservationId = observation.Id,
            PolicyId = policy.Id,
            PolicyRevision = policy.Revision,
            StartedAt = now
        });
        operation.EffectStarted = true;
        operation.State = ProvisionOperationState.Unknown;
        operation.Code = "external_effect_started_result_pending";
        operation.Revision++;
        operation.UpdatedAt = now;
        Event(db, "ProvisionEffectCommitted", operation.RuntimeId, payload: new
        {
            operationId = operation.Id,
            reservationId = reservation.Id,
            enrollmentId = executor.Id,
            attempt.ClaimGeneration,
            observationId = observation.Id
        }, provenance: "host-executor");
        return new HostProvisionEffectDecision(true, await ProvisionView(db, operation));
    });

    public Task<ProvisionOperationView> RecordHostProvisionProgress(HostExecutorPrincipal principal, string id,
        HostProvisionProgressInput input) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        var executor = await RequireProvisioningExecutor(db, principal, operation);
        var attempt = await RequireProvisionClaim(db, operation, executor, input.ClaimGeneration);
        if (input.Sequence < 1) throw new ProvisionAdmissionException("invalid_progress_metadata");
        var reportId = InventoryId(input.ReportId);
        var stage = BoundProvisionStage(input.Stage);
        var payload = Json.Write(new { stage });
        var hash = ReportHash(new { operation.Id, input.ClaimGeneration, input.Sequence, stage });
        if (await ReplayReport(db, reportId, operation.Id, input.ClaimGeneration, input.Sequence, "Progress", hash))
            return await ProvisionView(db, operation);
        if (operation.State is not (ProvisionOperationState.AwaitingExecution or ProvisionOperationState.Unknown or ProvisionOperationState.AwaitingEnrollment))
            throw new ControlException("Provisioning operation is not accepting execution progress.", 409);
        db.Set<ProvisionExecutorReportRecord>().Add(new()
        {
            ReportId = reportId,
            OperationId = operation.Id,
            ClaimGeneration = attempt.ClaimGeneration,
            Sequence = input.Sequence,
            Kind = "Progress",
            RequestHash = hash,
            PayloadJson = payload,
            Disposition = operation.State == ProvisionOperationState.AwaitingEnrollment ? "IgnoredAfterSuccess" : "Applied",
            ReceivedAt = Now
        });
        if (operation.State != ProvisionOperationState.AwaitingEnrollment)
            return await RecordProvisionProgressCore(db, operation, new(input.Sequence, stage, ""));
        return await ProvisionView(db, operation);
    });

    public Task<ProvisionOperationView> RecordHostProvisionResult(HostExecutorPrincipal principal, string id,
        HostProvisionResultInput input) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        var executor = await RequireProvisioningExecutor(db, principal, operation);
        var attempt = await RequireProvisionClaim(db, operation, executor, input.ClaimGeneration);
        if (!string.Equals(operation.Id, InventoryId(input.OperationId), StringComparison.Ordinal))
            throw new ProvisionIntentConflictException();
        if (!SameDigest(input.IntentDigest, operation.IntentDigest)) throw new ProvisionIntentConflictException();
        if (input.State is not ("VerifiedEnvironment" or "Unknown" or "Observed" or "Failed" or "Unsupported"))
            throw new ProvisionAdmissionException("invalid_result_state");
        if (input.Sequence < 1) throw new ProvisionAdmissionException("invalid_result_sequence");
        var reportId = InventoryId(input.ReportId);
        var code = BoundProvisionCode(input.Code);
        var hash = ResultReportHash(operation.Id, input with { ReportId = reportId, Code = code });
        if (await ReplayReport(db, reportId, operation.Id, input.ClaimGeneration, input.Sequence, "Result", hash))
            return await ProvisionView(db, operation);
        var disposition = input.Sequence <= operation.LastAppliedResultSequence ? "Superseded" :
            operation.State == ProvisionOperationState.AwaitingEnrollment ? "IgnoredAfterSuccess" : "Applied";
        db.Set<ProvisionExecutorReportRecord>().Add(new()
        {
            ReportId = reportId,
            OperationId = operation.Id,
            ClaimGeneration = attempt.ClaimGeneration,
            Sequence = input.Sequence,
            Kind = "Result",
            RequestHash = hash,
            PayloadJson = "{}",
            Disposition = disposition,
            ReceivedAt = Now
        });
        if (disposition != "Applied") return await ProvisionView(db, operation);
        var effect = await db.ProvisionEffects.SingleOrDefaultAsync(x => x.OperationId == operation.Id && x.Effect == "up");
        var effectStarted = effect is not null;
        var approved = JsonSerializer.Deserialize<ProvisionIntent>(operation.ApprovedIntentJson)!;
        var result = new HostProvisionResult(operation.Id, input.State, code,
            effectStarted, approved, null, input.Observed, input.Executed, ImmutableArray<ProvisionProgress>.Empty,
            input.RetainedResources.IsDefault ? ImmutableArray<string>.Empty : input.RetainedResources);
        if (input.State == "VerifiedEnvironment")
            RequireVerifiedEnvironment(operation, attempt, effect, input.Observed, input.Executed, approved);
        operation.LastAppliedResultSequence = input.Sequence;
        if (effectStarted && input.State != "VerifiedEnvironment")
            result = result with { State = "Unknown", Code = "external_effect_result_requires_reconciliation" };
        return await RecordProvisionResultCore(db, operation, result);
    });

    private static async Task<HostExecutorEnrollment> RequireProvisioningExecutor(ControlDb db, HostExecutorPrincipal principal,
        ProvisionOperationRecord operation)
    {
        var executor = await RequireAuthenticatedExecutor(db, principal, allowPending: false);
        if (executor.State != HostExecutorState.Active || executor.HostId != operation.HostId)
            throw new ControlException("An active executor for the assigned host is required.", 403);
        if (operation.SelectedEnrollmentId.Length != 0 && (operation.SelectedEnrollmentId != executor.Id ||
            operation.SelectedAuthorityGeneration != executor.AuthorityGeneration || operation.SelectedIncarnationId != executor.IncarnationId))
            throw new ControlException("Provisioning operation belongs to another executor authority.", 403);
        return executor;
    }

    internal static readonly ConcurrentDictionary<string, SemaphoreSlim> ProvisionAdmissions = new(StringComparer.Ordinal);

    public Task<ProvisionOperationView> CreateProvisionOperation(CreateProvisionOperationInput input)
    {
        var normalized = NormalizeProvisionRequest(input);
        var hash = HashProvisionRequest(normalized);
        return Write(async db =>
        {
            if (await db.ProvisionOperations.SingleOrDefaultAsync(x => x.Id == normalized.RequestId) is { } prior)
            {
                if (prior.RequestHash != hash) throw InventoryConflict("idempotency_conflict", "This operation ID belongs to different provisioning intent.");
                return await ProvisionView(db, prior);
            }

            var host = await RequireHost(db, normalized.HostId);
            var project = await RequireProject(db, normalized.ProjectId);
            var runtime = await RequireEnvironmentRuntime(db, normalized.RuntimeId);
            var environment = await db.RuntimeEnvironments.FindAsync(normalized.RuntimeId);

            if (host.Archived || project.Archived) throw InventoryConflict("resource_archived", "Provisioning identities must be active.");
            if (host.Revision != normalized.ExpectedHostRevision || project.Revision != normalized.ExpectedProjectRevision ||
                runtime.Revision != normalized.ExpectedRuntimeRevision || environment?.Revision != normalized.ExpectedEnvironmentRevision)
                throw InventoryConflict("revision_conflict", "Host, project, runtime or environment changed; refresh before provisioning.");
            if (environment.Kind != RuntimeEnvironmentKind.ManagedDevcontainer || environment.HostId != host.Id ||
                environment.ConfigurationProjectId != project.Id || environment.DevcontainerPath != normalized.ConfigurationPath)
                throw InventoryConflict("configuration_conflict", "Runtime environment does not match the requested managed Dev Container placement.");

            var record = new ProvisionOperationRecord
            {
                Id = normalized.RequestId,
                RequestHash = hash,
                RequestedJson = Json.Write(normalized),
                HostId = host.Id,
                HostRevision = host.Revision,
                RuntimeId = runtime.Id,
                RuntimeRevision = runtime.Revision,
                EnvironmentRevision = environment.Revision,
                ProjectId = project.Id,
                ProjectRevision = project.Revision,
                WorkspaceId = normalized.WorkspaceId,
                SourceRevision = normalized.SourceRevision,
                ConfigurationPath = normalized.ConfigurationPath,
                ConfigurationSha256 = normalized.ConfigurationSha256,
                RequestedBuildCpuMillis = normalized.RequestedBuildCpuMillis,
                RequestedBuildMemoryBytes = normalized.RequestedBuildMemoryBytes,
                RequestedRuntimeCpuMillis = normalized.RequestedRuntimeCpuMillis,
                RequestedRuntimeMemoryBytes = normalized.RequestedRuntimeMemoryBytes,
                ColdBuild = normalized.ColdBuild,
                CreatedAt = Now,
                UpdatedAt = Now
            };
            db.ProvisionOperations.Add(record);
            await db.SaveChangesAsync();
            Event(db, "ProvisionOperationAccepted", runtime.Id, payload: new { operationId = record.Id, record.State }, provenance: "user");
            return await ProvisionView(db, record);
        });
    }

    public Task<ProvisionOperationView> ProvisionOperation(string id) => Read(async db =>
    {
        var operationId = InventoryId(id);
        return await ProvisionView(db, await db.ProvisionOperations.SingleOrDefaultAsync(x => x.Id == operationId)
            ?? throw new InventoryException("not_found", "Provisioning operation not found.", 404));
    });

    public Task<ProvisionOperationPage> ProvisionOperations(long after = 0, int take = 50) => Read(async db =>
    {
        ValidateInventoryPage(after, take);
        var rows = await db.ProvisionOperations.AsNoTracking().Where(x => x.Sequence > after)
            .OrderBy(x => x.Sequence).Take(take + 1).ToListAsync();
        var views = new List<ProvisionOperationView>();
        foreach (var row in rows.Take(take)) views.Add(await ProvisionView(db, row));
        return new ProvisionOperationPage(views, rows.Count > take ? rows[take - 1].Sequence : null);
    });

    public Task<ProvisionOperationView> CancelProvisionOperation(string id, ProvisionOperationControlInput input) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        RequireProvisionRevision(operation, input.ExpectedRevision);
        if (operation.State == ProvisionOperationState.AwaitingEnrollment)
            throw InventoryConflict("terminal_success_immutable", "A verified environment cannot be cancelled as an unresolved provisioning attempt.");
        operation.CancelRequested = true;
        var hasEffect = await db.ProvisionEffects.AnyAsync(x => x.OperationId == operation.Id);
        operation.State = hasEffect ? ProvisionOperationState.Unknown : ProvisionOperationState.Cancelled;
        operation.Code = hasEffect ? "cancellation_requested_external_effect_unresolved" : "cancelled_before_external_effect";
        operation.Revision++;
        operation.UpdatedAt = Now;
        Event(db, "ProvisionOperationCancellationRequested", operation.RuntimeId, payload: new { operationId = operation.Id, operation.State }, provenance: "user");
        return await ProvisionView(db, operation);
    });

    public Task<ProvisionOperationView> RequestProvisionReconciliation(string id, ProvisionOperationControlInput input) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        RequireProvisionRevision(operation, input.ExpectedRevision);
        if (operation.State == ProvisionOperationState.AwaitingEnrollment)
            throw InventoryConflict("terminal_success_immutable", "A verified environment cannot be returned to provisioning reconciliation.");
        operation.ReconcileRequested = true;
        if (await db.ProvisionEffects.AnyAsync(x => x.OperationId == operation.Id))
        {
            operation.State = ProvisionOperationState.Unknown;
            operation.Code = "reconciliation_requested_external_effect_unresolved";
        }
        else
        {
            ClearProvisionCapacity(operation);
            operation.State = operation.ApprovedIntentJson.Length == 0
                ? ProvisionOperationState.AwaitingHostAuthority
                : ProvisionOperationState.AwaitingCapacity;
            operation.Code = operation.ApprovedIntentJson.Length == 0
                ? "reconciliation_awaiting_trusted_host_executor"
                : "reconciliation_awaiting_verified_capacity";
        }
        operation.Revision++;
        operation.UpdatedAt = Now;
        Event(db, "ProvisionOperationReconciliationRequested", operation.RuntimeId, payload: new { operationId = operation.Id, operation.State }, provenance: "user");
        return await ProvisionView(db, operation);
    });

    internal Task<ProvisionOperationView> ApproveProvisionAuthority(string id, ProvisionIntent intent) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        return await ApproveProvisionAuthorityCore(db, operation, intent);
    });

    private static async Task<ProvisionOperationView> ApproveProvisionAuthorityCore(ControlDb db,
        ProvisionOperationRecord operation, ProvisionIntent intent)
    {
        var project = await RequireProject(db, operation.ProjectId);
        if (operation.ApprovedIntentJson.Length != 0)
        {
            if (!SameProvisionIntent(intent, JsonSerializer.Deserialize<ProvisionIntent>(operation.ApprovedIntentJson)!))
                throw new ProvisionIntentConflictException();
            return await ProvisionView(db, operation);
        }
        if (operation.CancelRequested || operation.EffectStarted) throw new ProvisionAdmissionException("operation_not_eligible_for_authority");
        if (InventoryId(intent.OperationId) != operation.Id || intent.HostId != operation.HostId || intent.Workspace.Id != operation.WorkspaceId ||
            intent.AuthorityRevision < 1 || intent.Workspace.SourceRevision != operation.SourceRevision ||
            intent.Workspace.ConfigurationPath != operation.ConfigurationPath || intent.Workspace.ConfigurationSha256 != operation.ConfigurationSha256 ||
            intent.ColdBuild != operation.ColdBuild || intent.CliVersion != LocalDevContainerRunner.CliVersion ||
            intent.CliSha256 != LocalDevContainerRunner.CliSha256 || intent.Digest.Length != 64 || intent.Digest.Any(x => !Uri.IsHexDigit(x)) ||
            CanonicalProjectRepository(intent.Workspace.Repository) != project.RepositoryUrl ||
            !RequiredIntentLabels(intent).All(x => intent.Labels.TryGetValue(x.Key, out var value) && value == x.Value) ||
            intent.Labels.Count != RequiredIntentLabels(intent).Count)
            throw new ProvisionIntentConflictException();
        operation.ApprovedIntentJson = JsonSerializer.Serialize(intent);
        operation.IntentDigest = intent.Digest;
        operation.AuthorityRevision = intent.AuthorityRevision;
        operation.ApprovedWorkspaceIdentity = CanonicalProvisionWorkspace(intent.Workspace.Directory);
        ClearProvisionCapacity(operation);
        operation.State = ProvisionOperationState.AwaitingCapacity;
        operation.Code = "verified_capacity_reservation_required";
        operation.Revision++;
        operation.UpdatedAt = Now;
        return await ProvisionView(db, operation);
    }

    internal Task<ProvisionOperationView> ApproveProvisionCapacity(string id, ProvisionCapacityGrant grant) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        if (operation.ApprovedIntentJson.Length == 0) throw new ProvisionAdmissionException("trusted_host_authority_required");
        if (operation.CancelRequested || operation.EffectStarted || string.IsNullOrWhiteSpace(grant.ReservationId) || grant.ReservationId.Length > 200 ||
            grant.ReservationId.Any(char.IsControl) || grant.Revision < 1 || grant.ValidUntil <= Now ||
            grant.BuildCpuMillis < operation.RequestedBuildCpuMillis || grant.BuildMemoryBytes < operation.RequestedBuildMemoryBytes ||
            grant.RuntimeCpuMillis < operation.RequestedRuntimeCpuMillis || grant.RuntimeMemoryBytes < operation.RequestedRuntimeMemoryBytes)
            throw new ProvisionAdmissionException("valid_capacity_reservation_required");
        operation.CapacityReservationId = grant.ReservationId;
        operation.CapacityRevision = grant.Revision;
        operation.CapacityValidUntil = grant.ValidUntil;
        operation.ReservedBuildCpuMillis = grant.BuildCpuMillis;
        operation.ReservedBuildMemoryBytes = grant.BuildMemoryBytes;
        operation.ReservedRuntimeCpuMillis = grant.RuntimeCpuMillis;
        operation.ReservedRuntimeMemoryBytes = grant.RuntimeMemoryBytes;
        operation.ReconcileRequested = false;
        operation.State = ProvisionOperationState.AwaitingExecution;
        operation.Code = "trusted_executor_may_acquire";
        operation.Revision++;
        operation.UpdatedAt = Now;
        return await ProvisionView(db, operation);
    });

    internal Task<ProvisionOperationView> RecordProvisionProgress(string id, ProvisionProgress progress) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        return await RecordProvisionProgressCore(db, operation, progress);
    });

    private static async Task<ProvisionOperationView> RecordProvisionProgressCore(ControlDb db,
        ProvisionOperationRecord operation, ProvisionProgress progress)
    {
        var items = Json.Read<List<ProvisionProgress>>(operation.ProgressJson);
        if (progress.Sequence < 0 || items.Count > 0 && progress.Sequence <= items[^1].Sequence)
            throw new ProvisionAdmissionException("invalid_progress_metadata");
        // Raw CLI output may include configuration values. Persist only a bounded
        // stage marker; native output belongs in host diagnostics, not the owner API.
        items.Add(progress with { Text = "", Stage = BoundProvisionStage(progress.Stage) });
        operation.ProgressJson = Json.Write(items.TakeLast(64).ToList());
        operation.Revision++;
        operation.UpdatedAt = Now;
        return await ProvisionView(db, operation);
    }

    internal Task<ProvisionOperationView> RecordProvisionResult(string id, HostProvisionResult result) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
        return await RecordProvisionResultCore(db, operation, result);
    });

    private static async Task<ProvisionOperationView> RecordProvisionResultCore(ControlDb db,
        ProvisionOperationRecord operation, HostProvisionResult result)
    {
        if (operation.State == ProvisionOperationState.AwaitingEnrollment)
        {
            var existing = operation.ResultJson.Length == 0 ? null : JsonSerializer.Deserialize<ProvisionResultReceipt>(operation.ResultJson);
            var incoming = ProvisionReceipt(result);
            if (existing is not null && Json.Write(existing) == Json.Write(incoming)) return await ProvisionView(db, operation);
            return await ProvisionView(db, operation);
        }
        if (InventoryId(result.OperationId) != operation.Id) throw new ProvisionIntentConflictException();
        var receipt = ProvisionReceipt(result);
        if (result.EffectStarted && !await db.ProvisionEffects.AnyAsync(x => x.OperationId == operation.Id))
            throw new ProvisionAdmissionException("effect_receipt_required");
        if (result.Requested is { } requested && (operation.ApprovedIntentJson.Length == 0 || !SameProvisionIntent(requested,
            JsonSerializer.Deserialize<ProvisionIntent>(operation.ApprovedIntentJson)!)))
            throw new ProvisionIntentConflictException();
        if (result.Observed is { } observed)
        {
            if (operation.ApprovedIntentJson.Length == 0) throw new ProvisionAdmissionException("approved_intent_required_for_ownership_receipt");
            var approved = JsonSerializer.Deserialize<ProvisionIntent>(operation.ApprovedIntentJson)!;
            if (approved.Labels.Any(x => !observed.Labels.TryGetValue(x.Key, out var value) || value != x.Value))
                throw new ProvisionAdmissionException("ownership_receipt_mismatch");
        }
        operation.ResultJson = JsonSerializer.Serialize(receipt);
        operation.ObservedContainerId = result.Observed?.ContainerId ?? "";
        operation.ObservedImageId = result.Observed?.ImageId ?? "";
        if (operation.CancelRequested)
        {
            operation.State = operation.EffectStarted ? ProvisionOperationState.Unknown : ProvisionOperationState.Cancelled;
            operation.Code = operation.EffectStarted ? "cancelled_after_effect_result_recorded_reconciliation_required" : "cancelled_before_external_effect";
        }
        else
        {
            operation.State = result.State == "VerifiedEnvironment" ? ProvisionOperationState.AwaitingEnrollment :
                result.State is "Unknown" or "Observed" ? ProvisionOperationState.Unknown : ProvisionOperationState.Failed;
            operation.Code = result.State == "VerifiedEnvironment" ? "environment_verified_enrollment_not_performed" : result.Code;
        }
        if (result.State == "VerifiedEnvironment")
        {
            operation.TerminalSuccessSequence = operation.LastAppliedResultSequence == 0 ? null : operation.LastAppliedResultSequence;
            operation.TerminalSuccessAt = Now;
        }
        if (!operation.EffectStarted && result.State is ("Failed" or "Unsupported") && await db.ProvisionAttempts.FindAsync(operation.Id) is { } unused)
            db.ProvisionAttempts.Remove(unused);
        operation.Revision++;
        operation.UpdatedAt = Now;
        return await ProvisionView(db, operation);
    }

    private static CreateProvisionOperationInput NormalizeProvisionRequest(CreateProvisionOperationInput input)
    {
        var path = ValidateDevcontainerPath(input.ConfigurationPath) ?? throw new InventoryException("validation", "Dev Container configuration path is required.");
        var source = input.SourceRevision?.Trim().ToLowerInvariant() ?? "";
        var config = input.ConfigurationSha256?.Trim().ToLowerInvariant() ?? "";
        if (source.Length != 40 || source.Any(x => !Uri.IsHexDigit(x)) || config.Length != 64 || config.Any(x => !Uri.IsHexDigit(x)))
            throw new InventoryException("validation", "Source revision and configuration SHA-256 must be full hexadecimal digests.");
        if (input.ExpectedHostRevision < 1 || input.ExpectedRuntimeRevision < 1 || input.ExpectedEnvironmentRevision < 1 || input.ExpectedProjectRevision < 1)
            throw new InventoryException("validation", "All expected revisions must be positive.");
        if (input.RequestedBuildCpuMillis is < 1 or > 128000 || input.RequestedRuntimeCpuMillis is < 1 or > 128000 ||
            input.RequestedBuildMemoryBytes is < 134217728 or > 1099511627776 || input.RequestedRuntimeMemoryBytes is < 134217728 or > 1099511627776)
            throw new InventoryException("validation", "Requested build/runtime CPU and memory must be positive and within supported bounds.");
        return input with
        {
            RequestId = InventoryId(input.RequestId),
            HostId = InventoryId(input.HostId),
            RuntimeId = RuntimeEnvironmentId(input.RuntimeId),
            ProjectId = InventoryId(input.ProjectId),
            WorkspaceId = InventoryId(input.WorkspaceId),
            SourceRevision = source,
            ConfigurationPath = path,
            ConfigurationSha256 = config
        };
    }

    private static string HashProvisionRequest(CreateProvisionOperationInput input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(input))));

    private static async Task<ProvisionOperationRecord> RequireProvisionOperation(ControlDb db, string id) =>
        await db.ProvisionOperations.SingleOrDefaultAsync(x => x.Id == InventoryId(id))
        ?? throw new InventoryException("not_found", "Provisioning operation not found.", 404);

    private static void RequireProvisionRevision(ProvisionOperationRecord operation, long expected)
    {
        if (expected < 1) throw new InventoryException("validation", "Expected revision must be positive.");
        if (operation.Revision != expected) throw InventoryConflict("revision_conflict", "Provisioning operation changed; refresh before retrying.");
    }

    private static async Task<ProvisionOperationView> ProvisionView(ControlDb db, ProvisionOperationRecord operation)
    {
        var effects = await db.ProvisionEffects.AsNoTracking().Where(x => x.OperationId == operation.Id)
            .OrderBy(x => x.StartedAt).ThenBy(x => x.Effect).ThenBy(x => x.ResourceId)
            .Take(100).Select(x => new ProvisionEffectView(x.Effect, x.ResourceId, x.StartedAt)).ToListAsync();
        var progress = Json.Read<List<ProvisionProgress>>(operation.ProgressJson);
        return new(operation.Sequence, operation.Id, operation.HostId, operation.HostRevision, operation.RuntimeId,
            operation.RuntimeRevision, operation.EnvironmentRevision, operation.ProjectId, operation.ProjectRevision,
            operation.WorkspaceId, operation.SourceRevision, operation.ConfigurationPath, operation.ConfigurationSha256,
            operation.RequestedBuildCpuMillis, operation.RequestedBuildMemoryBytes, operation.RequestedRuntimeCpuMillis,
            operation.RequestedRuntimeMemoryBytes,
            operation.ColdBuild, operation.AuthorityRevision, operation.IntentDigest.Length == 0 ? null : operation.IntentDigest,
            operation.CapacityReservationId.Length == 0 ? null : operation.CapacityReservationId, operation.CapacityRevision,
            operation.CapacityValidUntil, operation.State, operation.Code, operation.CancelRequested, operation.ReconcileRequested,
            operation.EffectStarted, operation.Revision, operation.CreatedAt, operation.UpdatedAt, progress, effects,
            operation.ObservedContainerId.Length == 0 ? null : operation.ObservedContainerId,
            operation.ObservedImageId.Length == 0 ? null : operation.ObservedImageId);
    }

    private static string BoundProvisionStage(string? value)
    {
        var text = value?.Replace("\0", "", StringComparison.Ordinal) ?? "";
        if (text.Length is < 1 or > 64 || text.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_' or '.')))
            return "host-progress";
        return text;
    }

    private static string BoundProvisionCode(string? value)
    {
        var text = value?.Replace("\0", "", StringComparison.Ordinal) ?? "";
        if (text.Length is < 1 or > 200 || text.Any(char.IsControl))
            throw new ProvisionAdmissionException("result_evidence_outside_bounds");
        return text;
    }

    private static ProvisionResultReceipt ProvisionReceipt(HostProvisionResult result)
    {
        if (result.State is not ("VerifiedEnvironment" or "Observed" or "Unknown" or "Failed" or "Unsupported" or "Removed" or "AbsentObserved") ||
            result.Code.Length is < 1 or > 200 || result.Code.Any(char.IsControl) || result.RetainedResources.Length > 32 ||
            result.RetainedResources.Any(x => x.Length > 2048 || x.Any(char.IsControl)))
            throw new ProvisionAdmissionException("result_evidence_outside_bounds");
        if (result.Observed is { } observed && (observed.ContainerId.Length > 128 || observed.ImageId.Length > 128 || observed.ImageReference.Length > 512 ||
            observed.ContainerUser.Length > 128 || observed.Labels.Count > 32 || observed.Mounts.Length > 32 ||
            observed.Labels.Any(x => x.Key.Length > 128 || x.Value.Length > 1024 || x.Key.Any(char.IsControl) || x.Value.Any(char.IsControl)) ||
            observed.Mounts.Any(x => x.Type.Length > 32 || x.Source.Length > 2048 || x.Destination.Length > 2048 || (x.VolumeName?.Length ?? 0) > 256)))
            throw new ProvisionAdmissionException("result_evidence_outside_bounds");
        if (result.Executed is { } executed && (executed.RemoteUser.Length > 128 || executed.UserId.Length > 64 || executed.Workspace.Length > 2048 ||
            executed.Tools.Count > 32 || executed.Tools.Any(x => x.Key.Length > 128 || x.Value.Length > 512 || x.Key.Any(char.IsControl) || x.Value.Any(char.IsControl))))
            throw new ProvisionAdmissionException("result_evidence_outside_bounds");
        return new(result.OperationId, result.State, result.Code, result.EffectStarted, result.Requested?.Digest,
            result.Observed, result.Executed, result.RetainedResources);
    }

    internal static bool SameProvisionIntent(ProvisionIntent left, ProvisionIntent right) => ProvisionIntentIdentity.Same(left, right);

    private static Dictionary<string, string> RequiredIntentLabels(ProvisionIntent intent) => new(StringComparer.Ordinal)
    {
        ["hvo.agentcontrol.provisioner"] = "official-cli-v1",
        ["hvo.agentcontrol.operation"] = intent.OperationId,
        ["hvo.agentcontrol.host"] = intent.HostId,
        ["hvo.agentcontrol.workspace"] = intent.Workspace.Id,
        ["hvo.agentcontrol.intent"] = intent.Digest,
        ["hvo.agentcontrol.source"] = intent.Workspace.SourceRevision,
        ["hvo.agentcontrol.config"] = intent.Workspace.ConfigurationSha256
    };

    internal static string CanonicalProvisionWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new ProvisionAdmissionException("invalid_workspace_identity");
        try
        {
            var canonical = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return canonical.Length == 0 ? Path.DirectorySeparatorChar.ToString() : canonical;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProvisionAdmissionException("invalid_workspace_identity");
        }
    }

    internal static string ProvisionCapacityFingerprint(ProvisionOperationRecord operation) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Json.Write(new
        {
            operation.CapacityReservationId,
            operation.CapacityRevision,
            operation.CapacityValidUntil,
            operation.ReservedBuildCpuMillis,
            operation.ReservedBuildMemoryBytes,
            operation.ReservedRuntimeCpuMillis,
            operation.ReservedRuntimeMemoryBytes
        }))));

    internal static string ProvisionWorkspaceDigest(string canonicalPath) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(CanonicalProvisionWorkspace(canonicalPath))));

    private static bool SameDigest(string? left, string? right) => left is not null && right is not null &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static async Task<HostResourceReservation> RequireProvisionReservation(ControlDb db,
        ProvisionOperationRecord operation, string reservationId)
    {
        var reservation = await db.HostResourceReservations.FindAsync(ResourceId(reservationId))
            ?? throw new ControlException("Resource reservation not found.", 404);
        if (reservation.OperationId != operation.Id || reservation.HostId != operation.HostId ||
            reservation.WorkspaceId != operation.WorkspaceId || reservation.EnrollmentId != operation.SelectedEnrollmentId ||
            reservation.AuthorityGeneration != operation.SelectedAuthorityGeneration || reservation.IncarnationId != operation.SelectedIncarnationId ||
            !SameDigest(reservation.IntentDigest, operation.IntentDigest) || reservation.CanonicalWorkspaceIdentity != ProvisionWorkspaceDigest(operation.ApprovedWorkspaceIdentity))
            throw new ProvisionAdmissionException("provision_reservation_authority_mismatch");
        return reservation;
    }

    private static async Task<HostProvisioningAssignment> Assignment(ControlDb db, ProvisionOperationRecord operation,
        ProvisionAttemptRecord attempt, HostResourceReservation reservation)
    {
        var committed = await db.ProvisionEffects.AnyAsync(x => x.OperationId == operation.Id && x.Effect == "up" && x.ReservationId == reservation.Id);
        if (operation.State == ProvisionOperationState.AwaitingExecution)
        {
            if (reservation.State != HostReservationState.Held || reservation.GrantExpiresAt <= Now)
                throw new ControlException("Provisioning assignment no longer has a fresh permit.", 409);
        }
        else if (operation.State != ProvisionOperationState.Unknown || !committed ||
            reservation.State is not (HostReservationState.EffectCommitted or HostReservationState.Unknown))
            throw new ControlException("Provisioning operation is not available to this claim.", 409);
        var intent = JsonSerializer.Deserialize<ProvisionIntent>(operation.ApprovedIntentJson)
            ?? throw new ControlException("Approved provisioning intent is unavailable.", 503);
        return new(await ProvisionView(db, operation), intent, reservation.Id, reservation.Revision,
            reservation.GrantExpiresAt, operation.ReservedBuildCpuMillis ?? 0, operation.ReservedBuildMemoryBytes ?? 0,
            operation.ReservedRuntimeCpuMillis ?? 0, operation.ReservedRuntimeMemoryBytes ?? 0,
            attempt.ClaimGeneration, attempt.EnrollmentId, attempt.ExecutorAuthorityGeneration, attempt.IncarnationId,
            attempt.ReservationGrantGeneration);
    }

    private static void RequireClaim(ProvisionAttemptRecord attempt, HostExecutorEnrollment executor, long claimGeneration)
    {
        if (claimGeneration < 1 || attempt.ClaimGeneration != claimGeneration || attempt.EnrollmentId != executor.Id ||
            attempt.ExecutorAuthorityGeneration != executor.AuthorityGeneration || attempt.IncarnationId != executor.IncarnationId)
            throw new ControlException("Provisioning claim does not match the authenticated executor incarnation.", 403);
    }

    private static async Task<ProvisionAttemptRecord> RequireProvisionClaim(ControlDb db, ProvisionOperationRecord operation,
        HostExecutorEnrollment executor, long claimGeneration)
    {
        var attempt = await db.ProvisionAttempts.SingleOrDefaultAsync(x => x.OperationId == operation.Id)
            ?? throw new ControlException("Provisioning assignment has not been claimed.", 409);
        RequireClaim(attempt, executor, claimGeneration);
        return attempt;
    }

    private static async Task<bool> ReplayReport(ControlDb db, string reportId, string operationId,
        long claimGeneration, long sequence, string kind, string hash)
    {
        if (await db.Set<ProvisionExecutorReportRecord>().FindAsync(reportId) is { } prior)
        {
            if (prior.OperationId != operationId || prior.ClaimGeneration != claimGeneration || prior.Sequence != sequence ||
                prior.Kind != kind || prior.RequestHash != hash)
                throw new ProvisionAdmissionException("report_id_conflict");
            return true;
        }
        if (await db.Set<ProvisionExecutorReportRecord>().AnyAsync(x => x.OperationId == operationId &&
            x.ClaimGeneration == claimGeneration && x.Sequence == sequence))
            throw new ProvisionAdmissionException("report_sequence_conflict");
        return false;
    }

    private static string ReportHash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(value))));

    private static string ResultReportHash(string operationId, HostProvisionResultInput input) => ReportHash(new
    {
        operationId,
        input.ClaimGeneration,
        input.Sequence,
        intentDigest = input.IntentDigest.ToUpperInvariant(),
        input.State,
        input.Code,
        observed = input.Observed is null ? null : new
        {
            input.Observed.ContainerId,
            input.Observed.ImageId,
            input.Observed.ImageReference,
            input.Observed.ContainerUser,
            input.Observed.Running,
            labels = input.Observed.Labels.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
            mounts = input.Observed.Mounts.OrderBy(x => x.Destination, StringComparer.Ordinal).ToArray(),
            input.Observed.CpuLimitMillis,
            input.Observed.MemoryLimitBytes
        },
        executed = input.Executed is null ? null : new
        {
            input.Executed.RemoteUser,
            input.Executed.UserId,
            input.Executed.Workspace,
            tools = input.Executed.Tools.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()
        },
        retained = (input.RetainedResources.IsDefault ? ImmutableArray<string>.Empty : input.RetainedResources)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray()
    });

    private static void RequireVerifiedEnvironment(ProvisionOperationRecord operation, ProvisionAttemptRecord attempt,
        ProvisionEffectRecord? effect, ProvisionContainerObservation? observed, ProvisionExecutionEvidence? executed,
        ProvisionIntent approved)
    {
        if (operation.State != ProvisionOperationState.Unknown || effect is null || effect.ClaimGeneration != attempt.ClaimGeneration ||
            effect.EnrollmentId != attempt.EnrollmentId || effect.AuthorityGeneration != attempt.ExecutorAuthorityGeneration ||
            effect.IncarnationId != attempt.IncarnationId || effect.ReservationId != attempt.CapacityReservationId ||
            effect.ReservationGrantGeneration != attempt.ReservationGrantGeneration || effect.WorkspaceId != attempt.WorkspaceId ||
            effect.CanonicalWorkspaceIdentity != ProvisionWorkspaceDigest(attempt.WorkspaceIdentity) ||
            !SameDigest(effect.IntentDigest, approved.Digest) ||
            observed is null || executed is null || !observed.Running || observed.ContainerId.Length != 64 ||
            observed.ContainerId.Any(x => !Uri.IsHexDigit(x)) || !observed.ImageId.StartsWith("sha256:", StringComparison.Ordinal) ||
            observed.ImageId.Length != 71 || observed.ImageId[7..].Any(x => !Uri.IsHexDigit(x)) ||
            observed.ContainerUser != approved.Workspace.RemoteUser || observed.CpuLimitMillis != operation.RequestedRuntimeCpuMillis ||
            observed.MemoryLimitBytes != operation.RequestedRuntimeMemoryBytes ||
            approved.Labels.Any(x => !observed.Labels.TryGetValue(x.Key, out var value) || value != x.Value) ||
            !observed.Mounts.Any(x => x.Type == "bind" && x.Source == approved.Workspace.Directory &&
                x.Destination == approved.Workspace.ContainerWorkspace && x.Writable) ||
            executed.RemoteUser != approved.Workspace.RemoteUser || executed.Workspace != approved.Workspace.ContainerWorkspace ||
            !AuthorizedExecutionUserId(executed) || approved.Workspace.Tools.Any(x =>
                !executed.Tools.TryGetValue(x.Name, out var output) || !output.Contains(x.ExpectedOutput, StringComparison.Ordinal)))
            throw new ProvisionAdmissionException("verified_environment_evidence_mismatch");
    }

    // A non-root user must not claim a root or negative numeric identity. The approved
    // RemoteUser is an explicit root identity only when it is "root" or a numeric "0".
    private static bool AuthorizedExecutionUserId(ProvisionExecutionEvidence executed)
    {
        if (!long.TryParse(executed.UserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
            return false;
        if (uid > 0) return true;
        if (uid < 0) return false;
        var user = executed.RemoteUser.Trim();
        return user == "root" || user == "0";
    }

    private static void ClearProvisionCapacity(ProvisionOperationRecord operation)
    {
        operation.CapacityReservationId = "";
        operation.CapacityRevision = null;
        operation.CapacityValidUntil = null;
        operation.ReservedBuildCpuMillis = null;
        operation.ReservedBuildMemoryBytes = null;
        operation.ReservedRuntimeCpuMillis = null;
        operation.ReservedRuntimeMemoryBytes = null;
    }
}

public sealed class DbProvisionAttemptLedger(ControlStore store) : IProvisionAttemptLedger
{
    public async Task<IProvisionAttempt> Acquire(ProvisionIntent intent, ProvisionAction action, CancellationToken token)
    {
        var operationId = NormalizeOperationId(intent.OperationId);
        var workspaceIdentity = ControlStore.CanonicalProvisionWorkspace(intent.Workspace.Directory);
        var admissionKey = intent.HostId + ":" + workspaceIdentity;
        var admission = ControlStore.ProvisionAdmissions.GetOrAdd(admissionKey, _ => new SemaphoreSlim(1, 1));
        await admission.WaitAsync(token);
        try
        {
            var snapshot = await store.Write(async db =>
            {
                var operation = await db.ProvisionOperations.SingleOrDefaultAsync(x => x.Id == operationId)
                    ?? throw new ProvisionAdmissionException("durable_operation_required");
                if (operation.CancelRequested && action != ProvisionAction.Observe) throw new OperationCanceledException(token);
                if (operation.ApprovedIntentJson.Length == 0 || operation.IntentDigest != intent.Digest ||
                    !ControlStore.SameProvisionIntent(intent, JsonSerializer.Deserialize<ProvisionIntent>(operation.ApprovedIntentJson)!))
                    throw new ProvisionIntentConflictException();
                if (operation.ApprovedWorkspaceIdentity != workspaceIdentity)
                    throw new ProvisionIntentConflictException();
                if (action == ProvisionAction.Remove) throw new ProvisionAdmissionException("drained_removal_authority_required");
                var prior = await db.ProvisionAttempts.SingleOrDefaultAsync(x => x.HostId == intent.HostId && x.WorkspaceIdentity == workspaceIdentity);
                var effectStarted = await db.ProvisionEffects.AnyAsync(x => x.OperationId == operationId && x.Effect == "up", token);
                if (action == ProvisionAction.CreateOrObserve && !effectStarted && (operation.State != ProvisionOperationState.AwaitingExecution ||
                    operation.CapacityRevision is null || operation.CapacityValidUntil <= ControlStore.Now || operation.CapacityReservationId.Length == 0 ||
                    operation.ReservedBuildCpuMillis < operation.RequestedBuildCpuMillis || operation.ReservedBuildMemoryBytes < operation.RequestedBuildMemoryBytes ||
                    operation.ReservedRuntimeCpuMillis < operation.RequestedRuntimeCpuMillis || operation.ReservedRuntimeMemoryBytes < operation.RequestedRuntimeMemoryBytes))
                    throw new ProvisionAdmissionException("fresh_capacity_reservation_required");
                if (action == ProvisionAction.Observe && !effectStarted)
                    throw new ProvisionAdmissionException("external_effect_evidence_required");
                if (prior is not null && prior.OperationId != operationId) throw new ProvisionAdmissionException("workspace_admission_held_for_reconciliation");
                if (prior is not null && (prior.IntentDigest != intent.Digest || prior.IntentJson != operation.ApprovedIntentJson ||
                    prior.AuthorityRevision != operation.AuthorityRevision || prior.WorkspaceId != intent.Workspace.Id))
                    throw new ProvisionIntentConflictException();
                if (prior is null)
                {
                    prior = new()
                    {
                        OperationId = operationId,
                        IntentDigest = intent.Digest,
                        IntentJson = operation.ApprovedIntentJson,
                        HostId = intent.HostId,
                        WorkspaceId = intent.Workspace.Id,
                        WorkspaceIdentity = workspaceIdentity,
                        AuthorityRevision = operation.AuthorityRevision!.Value,
                        CapacityReservationId = operation.CapacityReservationId,
                        CapacityRevision = operation.CapacityRevision,
                        CapacityFingerprint = ControlStore.ProvisionCapacityFingerprint(operation),
                        Revision = 1,
                        CreatedAt = ControlStore.Now,
                        UpdatedAt = ControlStore.Now
                    };
                    db.ProvisionAttempts.Add(prior);
                }
                else if (!effectStarted)
                {
                    prior.CapacityReservationId = operation.CapacityReservationId;
                    prior.CapacityRevision = operation.CapacityRevision;
                    prior.CapacityFingerprint = ControlStore.ProvisionCapacityFingerprint(operation);
                    prior.Revision++;
                    prior.UpdatedAt = ControlStore.Now;
                }
                return new AdmissionSnapshot(prior.Revision, prior.IntentJson, prior.WorkspaceId, prior.WorkspaceIdentity,
                    prior.AuthorityRevision, prior.CapacityReservationId, prior.CapacityRevision, prior.CapacityFingerprint);
            });
            return new Attempt(store, admission, operationId, action, snapshot);
        }
        catch
        {
            admission.Release();
            throw;
        }
    }

    private sealed record AdmissionSnapshot(long Revision, string IntentJson, string WorkspaceId, string WorkspaceIdentity,
        long AuthorityRevision, string CapacityReservationId, long? CapacityRevision, string CapacityFingerprint);

    private sealed class Attempt(ControlStore store, SemaphoreSlim admission, string operationId, ProvisionAction action,
        AdmissionSnapshot snapshot) : IProvisionAttempt
    {
        private readonly SemaphoreSlim useGate = new(1, 1);
        private bool disposed;

        public async Task<bool> HasEffect(string effect, string resourceId, CancellationToken token)
        {
            ValidateEffectIdentity(effect, resourceId);
            await useGate.WaitAsync(token);
            try
            {
                ThrowIfDisposed();
                return await store.Read(db => db.ProvisionEffects.AnyAsync(
                    x => x.OperationId == operationId && x.Effect == effect && x.ResourceId == resourceId, token));
            }
            finally
            {
                useGate.Release();
            }
        }

        public async Task<bool> TryBeginEffect(string effect, string resourceId, CancellationToken token)
        {
            ValidateEffect(effect, resourceId);
            await useGate.WaitAsync(token);
            try
            {
                ThrowIfDisposed();
                return await store.Write(async db =>
                {
                    var operation = await db.ProvisionOperations.SingleAsync(x => x.Id == operationId);
                    var attempt = await db.ProvisionAttempts.SingleOrDefaultAsync(x => x.OperationId == operationId)
                        ?? throw new ProvisionAdmissionException("durable_attempt_required");
                    if (attempt.Revision != snapshot.Revision || attempt.IntentJson != snapshot.IntentJson || attempt.WorkspaceId != snapshot.WorkspaceId ||
                        attempt.WorkspaceIdentity != snapshot.WorkspaceIdentity || attempt.AuthorityRevision != snapshot.AuthorityRevision ||
                        attempt.CapacityReservationId != snapshot.CapacityReservationId || attempt.CapacityRevision != snapshot.CapacityRevision ||
                        attempt.CapacityFingerprint != snapshot.CapacityFingerprint)
                        throw new ProvisionAdmissionException("attempt_authority_changed");
                    if (operation.ApprovedIntentJson != snapshot.IntentJson || operation.ApprovedWorkspaceIdentity != snapshot.WorkspaceIdentity ||
                        operation.AuthorityRevision != snapshot.AuthorityRevision)
                        throw new ProvisionAdmissionException("attempt_authority_changed");
                    if (await db.ProvisionEffects.AnyAsync(x => x.OperationId == operationId && x.Effect == effect, token)) return false;
                    if (operation.CancelRequested) throw new OperationCanceledException(token);
                    if (operation.ReconcileRequested || operation.State != ProvisionOperationState.AwaitingExecution ||
                        operation.CapacityReservationId != snapshot.CapacityReservationId || operation.CapacityRevision != snapshot.CapacityRevision ||
                        operation.CapacityValidUntil <= ControlStore.Now || operation.CapacityReservationId.Length == 0 ||
                        ControlStore.ProvisionCapacityFingerprint(operation) != snapshot.CapacityFingerprint)
                        throw new ProvisionAdmissionException("fresh_capacity_reservation_required");
                    db.ProvisionEffects.Add(new() { OperationId = operationId, Effect = effect, ResourceId = resourceId, StartedAt = ControlStore.Now });
                    operation.EffectStarted = true;
                    operation.State = ProvisionOperationState.Unknown;
                    operation.Code = "external_effect_started_result_pending";
                    operation.Revision++;
                    operation.UpdatedAt = ControlStore.Now;
                    return true;
                });
            }
            finally
            {
                useGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await useGate.WaitAsync();
            try
            {
                if (!disposed)
                {
                    disposed = true;
                    admission.Release();
                }
            }
            finally
            {
                useGate.Release();
            }
        }

        private void ValidateEffect(string effect, string resourceId)
        {
            if (effect != "up" || action != ProvisionAction.CreateOrObserve)
                throw new ProvisionAdmissionException("effect_not_authorized_for_action");
            ValidateEffectIdentity(effect, resourceId);
        }

        private void ValidateEffectIdentity(string effect, string resourceId)
        {
            if (effect != "up")
                throw new ProvisionAdmissionException("invalid_effect_identity");
            if (resourceId != snapshot.WorkspaceId || resourceId.Length > 200 || resourceId.Any(char.IsControl))
                throw new ProvisionAdmissionException("invalid_effect_identity");
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(IProvisionAttempt));
        }
    }

    private static string NormalizeOperationId(string id)
    {
        if (!Guid.TryParse(id, out var parsed) || parsed == Guid.Empty) throw new ProvisionIntentConflictException();
        return parsed.ToString("N");
    }
}
