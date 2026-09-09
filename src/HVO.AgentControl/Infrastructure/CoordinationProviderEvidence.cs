using HVO.AgentControl.Core;
using HVO.AgentControl.Services;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    private static async Task<CoordinatorProviderEvidence> CoordinatorProviders(ControlDb db, WorkerRecord[] workers)
    {
        // Catalogs are observed workspace choices, not an allowance or permission to spend.
        // Keep the default plus provider diversity; blocked catalogs must not hide an
        // observed alternative just because its provider sorts after many held models.
        var providers = workers.Select(x => x.ProviderId)
            .Concat(workers.SelectMany(x => Json.Read<List<ModelChoice>>(x.ModelsJson)).Select(x => x.ProviderId))
            .Where(x => !string.IsNullOrEmpty(x)).Distinct().Select(x => "provider:" + x).ToArray();
        var pools = await db.Set<ProviderPool>().AsNoTracking().Where(x => providers.Contains(x.Id))
            .OrderByDescending(x => x.State != "Available" || x.RecoveryOwnershipUnknown || x.RecoveryCommandId != "")
            .ThenBy(x => x.Id).Take(33).ToArrayAsync();
        var poolEvidence = new List<CoordinatorProviderPool>();
        foreach (var pool in pools.Take(32))
        {
            var failures = await db.Set<ProviderFailureReceipt>().AsNoTracking().Where(x => x.PoolId == pool.Id)
                .OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.Id).Take(2).ToArrayAsync();
            var failure = failures.FirstOrDefault();
            var source = failure is null ? null : await db.Commands.Where(x => x.Id == failure.CommandId)
                .Select(x => new { x.WorkerId, x.RuntimeId }).FirstOrDefaultAsync();
            var admission = pool.State == "Available" && !pool.RecoveryOwnershipUnknown && pool.RecoveryCommandId.Length == 0
                ? "Available" : await ProviderRecoveryEligible(db, pool) ? "RecoveryEligible" : "Held";
            poolEvidence.Add(new(pool.Id, pool.State, pool.Revision, pool.RetryAt, pool.RecoveryCommandId, pool.RecoveryOwnershipUnknown,
                admission, failure is null ? null : new(failure.Id, failure.CommandId, failure.Category, failure.Status, failure.ObservedAt,
                    source?.WorkerId, source?.RuntimeId), failures.Length > 1));
        }
        var catalogs = workers.OrderBy(x => x.Id).Select(worker =>
        {
            var models = Json.Read<List<ModelChoice>>(worker.ModelsJson).DistinctBy(x => (x.ProviderId, x.ModelId)).ToArray();
            var groups = models.Where(x => x.ProviderId.Length <= 200 && x.ModelId.Length <= 200)
                .OrderBy(x => x.ModelId, StringComparer.Ordinal).GroupBy(x => x.ProviderId)
                .OrderBy(x => poolEvidence.Any(pool => pool.Id == "provider:" + x.Key && pool.AdmissionState == "Held"))
                .ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
            var selected = models.Where(x => x.ProviderId == worker.ProviderId && x.ModelId == worker.ModelId)
                .Concat(groups.Select(x => x.First())).Concat(groups.SelectMany(x => x.Skip(1)))
                .DistinctBy(x => (x.ProviderId, x.ModelId)).Take(32)
                .Select(x => new CoordinatorModel(x.ProviderId, x.ModelId, (x.Variants ?? []).Order(StringComparer.Ordinal).Take(8).ToArray(), Math.Max(0, (x.Variants?.Length ?? 0) - 8))).ToArray();
            return new CoordinatorModelCatalog(worker.Id, selected, models.Length - selected.Length);
        }).ToArray();
        var readiness = new List<CoordinatorProviderReadiness>();
        foreach (var runtimeId in workers.Select(x => x.RuntimeId).Distinct().Order())
        {
            var instance = await db.Set<ProviderReadinessReceipt>().FindAsync("instance:" + runtimeId);
            readiness.Add(new(runtimeId, "instance", instance?.State ?? "NoRecordedHold", instance?.KeyRevision));
            var go = await db.Set<ProviderReadinessReceipt>().FindAsync(ProviderKeyService.ProviderId + ":" + runtimeId);
            readiness.Add(new(runtimeId, ProviderKeyService.ProviderId, go?.State ?? "NotRecorded", go?.KeyRevision));
        }
        return new(poolEvidence.ToArray(), readiness.ToArray(), catalogs, pools.Length > 32);
    }

    private static async Task RequireCoordinatorProviderRoute(ControlDb db, WorkerRecord worker, string providerId, int actionIndex)
    {
        // Pure admission check: validation must not acquire dispatch recovery leases or
        // modify a circuit while another action in this batch can still be rejected.
        var instance = await db.Set<ProviderReadinessReceipt>().FindAsync("instance:" + worker.RuntimeId);
        if (instance is not null && instance.State != "RefreshCompleted")
            throw new ControlException($"actions[{actionIndex}] cannot send to worker {worker.Id}: runtime instance is {instance.State}. Wait for verified instance refresh; changing models cannot bypass this hold.");
        if (providerId == ProviderKeyService.ProviderId)
        {
            var readiness = await db.Set<ProviderReadinessReceipt>().FindAsync(providerId + ":" + worker.RuntimeId);
            if (readiness?.State != "Ready")
                throw new ControlException($"actions[{actionIndex}].providerId {providerId} has no Ready runtime receipt ({readiness?.State ?? "NotRecorded"}). Choose an owner-approved observed route with verified readiness, or wait for provider setup.");
        }
        var poolId = "provider:" + providerId;
        var pool = await db.Set<ProviderPool>().FindAsync(poolId);
        if (pool is not null && (pool.State != "Available" || pool.RecoveryOwnershipUnknown || pool.RecoveryCommandId.Length > 0) &&
            !await ProviderRecoveryEligible(db, pool))
            throw new ControlException($"actions[{actionIndex}].providerId resolves to held pool {poolId} ({pool.State}; recovery ownership {(pool.RecoveryOwnershipUnknown ? "unknown" : pool.RecoveryCommandId.Length > 0 ? "reserved" : "none")}). Changing only modelId cannot bypass this pool. Supply both providerId and modelId for an owner-approved available different provider from the worker catalog, or wait. Do not replay failed or uncertain work.");
    }
}
