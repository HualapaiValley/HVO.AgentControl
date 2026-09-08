using System.Text.Json;
using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    private sealed record NativeDecisionFailure(string Category, int? Status, long? RetryAt,
        string? AssistantId, string? SessionId, bool HasText, bool HasTools);

    // ResultJson is already caller-scoped by native reconciliation. Do not scan other
    // session messages, infer ownership from text, or salvage actions from failed turns.
    private static NativeDecisionFailure? ReadNativeDecisionFailure(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return null;
            NativeDecisionFailure? failure = null;
            var hasText = false;
            var hasTools = false;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object) continue;
                if (message.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object) continue;
                        hasText |= NativeText(part, "type") == "text" && !string.IsNullOrWhiteSpace(NativeText(part, "text"));
                        hasTools |= NativeText(part, "type") == "tool";
                    }
                if (!message.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object ||
                    !info.TryGetProperty("error", out var error) || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                var providerFailure = ProviderFailure.Parse(error, Now);
                var name = error.ValueKind == JsonValueKind.Object ? NativeText(error, "name") : null;
                int? status = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("statusCode", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) && number is >= 100 and <= 599 ? number : null;
                var category = providerFailure?.Category ?? (name switch
                {
                    "APIError" when status is >= 400 and < 500 => "InvalidRequest",
                    "MessageAbortedError" => "Cancelled",
                    "ContextOverflowError" => "ContextLimit",
                    "MessageOutputLengthError" => "OutputLimit",
                    _ => "NativeError"
                });
                failure = new(category, providerFailure?.Status ?? status, providerFailure?.RetryAt,
                    BoundNativeId(NativeText(info, "id")), BoundNativeId(NativeText(info, "sessionID")), false, false);
            }
            return failure is null ? null : failure with { HasText = hasText, HasTools = hasTools };
        }
        catch (JsonException) { return null; } // Existing bounded format repair owns malformed result envelopes.
        catch (InvalidOperationException) { return null; }
    }

    private static string? NativeText(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string? BoundNativeId(string? value) => value is { Length: > 200 } ? null : value;

    private static async Task HoldNativeDecision(ControlDb db, CoordinationRun run, CommandRecord command,
        CoordinatorContext context, NativeDecisionFailure failure)
    {
        // Use the recorded effective request, never current worker defaults: settings
        // may already have changed while this earlier decision was queued or running.
        var request = Json.Read<PromptInput>(command.ExecutionPayload.Length > 0 ? command.ExecutionPayload : command.Payload);
        var poolId = command.ProviderPoolId.Length > 0 ? command.ProviderPoolId : "provider:" + request.ProviderId;
        var pool = await db.Set<ProviderPool>().FindAsync(poolId);
        var checkpoint = new CoordinatorNativeFailure(command.Id, command.NativeMessageId, failure.SessionId, failure.AssistantId,
            failure.Category, failure.Status, failure.RetryAt, request.ProviderId ?? "", request.ModelId ?? "", request.Agent ?? "", request.Variant ?? "",
            poolId, pool?.Revision, failure.HasText, failure.HasTools, PreviousCommandId: context.NativeFailure?.CommandId);
        run.InputJson = Json.Write(context with { NativeFailure = checkpoint, Repair = null, Recovery = null });
        run.State = "Waiting"; run.Revision++;
        run.Detail = $"Coordinator held after native {failure.Category}" + (failure.Status is { } status ? $" (HTTP {status})" : "") +
            ". No routing actions were applied. " +
            (failure.HasTools ? "Tool evidence requires review; stop this run and start a reviewed recovery after checking effects. " :
             checkpoint.ProviderId.Length == 0 || checkpoint.ModelId.Length == 0 ? "Effective request routing is unavailable; stop this run and start a reviewed recovery. " :
             "Select a verified available coordinator route or recover its provider access. Automatic fallback dispatch is not configured. ") +
            "Worker monitoring continues; the failed turn will not be replayed.";
        Event(db, "CoordinatorNativeFailureHeld", command.RuntimeId, command.WorkerId, command.Id,
            new { run.Id, failure = checkpoint }, provenance: "service", nativeId: failure.AssistantId);
    }

    private static async Task<bool> ReleaseNativeDecisionHold(ControlDb db, CoordinationRun run, CoordinatorNativeFailure held)
    {
        if (held.HasTools) return false;
        var coordinator = await db.Workers.FindAsync(run.CoordinatorWorkerId);
        if (coordinator is null || coordinator.Archived || coordinator.Role != SessionRoles.Coordinator || coordinator.Stale ||
            coordinator.Activity != "Idle" || coordinator.LastObservedAt is null || coordinator.LastObservedAt < Now - 15000 ||
            await db.Commands.AnyAsync(x => x.WorkerId == coordinator.Id && (x.State == Delivery.Queued || x.State == Delivery.Dispatching ||
                x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown))) return false;
        // Missing legacy effective-model evidence cannot authorize an automatic new attempt.
        if (held.ProviderId.Length == 0 || held.ModelId.Length == 0) return false;
        var changedRoute = coordinator.ProviderId != held.ProviderId || coordinator.ModelId != held.ModelId ||
            coordinator.Agent != held.Agent || coordinator.Variant != held.Variant;
        var pool = await db.Set<ProviderPool>().FindAsync("provider:" + coordinator.ProviderId);
        var recoveredProvider = held.Category is "AuthenticationRequired" or "Exhausted" or "Throttled" or "Unavailable" &&
            pool is { State: "Available" } && pool.Id == held.ProviderPoolId && held.ProviderPoolRevision is { } revision && pool.Revision > revision;
        if (!changedRoute && !recoveredProvider || pool is not null && pool.State != "Available") return false;
        try { ValidateModelOptions(Json.Read<List<ModelChoice>>(coordinator.ModelsJson), coordinator.ProviderId, coordinator.ModelId, coordinator.Agent, coordinator.Variant); }
        catch (Exception ex) when (ex is ControlException or JsonException or InvalidOperationException) { return false; }
        var context = ReadRecoveryContext(run);
        run.InputJson = Json.Write(context with { NativeFailure = held with { Held = false }, Repair = null, Recovery = null });
        run.DecisionCommandId = null; run.LastObservation = ""; run.State = "Ready"; run.Revision++;
        run.Detail = "Coordinator access changed and is eligible. A fresh decision will use current evidence; the failed command remains retained.";
        Event(db, "CoordinatorNativeRecoveryEligible", coordinator.RuntimeId, coordinator.Id, held.CommandId,
            new { run.Id, failedCommandId = held.CommandId, coordinator.ProviderId, coordinator.ModelId, coordinator.Agent, coordinator.Variant, recoveredProvider });
        return true;
    }
}
