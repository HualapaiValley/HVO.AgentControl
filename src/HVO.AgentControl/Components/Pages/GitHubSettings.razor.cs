using HVO.AgentControl.Core;
using HVO.AgentControl.GitHub;
using Microsoft.AspNetCore.Components;

namespace HVO.AgentControl.Components.Pages;

public partial class GitHubSettings
{
    [Inject] private GitHubAccessService GitHub { get; set; } = default!;
    private List<GitHubAccess> grants = [];
    private string runtimeId = "", repositories = "", privateKey = "";
    private long appId, installationId, revision;

    protected override async Task SnapshotChanged() => grants = await GitHub.List();

    private void SelectRuntime()
    {
        var grant = grants.FirstOrDefault(x => x.Id == runtimeId);
        appId = grant?.AppId ?? 0; installationId = grant?.InstallationId ?? 0; revision = grant?.Revision ?? 0;
        repositories = grant is null ? "" : string.Join('\n', Json.Read<string[]>(grant.RepositoriesJson));
        privateKey = "";
    }

    private Task Save() => Execute(async () =>
    {
        try
        {
            var result = await GitHub.Configure(runtimeId, new(appId, installationId, privateKey,
                repositories.Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), revision), lifetime.Token);
            revision = result.Revision;
            notice = "Repository access verified. Credential delivery and renewal run while the runtime is connected.";
        }
        finally { privateKey = ""; }
    });

    private Task Disable(GitHubAccess access) => Execute(async () =>
    {
        var updated = await GitHub.Disable(access.Id, access.Revision, lifetime.Token);
        if (runtimeId == access.Id) revision = updated.Revision;
        notice = "Renewal disabled. For immediate revocation, suspend or uninstall the App in GitHub settings.";
    });
}
