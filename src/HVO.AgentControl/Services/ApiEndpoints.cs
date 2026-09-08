using System.Text;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Services;

public static class ApiEndpoints
{
    public static void MapControlApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.HttpContext.Request.Method))
                await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
            return await next(context);
        });
        group.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) => new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        group.MapInventoryApi();
        group.MapTaskBindingApi();
        group.MapGet("/control-services", (ControlStore store) => store.ControlServices());
        group.MapPost("/control-services", (RegisterControlServiceInput input, ControlServiceRegistration registration, CancellationToken token) => registration.Register(input, token));
        group.MapPost("/control-services/{id}/sessions", (string id, CreateControlSessionInput input, ControlStore store) => store.CreateControlSession(id, input));
        group.MapPost("/control-services/{id}/sessions/{sessionId}/retry", (string id, string sessionId, RetryControlSessionInput input, ControlStore store) => store.RetryControlSession(id, sessionId, input));
        group.MapPost("/coordinations/{id}/control-session", (string id, MigrateControlSessionInput input, ControlStore store) => store.MigrateControlSession(id, input));
        group.MapGet("/providers/opencode-go/key", (ProviderKeyService keys) => keys.Status());
        group.MapPost("/providers/opencode-go/key", (SaveProviderKey input, ProviderKeyService keys) => keys.Save(input));
        group.MapPost("/runtimes/{id}/providers/opencode-go", (string id, ApplyProviderKey input, ProviderKeyService keys, CancellationToken token) => keys.Apply(id, input, token));
        group.MapGet("/providers/pools", (ControlStore store) => store.ProviderPools());
        group.MapPost("/providers/pools/{id}/resume", (string id, ResumeProviderPool input, ControlStore store) => store.ResumePool(id, input));
        group.MapGet("/providers/fallbacks/{sourceCommandId}", (string sourceCommandId, ControlStore store) => store.ProviderFallbacks(sourceCommandId));
        group.MapPost("/providers/fallbacks", (ProviderFallbackRecommendation input, ControlStore store) => store.RecordFallbackRecommendation(input));
        group.MapGet("/providers/logins", (ProviderLoginService logins) => logins.List());
        group.MapPost("/runtimes/{id}/providers/chatgpt", (string id, RequestId input, ProviderLoginService logins) => logins.Start(id, input.Id));
        group.MapGet("/github/access", (HVO.AgentControl.GitHub.GitHubAccessService github) => github.List());
        group.MapPost("/runtimes/{id}/github/disable", (string id, DeleteRegistrationInput input,
            HVO.AgentControl.GitHub.GitHubAccessService github, CancellationToken token) => github.Disable(id, input.ExpectedRevision, token));
        group.MapPost("/runtimes/{id}/github", (string id, HVO.AgentControl.GitHub.ConfigureGitHubAccess input,
            HVO.AgentControl.GitHub.GitHubAccessService github, CancellationToken token) => github.Configure(id, input, token));
        group.MapGet("/coordinations", (ControlStore store) => store.Coordinations());
        group.MapPost("/coordinations", (StartCoordinationInput input, ControlStore store) => store.StartCoordination(input));
        group.MapPost("/coordinations/{id}/control", (string id, CoordinationControlInput input, ControlStore store) => store.ControlCoordination(id, input));
        group.MapPost("/coordinations/{id}/prompts", (string id, CoordinationPromptInput input, ControlStore store) => store.PromptCoordination(id, input));
        group.MapPost("/coordinations/{id}/renew", (string id, CoordinationRenewalInput input, ControlStore store) => store.RenewCoordination(id, input));
        group.MapPost("/coordinations/{id}/operator-updates", (string id, ConfigureOperatorUpdatesInput input, ControlStore store) => store.ConfigureOperatorUpdates(id, input));
        group.MapGet("/operator-updates", (long? after, int? take, ControlStore store) => store.OperatorUpdates(after ?? 0, take ?? 50));
        group.MapPost("/operator-updates/{id}/ack", (string id, ControlStore store) => store.AcknowledgeOperatorUpdate(id));
        group.MapGet("/snapshot", (ControlStore store) => store.Snapshot());
        group.MapGet("/usage", (string? workerId, string? providerId, string? modelId, long? from, long? to, ControlStore store) =>
            store.Usage(new(workerId, providerId, modelId, from, to)));
        group.MapGet("/usage/export", async (string? workerId, string? providerId, string? modelId, long? from, long? to, ControlStore store) =>
            Results.File(Encoding.UTF8.GetBytes(await store.ExportUsageCsv(new(workerId, providerId, modelId, from, to))),
                "text/csv; charset=utf-8", "model-usage.csv"));
        group.MapGet("/runtimes", (ControlStore store) => store.Read(db => db.Runtimes.AsNoTracking().ToListAsync()));
        group.MapGet("/runtimes/{id}/telemetry-history", (string id, int? take, ControlStore store) => store.TelemetryHistory(id, take ?? 10));
        group.MapPost("/runtimes", (RuntimeRecord input, ControlStore store) => store.SaveRuntime(input));
        group.MapPost("/runtimes/verify", (RuntimeVerifyInput input, RuntimeVerificationService verification, CancellationToken token) => verification.Verify(input, token));
        group.MapPost("/runtimes/verified-save", (VerifiedRuntimeInput input, RuntimeVerificationService verification) => verification.SaveForSetup(input));
        group.MapPost("/runtimes/verified-connect", (VerifiedRuntimeInput input, RuntimeVerificationService verification) => verification.SaveAndConnect(input));
        group.MapPost("/runtimes/{id}/connect", (string id, RequestId input, ControlStore store) => store.RuntimeCommand(id, "EnsureServer", input.Id));
        group.MapPost("/runtimes/{id}/refresh", (string id, RequestId input, ControlStore store) => store.RuntimeCommand(id, "RefreshState", input.Id));
        group.MapPost("/runtimes/{id}/disconnect", (string id, RequestId input, ControlStore store) => store.RuntimeCommand(id, "DisconnectRuntime", input.Id));
        group.MapPost("/runtimes/{id}/stop", (string id, RequestId input, ControlStore store) => store.RuntimeCommand(id, "StopManagedServer", input.Id));
        group.MapPost("/workers", (CreateWorkerInput input, ControlStore store) => store.CreateWorker(input));
        group.MapPut("/workers/{id}", (string id, UpdateWorkerInput input, ControlStore store) => store.UpdateWorker(id, input));
        group.MapPost("/workers/{id}/reserve-coordinator", (string id, ReserveCoordinatorInput input, ControlStore store) => store.ReserveCoordinator(id, input));
        group.MapPost("/workers/{id}/capabilities", (string id, RequestId input, ControlStore store) => store.DiscoverCapabilities(id, input));
        group.MapPost("/workers/{id}/delete", (string id, DeleteRegistrationInput input, ControlStore store) => store.DeleteWorker(id, input));
        group.MapPost("/runtimes/{id}/delete", (string id, DeleteRegistrationInput input, ControlStore store) => store.DeleteRuntime(id, input));
        group.MapPost("/commands/{id}/dismiss-setup", (string id, ControlStore store) => store.DismissCreation(id));
        group.MapPost("/workers/{id}/archive", (string id, WorkerArchiveInput input, ControlStore store) => store.ArchiveWorker(id, input));
        group.MapPost("/workspaces/inspect", (InspectWorkspaceInput input, ControlStore store) => store.InspectWorkspace(input));
        group.MapGet("/workers/{id}", (string id, ControlStore store) => store.Detail(id));
        group.MapGet("/workers/{id}/history", (string id, long? before, ControlStore store) => store.Detail(id, before));
        group.MapPost("/workers/{id}/prompts", (string id, PromptInput input, ControlStore store) => store.Prompt(id, input));
        group.MapPost("/workers/{id}/abort", (string id, RequestId input, ControlStore store) => store.Abort(id, input.Id));
        group.MapPost("/workers/{id}/outcome", (string id, OutcomeInput input, ControlStore store) => store.SetOutcome(id, input));
        group.MapPost("/requests/{id}/reply", (string id, ReplyInput input, ControlStore store) =>
            id == input.RequestId ? store.Reply(input) : throw new ControlException("Reply request identity mismatch.", 400));
        group.MapGet("/commands/{id}", (string id, ControlStore store) => store.Read(async db => await db.Commands.FindAsync(id) ?? throw new ControlException("Command not found.", 404)));
        group.MapPost("/commands/{id}/queue", (string id, QueueEdit input, ControlStore store) => store.EditQueue(id, input.Action));
        group.MapGet("/events", (long? after, string? workerId, ControlStore store) => store.Read(db => db.Events.AsNoTracking()
            .Where(x => x.Sequence > (after ?? 0) && (workerId == null || x.WorkerId == workerId)).OrderBy(x => x.Sequence).Take(200).ToListAsync()));
        group.MapGet("/evidence", (long? after, int? take, ControlStore store) => store.Evidence(after ?? 0, take ?? 50));
        group.MapPost("/evidence/reads", (EvidenceReadInput input, ControlStore store) => store.ReadEvidence(input));
        group.MapPost("/evidence/reads/ack", (EvidenceAcknowledgeInput input, ControlStore store) => store.AcknowledgeEvidence(input));
        app.MapHub<ActivityHub>("/hubs/activity").RequireAuthorization();
    }
}
