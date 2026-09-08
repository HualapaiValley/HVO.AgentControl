using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<List<HostExecutorEnrollment>> HostExecutors(string? hostId = null) => Read(db => db.HostExecutors.AsNoTracking()
        .Where(x => hostId == null || x.HostId == hostId).OrderBy(x => x.HostId).ThenBy(x => x.Id).ToListAsync());

    public Task<HostExecutorEnrollment> CreateHostExecutor(CreateHostExecutorInput input) => Write(async db =>
    {
        var requestId = ResourceId(input.RequestId);
        var id = ResourceId(input.Id);
        var hostId = ResourceId(input.HostId);
        var endpointId = Digest(input.EndpointId, "endpoint identity");
        var physicalHostId = Digest(input.PhysicalHostId, "physical host identity");
        var engineId = Digest(input.EngineId, "engine identity");
        var builderId = Digest(input.BuilderId, "builder identity");
        var cliBundle = Digest(input.CliBundleDigest, "CLI bundle digest");
        var protectedRoot = Digest(input.ProtectedRootDigest, "protected root digest");
        var authority = Digest(input.AuthorityDigest, "authority digest");
        var credential = Digest(input.CredentialDigest, "credential digest");
        return await MutateHostResource(db, requestId, "CreateExecutor", id,
            new { id, hostId, input.ExpectedHostRevision, endpointId, physicalHostId, engineId, builderId, cliBundle, protectedRoot, authority, credential }, async () =>
            {
                var host = await RequireHost(db, hostId);
                if (host.Archived || host.Revision != input.ExpectedHostRevision)
                    throw new ControlException("Host changed or is archived; refresh before enrolling its executor.");
                if (await db.HostExecutors.AnyAsync(x => x.Id == id || x.EndpointId == endpointId))
                    throw new ControlException("Executor or endpoint identity already exists.");
                var engine = await db.HostExecutors.AsNoTracking().FirstOrDefaultAsync(x => x.EngineId == engineId);
                if (engine is not null && engine.PhysicalHostId != physicalHostId)
                    throw new ControlException("The engine identity is already bound to a different physical host.");
                var record = new HostExecutorEnrollment
                {
                    Id = id,
                    RequestId = requestId,
                    HostId = hostId,
                    EndpointId = endpointId,
                    PhysicalHostId = physicalHostId,
                    EngineId = engineId,
                    BuilderId = builderId,
                    CliBundleDigest = cliBundle,
                    ProtectedRootDigest = protectedRoot,
                    AuthorityDigest = authority,
                    CredentialDigest = credential,
                    State = HostExecutorState.Pending,
                    CreatedAt = Now
                };
                db.HostExecutors.Add(record);
                Event(db, "HostExecutorEnrollmentCreated", payload: new
                {
                    record.Id,
                    record.HostId,
                    record.EndpointId,
                    record.PhysicalHostId,
                    record.EngineId,
                    record.BuilderId,
                    record.AuthorityGeneration
                }, provenance: "user");
                return record;
            });
    });

    public Task<HostExecutorEnrollment> ActivateHostExecutor(HostExecutorPrincipal principal, ActivateHostExecutorInput input) => Write(async db =>
    {
        var executor = await RequireAuthenticatedExecutor(db, principal, allowPending: true);
        if (executor.Id != ResourceId(input.EnrollmentId) || executor.HostId != ResourceId(input.HostId) ||
            executor.AuthorityGeneration != input.AuthorityGeneration || executor.AuthorityDigest != Digest(input.AuthorityDigest, "authority digest"))
            throw new ControlException("Enrollment activation identity does not match authenticated authority.", 403);
        var bootId = Digest(input.BootId, "boot identity");
        var incarnationId = Digest(input.IncarnationId, "process incarnation");
        if (executor.State == HostExecutorState.Active)
        {
            if (executor.BootId == bootId && executor.IncarnationId == incarnationId) return executor;
            throw new ControlException("The one-use enrollment exchange was already consumed; rotate authority for a new incarnation.");
        }
        if (executor.State != HostExecutorState.Pending) throw new ControlException("Executor enrollment is not pending.", 403);
        executor.State = HostExecutorState.Active;
        executor.BootId = bootId;
        executor.IncarnationId = incarnationId;
        executor.ActivatedAt = Now;
        executor.LastSeenAt = Now;
        executor.Revision++;
        Event(db, "HostExecutorActivated", payload: new
        {
            executor.Id,
            executor.HostId,
            executor.EndpointId,
            executor.AuthorityGeneration,
            executor.BootId,
            executor.IncarnationId
        }, provenance: "host-executor");
        return executor;
    });

    public Task<HostExecutorEnrollment> RotateHostExecutor(string id, RotateHostExecutorInput input) => Write(async db =>
    {
        var executorId = ResourceId(id);
        var requestId = ResourceId(input.RequestId);
        var authority = Digest(input.AuthorityDigest, "authority digest");
        var credential = Digest(input.CredentialDigest, "credential digest");
        return await MutateHostResource(db, requestId, "RotateExecutor", executorId,
            new { input.ExpectedRevision, authority, credential }, async () =>
            {
                var executor = await db.HostExecutors.FindAsync(executorId) ?? throw new ControlException("Host executor not found.", 404);
                if (executor.State == HostExecutorState.Revoked || executor.Revision != input.ExpectedRevision)
                    throw new ControlException("Executor changed or is revoked; refresh before rotating.");
                executor.AuthorityGeneration++;
                executor.AuthorityDigest = authority;
                executor.CredentialDigest = credential;
                executor.State = HostExecutorState.Pending;
                executor.BootId = "";
                executor.IncarnationId = "";
                executor.LastSeenAt = null;
                executor.Revision++;
                Event(db, "HostExecutorAuthorityRotated", payload: new { executor.Id, executor.HostId, executor.AuthorityGeneration }, provenance: "user");
                return executor;
            });
    });

    public Task<HostExecutorEnrollment> ChangeHostExecutorState(string id, ChangeHostExecutorStateInput input, string state) => Write(async db =>
    {
        if (state is not (HostExecutorState.Suspended or HostExecutorState.Revoked)) throw new ArgumentOutOfRangeException(nameof(state));
        var executorId = ResourceId(id);
        return await MutateHostResource(db, ResourceId(input.RequestId), state + "Executor", executorId,
            new { input.ExpectedRevision }, async () =>
            {
                var executor = await db.HostExecutors.FindAsync(executorId) ?? throw new ControlException("Host executor not found.", 404);
                if (executor.Revision != input.ExpectedRevision || executor.State == HostExecutorState.Revoked)
                    throw new ControlException("Executor changed or is revoked; refresh before changing authority.");
                executor.State = state;
                executor.Revision++;
                Event(db, "HostExecutor" + state, payload: new { executor.Id, executor.HostId, executor.AuthorityGeneration }, provenance: "user");
                return executor;
            });
    });

    public Task<HostResourcePolicy> ConfigureHostResourcePolicy(ConfigureHostResourcePolicyInput input) => Write(async db =>
    {
        var requestId = ResourceId(input.RequestId);
        var id = ResourceId(input.Id);
        ValidatePolicy(input);
        return await MutateHostResource(db, requestId, "ConfigurePolicy", id,
            new
            {
                input.EnrollmentId,
                input.ExpectedRevision,
                input.MaxCpuMillis,
                input.MaxMemoryBytes,
                input.MaxDiskBytes,
                input.BuildSlots,
                input.ControllerReserveCpuMillis,
                input.ControllerReserveMemoryBytes,
                input.ControllerReserveDiskBytes,
                input.ObservationMaxAgeSeconds,
                input.GrantLifetimeSeconds,
                input.Active
            }, async () =>
            {
                var enrollment = await db.HostExecutors.FindAsync(ResourceId(input.EnrollmentId)) ?? throw new ControlException("Host executor not found.", 404);
                if (enrollment.State != HostExecutorState.Active) throw new ControlException("An active authenticated executor is required before configuring capacity.");
                var latest = await db.HostResourcePolicies.Where(x => x.Id == id).OrderByDescending(x => x.Revision).FirstOrDefaultAsync();
                if ((latest?.Revision ?? 0) != input.ExpectedRevision)
                    throw new ControlException("Resource policy changed; refresh before saving.");
                if (latest is not null && latest.PhysicalHostId != enrollment.PhysicalHostId)
                    throw new ControlException("Policy identity belongs to a different physical host.");
                var physicalLatest = await db.HostResourcePolicies.Where(x => x.PhysicalHostId == enrollment.PhysicalHostId)
                    .OrderByDescending(x => x.Revision).FirstOrDefaultAsync();
                if (physicalLatest is not null && physicalLatest.Id != id)
                    throw new ControlException("The physical host already has a different policy identity.");
                var host = await RequireHost(db, enrollment.HostId);
                if (host.Archived) throw new ControlException("Archived hosts cannot receive resource policy revisions.");
                var policy = new HostResourcePolicy
                {
                    Id = id,
                    Revision = input.ExpectedRevision + 1,
                    RequestId = requestId,
                    EnrollmentId = enrollment.Id,
                    PhysicalHostId = enrollment.PhysicalHostId,
                    State = input.Active ? "Active" : "Disabled",
                    MaxCpuMillis = input.MaxCpuMillis,
                    MaxMemoryBytes = input.MaxMemoryBytes,
                    MaxDiskBytes = input.MaxDiskBytes,
                    BuildSlots = input.BuildSlots,
                    ControllerReserveCpuMillis = input.ControllerReserveCpuMillis,
                    ControllerReserveMemoryBytes = input.ControllerReserveMemoryBytes,
                    ControllerReserveDiskBytes = input.ControllerReserveDiskBytes,
                    ObservationMaxAgeSeconds = input.ObservationMaxAgeSeconds,
                    GrantLifetimeSeconds = input.GrantLifetimeSeconds,
                    CreatedAt = Now
                };
                db.HostResourcePolicies.Add(policy);
                Event(db, "HostResourcePolicyConfigured", payload: new
                {
                    policy.Id,
                    policy.Revision,
                    policy.EnrollmentId,
                    policy.PhysicalHostId,
                    policy.State,
                    policy.MaxCpuMillis,
                    policy.MaxMemoryBytes,
                    policy.MaxDiskBytes,
                    policy.BuildSlots
                }, provenance: "user");
                return policy;
            });
    });

    public Task<HostResourceObservation> SubmitHostResourceObservation(HostExecutorPrincipal principal,
        SubmitHostResourceObservationInput input) => Write(async db =>
    {
        var executor = await RequireAuthenticatedExecutor(db, principal, allowPending: false);
        var observationId = ResourceId(input.Id);
        var evidenceHash = ObservationHash(input);
        if (await db.HostResourceObservations.FindAsync(observationId) is { } prior)
        {
            if (prior.EnrollmentId != executor.Id || prior.EvidenceDigest != evidenceHash)
                throw new ControlException("Observation identity belongs to different immutable evidence.");
            return prior;
        }
        ValidateObservation(input);
        if (executor.Id != ResourceId(input.EnrollmentId) || executor.HostId != ResourceId(input.HostId) ||
            executor.EndpointId != Digest(input.EndpointId, "endpoint identity") || executor.PhysicalHostId != Digest(input.PhysicalHostId, "physical host identity") ||
            executor.EngineId != Digest(input.EngineId, "engine identity") || executor.BuilderId != Digest(input.BuilderId, "builder identity") ||
            executor.AuthorityGeneration != input.AuthorityGeneration || executor.BootId != Digest(input.BootId, "boot identity") ||
            executor.IncarnationId != Digest(input.IncarnationId, "process incarnation"))
            throw new ControlException("Observation identity does not match authenticated executor authority.", 403);
        if (input.Sequence <= executor.LastSequence)
            throw new ControlException("Observation sequence must advance monotonically.");
        var observation = new HostResourceObservation
        {
            Id = observationId,
            EnrollmentId = executor.Id,
            HostId = executor.HostId,
            EndpointId = executor.EndpointId,
            PhysicalHostId = executor.PhysicalHostId,
            EngineId = executor.EngineId,
            BuilderId = executor.BuilderId,
            AuthorityGeneration = executor.AuthorityGeneration,
            BootId = executor.BootId,
            IncarnationId = executor.IncarnationId,
            Sequence = input.Sequence,
            PhysicalRevision = (await db.HostResourceObservations.Where(x => x.PhysicalHostId == executor.PhysicalHostId)
                .MaxAsync(x => (long?)x.PhysicalRevision) ?? 0) + 1,
            SchemaVersion = input.SchemaVersion,
            State = input.State,
            CollectedFrom = input.CollectedFrom,
            CollectedTo = input.CollectedTo,
            ReceivedAt = Now,
            Architecture = BoundedText(input.Architecture, "architecture", 80, true),
            EffectiveCpuMillis = input.EffectiveCpuMillis,
            AvailableCpuMillis = input.AvailableCpuMillis,
            EffectiveMemoryBytes = input.EffectiveMemoryBytes,
            AvailableMemoryBytes = input.AvailableMemoryBytes,
            SwapUsedBytes = input.SwapUsedBytes,
            SwapLimitBytes = input.SwapLimitBytes,
            NativeSessionBytes = input.NativeSessionBytes,
            CompilerPeakBytes = input.CompilerPeakBytes,
            MemoryPressureEvents = input.MemoryPressureEvents,
            WorkspaceId = ResourceId(input.WorkspaceId),
            CanonicalWorkspaceIdentity = Digest(input.CanonicalWorkspaceIdentity, "canonical workspace identity"),
            WorkspaceFilesystemId = Digest(input.WorkspaceFilesystemId, "workspace filesystem identity"),
            WorkspaceAvailableBytes = input.WorkspaceAvailableBytes,
            WorkspaceAvailableInodes = input.WorkspaceAvailableInodes,
            DockerFilesystemId = OptionalDigest(input.DockerFilesystemId, "Docker filesystem identity"),
            DockerAvailableBytes = input.DockerAvailableBytes,
            DockerAvailableInodes = input.DockerAvailableInodes,
            DockerAvailable = input.DockerAvailable,
            BuildSlots = input.BuildSlots,
            ExternalOwnershipState = input.ExternalOwnershipState,
            ExternalOwnershipIntentDigest = OptionalDigest(input.ExternalOwnershipIntentDigest, "external ownership intent"),
            EvidenceDigest = evidenceHash
        };
        db.HostResourceObservations.Add(observation);
        executor.LastSequence = input.Sequence;
        executor.LastSeenAt = observation.ReceivedAt;
        executor.Revision++;
        Event(db, "HostResourceObserved", payload: new
        {
            observation.Id,
            observation.EnrollmentId,
            observation.HostId,
            observation.PhysicalHostId,
            observation.Sequence,
            observation.PhysicalRevision,
            observation.State,
            observation.WorkspaceId,
            observation.CanonicalWorkspaceIdentity,
            observation.ReceivedAt
        }, provenance: "host-executor");
        return observation;
    });

    public Task<HostResourceReservation> AcquireHostResourceReservation(AcquireHostResourceReservationInput input) => Write(async db =>
    {
        var normalized = NormalizeReservation(input);
        return await MutateHostResource(db, normalized.RequestId, "AcquireReservation", normalized.Id, normalized, async () =>
        {
            if (await db.HostResourceReservations.FindAsync(normalized.Id) is not null)
                throw new ControlException("Reservation identity already exists.");
            var executor = await db.HostExecutors.FindAsync(normalized.EnrollmentId) ?? throw new ControlException("Host executor not found.", 404);
            var observation = await db.HostResourceObservations.FindAsync(normalized.ObservationId) ?? throw new ControlException("Resource observation not found.", 404);
            var policy = await db.HostResourcePolicies.SingleOrDefaultAsync(x => x.Id == normalized.PolicyId && x.Revision == normalized.PolicyRevision)
                ?? throw new ControlException("Resource policy revision not found.", 404);
            await RequireAdmissionAuthority(db, normalized, executor, observation, policy);
            var held = await HeldReservations(db, executor.PhysicalHostId);
            if (held.Any(x => x.CanonicalWorkspaceIdentity == observation.CanonicalWorkspaceIdentity))
                throw new ControlException("The authenticated canonical workspace is already reserved.");
            if (normalized.Port is { } port && held.Any(x => x.Port == port)) throw new ControlException("The physical-host port is already reserved.");
            if (normalized.SharedResourceKey is { } key && held.Any(x => x.SharedResourceKey == key))
                throw new ControlException("The physical-host shared resource is already reserved.");
            RequireCapacity(normalized.Kind, normalized.CpuMillis, normalized.MemoryBytes, normalized.DiskBytes,
                normalized.BuildSlots, observation, policy, held);

            var reservation = new HostResourceReservation
            {
                Id = normalized.Id,
                RequestId = normalized.RequestId,
                RequestHash = RequestHash(normalized),
                IntentDigest = normalized.IntentDigest,
                HostId = executor.HostId,
                PhysicalHostId = executor.PhysicalHostId,
                EndpointId = executor.EndpointId,
                EnrollmentId = executor.Id,
                AuthorityGeneration = executor.AuthorityGeneration,
                IncarnationId = executor.IncarnationId,
                PolicyId = policy.Id,
                PolicyRevision = policy.Revision,
                ObservationId = observation.Id,
                WorkspaceId = observation.WorkspaceId,
                CanonicalWorkspaceIdentity = observation.CanonicalWorkspaceIdentity,
                WorkspaceFilesystemId = observation.WorkspaceFilesystemId,
                DockerFilesystemId = observation.DockerFilesystemId,
                OperationId = normalized.OperationId,
                Kind = normalized.Kind,
                CpuMillis = normalized.CpuMillis,
                MemoryBytes = normalized.MemoryBytes,
                DiskBytes = normalized.DiskBytes,
                BuildSlots = normalized.BuildSlots,
                Port = normalized.Port,
                SharedResourceKey = normalized.SharedResourceKey,
                GrantedAt = Now,
                GrantExpiresAt = checked(Now + policy.GrantLifetimeSeconds * 1000L)
            };
            db.HostResourceReservations.Add(reservation);
            Event(db, "HostResourceReserved", payload: new
            {
                reservation.Id,
                reservation.RequestId,
                reservation.IntentDigest,
                reservation.HostId,
                reservation.PhysicalHostId,
                reservation.EndpointId,
                reservation.ObservationId,
                reservation.PolicyId,
                reservation.PolicyRevision,
                reservation.CpuMillis,
                reservation.MemoryBytes,
                reservation.DiskBytes,
                reservation.BuildSlots,
                reservation.Port,
                reservation.SharedResourceKey
            }, provenance: "user");
            return reservation;
        });
    });

    public Task<HostResourceEffectDecision> BeginHostResourceEffect(HostExecutorPrincipal principal, string id,
        BeginHostResourceEffectInput input) => Write(async db =>
    {
        var authenticatedExecutor = await RequireAuthenticatedExecutor(db, principal, allowPending: false);
        var reservation = await db.HostResourceReservations.FindAsync(ResourceId(id)) ?? throw new ControlException("Resource reservation not found.", 404);
        if (reservation.EnrollmentId != authenticatedExecutor.Id || reservation.AuthorityGeneration != authenticatedExecutor.AuthorityGeneration ||
            reservation.IncarnationId != authenticatedExecutor.IncarnationId)
            throw new ControlException("Reservation does not belong to the authenticated executor authority.", 403);
        var intentDigest = Digest(input.IntentDigest, "intent digest");
        var observationId = ResourceId(input.ObservationId);
        if (reservation.State == HostReservationState.EffectCommitted && reservation.IntentDigest == intentDigest &&
            reservation.ObservationId == observationId && reservation.Revision == input.ExpectedRevision + 1)
            return new HostResourceEffectDecision(reservation, false);
        if (reservation.State != HostReservationState.Held || reservation.Revision != input.ExpectedRevision || reservation.IntentDigest != intentDigest)
            throw new ControlException("Reservation effect identity, state or revision changed.");
        if (reservation.GrantExpiresAt < Now) throw new ControlException("Unconsumed reservation grant expired; no effect was authorized.");
        var executor = await db.HostExecutors.FindAsync(reservation.EnrollmentId) ?? throw new ControlException("Host executor not found.", 404);
        var observation = await db.HostResourceObservations.FindAsync(observationId) ?? throw new ControlException("Effect-time observation not found.", 404);
        var policy = await db.HostResourcePolicies.Where(x => x.Id == reservation.PolicyId).OrderByDescending(x => x.Revision).FirstOrDefaultAsync();
        var host = await RequireHost(db, reservation.HostId);
        if (executor.State != HostExecutorState.Active || executor.AuthorityGeneration != reservation.AuthorityGeneration ||
            executor.IncarnationId != reservation.IncarnationId || policy is null || policy.State != "Active" || policy.Revision != reservation.PolicyRevision ||
            host.Archived || !ObservationMatches(reservation, observation) || !Fresh(observation, policy) ||
            observation.PhysicalRevision != await LatestPhysicalRevision(db, reservation.PhysicalHostId) ||
            observation.Sequence != await LatestWorkspaceSequence(db, reservation.EnrollmentId, reservation.WorkspaceId))
            throw new ControlException("Effect-time executor, policy, workspace or freshness authority changed; no effect was authorized.");
        var otherHeld = (await HeldReservations(db, reservation.PhysicalHostId)).Where(x => x.Id != reservation.Id).ToList();
        RequireCapacity(reservation.Kind, reservation.CpuMillis, reservation.MemoryBytes, reservation.DiskBytes,
            reservation.BuildSlots, observation, policy, otherHeld);
        reservation.State = HostReservationState.EffectCommitted;
        reservation.EffectCommittedAt = Now;
        reservation.ObservationId = observation.Id;
        reservation.Revision++;
        Event(db, "HostResourceEffectCommitted", payload: new
        {
            reservation.Id,
            reservation.IntentDigest,
            reservation.ObservationId,
            reservation.AuthorityGeneration,
            reservation.PolicyRevision,
            reservation.Revision
        }, provenance: "host-executor");
        return new HostResourceEffectDecision(reservation, true);
    });

    public Task<HostResourceReservation> MarkHostResourceUnknown(string id, MarkHostResourceUnknownInput input) => Write(async db =>
    {
        var reservationId = ResourceId(id);
        var detail = BoundedText(input.Detail, "uncertainty detail", 500, true);
        return await MutateHostResource(db, ResourceId(input.RequestId), "MarkUnknown", reservationId,
            new { input.ExpectedRevision, input.IntentDigest, detail }, async () =>
            {
                var reservation = await db.HostResourceReservations.FindAsync(reservationId) ?? throw new ControlException("Resource reservation not found.", 404);
                if (reservation.Revision != input.ExpectedRevision || reservation.IntentDigest != Digest(input.IntentDigest, "intent digest") ||
                    reservation.State != HostReservationState.EffectCommitted)
                    throw new ControlException("Reservation cannot enter unknown reconciliation from this state.");
                reservation.State = HostReservationState.Unknown;
                reservation.ReleaseEvidence = detail;
                reservation.Revision++;
                Event(db, "HostResourceOwnershipUnknown", payload: new { reservation.Id, reservation.IntentDigest, reservation.Revision }, provenance: "user");
                return reservation;
            });
    });

    public Task<HostResourceReservation> ReleaseHostResourceReservation(string id, ReleaseHostResourceReservationInput input) => Write(async db =>
    {
        var reservationId = ResourceId(id);
        var evidence = BoundedText(input.Evidence, "release evidence", 500, true);
        return await MutateHostResource(db, ResourceId(input.RequestId), "ReleaseReservation", reservationId,
            new { input.ExpectedRevision, evidence, input.ObservationId }, async () =>
            {
                var reservation = await db.HostResourceReservations.FindAsync(reservationId) ?? throw new ControlException("Resource reservation not found.", 404);
                if (reservation.Revision != input.ExpectedRevision || !HostReservationState.HoldsCapacity(reservation.State))
                    throw new ControlException("Reservation state or revision changed; refresh before release.");
                HostResourceObservation? observation = null;
                if (reservation.State == HostReservationState.Held)
                {
                    if (evidence != "RevokedBeforeEffect" || input.ObservationId is not null)
                        throw new ControlException("An unconsumed grant can only be released as RevokedBeforeEffect without fabricated host evidence.");
                    reservation.State = HostReservationState.Revoked;
                }
                else
                {
                    if (evidence != "ObservedAbsent" || input.ObservationId is null)
                        throw new ControlException("Committed or unknown ownership requires an authenticated ObservedAbsent receipt.");
                    observation = await db.HostResourceObservations.FindAsync(ResourceId(input.ObservationId)) ?? throw new ControlException("Release observation not found.", 404);
                    var effectObservation = await db.HostResourceObservations.FindAsync(reservation.ObservationId)
                        ?? throw new ControlException("Committed effect observation not found.", 404);
                    var latestScopeRevision = await db.HostResourceObservations.Where(x => x.PhysicalHostId == reservation.PhysicalHostId &&
                        x.CanonicalWorkspaceIdentity == reservation.CanonicalWorkspaceIdentity).MaxAsync(x => x.PhysicalRevision);
                    if (!ReleaseObservationMatches(reservation, observation) || observation.ExternalOwnershipState != "Absent" ||
                        observation.ExternalOwnershipIntentDigest != reservation.IntentDigest || reservation.EffectCommittedAt is null ||
                        observation.Id == effectObservation.Id || observation.PhysicalRevision <= effectObservation.PhysicalRevision ||
                        observation.Sequence <= effectObservation.Sequence || observation.ReceivedAt < reservation.EffectCommittedAt.Value ||
                        observation.CollectedFrom < reservation.EffectCommittedAt.Value || observation.PhysicalRevision != latestScopeRevision ||
                        observation.Sequence != await LatestWorkspaceSequence(db, reservation.EnrollmentId, reservation.WorkspaceId))
                        throw new ControlException("Release observation does not prove absence for the exact committed workspace authority.");
                    reservation.State = HostReservationState.Released;
                    reservation.ReleaseObservationId = observation.Id;
                }
                reservation.ReleaseEvidence = evidence;
                reservation.ReleasedAt = Now;
                reservation.Revision++;
                Event(db, "HostResourceReleased", payload: new
                {
                    reservation.Id,
                    reservation.State,
                    reservation.ReleaseEvidence,
                    reservation.ReleaseObservationId,
                    reservation.Revision
                }, provenance: "user");
                return reservation;
            });
    });

    public Task<HostCapacityView> HostCapacity(string hostId) => Read(async db =>
    {
        var host = ResourceId(hostId);
        var executor = await db.HostExecutors.AsNoTracking().Where(x => x.HostId == host).OrderByDescending(x => x.Revision).FirstOrDefaultAsync()
            ?? throw new ControlException("Host executor not found.", 404);
        var policy = await db.HostResourcePolicies.AsNoTracking().Where(x => x.PhysicalHostId == executor.PhysicalHostId).OrderByDescending(x => x.Revision).FirstOrDefaultAsync();
        var observation = await db.HostResourceObservations.AsNoTracking().Where(x => x.EnrollmentId == executor.Id).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync();
        var reservations = await db.HostResourceReservations.AsNoTracking().Where(x => x.PhysicalHostId == executor.PhysicalHostId).OrderBy(x => x.GrantedAt).ToListAsync();
        return new HostCapacityView(executor, policy, observation, reservations);
    });

    public Task<List<HostResourceReservation>> HostResourceReservations(string? physicalHostId = null) => Read(db => db.HostResourceReservations.AsNoTracking()
        .Where(x => physicalHostId == null || x.PhysicalHostId == physicalHostId).OrderByDescending(x => x.GrantedAt).Take(500).ToListAsync());

    private static async Task<HostExecutorEnrollment> RequireAuthenticatedExecutor(ControlDb db, HostExecutorPrincipal principal, bool allowPending)
    {
        var executor = await db.HostExecutors.FindAsync(principal.EnrollmentId) ?? throw new ControlException("Host executor authentication failed.", 401);
        if (executor.HostId != principal.HostId || executor.AuthorityGeneration != principal.AuthorityGeneration ||
            !FixedDigest(executor.CredentialDigest, principal.PresentedCredentialDigest) ||
            executor.State != principal.State || executor.State != HostExecutorState.Active && !(allowPending && executor.State == HostExecutorState.Pending))
            throw new ControlException("Host executor authentication is stale or revoked.", 401);
        return executor;
    }

    private static async Task RequireAdmissionAuthority(ControlDb db, AcquireHostResourceReservationInput input,
        HostExecutorEnrollment executor, HostResourceObservation observation, HostResourcePolicy policy)
    {
        if (executor.State != HostExecutorState.Active || executor.HostId != input.HostId || executor.Id != input.EnrollmentId ||
            observation.EnrollmentId != executor.Id || observation.AuthorityGeneration != executor.AuthorityGeneration ||
            observation.IncarnationId != executor.IncarnationId || observation.WorkspaceId != input.WorkspaceId ||
            policy.PhysicalHostId != executor.PhysicalHostId || policy.State != "Active")
            throw new ControlException("Reservation authority does not match the active executor, host, workspace and physical budget.");
        var host = await RequireHost(db, executor.HostId);
        if (host.Archived) throw new ControlException("Archived hosts cannot receive resource reservations.");
        var latestPolicy = await db.HostResourcePolicies.Where(x => x.Id == policy.Id).MaxAsync(x => x.Revision);
        if (latestPolicy != policy.Revision) throw new ControlException("Resource policy revision is no longer current.");
        var latestWorkspaceSequence = await LatestWorkspaceSequence(db, executor.Id, input.WorkspaceId);
        if (latestWorkspaceSequence != observation.Sequence || observation.PhysicalRevision != await LatestPhysicalRevision(db, executor.PhysicalHostId) ||
            !Fresh(observation, policy))
            throw new ControlException("Resource observation is stale or superseded.");
        if (observation.State != HostObservationState.Complete || observation.EffectiveCpuMillis is null || observation.AvailableCpuMillis is null ||
            observation.EffectiveMemoryBytes is null || observation.AvailableMemoryBytes is null || observation.WorkspaceAvailableBytes is null ||
            observation.WorkspaceAvailableInodes is null || observation.BuildSlots is null)
            throw new ControlException("Complete authenticated CPU, memory, filesystem and build-lane evidence is required.");
        if (input.CpuMillis > observation.EffectiveCpuMillis || input.MemoryBytes > observation.EffectiveMemoryBytes || input.BuildSlots > observation.BuildSlots)
            throw new ControlException("Requested resources exceed the executor-observed effective scope.");
    }

    private static bool ObservationMatches(HostResourceReservation reservation, HostResourceObservation observation) =>
        observation.EnrollmentId == reservation.EnrollmentId && observation.HostId == reservation.HostId &&
        observation.PhysicalHostId == reservation.PhysicalHostId && observation.EndpointId == reservation.EndpointId &&
        observation.AuthorityGeneration == reservation.AuthorityGeneration && observation.IncarnationId == reservation.IncarnationId &&
        observation.WorkspaceId == reservation.WorkspaceId && observation.CanonicalWorkspaceIdentity == reservation.CanonicalWorkspaceIdentity &&
        observation.WorkspaceFilesystemId == reservation.WorkspaceFilesystemId &&
        (reservation.Kind != "Build" || observation.DockerFilesystemId == reservation.DockerFilesystemId) &&
        observation.State == HostObservationState.Complete;

    private static bool ReleaseObservationMatches(HostResourceReservation reservation, HostResourceObservation observation) =>
        observation.EnrollmentId == reservation.EnrollmentId && observation.HostId == reservation.HostId &&
        observation.PhysicalHostId == reservation.PhysicalHostId && observation.EndpointId == reservation.EndpointId &&
        observation.AuthorityGeneration >= reservation.AuthorityGeneration && observation.WorkspaceId == reservation.WorkspaceId &&
        observation.CanonicalWorkspaceIdentity == reservation.CanonicalWorkspaceIdentity &&
        observation.WorkspaceFilesystemId == reservation.WorkspaceFilesystemId &&
        (reservation.Kind != "Build" || observation.DockerFilesystemId == reservation.DockerFilesystemId) &&
        observation.State == HostObservationState.Complete;

    private static bool Fresh(HostResourceObservation observation, HostResourcePolicy policy) =>
        observation.ReceivedAt <= Now && Now - observation.ReceivedAt <= policy.ObservationMaxAgeSeconds * 1000L &&
        observation.CollectedTo <= Now + 30_000 && Now - observation.CollectedTo <= policy.ObservationMaxAgeSeconds * 1000L;

    private static AcquireHostResourceReservationInput NormalizeReservation(AcquireHostResourceReservationInput input)
    {
        if (input.Kind is not ("Build" or "Runtime") || input.PolicyRevision <= 0 || input.CpuMillis <= 0 || input.MemoryBytes <= 0 || input.DiskBytes < 0 ||
            input.BuildSlots < 0 || input.Port is < 1024 or > 65535 || input.Kind == "Build" && input.BuildSlots < 1)
            throw new ControlException("Invalid host resource reservation request.", 400);
        return input with
        {
            RequestId = ResourceId(input.RequestId),
            Id = ResourceId(input.Id),
            IntentDigest = Digest(input.IntentDigest, "intent digest"),
            HostId = ResourceId(input.HostId),
            EnrollmentId = ResourceId(input.EnrollmentId),
            PolicyId = ResourceId(input.PolicyId),
            ObservationId = ResourceId(input.ObservationId),
            WorkspaceId = ResourceId(input.WorkspaceId),
            OperationId = ResourceId(input.OperationId),
            SharedResourceKey = input.SharedResourceKey is null ? null : BoundedText(input.SharedResourceKey, "shared resource key", 120, true)
        };
    }

    private static void ValidatePolicy(ConfigureHostResourcePolicyInput input)
    {
        if (input.ExpectedRevision < 0 || input.MaxCpuMillis <= 0 || input.MaxMemoryBytes <= 0 || input.MaxDiskBytes < 0 ||
            input.BuildSlots < 1 || input.ControllerReserveCpuMillis < 0 || input.ControllerReserveMemoryBytes < 0 || input.ControllerReserveDiskBytes < 0 ||
            input.ControllerReserveCpuMillis >= input.MaxCpuMillis || input.ControllerReserveMemoryBytes >= input.MaxMemoryBytes ||
            input.ControllerReserveDiskBytes > input.MaxDiskBytes || input.ObservationMaxAgeSeconds is < 10 or > 3600 ||
            input.GrantLifetimeSeconds is < 10 or > 3600)
            throw new ControlException("Invalid host resource policy limits.", 400);
    }

    private static void ValidateObservation(SubmitHostResourceObservationInput input)
    {
        var now = Now;
        if (input.SchemaVersion != 1 || input.Sequence <= 0 || input.State is not (HostObservationState.Complete or HostObservationState.Partial or HostObservationState.Unsupported) ||
            input.CollectedFrom <= 0 || input.CollectedTo < input.CollectedFrom || input.CollectedTo > now + 30_000 || input.CollectedTo - input.CollectedFrom > 1_800_000 ||
            input.ExternalOwnershipState is not ("Unknown" or "Present" or "Absent") || input.BuildSlots is < 0 or > 64)
            throw new ControlException("Invalid host resource observation envelope.", 400);
        foreach (var value in new long?[] { input.EffectiveCpuMillis, input.AvailableCpuMillis, input.EffectiveMemoryBytes, input.AvailableMemoryBytes,
                     input.SwapUsedBytes, input.SwapLimitBytes, input.NativeSessionBytes, input.CompilerPeakBytes, input.MemoryPressureEvents,
                     input.WorkspaceAvailableBytes, input.WorkspaceAvailableInodes, input.DockerAvailableBytes, input.DockerAvailableInodes })
            if (value < 0) throw new ControlException("Resource observation values cannot be negative.", 400);
        if (input.State == HostObservationState.Complete && (input.EffectiveCpuMillis is null || input.AvailableCpuMillis is null ||
            input.EffectiveMemoryBytes is null || input.AvailableMemoryBytes is null || input.WorkspaceAvailableBytes is null ||
            input.WorkspaceAvailableInodes is null || input.BuildSlots is null || input.DockerAvailable is null))
            throw new ControlException("Complete observations cannot omit required capacity evidence.", 400);
        if (input.DockerAvailable == true && (input.DockerFilesystemId is null || input.DockerAvailableBytes is null || input.DockerAvailableInodes is null))
            throw new ControlException("Available Docker evidence requires filesystem capacity and identity.", 400);
        if (input.ExternalOwnershipState == "Unknown" && input.ExternalOwnershipIntentDigest is not null ||
            input.ExternalOwnershipState != "Unknown" && input.ExternalOwnershipIntentDigest is null)
            throw new ControlException("External ownership evidence must identify the exact intent, or remain unknown.", 400);
    }

    private static string ObservationHash(SubmitHostResourceObservationInput input) => RequestHash(input with
    {
        Id = ResourceId(input.Id),
        EnrollmentId = ResourceId(input.EnrollmentId),
        HostId = ResourceId(input.HostId),
        EndpointId = Digest(input.EndpointId, "endpoint identity"),
        PhysicalHostId = Digest(input.PhysicalHostId, "physical host identity"),
        EngineId = Digest(input.EngineId, "engine identity"),
        BuilderId = Digest(input.BuilderId, "builder identity"),
        BootId = Digest(input.BootId, "boot identity"),
        IncarnationId = Digest(input.IncarnationId, "process incarnation"),
        Architecture = BoundedText(input.Architecture, "architecture", 80, true),
        WorkspaceId = ResourceId(input.WorkspaceId),
        CanonicalWorkspaceIdentity = Digest(input.CanonicalWorkspaceIdentity, "canonical workspace identity"),
        WorkspaceFilesystemId = Digest(input.WorkspaceFilesystemId, "workspace filesystem identity"),
        DockerFilesystemId = OptionalDigest(input.DockerFilesystemId, "Docker filesystem identity"),
        ExternalOwnershipIntentDigest = OptionalDigest(input.ExternalOwnershipIntentDigest, "external ownership intent")
    });

    private static Task<List<HostResourceReservation>> HeldReservations(ControlDb db, string physicalHostId) =>
        db.HostResourceReservations.Where(x => x.PhysicalHostId == physicalHostId &&
            (x.State == HostReservationState.Held || x.State == HostReservationState.EffectCommitted || x.State == HostReservationState.Unknown)).ToListAsync();

    private static Task<long> LatestPhysicalRevision(ControlDb db, string physicalHostId) =>
        db.HostResourceObservations.Where(x => x.PhysicalHostId == physicalHostId).MaxAsync(x => x.PhysicalRevision);

    private static Task<long> LatestWorkspaceSequence(ControlDb db, string enrollmentId, string workspaceId) =>
        db.HostResourceObservations.Where(x => x.EnrollmentId == enrollmentId && x.WorkspaceId == workspaceId).MaxAsync(x => x.Sequence);

    private static void RequireCapacity(string kind, long cpuMillis, long memoryBytes, long diskBytes, int buildSlots,
        HostResourceObservation observation, HostResourcePolicy policy, List<HostResourceReservation> held)
    {
        RequireWithin("configured CPU", cpuMillis, held.Sum(x => x.CpuMillis), policy.MaxCpuMillis);
        RequireWithin("configured memory", memoryBytes, held.Sum(x => x.MemoryBytes), policy.MaxMemoryBytes);
        RequireWithin("configured disk", diskBytes, held.Sum(x => x.DiskBytes), policy.MaxDiskBytes);
        RequireWithin("build lane", buildSlots, held.Sum(x => x.BuildSlots), policy.BuildSlots);
        RequireWithin("fresh physical CPU headroom", cpuMillis, held.Sum(x => x.CpuMillis),
            Required(observation.AvailableCpuMillis, "available CPU") - policy.ControllerReserveCpuMillis);
        RequireWithin("fresh physical memory headroom", memoryBytes, held.Sum(x => x.MemoryBytes),
            Required(observation.AvailableMemoryBytes, "available memory") - policy.ControllerReserveMemoryBytes);
        var sameFilesystem = held.Where(x => x.WorkspaceFilesystemId == observation.WorkspaceFilesystemId).Sum(x => x.DiskBytes);
        RequireWithin("fresh workspace disk headroom", diskBytes, sameFilesystem,
            Required(observation.WorkspaceAvailableBytes, "workspace disk") - policy.ControllerReserveDiskBytes);
        if (kind == "Build")
        {
            var dockerReserved = held.Where(x => x.DockerFilesystemId == observation.DockerFilesystemId).Sum(x => x.DiskBytes);
            if (observation.DockerAvailable != true || observation.DockerFilesystemId is null)
                throw new ControlException("Fresh Docker build capacity evidence is unavailable.");
            RequireWithin("fresh Docker disk headroom", diskBytes, dockerReserved,
                Required(observation.DockerAvailableBytes, "Docker disk") - policy.ControllerReserveDiskBytes);
        }
    }

    private static async Task<T> MutateHostResource<T>(ControlDb db, string requestId, string action, string resourceId,
        object request, Func<Task<T>> mutate)
    {
        var hash = RequestHash(request);
        if (await db.HostResourceMutations.FindAsync(requestId) is { } prior)
        {
            if (prior.Action != action || prior.ResourceId != resourceId || prior.RequestHash != hash)
                throw new ControlException("Host-resource request ID belongs to a different mutation.");
            return Json.Read<T>(prior.ResultJson);
        }
        var result = await mutate();
        await db.SaveChangesAsync();
        db.HostResourceMutations.Add(new HostResourceMutationReceipt
        {
            RequestId = requestId,
            Action = action,
            ResourceId = resourceId,
            RequestHash = hash,
            ResultJson = Json.Write(result),
            CreatedAt = Now
        });
        return result;
    }

    private static long Required(long? value, string name) => value ?? throw new ControlException("Authenticated " + name + " evidence is unavailable.");
    private static void RequireWithin(string name, long requested, long reserved, long available)
    {
        if (available < 0 || requested > available || reserved > available - requested)
            throw new ControlException("Insufficient " + name + ".");
    }

    private static void RequireWithin(string name, int requested, int reserved, int available)
    {
        if (available < 0 || requested > available || reserved > available - requested)
            throw new ControlException("Insufficient " + name + ".");
    }

    private static string ResourceId(string? value)
    {
        if (value is null || !Guid.TryParse(value, out var id) || id == Guid.Empty) throw new ControlException("A non-empty UUID identity is required.", 400);
        return id.ToString("N");
    }

    private static string Digest(string? value, string name)
    {
        if (value is null || !Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.IgnoreCase))
            throw new ControlException("Invalid " + name + "; use a SHA-256 hexadecimal digest.", 400);
        return value.ToUpperInvariant();
    }

    private static string? OptionalDigest(string? value, string name) => value is null ? null : Digest(value, name);

    private static string BoundedText(string? value, string name, int maximum, bool required)
    {
        if (value is null || value.Length > maximum || value.Any(char.IsControl) || required && string.IsNullOrWhiteSpace(value))
            throw new ControlException("Invalid " + name + ".", 400);
        return value.Trim();
    }

    private static string RequestHash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(value))));
    private static bool FixedDigest(string expected, string presented)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(presented)); }
        catch (FormatException) { return false; }
    }
}
