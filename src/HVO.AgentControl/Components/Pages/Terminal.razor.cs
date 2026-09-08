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
    private int opening;
    private bool disposed;
    protected override async Task OnInitializedAsync()
    {
        runtimes = (await Store.Snapshot()).Runtimes;
        runtimeId = runtimes.FirstOrDefault(x => x.Id == RequestedRuntime)?.Id ?? "";
    }
    private async Task Open()
    {
        var operation = ++opening;
        try
        {
            connected = true; status = "Loading terminal assets…";
            var imported = module is null;
            var loaded = module ?? await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Terminal.razor.js");
            if (disposed || operation != opening)
            {
                if (imported) await loaded.DisposeAsync();
                return;
            }
            module = loaded;
            self ??= DotNetObjectReference.Create(this);
            status = "Connecting…";
            await module.InvokeVoidAsync("open", host, runtimeId, self);
        }
        catch (JSException) { connected = false; status = "Unable to open terminal. Check runtime SSH access and sign-in."; }
    }
    [JSInvokable] public Task TerminalState(bool active, string message) => InvokeAsync(() => { connected = active; status = message; StateHasChanged(); });
    private async Task Close() { opening++; if (module is not null) await module.InvokeVoidAsync("close"); connected = false; status = "Terminal closed."; }
    public async ValueTask DisposeAsync()
    {
        disposed = true; opening++;
        var loaded = module;
        module = null;
        if (loaded is not null)
        {
            try { await loaded.InvokeVoidAsync("close"); await loaded.DisposeAsync(); } catch (JSDisconnectedException) { }
        }
        self?.Dispose();
        self = null;
    }
}
