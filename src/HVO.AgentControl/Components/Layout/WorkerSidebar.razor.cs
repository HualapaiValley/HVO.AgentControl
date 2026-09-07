using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;

namespace HVO.AgentControl.Components.Layout;

public partial class WorkerSidebar
{
    [Inject] private ControlStore Store { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IJSRuntime JavaScript { get; set; } = default!;

    private readonly CancellationTokenSource lifetime = new();
    private ControlSnapshot? snapshot;
    private IJSObjectReference? module;
    private Task? refreshLoop;
    private string? selectedWorkerId, error;
    private string search = "";
    private bool collapsed;
    private int dirty = 1;

    private IEnumerable<WorkerSidebarGroup> RuntimeGroups => snapshot is null ? [] : WorkerSidebarView.TaskWorkerGroups(snapshot, search, selectedWorkerId);

    private IEnumerable<WorkerRecord> Coordinators => snapshot is null ? [] : WorkerSidebarView.Coordinators(snapshot, search, selectedWorkerId);

    protected override async Task OnInitializedAsync()
    {
        ReadSelectedWorker();
        await Refresh();
        Store.Changed += Changed;
        Navigation.LocationChanged += LocationChanged;
        refreshLoop = RefreshLoop();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./Components/Layout/WorkerSidebar.razor.js");
        collapsed = await module.InvokeAsync<bool>("readCollapsed");
        StateHasChanged();
    }

    private void Changed() => Interlocked.Exchange(ref dirty, 1);

    private async Task RefreshLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                if (Interlocked.Exchange(ref dirty, 0) != 0)
                    await InvokeAsync(async () => { await Refresh(); StateHasChanged(); });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task Refresh()
    {
        try { snapshot = await Store.Snapshot(); error = null; }
        catch (Exception ex) { error = RuntimeSupervisor.SafeError(ex); }
    }

    private void LocationChanged(object? sender, LocationChangedEventArgs args)
    {
        ReadSelectedWorker();
        _ = InvokeAsync(StateHasChanged);
    }

    private void ReadSelectedWorker()
    {
        var query = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query);
        selectedWorkerId = query.TryGetValue("worker", out var worker) ? worker.ToString() : null;
    }

    private static string ConversationUrl(string id) => WorkerSidebarView.ConversationUrl(id);
    private static string ProjectSuffix(WorkerRecord worker) => worker.Project.Length == 0 ? "" : " / " + worker.Project;
    private static string StatusTone(WorkerRecord worker) => worker.Stale || worker.Activity is "WaitingPermission" or "WaitingQuestion" ? "attention" : worker.Activity is "Active" or "Retrying" ? "active" : worker.Activity == "Idle" ? "ready" : "neutral";

    private async Task Toggle()
    {
        collapsed = !collapsed;
        if (module is not null) await module.InvokeVoidAsync("writeCollapsed", collapsed);
    }

    public async ValueTask DisposeAsync()
    {
        Store.Changed -= Changed;
        Navigation.LocationChanged -= LocationChanged;
        await lifetime.CancelAsync();
        if (refreshLoop is not null) await refreshLoop;
        if (module is not null)
        {
            try { await module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        lifetime.Dispose();
    }
}
