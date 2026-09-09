using System.Text.Json.Serialization;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Services;

public sealed class ProviderCredential
{
    public string Id { get; set; } = "";
    [JsonIgnore] public string SecretReference { get; set; } = "";
    public long Revision { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class ProviderKeyDelivery
{
    public string Id { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public long KeyRevision { get; set; }
    public string State { get; set; } = "Unconfirmed";
    public long UpdatedAt { get; set; }
}

public sealed class ProviderReadinessReceipt
{
    public string Id { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public long KeyRevision { get; set; }
    public string State { get; set; } = "RefreshRequired";
    public string Detail { get; set; } = "Credential delivery has not refreshed native provider state.";
    public long UpdatedAt { get; set; }
    [JsonIgnore] public string PendingDisposalJson { get; set; } = "";
}

internal sealed record ProviderDisposalAttempt(string OperationId, string Directory, RuntimeProcessIdentity Process);

public sealed record SaveProviderKey(string Key, long ExpectedRevision);
public sealed record ApplyProviderKey(long ExpectedRevision);
public sealed record AttestProviderReady(long ExpectedRevision, bool ExternalCanaryObserved);
public sealed record ProviderKeyStatus(string ProviderId, bool Saved, long Revision, long? UpdatedAt,
    List<ProviderKeyDelivery> Deliveries, List<ProviderReadinessReceipt> Readiness);

// Only metadata enters the database or event journal. The existing vault encrypts the key.
public sealed class ProviderKeyService(ControlStore store, Secrets secrets, IRuntimeTransportFactory transports)
{
    public const string ProviderId = "opencode-go";
    internal const string LegacyPendingDisposal = "legacy-unattributed";
    private sealed class RefreshUnverifiedException(string message) : Exception(message);
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task<ProviderKeyStatus> Status() => store.Read(async db =>
    {
        var key = await db.Set<ProviderCredential>().FindAsync(ProviderId);
        return new ProviderKeyStatus(ProviderId, key is not null, key?.Revision ?? 0, key?.UpdatedAt,
            await db.Set<ProviderKeyDelivery>().AsNoTracking().Where(x => x.ProviderId == ProviderId).ToListAsync(),
            await db.Set<ProviderReadinessReceipt>().AsNoTracking().Where(x => x.ProviderId == ProviderId).ToListAsync());
    });

    public async Task<ProviderKeyStatus> Save(SaveProviderKey input)
    {
        if (string.IsNullOrWhiteSpace(input.Key) || input.Key.Length > 8192 || input.Key.Any(char.IsControl))
            throw new ControlException("Enter an API key of at most 8192 characters without control characters.", 400);
        await gate.WaitAsync();
        try
        {
            await store.Write(async db =>
            {
                var key = await db.Set<ProviderCredential>().FindAsync(ProviderId);
                if ((key?.Revision ?? 0) != input.ExpectedRevision) throw new ControlException("The saved key changed. Refresh before replacing it.");
                var reference = secrets.StoreEncrypted(input.Key.Trim());
                if (key is null) { key = new() { Id = ProviderId }; db.Add(key); }
                key.SecretReference = reference; key.Revision++; key.UpdatedAt = ControlStore.Now;
                foreach (var readiness in await db.Set<ProviderReadinessReceipt>().Where(x => x.ProviderId == ProviderId).ToListAsync())
                {
                    readiness.KeyRevision = key.Revision;
                    readiness.State = "RefreshRequired";
                    readiness.Detail = "Saved credentials changed; native provider state must be refreshed.";
                    readiness.UpdatedAt = ControlStore.Now;
                }
                ControlStore.Event(db, "ProviderKeySaved", payload: new { ProviderId, key.Revision }, provenance: "user");
                return true;
            });
            return await Status();
        }
        finally { gate.Release(); }
    }

    public async Task<ProviderKeyStatus> Apply(string runtimeId, ApplyProviderKey input, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            // Install the durable dispatch hold in the same transaction as revision validation.
            // A claim that raced this mutation rechecks this hold before native submission.
            var (runtime, reference, revision) = await store.Write(async db =>
            {
                var runtime = await db.Runtimes.FindAsync(runtimeId) ?? throw new ControlException("Runtime not found.", 404);
                if (runtime.ConnectionKind == RuntimeConnections.ManagedDraft)
                    throw new ControlException("Managed runtime enrollment is pending; provider setup is unavailable until transport ownership is verified.");
                if (!runtime.DesiredConnected || runtime.Health != "Healthy") throw new ControlException("Verify and connect the runtime first.");
                var key = await db.Set<ProviderCredential>().FindAsync(ProviderId) ?? throw new ControlException("Save the OpenCode Go key first.");
                if (key.Revision != input.ExpectedRevision) throw new ControlException("The saved key changed. Refresh before applying it.");
                var deliveryId = ProviderId + ":" + runtimeId;
                var delivery = await db.Set<ProviderKeyDelivery>().FindAsync(deliveryId);
                if (delivery is null) { delivery = new() { Id = deliveryId, RuntimeId = runtimeId, ProviderId = ProviderId }; db.Add(delivery); }
                delivery.KeyRevision = key.Revision; delivery.State = "Unconfirmed"; delivery.UpdatedAt = ControlStore.Now;
                var receipt = await db.Set<ProviderReadinessReceipt>().FindAsync(deliveryId);
                if (receipt is null) { receipt = new() { Id = deliveryId, RuntimeId = runtimeId, ProviderId = ProviderId }; db.Add(receipt); }
                receipt.KeyRevision = key.Revision; receipt.State = "RefreshRequired";
                receipt.Detail = "Credential delivery is pending; native provider state was not refreshed.";
                receipt.UpdatedAt = ControlStore.Now;
                var instance = await db.Set<ProviderReadinessReceipt>().FindAsync("instance:" + runtimeId);
                if (instance is null) { instance = new() { Id = "instance:" + runtimeId, RuntimeId = runtimeId, ProviderId = "instance" }; db.Add(instance); }
                instance.KeyRevision = key.Revision; instance.State = "RefreshRequired";
                instance.Detail = "Credential delivery is pending; affected native instances are not safe to dispatch.";
                instance.UpdatedAt = ControlStore.Now;
                ControlStore.Event(db, "ProviderKeyDeliveryChanged", runtimeId, payload: new { ProviderId, revision = key.Revision, state = delivery.State }, provenance: "user");
                ControlStore.Event(db, "ProviderReadinessChanged", runtimeId, payload: new { ProviderId, revision = key.Revision, state = receipt.State }, provenance: "user");
                return (runtime, key.SecretReference, key.Revision);
            });
            // Persist before sending. A host restart or lost response leaves honest uncertainty.
            var deliveryId = ProviderId + ":" + runtimeId;
            var sent = false;
            var stored = false;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                await using var transport = await transports.Connect(runtime, deadline.Token);
                var key = secrets.Read(reference);
                sent = true;
                var result = await transport.Api.SetProviderKey(ProviderId, key, deadline.Token);
                stored = result.ValueKind == System.Text.Json.JsonValueKind.True;
                await Receipt(result.ValueKind == System.Text.Json.JsonValueKind.True ? "StoredOnRuntime" : "Unconfirmed");
                if (result.ValueKind != System.Text.Json.JsonValueKind.True)
                {
                    await Readiness("Unknown", "Credential delivery was not confirmed; native provider state was not changed.");
                }
                else
                {
                    await Refresh(transport, runtimeId, deadline.Token);
                }
            }
            catch (Exception error)
            {
                // Never expose provider responses, transport exceptions or request bodies here.
                if (!stored) await Receipt(sent ? "Unconfirmed" : "FailedBeforeSend");
                await Readiness(sent ? "Unknown" : "RefreshRequired", error is RefreshUnverifiedException ? error.Message : sent
                    ? "Credential delivery or native refresh outcome is unknown."
                    : "Credential delivery did not start; native provider state was not refreshed.");
                await InstanceReadiness("Unknown", error is RefreshUnverifiedException ? error.Message : "Native refresh completion is unverified; affected instances remain held.");
            }
            return await Status();

            Task<bool> Receipt(string state) => store.Write(async db =>
            {
                var delivery = await db.Set<ProviderKeyDelivery>().FindAsync(deliveryId);
                if (delivery is null) { delivery = new() { Id = deliveryId, RuntimeId = runtimeId, ProviderId = ProviderId }; db.Add(delivery); }
                delivery.KeyRevision = revision; delivery.State = state; delivery.UpdatedAt = ControlStore.Now;
                ControlStore.Event(db, "ProviderKeyDeliveryChanged", runtimeId, payload: new { ProviderId, revision, state }, provenance: "user");
                return true;
            });

            Task<bool> Readiness(string state, string detail) => store.Write(async db =>
            {
                var id = ProviderId + ":" + runtimeId;
                var receipt = await db.Set<ProviderReadinessReceipt>().FindAsync(id);
                if (receipt is null) { receipt = new() { Id = id, RuntimeId = runtimeId, ProviderId = ProviderId }; db.Add(receipt); }
                receipt.KeyRevision = revision; receipt.State = state; receipt.Detail = detail; receipt.UpdatedAt = ControlStore.Now;
                ControlStore.Event(db, "ProviderReadinessChanged", runtimeId, payload: new { ProviderId, revision, state }, provenance: "user");
                return true;
            });

            Task<bool> InstanceReadiness(string state, string detail) => store.Write(async db =>
            {
                var receipt = await db.Set<ProviderReadinessReceipt>().FindAsync("instance:" + runtimeId);
                if (receipt is null) { receipt = new() { Id = "instance:" + runtimeId, RuntimeId = runtimeId, ProviderId = "instance" }; db.Add(receipt); }
                receipt.KeyRevision = revision; receipt.State = state; receipt.Detail = detail; receipt.UpdatedAt = ControlStore.Now;
                ControlStore.Event(db, "ProviderInstanceRefreshChanged", runtimeId, payload: new { revision, state }, provenance: "user");
                return true;
            });

            async Task Refresh(IRuntimeTransport transport, string id, CancellationToken cancellation)
            {
                var api = transport.Api;
                var directories = runtime.ConnectionKind == RuntimeConnections.ControlHttp ? [ControlStore.ControlDirectory] :
                    (await store.Read(async db => await db.Workers.Where(x => x.RuntimeId == id && !x.Archived)
                        .Select(x => x.Directory).Distinct().ToListAsync())).Concat(ControlStore.Roots(runtime))
                        .Append(runtime.StateDirectory).Where(x => x.Length > 0).Distinct().ToList();
                await Readiness("Refreshing", "Waiting for fresh idle evidence before scoped native refresh.");
                await InstanceReadiness("Refreshing", "Waiting for all affected native instances to become idle.");
                var pendingJson = await store.Read(async db => (await db.Set<ProviderReadinessReceipt>().FindAsync("instance:" + id))!.PendingDisposalJson);
                if (pendingJson.Length > 0)
                {
                    if (pendingJson == LegacyPendingDisposal)
                        throw new RefreshUnverifiedException("An earlier refresh has no process attribution. Automatic retry is held until explicit legacy recovery disposition.");
                    var pending = Json.Read<ProviderDisposalAttempt>(pendingJson);
                    var current = await FreshProcess(cancellation);
                    // An old unobserved disposal can still emit a directory-only
                    // completion. It cannot be attributed to another same-process
                    // attempt, including after a host restart or event pruning.
                    if (!pending.Process.MatchesOwner(runtime) || current.SameProcess(pending.Process))
                        throw new RefreshUnverifiedException("A previous disposal is unresolved on this process; a verified replacement is required before another controlled refresh.");
                    await store.Write(async db =>
                    {
                        var receipt = (await db.Set<ProviderReadinessReceipt>().FindAsync("instance:" + id))!;
                        if (receipt.PendingDisposalJson != pendingJson) throw new ControlException("Pending refresh changed.");
                        receipt.PendingDisposalJson = "";
                        ControlStore.Event(db, "ProviderDisposalSupersededByProcessReplacement", id, payload: new { pending, current }, provenance: "observed");
                        return true;
                    });
                }
                var outstanding = await store.Read(db => db.Commands.AnyAsync(x => x.RuntimeId == id && x.Kind == "Prompt" &&
                    (x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)));
                if (outstanding)
                {
                    await Readiness("RefreshRequired", "A managed delivery is outstanding; no instance was disposed.");
                    await InstanceReadiness("RefreshRequired", "A managed delivery is outstanding; no instance was disposed.");
                    return;
                }
                foreach (var directory in directories)
                {
                    if (!await SessionsIdle(directory))
                    {
                        await Readiness("RefreshRequired", "A managed or child native session is active or cannot be observed; no instance was disposed.");
                        await InstanceReadiness("RefreshRequired", "A managed or child native session is active or cannot be observed; no instance was disposed.");
                        return;
                    }
                }
                foreach (var directory in directories)
                {
                    if (!await SessionsIdle(directory))
                    {
                        await Readiness("RefreshRequired", "A managed or child native session became active; no further instance was disposed.");
                        await InstanceReadiness("RefreshRequired", "A managed or child native session became active; no further instance was disposed.");
                        return;
                    }
                    using var completionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    completionDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                    var process = await FreshProcess(completionDeadline.Token);
                    using var completion = await api.Subscribe(completionDeadline.Token);
                    await SameProcess(process, completionDeadline.Token);
                    var attempt = new ProviderDisposalAttempt(Guid.NewGuid().ToString(), directory, process);
                    var attemptJson = Json.Write(attempt);
                    await store.Write(async db =>
                    {
                        var receipt = (await db.Set<ProviderReadinessReceipt>().FindAsync("instance:" + id))!;
                        if (receipt.PendingDisposalJson.Length > 0) throw new ControlException("Another disposal is unresolved.");
                        receipt.PendingDisposalJson = attemptJson;
                        ControlStore.Event(db, "ProviderDisposalStarted", id, payload: attempt, provenance: "controller");
                        return true;
                    });
                    var acknowledgement = await api.DisposeInstance(directory, completionDeadline.Token);
                    if (acknowledgement.ValueKind != System.Text.Json.JsonValueKind.True)
                        throw new ControlException("Native disposal acknowledgement was not confirmed.");
                    await api.WaitForInstanceDisposed(completion, directory, completionDeadline.Token);
                    await SameProcess(process, completionDeadline.Token);
                    await api.Models(directory, completionDeadline.Token);
                    await SameProcess(process, completionDeadline.Token);
                    await store.Write(async db =>
                    {
                        var receipt = (await db.Set<ProviderReadinessReceipt>().FindAsync("instance:" + id))!;
                        if (receipt.PendingDisposalJson != attemptJson) throw new ControlException("Pending refresh changed.");
                        receipt.PendingDisposalJson = "";
                        ControlStore.Event(db, "ProviderDisposalCompleted", id, payload: attempt, provenance: "observed");
                        return true;
                    });
                }
                await Readiness("RefreshCompleted", "Scoped native provider caches were refreshed after fresh idle evidence; model access is not yet tested.");
                await InstanceReadiness("RefreshCompleted", "Affected native caches were refreshed; model access remains untested.");

                async Task<RuntimeProcessIdentity> FreshProcess(CancellationToken token)
                {
                    var started = ControlStore.Now;
                    var observed = await transport.ProbeProcessIdentity(token);
                    var unchanged = await store.Read(async db => await db.Runtimes.AnyAsync(x => x.Id == runtime.Id &&
                        x.Revision == runtime.Revision && x.DesiredConnected && x.ManagedServerId == runtime.ManagedServerId &&
                        x.ConnectionKind == runtime.ConnectionKind, token));
                    if (!unchanged || !transport.Connected || observed is null || !observed.MatchesOwner(runtime) ||
                        observed.ObservedAt < started || observed.ObservedAt > ControlStore.Now)
                        throw new RefreshUnverifiedException("A fresh owned runtime process identity could not be verified; refresh remains held.");
                    return observed;
                }

                async Task SameProcess(RuntimeProcessIdentity expected, CancellationToken token)
                {
                    if (!expected.SameProcess(await FreshProcess(token)))
                        throw new RefreshUnverifiedException("The runtime process changed during controlled refresh; completion remains unverified.");
                }

                async Task<bool> SessionsIdle(string directory)
                {
                    var sessions = await api.Sessions(directory, cancellation);
                    var statuses = await api.SessionStatuses(directory, cancellation);
                    return sessions.ValueKind == System.Text.Json.JsonValueKind.Array && statuses.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        statuses.EnumerateObject().All(status => status.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            status.Value.TryGetProperty("type", out var type) && type.GetString() == "idle");
                }
            }
        }
        finally { gate.Release(); }
    }

    public async Task<ProviderKeyStatus> AttestReady(string runtimeId, AttestProviderReady input)
    {
        if (!input.ExternalCanaryObserved) throw new ControlException("Record external canary evidence before enabling provider dispatch.");
        await gate.WaitAsync();
        try
        {
            await store.Write(async db =>
            {
                var key = await db.Set<ProviderCredential>().FindAsync(ProviderId) ?? throw new ControlException("Save the OpenCode Go key first.");
                if (key.Revision != input.ExpectedRevision) throw new ControlException("The saved key changed. Refresh before recording evidence.");
                var receipt = await db.Set<ProviderReadinessReceipt>().FindAsync(ProviderId + ":" + runtimeId) ?? throw new ControlException("Apply and refresh the saved key before recording evidence.");
                var delivery = await db.Set<ProviderKeyDelivery>().FindAsync(ProviderId + ":" + runtimeId);
                var instance = await db.Set<ProviderReadinessReceipt>().FindAsync("instance:" + runtimeId);
                if (receipt.KeyRevision != key.Revision || receipt.State != "RefreshCompleted" ||
                    delivery?.KeyRevision != key.Revision || delivery.State != "StoredOnRuntime" ||
                    instance?.KeyRevision != key.Revision || instance.State != "RefreshCompleted")
                    throw new ControlException("Apply and complete a refresh of this exact key revision before recording evidence.");
                receipt.State = "Ready"; receipt.Detail = "External canary evidence recorded; model access was not inferred from catalogue data."; receipt.UpdatedAt = ControlStore.Now;
                ControlStore.Event(db, "ProviderReadinessChanged", runtimeId, payload: new { ProviderId, key.Revision, state = receipt.State }, provenance: "user");
                return true;
            });
            return await Status();
        }
        finally { gate.Release(); }
    }
}
