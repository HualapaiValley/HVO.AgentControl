using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.GitHub;

public sealed class GitHubAccessService(ControlStore store, Secrets secrets, GitHubAppClient github)
{
    // Configure and delivery serialize so an old grant cannot overwrite a newer one remotely.
    public SemaphoreSlim Gate { get; } = new(1);

    public async Task<List<GitHubAccess>> List()
    {
        var records = await store.Read(db => db.GitHubAccess.AsNoTracking().ToListAsync());
        foreach (var record in records.Where(x => x.State == "Ready" && x.ExpiresAt <= ControlStore.Now))
        { record.State = "Expired"; record.Detail = "The last delivered credential expired. Connect the runtime to renew it."; }
        return records;
    }

    public async Task<GitHubAccess> Disable(string runtimeId, long expectedRevision, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            return await store.Write(async db =>
            {
                var record = await db.GitHubAccess.FindAsync(runtimeId) ?? throw new ControlException("GitHub access not found.", 404);
                if (record.Revision != expectedRevision) throw new ControlException("GitHub access changed; refresh before disabling renewal.");
                record.State = "Disabled"; record.Revision++;
                record.Detail = "Renewal disabled. Previously issued credentials remain valid until expiry; suspend or uninstall the GitHub App for immediate revocation.";
                ControlStore.Event(db, "GitHubRenewalDisabled", runtimeId, provenance: "user");
                return record;
            });
        }
        finally { Gate.Release(); }
    }

    public async Task<GitHubAccess> Configure(string runtimeId, ConfigureGitHubAccess input, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var runtime = await store.Read(async db => await db.Runtimes.FindAsync(runtimeId) ?? throw new ControlException("Runtime not found.", 404));
            var previous = await store.Read(async db => await db.GitHubAccess.FindAsync(runtimeId));
            if (input.ExpectedRevision != (previous?.Revision ?? 0)) throw new ControlException("GitHub access changed; refresh before saving.");
            var enteredKey = input.PrivateKey ?? "";
            if (enteredKey.Length > 64000) throw new ControlException("Private key is too large.", 400);
            var repositories = GitHubAppClient.ValidateRepositories(input.Repositories ?? []);
            GitHubAccess? source = null;
            if (input.SourceRuntimeId is not null)
            {
                if (input.SourceRuntimeId == runtimeId || enteredKey.Length > 0)
                    throw new ControlException("Choose a different source registration without entering a private key.", 400);
                source = await store.Read(async db => await db.GitHubAccess.FindAsync(input.SourceRuntimeId))
                    ?? throw new ControlException("Source GitHub registration not found.", 404);
                if (source.State == "Disabled" || source.Revision != input.SourceRevision)
                    throw new ControlException("Source registration changed or is disabled; select it again.");
                if (source.AppId != input.AppId || source.InstallationId != input.InstallationId)
                    throw new ControlException("App and installation must match the selected source registration.", 400);
                var scope = Json.Read<string[]>(source.RepositoriesJson);
                if (repositories.Any(repository => !scope.Contains(repository, StringComparer.OrdinalIgnoreCase)))
                    throw new ControlException("Reused access must stay within the source registration's repositories.", 400);
            }
            var key = source is not null ? secrets.Read(source.PrivateKeyReference)
                : enteredKey.Length > 0 ? enteredKey : previous is not null ? secrets.Read(previous.PrivateKeyReference) : "";
            // Network validation happens outside the database writer gate. No token enters the database.
            _ = await github.Issue(input.AppId, input.InstallationId, key, repositories, token);
            // Each target owns its encrypted key copy and independently issues expiring tokens.
            var reference = enteredKey.Length > 0 || source is not null ? secrets.StoreEncrypted(key) : previous!.PrivateKeyReference;
            return await store.Write(async db =>
            {
                if (await db.Runtimes.FindAsync(runtimeId) is not { } current || current.ManagedServerId != runtime.ManagedServerId || current.Revision != runtime.Revision)
                    throw new ControlException("Runtime changed during GitHub verification.");
                var record = await db.GitHubAccess.FindAsync(runtimeId) ?? new GitHubAccess { Id = runtimeId };
                record.AppId = input.AppId; record.InstallationId = input.InstallationId; record.PrivateKeyReference = reference;
                record.RepositoriesJson = Json.Write(repositories); record.State = "Pending"; record.Detail = "Repository access verified; credential delivery pending.";
                record.RetryAt = 0; record.ExpiresAt = null; record.Revision++;
                if (previous is null) db.GitHubAccess.Add(record);
                ControlStore.Event(db, "GitHubAccessConfigured", runtimeId, payload: new { record.AppId, record.InstallationId, repositories, sourceRuntimeId = source?.Id }, provenance: "user");
                return record;
            });
        }
        finally { Gate.Release(); }
    }
}
