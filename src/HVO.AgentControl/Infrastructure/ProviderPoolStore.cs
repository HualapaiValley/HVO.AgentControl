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

// A validated advisory route only. A later receipt-aware continuation owns dispatch.
public sealed class ProviderFallbackReceipt
{
    public string Id { get; set; } = "";
    public string SourceCommandId { get; set; } = "";
    public string SourcePoolId { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string TargetPoolId { get; set; } = "";
    public string SourceFailureReceiptId { get; set; } = "";
    public string SourceFailureCategory { get; set; } = "";
    public string SourceTerminalState { get; set; } = "";
    public string SourceTaskOutcome { get; set; } = "";
    public long CreatedAt { get; set; }
}

public sealed record ResumeProviderPool(long ExpectedRevision, bool RecoveryVerified);
public sealed record ProviderFallbackRecommendation(string Id, string SourceCommandId, long ExpectedWorkerRevision,
    string ProviderId, string ModelId);
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

public sealed record NativeRetryFailure(int Attempt, string ProviderId, long? Next, ProviderFailure Failure)
{
    public static NativeRetryFailure? Parse(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object || Text(status, "type") != "retry" ||
            !status.TryGetProperty("attempt", out var attemptValue) || !attemptValue.TryGetInt32(out var attempt) || attempt < 0 ||
            !status.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object ||
            Text(action, "reason") != "account_rate_limit") return null;
        var providerId = Text(action, "provider");
        if (providerId.Length is 0 or > 200) return null;
        long? next = status.TryGetProperty("next", out var nextValue) && nextValue.TryGetInt64(out var value) && value >= 0 ? value : null;
        return new(attempt, providerId, next, new ProviderFailure("Exhausted", null, null));
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}

public sealed partial class ControlStore
{
    public Task<List<ProviderPool>> ProviderPools() => Read(db => db.Set<ProviderPool>().AsNoTracking().OrderBy(x => x.Id).ToListAsync());

    public Task<List<ProviderFallbackReceipt>> ProviderFallbacks(string sourceCommandId) => Read(db =>
        db.Set<ProviderFallbackReceipt>().AsNoTracking().Where(x => x.SourceCommandId == sourceCommandId).ToListAsync());

    public Task<ProviderFallbackReceipt> RecordFallbackRecommendation(ProviderFallbackRecommendation input) => Write(async db =>
    {
        ValidateRequestId(input.Id);
        if (string.IsNullOrWhiteSpace(input.SourceCommandId) || string.IsNullOrWhiteSpace(input.ProviderId) ||
            string.IsNullOrWhiteSpace(input.ModelId) || input.ProviderId.Length > 200 || input.ModelId.Length > 200)
            throw new ControlException("Provide a source command and configured replacement provider/model.", 400);
        if (await db.Set<ProviderFallbackReceipt>().FindAsync(input.Id) is { } prior)
        {
            if (prior.SourceCommandId != input.SourceCommandId || prior.ProviderId != input.ProviderId || prior.ModelId != input.ModelId)
                throw new ControlException("Fallback recommendation ID belongs to a different proposal.");
            return prior;
        }
        var source = await db.Commands.FindAsync(input.SourceCommandId) ?? throw new ControlException("Source command not found.", 404);
        if (source.Kind != "Prompt" || source.WorkerId is null || source.State is not (Delivery.Finished or Delivery.Cancelled))
            throw new ControlException("Reconcile the source prompt to a completed or cancelled turn before proposing fallback.");
        var assignment = await db.Assignments.FindAsync(source.Id);
        // Delivery completion and historical retry receipts do not prove task failure.
        // Worker.Outcome can describe later work; only this command's assignment qualifies.
        if (assignment is null || assignment.WorkerId != source.WorkerId ||
            assignment.Outcome != (source.State == Delivery.Cancelled ? "Cancelled" : "Failed"))
            throw new ControlException("The source prompt has no matching terminal failed or cancelled assignment outcome.");
        if (await db.Set<ProviderFallbackReceipt>().FirstOrDefaultAsync(x => x.SourceCommandId == source.Id) is not null)
            throw new ControlException("A fallback recommendation is already recorded for this source command.");
        var worker = await db.Workers.FindAsync(source.WorkerId) ?? throw new ControlException("Source worker not found.", 404);
        if (worker.Archived || worker.Stale || worker.Revision != input.ExpectedWorkerRevision || worker.Activity != "Idle")
            throw new ControlException("Refresh the source worker before proposing fallback; it is unavailable or changed.");
        if (await db.Commands.AnyAsync(x => x.WorkerId == worker.Id && x.Id != source.Id &&
            (x.State == Delivery.Queued || x.State == Delivery.Dispatching || x.State == Delivery.Accepted ||
             x.State == Delivery.Running || x.State == Delivery.Unknown)))
            throw new ControlException("Source worker still has outstanding work; reconcile it before proposing fallback.");
        var sourcePoolId = source.ProviderPoolId.Length > 0 ? source.ProviderPoolId : PoolId(worker, source);
        var sourcePool = await db.Set<ProviderPool>().FindAsync(sourcePoolId);
        if (sourcePool is null || sourcePool.State == "Available")
            throw new ControlException("The source provider pool is not held by a recorded provider failure.");
        var failure = await db.Set<ProviderFailureReceipt>().Where(x => x.CommandId == source.Id && x.PoolId == sourcePoolId &&
                (source.State != Delivery.Cancelled || x.Category == "Exhausted"))
            .OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
        if (failure is null)
            throw new ControlException("The source prompt needs its own provider failure receipt; cancellation requires proven quota exhaustion.");
        var targetPoolId = "provider:" + input.ProviderId;
        if (targetPoolId == sourcePoolId) throw new ControlException("Fallback must use a different provider pool.");
        var targetPool = await db.Set<ProviderPool>().FindAsync(targetPoolId);
        if (targetPool is not null && targetPool.State != "Available")
            throw new ControlException("The replacement provider pool is unavailable.");
        ValidateModelOptions(Json.Read<List<ModelChoice>>(worker.ModelsJson), input.ProviderId, input.ModelId, "", "");
        var receipt = new ProviderFallbackReceipt
        {
            Id = input.Id,
            SourceCommandId = source.Id,
            SourcePoolId = sourcePoolId,
            WorkerId = worker.Id,
            ProviderId = input.ProviderId,
            ModelId = input.ModelId,
            TargetPoolId = targetPoolId,
            SourceFailureReceiptId = failure.Id,
            SourceFailureCategory = failure.Category,
            SourceTerminalState = source.State,
            SourceTaskOutcome = assignment.Outcome,
            CreatedAt = Now
        };
        db.Add(receipt);
        Event(db, "ProviderFallbackRecommended", worker.RuntimeId, worker.Id, source.Id,
            new
            {
                receipt.Id,
                receipt.SourceCommandId,
                receipt.SourcePoolId,
                receipt.SourceFailureReceiptId,
                receipt.SourceFailureCategory,
                receipt.SourceTerminalState,
                receipt.SourceTaskOutcome,
                receipt.ProviderId,
                receipt.ModelId,
                receipt.TargetPoolId
            }, provenance: "advisor");
        return receipt;
    });

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

    internal static async Task ObserveProviderFailure(ControlDb db, WorkerRecord worker, CommandRecord command, string nativeId, ProviderFailure failure,
        NativeRetryFailure? nativeRetry = null)
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
        if (nativeRetry is not null)
            Event(db, "ProviderNativeRetryObserved", worker.RuntimeId, worker.Id, command.Id,
                new { poolId, nativeRetry.Attempt, nativeRetry.Next }, provenance: "native", nativeId: nativeId);
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
