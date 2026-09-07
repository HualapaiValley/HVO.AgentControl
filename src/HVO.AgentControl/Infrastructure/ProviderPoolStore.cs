using System.Text.Json;
using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed class ProviderPool
{
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string State { get; set; } = "Available";
    public long? RetryAt { get; set; }
    public long ObservedAt { get; set; }
    public long Revision { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string LastCommandId { get; set; } = "";
}

public sealed class ProviderFailureReceipt
{
    public string Id { get; set; } = "";
    public string PoolId { get; set; } = "";
    public string CommandId { get; set; } = "";
    public string Category { get; set; } = "";
    public int? Status { get; set; }
    public long ObservedAt { get; set; }
    public long? RetryAt { get; set; }
}

public sealed record ResumeProviderPool(long ExpectedRevision, bool RecoveryVerified);
public sealed record ProviderFailure(string Category, int? Status, long? RetryAt)
{
    // Only native assistant provider errors qualify. OpenCode HTTP authentication and
    // transport failures are not evidence about the upstream subscription.
    public static ProviderFailure? Parse(JsonElement error, long now)
    {
        if (error.ValueKind != JsonValueKind.Object) return null;
        var name = Text(error, "name");
        if (name is "ProviderAuthError") return new("AuthenticationRequired", null, null);
        if (name != "APIError" || !error.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        int? status = data.TryGetProperty("statusCode", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var code) ? code : null;
        var providerCode = Text(data, "code");
        // Inspect bounded structured error codes, never infer exhaustion from prose.
        var body = Text(data, "responseBody");
        if (providerCode.Length == 0 && body.Length is > 0 and <= 16384)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
                    providerCode = Text(nested, "code");
            }
            catch (JsonException) { }
        }
        if (providerCode is "insufficient_quota" or "quota_exceeded" or "billing_hard_limit_reached" or "usage_limit_reached")
            return new("Exhausted", status, null);
        if (status is 401 or 403) return new("AuthenticationRequired", status, null);
        if (status != 429 && status is not (>= 500 and <= 599)) return null;
        long? retryAt = null;
        if (data.TryGetProperty("responseHeaders", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headers.EnumerateObject())
            {
                if (!header.Name.Equals("retry-after", StringComparison.OrdinalIgnoreCase) || header.Value.ValueKind != JsonValueKind.String) continue;
                var text = header.Value.GetString();
                if (long.TryParse(text, out var seconds) && seconds >= 0 && seconds <= (DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() - now) / 1000)
                    retryAt = now + seconds * 1000;
                else if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
                    retryAt = Math.Max(now, date.ToUnixTimeMilliseconds());
            }
        }
        return new(status == 429 ? "Throttled" : "Unavailable", status, retryAt);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}

public sealed partial class ControlStore
{
    public Task<List<ProviderPool>> ProviderPools() => Read(db => db.Set<ProviderPool>().AsNoTracking().OrderBy(x => x.Id).ToListAsync());

    internal static string PoolId(WorkerRecord worker, CommandRecord command)
    {
        var prompt = Json.Read<PromptInput>(string.IsNullOrEmpty(command.ExecutionPayload) ? command.Payload : command.ExecutionPayload);
        // Conservative safety group, not a claim that credentials are identical. Go,
        // Zen and ChatGPT routes remain separate. Explicit account bindings follow later.
        return "provider:" + (prompt.ProviderId ?? worker.ProviderId);
    }

    internal static async Task<bool> ProviderDispatchAllowed(ControlDb db, WorkerRecord worker, CommandRecord command)
    {
        var poolId = command.ProviderPoolId.Length > 0 ? command.ProviderPoolId : PoolId(worker, command);
        var pool = await db.Set<ProviderPool>().FindAsync(poolId);
        if (pool is null) return true;
        if (pool.State == "Available") return true;
        if (pool.State is "Throttled" or "Unavailable" && pool.RetryAt <= Now && pool.ConsecutiveFailures < 3)
        {
            // Admit one managed attempt after cooldown. Other runtimes wait until its
            // terminal evidence arrives; a restart never releases this lease silently.
            if (pool.LastCommandId.Length > 0 && await db.Commands.AnyAsync(x => x.Id == pool.LastCommandId &&
                (x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)))
                return false;
            pool.State = "Recovering"; pool.LastCommandId = command.Id; pool.Revision++;
            Event(db, "ProviderRecoveryAttemptAdmitted", worker.RuntimeId, worker.Id, command.Id, new { poolId });
            return true;
        }
        if (pool.State == "Recovering" && pool.LastCommandId == command.Id) return true;
        command.Detail = $"Waiting for model access: {pool.State} ({poolId}). " +
            (pool.RetryAt is { } retry ? $"Retry no earlier than {DateTimeOffset.FromUnixTimeMilliseconds(retry):u}. " : "") +
            "Remaining allowance unknown. Failed work is not replayed.";
        return false;
    }

    internal static async Task ObserveProviderFailure(ControlDb db, WorkerRecord worker, CommandRecord command, string nativeId, ProviderFailure failure)
    {
        var receiptId = command.Id + ":" + nativeId;
        if (await db.Set<ProviderFailureReceipt>().FindAsync(receiptId) is not null) return;
        var poolId = command.ProviderPoolId.Length > 0 ? command.ProviderPoolId : PoolId(worker, command);
        var pool = await db.Set<ProviderPool>().FindAsync(poolId);
        if (pool is null)
        {
            pool = new() { Id = poolId, ProviderId = poolId["provider:".Length..] };
            db.Add(pool);
        }
        var observed = Now;
        pool.ConsecutiveFailures++;
        // Exhaustion/authentication remain manual holds even if another in-flight
        // request reports a weaker transient failure or later succeeds.
        if (pool.State is not ("Exhausted" or "AuthenticationRequired"))
            pool.State = failure.Category;
        var jitter = Random.Shared.Next(1000, 5001);
        var retryAt = failure.Category is "Throttled" or "Unavailable"
            ? Math.Min(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(), Math.Max(failure.RetryAt ?? observed + 60000, observed + 1000) + jitter) : (long?)null;
        pool.RetryAt = pool.State is "Exhausted" or "AuthenticationRequired" ? null : Math.Max(pool.RetryAt ?? 0, retryAt ?? 0);
        if (pool.ConsecutiveFailures >= 3 && pool.State is "Throttled" or "Unavailable") { pool.State = "RecoveryRequired"; pool.RetryAt = null; }
        pool.ObservedAt = observed; pool.Revision++; pool.LastCommandId = command.Id;
        db.Add(new ProviderFailureReceipt { Id = receiptId, PoolId = poolId, CommandId = command.Id, Category = failure.Category, Status = failure.Status, ObservedAt = observed, RetryAt = retryAt });
        Event(db, "ProviderPoolBlocked", worker.RuntimeId, worker.Id, command.Id, new { poolId, pool.State, failure.Status, pool.RetryAt });
    }

    internal static async Task ObserveProviderCompletion(ControlDb db, CommandRecord command, bool successful)
    {
        if (command.ProviderPoolId.Length == 0) return;
        var pool = await db.Set<ProviderPool>().FindAsync(command.ProviderPoolId);
        if (pool?.State != "Recovering" || pool.LastCommandId != command.Id) return;
        pool.State = successful ? "Available" : "RecoveryRequired";
        pool.RetryAt = null; pool.Revision++;
        if (successful) pool.ConsecutiveFailures = 0;
        Event(db, "ProviderRecoveryObserved", command.RuntimeId, command.WorkerId, command.Id, new { pool.Id, pool.State });
    }

    public Task<ProviderPool> ResumePool(string id, ResumeProviderPool input) => Write(async db =>
    {
        var pool = await db.Set<ProviderPool>().FindAsync(id) ?? throw new ControlException("Provider pool not found.", 404);
        if (pool.Revision != input.ExpectedRevision) throw new ControlException("Provider state changed; refresh before resuming.");
        if (!input.RecoveryVerified) throw new ControlException("Verify provider access and allowance before resuming.");
        pool.State = "Available"; pool.RetryAt = null; pool.ConsecutiveFailures = 0; pool.Revision++;
        Event(db, "ProviderPoolResumed", payload: new { pool.Id, pool.Revision }, provenance: "user");
        return pool;
    });
}
