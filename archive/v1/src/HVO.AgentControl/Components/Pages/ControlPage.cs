using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.AgentControl.Components.Pages;

// Each page observes the same durable snapshot; navigation never controls remote lifetimes.
public abstract class ControlPage : ComponentBase, IAsyncDisposable
{
    [Inject] protected ControlStore Store { get; set; } = default!;
    [Inject] protected AuthenticationStateProvider Authentication { get; set; } = default!;
    [Inject] protected NavigationManager Navigation { get; set; } = default!;
    protected ControlSnapshot? snapshot;
    protected string? error, notice;
    protected bool busy;
    protected readonly CancellationTokenSource lifetime = new();
    private Task? refreshLoop;
    private int dirty = 1;

    protected override async Task OnInitializedAsync()
    {
        await Refresh();
        Store.Changed += Changed;
        refreshLoop = RefreshLoop();
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
    protected async Task Refresh()
    {
        try { snapshot = await Store.Snapshot(); await SnapshotChanged(); }
        catch (Exception ex) { error = RuntimeSupervisor.SafeError(ex); }
    }
    protected virtual Task SnapshotChanged() => Task.CompletedTask;
    protected async Task Execute(Func<Task> action)
    {
        if (busy) return;
        busy = true; error = null; notice = null;
        try
        {
            if ((await Authentication.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated != true)
                throw new ControlException("Sign in to perform this operation.", 401);
            await action(); await Refresh();
        }
        catch (Exception ex) { error = RuntimeSupervisor.SafeError(ex); }
        finally { busy = false; }
    }
    protected Task QueueAction(string id, string action) => Execute(async () => { await Store.EditQueue(id, action); });
    protected string RuntimeName(string id) => snapshot?.Runtimes.FirstOrDefault(x => x.Id == id)?.Name ?? id;
    protected static string ConversationUrl(string id) => "/?worker=" + Uri.EscapeDataString(id);
    protected static string WorkerSetupUrl(string id) => "/workers?runtime=" + Uri.EscapeDataString(id) + "&new=true";
    protected static string Age(long? time) => time is null ? "never" : TimeSpan.FromMilliseconds(Math.Max(0, ControlStore.Now - time.Value)) switch
    { { TotalSeconds: < 5 } => "just now", { TotalMinutes: < 1 } age => $"{age.Seconds}s ago", { TotalHours: < 1 } age => $"{(int)age.TotalMinutes}m ago", var age => $"{(int)age.TotalHours}h ago" };

    public virtual async ValueTask DisposeAsync()
    {
        Store.Changed -= Changed; await lifetime.CancelAsync();
        if (refreshLoop is not null) await refreshLoop;
        lifetime.Dispose();
    }
}
