using HVO.AgentControl.Ssh;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class EffectiveResourceTests
{
    private static CapabilitySnapshot Parse(params (string Key, string Value)[] facts) =>
        CapabilityProbe.Parse(string.Join('\n', facts.Select(f => $"{f.Key}\t{f.Value}")), "/work");

    private static Dictionary<string, string> Facts(params (string Key, string Value)[] facts) =>
        facts.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

    [Fact]
    public void BetaContainerYieldsTwoEffectiveCpuAndFourGiB()
    {
        var probe = Parse(
            ("logicalCores", "8"),
            ("memoryKiB", "24608548"),
            ("memoryLimitV2", "4294967296"),
            ("cpuQuotaV2", "200000 100000"),
            ("cpuSetV2", "0-7"),
            ("executionScope", "container"));
        Assert.Equal("2", probe.Facts["effectiveCpuCores"]);
        Assert.Equal("4294967296", probe.Facts["effectiveMemoryBytes"]);
        Assert.Equal("8", probe.Facts["logicalCores"]);
        Assert.Equal("200000 100000", probe.Facts["cpuQuotaV2"]);
        Assert.Equal("4294967296", probe.Facts["memoryLimitV2"]);
    }

    [Fact]
    public void FractionalQuotaIsRespected()
    {
        var cpu = CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "8"), ("cpuQuotaV2", "150000 100000"), ("cpuSetV2", "0-7"), ("executionScope", "container")));
        Assert.Equal("1.5", cpu);
        Assert.Equal("0.5", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "8"), ("cpuQuotaV2", "50000 100000"), ("cpuSetV2", "0-7"), ("executionScope", "container"))));
    }

    [Fact]
    public void UnlimitedQuotaDefersToCpusetAndHostMinimum()
    {
        var cpu = CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "16"), ("cpuQuotaV2", "max 100000"), ("cpuSetV2", "0-3"), ("executionScope", "container")));
        Assert.Equal("4", cpu);
    }

    [Fact]
    public void V1QuotaAndCpusetAreNormalized()
    {
        var cpu = CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "8"), ("cpuSetV2", "0-7"),
            ("cpuQuotaMicrosV1", "200000"), ("cpuPeriodMicrosV1", "100000"), ("executionScope", "container")));
        Assert.Equal("2", cpu);
        Assert.Equal("8", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "8"), ("cpuQuotaMicrosV1", "-1"), ("cpuPeriodMicrosV1", "100000"), ("executionScope", "container"))));
    }

    [Fact]
    public void MalformedQuotaPeriodAndCpusetDoNotCrash()
    {
        Assert.Equal("4", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "4"), ("cpuQuotaV2", "abc 100000"), ("cpuSetV2", "0-7"), ("executionScope", "container"))));
        Assert.Equal("4", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "4"), ("cpuQuotaV2", "200000 0"), ("cpuSetV2", "0-7"), ("executionScope", "container"))));
        Assert.Equal("4", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "4"), ("cpuQuotaV2", "max 100000"), ("cpuSetV2", "0-nope"), ("executionScope", "container"))));
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(
            ("cpuQuotaV2", "max 100000"), ("cpuSetV2", "0-nope"), ("executionScope", "container"))));
    }

    [Fact]
    public void CpusetBoundariesCountCpus()
    {
        Assert.Equal("1", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "8"), ("cpuQuotaV2", "max 100000"), ("cpuSetV2", "0"), ("executionScope", "container"))));
        Assert.Equal("4", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "8"), ("cpuQuotaV2", "max 100000"), ("cpuSetV2", "0-3"), ("executionScope", "container"))));
        Assert.Equal("5", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "8"), ("cpuQuotaV2", "max 100000"), ("cpuSetV2", "0,4-7"), ("executionScope", "container"))));
    }

    [Fact]
    public void MemoryUnlimitedMarkerFallsBackToHostBytes()
    {
        var memory = CapabilityProbe.EffectiveMemoryBytes(Facts(
            ("memoryKiB", "1048576"), ("memoryLimitV2", "max"), ("executionScope", "container")));
        Assert.Equal("1073741824", memory);
    }

    [Fact]
    public void MemoryV1SentinelAndNegativeAreTreatedAsUnlimited()
    {
        Assert.Equal("1073741824", CapabilityProbe.EffectiveMemoryBytes(Facts(
            ("memoryKiB", "1048576"), ("memoryLimitV1", "9223372036854771712"), ("executionScope", "container"))));
        Assert.Equal("1073741824", CapabilityProbe.EffectiveMemoryBytes(Facts(
            ("memoryKiB", "1048576"), ("memoryLimitV1", "-1"), ("executionScope", "container"))));
    }

    [Fact]
    public void MemoryCgroupCapBelowHostWins()
    {
        Assert.Equal("209715200", CapabilityProbe.EffectiveMemoryBytes(Facts(
            ("memoryKiB", "2097152"), ("memoryLimitV2", "209715200"), ("executionScope", "container"))));
        Assert.Equal("209715200", CapabilityProbe.EffectiveMemoryBytes(Facts(
            ("memoryKiB", "2097152"), ("memoryLimitV1", "209715200"), ("executionScope", "container"))));
    }

    [Fact]
    public void NonContainerFallsBackToHostVisibleValues()
    {
        var cpu = CapabilityProbe.EffectiveCpuCores(Facts(("logicalCores", "6")));
        Assert.Equal("6", cpu);
        var memory = CapabilityProbe.EffectiveMemoryBytes(Facts(("memoryKiB", "1048576")));
        Assert.Equal("1073741824", memory);
    }

    [Fact]
    public void MacOSByteInputsAreNormalized()
    {
        var cpu = CapabilityProbe.EffectiveCpuCores(Facts(("logicalCores", "10")));
        Assert.Equal("10", cpu);
        var memory = CapabilityProbe.EffectiveMemoryBytes(Facts(("memoryBytes", "17179869184")));
        Assert.Equal("17179869184", memory);
    }

    [Fact]
    public void MacProbeShapeWithUnknownScopeAndEmptyCgroupKeysFallsBackToHost()
    {
        var probe = Parse(
            ("os", "Darwin"), ("logicalCores", "10"), ("memoryBytes", "17179869184"),
            ("executionScope", "unknown"),
            ("cpuQuotaV2", ""), ("cpuSetV2", ""), ("cpuSetV1", ""),
            ("memoryLimitV2", ""), ("memoryCurrentV2", ""),
            ("cpuQuotaMicrosV1", ""), ("cpuPeriodMicrosV1", ""), ("memoryLimitV1", ""));
        Assert.Equal("10", probe.Facts["effectiveCpuCores"]);
        Assert.Equal("17179869184", probe.Facts["effectiveMemoryBytes"]);
        Assert.Equal("10", probe.Facts["logicalCores"]);
        Assert.Equal("17179869184", probe.Facts["memoryBytes"]);
        foreach (var key in new[] { "cpuQuotaV2", "cpuSetV2", "cpuSetV1", "memoryLimitV2", "memoryCurrentV2", "cpuQuotaMicrosV1", "cpuPeriodMicrosV1", "memoryLimitV1" })
            Assert.Equal("unknown", probe.Facts[key]);
    }

    [Fact]
    public void OverflowAndUnknownInputsYieldUnknownNotFabricatedValues()
    {
        Assert.Equal("unknown", CapabilityProbe.EffectiveMemoryBytes(Facts(("memoryKiB", "9223372036854775807"))));
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(("logicalCores", "0"))));
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts()));
        Assert.Equal("unknown", CapabilityProbe.EffectiveMemoryBytes(Facts()));
    }

    [Fact]
    public void RawFactsAndTimestampArePreservedAlongsideEffectiveValues()
    {
        var probe = Parse(
            ("logicalCores", "8"), ("memoryKiB", "24608548"), ("cpuQuotaV2", "200000 100000"),
            ("cpuSetV2", "0-7"), ("memoryLimitV2", "4294967296"), ("executionScope", "container"));
        Assert.False(probe.ObservedAt == 0);
        Assert.Equal("probe", probe.Source);
        Assert.Equal("8", probe.Facts["logicalCores"]);
        Assert.Equal("24608548", probe.Facts["memoryKiB"]);
        Assert.Equal("2", probe.Facts["effectiveCpuCores"]);
        Assert.Equal("4294967296", probe.Facts["effectiveMemoryBytes"]);
    }

    [Fact]
    public void V1CpusetConstraintIsEnforced()
    {
        Assert.Equal("2", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "8"), ("cpuSetV1", "0-1"), ("executionScope", "container"))));
        Assert.Equal("4", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "16"), ("cpuQuotaMicrosV1", "-1"), ("cpuPeriodMicrosV1", "100000"),
            ("cpuSetV1", "0-3"), ("executionScope", "container"))));
        Assert.Equal("8", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "8"), ("cpuSetV1", "0-7"), ("executionScope", "container"))));
    }

    [Fact]
    public void UnavailableContainerLimitsAreUnknownNotHostCapacity()
    {
        var cpu = Parse(("logicalCores", "8"), ("memoryKiB", "1048576"), ("executionScope", "container"));
        Assert.Equal("unknown", cpu.Facts["effectiveCpuCores"]);
        Assert.Equal("unknown", cpu.Facts["effectiveMemoryBytes"]);
        Assert.Equal("8", cpu.Facts["logicalCores"]);
        Assert.Equal("1048576", cpu.Facts["memoryKiB"]);
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "8"), ("cpuQuotaV2", "unknown"), ("cpuSetV2", "unknown"), ("executionScope", "container"))));
        Assert.Equal("unknown", CapabilityProbe.EffectiveMemoryBytes(Facts(
            ("memoryKiB", "1048576"), ("memoryLimitV2", "unknown"), ("executionScope", "container"))));
    }

    [Fact]
    public void CpusetOverflowIsRejectedNotPlausible()
    {
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "64"), ("cpuSetV2", "0-9223372036854775807,0-9223372036854775807,0"),
            ("executionScope", "container"))));
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "64"), ("cpuSetV2", "0-9223372036854775807"), ("executionScope", "container"))));
    }

    [Fact]
    public void CpusetMalformedRangesAreUnknown()
    {
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "64"), ("cpuSetV2", "4-1"), ("executionScope", "container"))));
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "64"), ("cpuSetV2", "0,-1"), ("executionScope", "container"))));
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "64"), ("cpuSetV2", "abc"), ("executionScope", "container"))));
    }

    [Fact]
    public void CpusetOverlappingRangesAreUnknown()
    {
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "64"), ("cpuSetV2", "0-7,3-4"), ("executionScope", "container"))));
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "64"), ("cpuSetV2", "0,0"), ("executionScope", "container"))));
        Assert.Equal("unknown", CapabilityProbe.EffectiveCpuCores(Facts(
            ("logicalCores", "64"), ("cpuSetV2", "0-7,7-9"), ("executionScope", "container"))));
    }
}
