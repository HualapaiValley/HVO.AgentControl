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
}

public sealed record SaveProviderKey(string Key, long ExpectedRevision);
public sealed record ApplyProviderKey(long ExpectedRevision);
public sealed record ProviderKeyStatus(string ProviderId, bool Saved, long Revision, long? UpdatedAt,
    List<ProviderKeyDelivery> Deliveries, List<ProviderReadinessReceipt> Readiness);

// Only metadata enters the database or event journal. The existing vault encrypts the key.
public sealed class ProviderKeyService(ControlStore store, Secrets secrets, IRuntimeTransportFactory transports)
{
    public const string ProviderId = "opencode-go";
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
                ControlStore.Event(db, "ProviderKeyDeliveryChanged", runtimeId, payload: new { ProviderId, revision = key.Revision, state = delivery.State }, provenance: "user");
                ControlStore.Event(db, "ProviderReadinessChanged", runtimeId, payload: new { ProviderId, revision = key.Revision, state = receipt.State }, provenance: "user");
                return (runtime, key.SecretReference, key.Revision);
            });
            // Persist before sending. A host restart or lost response leaves honest uncertainty.
            var deliveryId = ProviderId + ":" + runtimeId;
            var sent = false;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                await using var transport = await transports.Connect(runtime, deadline.Token);
                var key = secrets.Read(reference);
                sent = true;
                var result = await transport.Api.SetProviderKey(ProviderId, key, deadline.Token);
                await Receipt(result.ValueKind == System.Text.Json.JsonValueKind.True ? "StoredOnRuntime" : "Unconfirmed");
                if (result.ValueKind != System.Text.Json.JsonValueKind.True)
                {
                    await Readiness("Unknown", "Credential delivery was not confirmed; native provider state was not changed.");
                }
                else
                {
                    await Refresh(transport.Api, runtimeId, deadline.Token);
                }
            }
            catch (Exception)
            {
                // Never expose provider responses, transport exceptions or request bodies here.
                await Receipt(sent ? "Unconfirmed" : "FailedBeforeSend");
                await Readiness(sent ? "Unknown" : "RefreshRequired", sent
                    ? "Credential delivery or native refresh outcome is unknown."
                    : "Credential delivery did not start; native provider state was not refreshed.");
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

            async Task Refresh(OpenCode.OpenCodeClient api, string id, CancellationToken cancellation)
            {
                var directories = await store.Read(async db => await db.Workers.Where(x => x.RuntimeId == id && !x.Archived)
                    .Select(x => x.Directory).Distinct().ToListAsync());
                if (directories.Count == 0)
                {
                    await Readiness("RefreshCompleted", "No managed workspace has cached provider state; model access is not yet tested.");
                    return;
                }
                await Readiness("Refreshing", "Waiting for fresh idle evidence before scoped native refresh.");
                var outstanding = await store.Read(db => db.Commands.AnyAsync(x => x.RuntimeId == id && x.Kind == "Prompt" &&
                    (x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)));
                if (outstanding)
                {
                    await Readiness("RefreshRequired", "A managed delivery is outstanding; no instance was disposed.");
                    return;
                }
                foreach (var directory in directories)
                {
                    if (!await SessionsIdle(directory))
                    {
                        await Readiness("RefreshRequired", "A managed or child native session is active or cannot be observed; no instance was disposed.");
                        return;
                    }
                }
                foreach (var directory in directories)
                {
                    if (!await SessionsIdle(directory))
                    {
                        await Readiness("RefreshRequired", "A managed or child native session became active; no further instance was disposed.");
                        return;
                    }
                    await api.DisposeInstance(directory, cancellation);
                    await api.Models(directory, cancellation);
                }
                await Readiness("RefreshCompleted", "Scoped native provider caches were refreshed after fresh idle evidence; model access is not yet tested.");

                async Task<bool> SessionsIdle(string directory)
                {
                    var sessions = await api.Sessions(directory, cancellation);
                    var statuses = await api.SessionStatuses(directory, cancellation);
                    return sessions.ValueKind == System.Text.Json.JsonValueKind.Array && statuses.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        sessions.EnumerateArray().All(session => session.TryGetProperty("id", out var sessionId) && sessionId.ValueKind == System.Text.Json.JsonValueKind.String &&
                            (!statuses.TryGetProperty(sessionId.GetString()!, out var status) || status.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                status.TryGetProperty("type", out var type) && type.GetString() == "idle"));
                }
            }
        }
        finally { gate.Release(); }
    }
}
