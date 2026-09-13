using HVO.AgentControl.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace HVO.AgentControl.Services;

[Authorize]
public sealed class ActivityHub(ControlStore store) : Hub
{
    public async Task<object> Resume(long sequence)
    {
        var snapshot = await store.Snapshot();
        return new { snapshot.Sequence, resnapshot = sequence != snapshot.Sequence };
    }
}

public sealed class ActivityPublisher(ControlStore store, IHubContext<ActivityHub> hub) : BackgroundService
{
    private int dirty = 1;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Changed() => Interlocked.Exchange(ref dirty, 1);
        store.Changed += Changed;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                if (Interlocked.Exchange(ref dirty, 0) != 0)
                    await hub.Clients.All.SendAsync("Changed", cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { store.Changed -= Changed; }
    }
}
