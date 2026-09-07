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
                await RunIteration(() => store.CoordinationSupervisionTick(), detail => store.RecoverActiveCoordination(detail), logger, stoppingToken);
                await RunIteration(() => store.CoordinationTick(), detail => store.RecoverActiveCoordination(detail), logger, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static async Task RunIteration(Func<Task> tick, Func<string, Task> recover, ILogger logger, CancellationToken token)
    {
        try { await tick(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Coordinator tick failed ({Category}); committed delivery state retained", ex.GetType().Name);
            // Storage may be unavailable while recording recovery. Neither failure may kill the loop.
            try { await recover("Coordinator scheduling failed (" + ex.GetType().Name + ")."); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception recoveryError)
            { logger.LogWarning("Coordinator recovery could not be persisted ({Category}); the monitoring loop will try again", recoveryError.GetType().Name); }
        }
    }
}
