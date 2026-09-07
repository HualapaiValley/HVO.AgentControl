using HVO.AgentControl.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.AgentControl.Components.Pages;

public partial class Providers
{
    [Inject] private ProviderLoginService Logins { get; set; } = default!;
    [Inject] private ProviderKeyService Keys { get; set; } = default!;
    private ProviderKeyStatus? keyStatus;
    private string apiKey = "";
    private List<ProviderLogin> logins = [];
    protected override async Task SnapshotChanged() { logins = Logins.List(); keyStatus = await Keys.Status(); }
    private Task SaveKey() => Execute(async () =>
    {
        var entered = apiKey; apiKey = "";
        keyStatus = await Keys.Save(new(entered, keyStatus?.Revision ?? 0));
        notice = "Key saved encrypted. Apply it to the runtimes you want to use.";
    });
    private Task ApplyKey(string runtimeId) => Execute(async () =>
    {
        keyStatus = await Keys.Apply(runtimeId, new(keyStatus!.Revision), lifetime.Token);
        notice = "Delivery status updated. Refresh workspace models before selecting a Go model.";
    });
    private Task Start(string runtimeId) => Execute(async () => { await Logins.Start(runtimeId, Guid.NewGuid().ToString()); });
}
