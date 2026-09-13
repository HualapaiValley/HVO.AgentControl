using System.Threading.Channels;
using HVO.AgentControl.Ssh;
using HVO.AgentControl.Telemetry;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RuntimeTelemetrySamplerTests
{
    private static RuntimeTelemetrySample Sample(long time, long cpu) =>
        new(time, "connection-one", cpu, 1024, 4096, 2, "linux", null);

    [Fact]
    public async Task SlowProbeDoesNotBlockCallerAndShutdownDiscardsPendingObservation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = 0;
        var sampler = new RuntimeTelemetrySampler(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Sample(1000, 10);
        }, _ => { Interlocked.Increment(ref recorded); return Task.CompletedTask; }, _ => { }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sampler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, recorded);
    }

    [Fact]
    public async Task FailureAndUnsupportedObservationResetCounterWithoutStoppingSampling()
    {
        var observations = Channel.CreateUnbounded<object>();
        var results = Channel.CreateUnbounded<RuntimeTelemetry>();
        var failures = Channel.CreateUnbounded<Exception>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var sampler = new RuntimeTelemetrySampler(async token =>
        {
            var item = await observations.Reader.ReadAsync(token);
            if (item is Exception error) throw error;
            return item as RuntimeTelemetrySample;
        }, result => { results.Writer.TryWrite(result); return Task.CompletedTask; },
            error => failures.Writer.TryWrite(error), deadline.Token, TimeSpan.FromMilliseconds(1));
        observations.Writer.TryWrite(Sample(1000, 1_000_000));
        Assert.Equal(TelemetryState.NeedsSecondSample, (await results.Reader.ReadAsync(deadline.Token)).State);
        observations.Writer.TryWrite(Sample(31000, 31_000_000));
        var usage = await results.Reader.ReadAsync(deadline.Token);
        Assert.Equal(TelemetryState.OK, usage.State);
        Assert.Equal(50, usage.CpuQuotaPercent);
        observations.Writer.TryWrite(new IOException("Probe unavailable"));
        Assert.IsType<IOException>(await failures.Reader.ReadAsync(deadline.Token));
        observations.Writer.TryWrite(Sample(61000, 61_000_000));
        Assert.Equal(TelemetryState.NeedsSecondSample, (await results.Reader.ReadAsync(deadline.Token)).State);
        observations.Writer.TryWrite("unsupported scope");
        observations.Writer.TryWrite(Sample(91000, 91_000_000));
        Assert.Equal(TelemetryState.NeedsSecondSample, (await results.Reader.ReadAsync(deadline.Token)).State);
    }

    [Fact]
    public async Task StorageFailureDoesNotTerminateSamplerOrReuseUnrecordedCounter()
    {
        var failures = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new TaskCompletionSource<RuntimeTelemetry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        await using var sampler = new RuntimeTelemetrySampler(_ => Task.FromResult<RuntimeTelemetrySample?>(Sample(1000, 1000)), result =>
        {
            if (Interlocked.Increment(ref writes) == 1) throw new IOException("Store temporarily unavailable");
            saved.TrySetResult(result); return Task.CompletedTask;
        }, _ => failures.TrySetResult(), CancellationToken.None, TimeSpan.FromMilliseconds(1));
        await failures.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TelemetryState.NeedsSecondSample, (await saved.Task.WaitAsync(TimeSpan.FromSeconds(10))).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("os\tDarwin\naccumCpuUsec\t1000")]
    [InlineData("scope\thost\nos\tLinux\naccumCpuUsec\t1000")]
    public void UnsupportedScopeNeverProducesContainerUtilization(string output) => Assert.Null(TelemetryProbe.Parse(output, "connection"));

    [Fact]
    public void VerifiedProbeKeepsQuotaAndCumulativeUnits()
    {
        var sample = Assert.IsType<RuntimeTelemetrySample>(TelemetryProbe.Parse(
            "scope\tcontainer-cgroup-v2\nos\tLinux\naccumCpuUsec\t1000000\ncpuQuotaV2\t200000 100000\nmemoryCurrentV2\t1024\nmemoryLimitV2\t4096", "connection"));
        Assert.Equal(1_000_000, sample.CumulativeCpuUsec);
        Assert.Equal(2, sample.QuotaCores);
        Assert.Equal(1024, sample.MemoryBytes);
        Assert.Equal("connection", sample.Identity);
    }
}
