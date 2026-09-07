using HVO.AgentControl.Core;
using HVO.AgentControl.Ssh;
using HVO.AgentControl.Telemetry;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RuntimeTelemetryProjectionTests
{
    [Fact]
    public void ProjectsAuthenticatedCapabilitySnapshotWithoutFabricatingUtilization()
    {
        var snapshot = new CapabilitySnapshot(1234, "probe", "/work", new Dictionary<string, string>
        {
            ["os"] = "Linux",
            ["cpuQuotaV2"] = "200000 100000",
            ["memoryCurrentV2"] = "536870912",
            ["memoryLimitV2"] = "1073741824"
        });

        var projection = RuntimeTelemetryProjection.FromCapabilities(Json.Write(snapshot), "runtime:7");

        Assert.NotNull(projection);
        Assert.Equal(1234, projection.Sample.ObservedAt);
        Assert.Equal("runtime:7", projection.Sample.Identity);
        Assert.Equal(2, projection.Sample.QuotaCores);
        Assert.Equal(536_870_912, projection.Sample.MemoryBytes);
        Assert.Equal(1_073_741_824, projection.Sample.MemoryLimitBytes);
        Assert.Equal(TelemetryState.NeedsSecondSample, projection.Result.State);
        Assert.Null(projection.Result.CpuQuotaPercent);
        Assert.Null(projection.Result.MemoryPercent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"observedAt\":0,\"source\":\"probe\",\"scope\":\"/work\",\"facts\":{}}")]
    public void MissingOrMalformedSnapshotsAreUnavailable(string json)
    {
        Assert.Null(RuntimeTelemetryProjection.FromCapabilities(json, "runtime:1"));
    }

    [Fact]
    public void MissingResourceFactsRemainExplicitlyUnknown()
    {
        var snapshot = new CapabilitySnapshot(1234, "probe", "/work", new Dictionary<string, string>
        {
            ["os"] = "Darwin",
            ["memoryBytes"] = "17179869184"
        });

        var projection = RuntimeTelemetryProjection.FromCapabilities(Json.Write(snapshot), "runtime:1");

        Assert.NotNull(projection);
        Assert.Null(projection.Sample.MemoryBytes);
        Assert.Null(projection.Sample.MemoryLimitBytes);
        Assert.Null(projection.Sample.QuotaCores);
        Assert.Equal(TelemetryState.NeedsSecondSample, projection.Result.State);
    }
}
