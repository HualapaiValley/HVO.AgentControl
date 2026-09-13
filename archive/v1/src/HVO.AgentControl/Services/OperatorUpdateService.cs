using HVO.AgentControl.Infrastructure;

namespace HVO.AgentControl.Services;

public sealed class OperatorUpdateService(ControlStore store, ILogger<OperatorUpdateService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await store.OperatorUpdateTick(); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                { logger.LogWarning("Operator update tick failed ({Category}); durable schedules will be retried", ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
