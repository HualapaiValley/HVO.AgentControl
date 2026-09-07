using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Core;

namespace HVO.AgentControl.Services;

public sealed class CoordinatorService(ControlStore store, ILogger<CoordinatorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await store.CoordinationTick(); }
                catch (ControlException ex) { await store.PauseActiveCoordination(ex.Message); }
                catch (Exception ex) { logger.LogWarning("Coordinator tick failed ({Category}); committed delivery state retained", ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
