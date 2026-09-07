using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;

namespace HVO.AgentControl.GitHub;

public sealed class GitHubCredentialSupervisor(ControlStore store, Secrets secrets, GitHubAppClient github,
    GitHubAccessService access, GitHubCredentialDelivery delivery) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var grant in await access.List())
                {
                    if (grant.State == "Disabled" || grant.RetryAt > ControlStore.Now || (grant.State == "Ready" && grant.ExpiresAt > ControlStore.Now + 600000)) continue;
                    await access.Gate.WaitAsync(stoppingToken);
                    try
                    {
                        var current = await store.Read(async db => await db.GitHubAccess.FindAsync(grant.Id));
                        var runtime = await store.Read(async db => await db.Runtimes.FindAsync(grant.Id));
                        if (current is null || current.Revision != grant.Revision || runtime is null || !runtime.DesiredConnected || runtime.Health != "Healthy") continue;
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        deadline.CancelAfter(TimeSpan.FromSeconds(60));
                        try
                        {
                            var credential = await github.Issue(current.AppId, current.InstallationId, secrets.Read(current.PrivateKeyReference), Json.Read<string[]>(current.RepositoriesJson), deadline.Token);
                            await delivery.Deliver(runtime, credential, deadline.Token);
                            await Save(grant, "Ready", "GitHub CLI credential delivered; renewal is automatic while connected.", credential.ExpiresAt.ToUnixTimeMilliseconds());
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            await Save(grant, "Blocked", ex is ControlException control ? control.Message : "GitHub credential delivery failed; check connectivity and access.", null);
                        }
                    }
                    finally { access.Gate.Release(); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { /* Retry database availability without logging credential-bearing exceptions. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private Task<bool> Save(GitHubAccess grant, string state, string detail, long? expires) => store.Write(async db =>
    {
        var current = await db.GitHubAccess.FindAsync(grant.Id);
        if (current is null || current.Revision != grant.Revision) return false;
        current.State = state; current.Detail = detail;
        if (expires.HasValue) current.ExpiresAt = expires;
        current.RetryAt = state == "Blocked" ? ControlStore.Now + 120000 : 0;
        ControlStore.Event(db, "GitHubCredential" + state, grant.Id, payload: new { state, expires });
        return true;
    });
}
