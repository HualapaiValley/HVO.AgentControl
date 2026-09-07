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
        group.MapGet("/snapshot", (ControlStore store) => store.Snapshot());
        group.MapGet("/runtimes", (ControlStore store) => store.Read(db => db.Runtimes.AsNoTracking().ToListAsync()));
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
        app.MapHub<ActivityHub>("/hubs/activity").RequireAuthorization();
    }
}
