namespace HVO.AgentControl.Telemetry;

// One sampler belongs to one transport connection. Slow probes never block prompt dispatch,
// probes never overlap, and a reconnect starts with no previous CPU counter.
public sealed class RuntimeTelemetrySampler : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime;
    private readonly Task loop;

    public RuntimeTelemetrySampler(Func<CancellationToken, Task<RuntimeTelemetrySample?>> probe,
        Func<RuntimeTelemetry, Task> record, Action<Exception> reportFailure,
        CancellationToken cancellationToken, TimeSpan? interval = null)
    {
        var period = interval ?? TimeSpan.FromSeconds(30);
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loop = Task.Run(async () =>
        {
            RuntimeTelemetrySample? previous = null;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    var sample = await probe(lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (sample is not null) await record(TelemetryCalculator.Compute(previous, sample));
                    previous = sample;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    previous = null;
                    reportFailure(ex);
                }
                try { await Task.Delay(period, lifetime.Token); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            }
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await loop;
        lifetime.Dispose();
    }
}
