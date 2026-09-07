using HVO.AgentControl.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.AgentControl.Components.Pages;

public partial class Providers
{
    [Inject] private ProviderLoginService Logins { get; set; } = default!;
    private List<ProviderLogin> logins = [];
    protected override Task SnapshotChanged() { logins = Logins.List(); return Task.CompletedTask; }
    private Task Start(string runtimeId) => Execute(async () => { await Logins.Start(runtimeId, Guid.NewGuid().ToString()); });
}
