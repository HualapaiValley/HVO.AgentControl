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

public sealed record SaveProviderKey(string Key, long ExpectedRevision);
public sealed record ApplyProviderKey(long ExpectedRevision);
public sealed record ProviderKeyStatus(string ProviderId, bool Saved, long Revision, long? UpdatedAt,
    List<ProviderKeyDelivery> Deliveries);

// Only metadata enters the database or event journal. The existing vault encrypts the key.
public sealed class ProviderKeyService(ControlStore store, Secrets secrets, IRuntimeTransportFactory transports)
{
    public const string ProviderId = "opencode-go";
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task<ProviderKeyStatus> Status() => store.Read(async db =>
    {
        var key = await db.Set<ProviderCredential>().FindAsync(ProviderId);
        return new ProviderKeyStatus(ProviderId, key is not null, key?.Revision ?? 0, key?.UpdatedAt,
            await db.Set<ProviderKeyDelivery>().AsNoTracking().Where(x => x.ProviderId == ProviderId).ToListAsync());
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
            var (runtime, reference, revision) = await store.Read(async db =>
            {
                var runtime = await db.Runtimes.FindAsync(runtimeId) ?? throw new ControlException("Runtime not found.", 404);
                if (!runtime.DesiredConnected || runtime.Health != "Healthy") throw new ControlException("Verify and connect the runtime first.");
                var key = await db.Set<ProviderCredential>().FindAsync(ProviderId) ?? throw new ControlException("Save the OpenCode Go key first.");
                if (key.Revision != input.ExpectedRevision) throw new ControlException("The saved key changed. Refresh before applying it.");
                return (runtime, key.SecretReference, key.Revision);
            });
            // Persist before sending. A host restart or lost response leaves honest uncertainty.
            var deliveryId = ProviderId + ":" + runtimeId;
            await Receipt("Unconfirmed");
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
            }
            catch (Exception)
            {
                // Never expose provider responses, transport exceptions or request bodies here.
                await Receipt(sent ? "Unconfirmed" : "FailedBeforeSend");
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
        }
        finally { gate.Release(); }
    }
}
