using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
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
            CanonicalProjectRepository(intent.Workspace.Repository) != project.RepositoryUrl)
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
    });

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
    });

    internal Task<ProvisionOperationView> RecordProvisionResult(string id, HostProvisionResult result) => Write(async db =>
    {
        var operation = await RequireProvisionOperation(db, id);
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
        if (!operation.EffectStarted && result.State is ("Failed" or "Unsupported") && await db.ProvisionAttempts.FindAsync(operation.Id) is { } unused)
            db.ProvisionAttempts.Remove(unused);
        operation.Revision++;
        operation.UpdatedAt = Now;
        return await ProvisionView(db, operation);
    });

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

    internal static bool SameProvisionIntent(ProvisionIntent left, ProvisionIntent right) =>
        left.OperationId == right.OperationId && left.HostId == right.HostId && left.AuthorityRevision == right.AuthorityRevision &&
        SameProvisionWorkspace(left.Workspace, right.Workspace) && left.CliVersion == right.CliVersion && left.CliSha256 == right.CliSha256 &&
        left.ColdBuild == right.ColdBuild && left.Digest == right.Digest && left.Labels.Count == right.Labels.Count &&
        left.Labels.All(x => right.Labels.TryGetValue(x.Key, out var value) && value == x.Value);

    private static bool SameProvisionWorkspace(ApprovedProvisionWorkspace left, ApprovedProvisionWorkspace right) =>
        left.Id == right.Id && left.Directory == right.Directory && left.Repository == right.Repository &&
        left.SourceRevision == right.SourceRevision && left.ConfigurationPath == right.ConfigurationPath &&
        left.ConfigurationSha256 == right.ConfigurationSha256 && left.RemoteUser == right.RemoteUser &&
        left.ContainerWorkspace == right.ContainerWorkspace && left.Tools.Length == right.Tools.Length &&
        left.Tools.Zip(right.Tools).All(x => x.First.Name == x.Second.Name && x.First.ExpectedOutput == x.Second.ExpectedOutput &&
            x.First.Command.SequenceEqual(x.Second.Command));

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
