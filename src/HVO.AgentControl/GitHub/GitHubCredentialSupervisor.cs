using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;

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
                    if (grant.State == "Disabled" || grant.RetryAt > ControlStore.Now) continue;
                    await access.Gate.WaitAsync(stoppingToken);
                    try
                    {
                        var current = await store.Read(async db => await db.GitHubAccess.FindAsync(grant.Id));
                        var runtime = await store.Read(async db => await db.Runtimes.FindAsync(grant.Id));
                        if (current is null || current.Revision != grant.Revision || runtime is null || runtime.ConnectionKind != RuntimeConnections.Ssh || !runtime.DesiredConnected || runtime.Health != "Healthy") continue;
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        deadline.CancelAfter(TimeSpan.FromSeconds(60));
                        GitHubInstallationToken? credential = null;
                        try
                        {
                            var now = ControlStore.Now;
                            var storedCredentialIsCurrent = !CredentialNeedsDelivery(current, now);
                            var native = (await store.NativeProcessObservations(runtime.Id, 1)).SingleOrDefault();
                            if (storedCredentialIsCurrent && !GitHubProcessEnvironment.MustVerify(current, runtime, now) &&
                                !GitHubProcessEnvironment.NewerNativeProcessContradicts(current, native)) continue;
                            GitHubProcessEnvironmentEvidence environment;
                            if (storedCredentialIsCurrent)
                            {
                                environment = await delivery.VerifyEnvironment(runtime,
                                    current.CredentialConfigurationFingerprint, deadline.Token);
                            }
                            else
                            {
                                credential = await github.Issue(current.AppId, current.InstallationId, secrets.Read(current.PrivateKeyReference), Json.Read<string[]>(current.RepositoriesJson), deadline.Token);
                                environment = await delivery.Deliver(runtime, credential, deadline.Token,
                                    current.CredentialConfigurationFingerprint);
                            }
                            await SaveEnvironment(grant, runtime, environment, credential, credential is not null);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            await SaveFailure(grant, ex is ControlException control ? control.Message : "GitHub credential delivery failed; check connectivity and access.", credential);
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

    internal static bool CredentialNeedsDelivery(GitHubAccess grant, long now) =>
        grant.CredentialState != GitHubCredentialState.Delivered || grant.ExpiresAt <= now + 600000;

    internal Task<bool> SaveEnvironment(GitHubAccess grant, RuntimeRecord runtime, GitHubProcessEnvironmentEvidence environment,
        GitHubInstallationToken? credential, bool credentialDelivered) => store.Write(async db =>
    {
        var current = await db.GitHubAccess.FindAsync(grant.Id);
        if (current is null || current.Revision != grant.Revision) return false;
        var currentRuntime = await db.Runtimes.FindAsync(runtime.Id);
        if (currentRuntime is null || !currentRuntime.DesiredConnected || currentRuntime.Health != "Healthy" ||
            currentRuntime.ConnectionKind != runtime.ConnectionKind || currentRuntime.ManagedServerId != runtime.ManagedServerId ||
            currentRuntime.Host != runtime.Host || currentRuntime.Port != runtime.Port || currentRuntime.Username != runtime.Username ||
            currentRuntime.StateDirectory != runtime.StateDirectory || currentRuntime.ApiPort != runtime.ApiPort ||
            environment.PolicyFingerprint != GitHubProcessEnvironment.Fingerprint(runtime)) return false;
        var nativeEvent = await db.Events.AsNoTracking().Where(x => x.RuntimeId == runtime.Id && x.Type == "NativeProcessObserved")
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync();
        var native = nativeEvent is null ? null : Json.Read<NativeProcessObservationEvidence>(nativeEvent.Payload);
        if (GitHubProcessEnvironment.NewerNativeProcessContradicts(environment.ProcessId,
            environment.ProcessIncarnation, environment.ObservedAt, native)) return false;
        var changed = current.State != environment.State || current.Detail != environment.Detail ||
            current.EnvironmentPolicyVersion != environment.PolicyVersion || current.EnvironmentPolicyFingerprint != environment.PolicyFingerprint ||
            current.EnvironmentProcessId != environment.ProcessId || current.EnvironmentProcessIncarnation != environment.ProcessIncarnation;
        current.State = environment.State; current.Detail = environment.Detail;
        current.EnvironmentPolicyVersion = environment.PolicyVersion;
        current.EnvironmentPolicyFingerprint = environment.PolicyFingerprint;
        current.EnvironmentProcessId = environment.ProcessId;
        current.EnvironmentProcessIncarnation = environment.ProcessIncarnation;
        current.EnvironmentVerifiedAt = environment.ObservedAt;
        if (credential is not null) GitHubAccessService.SavePermissionEvidence(current, credential);
        if (credentialDelivered)
        {
            current.CredentialState = GitHubCredentialState.Delivered;
            current.CredentialConfigurationFingerprint = environment.CredentialConfigurationFingerprint;
            current.ExpiresAt = credential!.ExpiresAt.ToUnixTimeMilliseconds();
        }
        else if (environment.Status == GitHubProcessEnvironment.CredentialMismatch)
            current.CredentialState = GitHubCredentialState.Unknown;
        current.RetryAt = environment.State switch { "MigrationRequired" => ControlStore.Now + 30000, "Blocked" => ControlStore.Now + 120000, _ => 0 };
        if (changed || credentialDelivered)
            ControlStore.Event(db, "GitHubCredential" + environment.State, grant.Id, payload: new
            {
                state = environment.State,
                credentialState = current.CredentialState,
                expires = current.ExpiresAt,
                environment.Status,
                environment.Platform,
                environment.ProcessId,
                environment.ObservedAt,
                current.ChecksPermission,
                current.CommitStatusesPermission,
                current.ActionsPermission,
                current.PermissionsVerifiedAt
            });
        return true;
    });

    private Task<bool> SaveFailure(GitHubAccess grant, string detail, GitHubInstallationToken? credential) => store.Write(async db =>
    {
        var current = await db.GitHubAccess.FindAsync(grant.Id);
        if (current is null || current.Revision != grant.Revision) return false;
        var changed = current.State != "Blocked" || current.Detail != detail;
        current.State = "Blocked"; current.Detail = detail; current.RetryAt = ControlStore.Now + 120000;
        if (credential is not null) GitHubAccessService.SavePermissionEvidence(current, credential);
        if (changed) ControlStore.Event(db, "GitHubCredentialBlocked", grant.Id, payload: new
        {
            state = current.State,
            credentialState = current.CredentialState,
            current.ExpiresAt,
            current.ChecksPermission,
            current.CommitStatusesPermission,
            current.ActionsPermission,
            current.PermissionsVerifiedAt
        });
        return true;
    });
}
