using HVO.AgentControl.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.AgentControl.Components.Pages;

public partial class Terminal
{
    [SupplyParameterFromQuery(Name = "runtime")] public string? RequestedRuntime { get; set; }
    private List<RuntimeRecord> runtimes = [];
    private string runtimeId = "", status = "Choose a runtime and open its terminal.";
    private bool connected;
    private ElementReference host;
    private IJSObjectReference? module;
    private DotNetObjectReference<Terminal>? self;
    protected override async Task OnInitializedAsync()
    {
        runtimes = (await Store.Snapshot()).Runtimes;
        runtimeId = runtimes.FirstOrDefault(x => x.Id == RequestedRuntime)?.Id ?? "";
    }
    private async Task Open()
    {
        try
        {
            module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Terminal.razor.js");
            self ??= DotNetObjectReference.Create(this);
            connected = true; status = "Connecting…";
            await module.InvokeVoidAsync("open", host, runtimeId, self);
        }
        catch (JSException) { connected = false; status = "Unable to open terminal. Check runtime SSH access and sign-in."; }
    }
    [JSInvokable] public Task TerminalState(bool active, string message) => InvokeAsync(() => { connected = active; status = message; StateHasChanged(); });
    private async Task Close() { if (module is not null) await module.InvokeVoidAsync("close"); connected = false; status = "Terminal closed."; }
    public async ValueTask DisposeAsync()
    {
        if (module is not null)
        {
            try { await module.InvokeVoidAsync("close"); await module.DisposeAsync(); } catch (JSDisconnectedException) { }
        }
        self?.Dispose();
    }
}
