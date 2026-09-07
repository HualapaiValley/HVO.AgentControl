using HVO.AgentControl.Core;
using HVO.AgentControl.Telemetry;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class TelemetryHistoryTests
{
    [Fact]
    public async Task CalculatedTelemetryIsDurableAndReadNewestFirst()
    {
        string data, secrets;
        string runtimeId;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            runtimeId = (await app.Store.SaveRuntime(PersistenceTests.Profile())).Id;
            await app.Store.RecordTelemetry(runtimeId, Sample(2000, TelemetryState.NeedsSecondSample));
            await app.Store.RecordTelemetry(runtimeId, Sample(1000, TelemetryState.OK));
            var history = await app.Store.TelemetryHistory(runtimeId);
            Assert.Collection(history,
                newest => { Assert.Equal(2000, newest.ObservedAt); Assert.Equal("NeedsSecondSample", newest.State); Assert.Null(newest.CpuQuotaPercent); },
                oldest => { Assert.Equal(1000, oldest.ObservedAt); Assert.Equal("OK", oldest.State); Assert.Equal(75.5, oldest.CpuQuotaPercent); });
        }
        await using var restarted = new TestApp(data, secrets);
        var persisted = await restarted.Store.TelemetryHistory(runtimeId);
        Assert.Equal(2, persisted.Count);
        Assert.Equal(2000, persisted[0].ObservedAt);
    }

    [Fact]
    public async Task TelemetryHistoryIsBoundedPerRuntimeAndRemovedWithRegistration()
    {
        await using var app = new TestApp();
        var runtime = await app.Store.SaveRuntime(PersistenceTests.Profile());
        for (var observedAt = 0; observedAt <= 200; observedAt++)
            await app.Store.RecordTelemetry(runtime.Id, Sample(observedAt, TelemetryState.OK));

        var history = await app.Store.TelemetryHistory(runtime.Id, 200);
        Assert.Equal(200, history.Count);
        Assert.Equal(200, history[0].ObservedAt);
        Assert.Equal(1, history[^1].ObservedAt);
        await app.Store.DeleteRuntime(runtime.Id, new(Guid.NewGuid().ToString(), runtime.Revision));
        Assert.Equal(0, await app.Store.Read(db => Task.FromResult(db.TelemetryHistory.Count())));
    }

    private static RuntimeTelemetry Sample(long observedAt, TelemetryState state) => new(
        state, observedAt, state == TelemetryState.OK ? 75.5 : null, 1.5, 30, 2, 1000, 1024, 4096, "fixture");
}
