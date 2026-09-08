using HVO.AgentControl.Infrastructure;

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
                // Scheduling owns recovery for the specific failing run. An unscoped
                // heartbeat/storage failure must not back off healthy workgroups.
                await RunIteration(() => store.CoordinationSupervisionTick(), logger, stoppingToken);
                await RunIteration(() => store.CoordinationTick(), logger, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static async Task RunIteration(Func<Task> tick, ILogger logger, CancellationToken token)
    {
        try { await tick(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Coordinator tick failed ({Category}); committed delivery state retained", ex.GetType().Name);
        }
    }
}
