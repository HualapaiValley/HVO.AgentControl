using System.Collections.Generic;
using HVO.AgentControl.Telemetry;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class TelemetryCalculatorTests
{
    private const string SessA = "session-a";
    private const string SessB = "session-b";

    private static RuntimeTelemetrySample Sample(long at, long? cpu, long? mem = null, long? limit = null, double? quota = null, string platform = "linux", string identity = SessA)
        => new(at, identity, cpu, mem, limit, quota, platform, HostLogicalCores: null);

    private static RuntimeTelemetrySample LinuxV2(long at, long cpu, long mib) =>
        TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["os"] = TelemetryParser.PlatformLinux,
            ["accumCpuUsec"] = cpu.ToString(),
            ["cpuQuotaV2"] = "200000 100000",
            ["memoryCurrentV2"] = (mib * 1024 * 1024).ToString(),
            ["memoryLimitV2"] = (2 * mib * 1024 * 1024).ToString()
        }, at, SessA);

    [Fact]
    public void FirstSampleRequiresSecondAndPreservesRawMemory()
    {
        var current = Sample(1000, cpu: 1_000_000, mem: 5120, limit: 10240, quota: 2);
        var result = TelemetryCalculator.Compute(previous: null, current);

        Assert.Equal(TelemetryState.NeedsSecondSample, result.State);
        Assert.Null(result.CpuQuotaPercent);
        Assert.Null(result.CpuCoreUsage);
        Assert.Null(result.MemoryPercent);
        Assert.Equal(5120, result.MemoryBytes);
        Assert.Equal(10240, result.MemoryLimitBytes);
        Assert.False(result.IsUsable);
    }

    [Fact]
    public void StaticQuotaSnapshotIsCapacityNotUtilization()
    {
        var snapshot = TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["os"] = TelemetryParser.PlatformLinux,
            ["cpuQuotaV2"] = "200000 100000",
            ["memoryLimitV2"] = "1073741824"
        }, observedAt: 1000, identity: SessA);

        var result = TelemetryCalculator.Compute(previous: null, snapshot);
        Assert.Equal(TelemetryState.NeedsSecondSample, result.State);
        Assert.Null(result.CpuQuotaPercent);
    }

    [Fact]
    public void IdentityChangeAcrossSamplesNeverFabricates()
    {
        var old = Sample(1000, cpu: 1_000_000, quota: 2);
        var fresh = Sample(3000, cpu: 3_000_000, quota: 2, identity: SessB);
        var result = TelemetryCalculator.Compute(old, fresh);

        Assert.Equal(TelemetryState.IdentityChanged, result.State);
        Assert.Null(result.CpuQuotaPercent);
    }

    [Theory]
    [InlineData(3000, 3000)]
    [InlineData(3000, 2000)]
    public void NonPositiveWindowIsInvalid(long oldAt, long newAt)
    {
        var old = Sample(oldAt, cpu: 1_000_000, quota: 2);
        var fresh = Sample(newAt, cpu: 3_000_000, quota: 2);
        var result = TelemetryCalculator.Compute(old, fresh);

        Assert.Equal(TelemetryState.InvalidElapsed, result.State);
        Assert.Null(result.CpuQuotaPercent);
        Assert.Null(result.CpuCoreUsage);
    }

    [Fact]
    public void GoingBackwardsCumulativeCounterIsResetNotNegativeUsage()
    {
        var old = Sample(1000, cpu: 5_000_000, quota: 2);
        var fresh = Sample(3000, cpu: 2_000_000, quota: 2);
        var result = TelemetryCalculator.Compute(old, fresh);

        Assert.Equal(TelemetryState.CounterReset, result.State);
        Assert.Null(result.CpuQuotaPercent);
    }

    [Fact]
    public void StaleWindowPreservesRawValuesButSuppressesPercentages()
    {
        var options = new TelemetryCalculatorOptions(MaxStaleMs: 200);
        var old = Sample(1000, cpu: 1_000_000, mem: 5120, limit: 10240, quota: 2);
        var fresh = Sample(1300, cpu: 2_000_000, mem: 6144, limit: 10240, quota: 2);
        var result = TelemetryCalculator.Compute(old, fresh, options);

        Assert.Equal(TelemetryState.Stale, result.State);
        Assert.Null(result.CpuQuotaPercent);
        Assert.Null(result.CpuCoreUsage);
        Assert.Null(result.MemoryPercent);
        Assert.Equal(6144, result.MemoryBytes);
        Assert.Equal(10240, result.MemoryLimitBytes);
    }

    [Fact]
    public void MissingCpuCounterIsNotZeroUsage()
    {
        var old = Sample(1000, cpu: null, quota: 2);
        var fresh = Sample(3000, cpu: null, quota: 2);
        var result = TelemetryCalculator.Compute(old, fresh);

        Assert.Equal(TelemetryState.CpuUnavailable, result.State);
        Assert.Null(result.CpuQuotaPercent);
        Assert.Null(result.CpuCoreUsage);
    }

    [Fact]
    public void UnlimitedQuotaMeansQuotaPercentUnknownButCoreUsageReported()
    {
        var first = TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["os"] = TelemetryParser.PlatformLinux,
            ["accumCpuUsec"] = "1000000",
            ["cpuQuotaV2"] = "max 100000",
            ["memoryCurrentV2"] = "536870912",
            ["memoryLimitV2"] = "1073741824"
        }, 1000, SessA);
        var second = first with { ObservedAt = 3000, CumulativeCpuUsec = 3_000_000 };
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(TelemetryState.QuotaUnavailable, result.State);
        Assert.Null(result.CpuQuotaPercent);
        Assert.Equal(1.0, result.CpuCoreUsage!.Value);
    }

    [Fact]
    public void MacOsHasNoQuotaContractAndNeverGetsFabricatedPercent()
    {
        var facts = new Dictionary<string, string>
        {
            ["os"] = "Darwin",
            ["accumCpuUsec"] = "3000000",
            ["memoryBytes"] = "17179869184"
        };
        var first = TelemetryParser.Parse(facts, 1000, SessA);
        var second = first with { ObservedAt = 3000, CumulativeCpuUsec = 5_000_000 };
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(TelemetryState.QuotaUnavailable, result.State);
        Assert.Null(result.CpuQuotaPercent);
        Assert.Null(result.MemoryPercent);
        Assert.Null(result.MemoryBytes);
        Assert.NotNull(result.CpuCoreUsage);
    }

    [Fact]
    public void MissingMemoryLimitKeepsCpuPercentAndSuppressesMemoryPercent()
    {
        var first = Sample(1000, cpu: 1_000_000, mem: 5120, limit: null, quota: 2);
        var second = Sample(3000, cpu: 3_000_000, mem: 6144, limit: null, quota: 2);
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(TelemetryState.MemoryLimitUnknown, result.State);
        Assert.NotNull(result.CpuQuotaPercent);
        Assert.Null(result.MemoryPercent);
        Assert.Equal(6144, result.MemoryBytes);
    }

    [Fact]
    public void NominalWindowComputesQuotaPercentDistinctFromCoreUsage()
    {
        var first = Sample(1000, cpu: 1_000_000, mem: 536_870_912, limit: 1_073_741_824, quota: 4);
        var second = Sample(3000, cpu: 3_000_000, mem: 536_870_912, limit: 1_073_741_824, quota: 4);
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(TelemetryState.OK, result.State);
        Assert.Equal(2000, result.CpuWindowMs);
        Assert.Equal(1.0, result.CpuCoreUsage!.Value);
        Assert.Equal(25.0, result.CpuQuotaPercent!.Value);
        Assert.Equal(50.0, result.MemoryPercent!.Value);
        Assert.True(result.IsUsable);
    }

    [Fact]
    public void OverQuotaRatioIsPreservedAndExplained()
    {
        var first = Sample(1000, cpu: 0, mem: 5120, limit: 10240, quota: 2);
        var second = Sample(2000, cpu: 5_000_000, mem: 5120, limit: 10240, quota: 2);
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(250.0, result.CpuQuotaPercent!.Value);
        Assert.Equal(5.0, result.CpuCoreUsage!.Value);
        Assert.Contains("exceeds 100%", result.Note);
    }

    [Fact]
    public void ZeroCpuDeltaIsZeroPercentNotUnknown()
    {
        var first = Sample(1000, cpu: 4_000_000, mem: 5120, limit: 10240, quota: 2);
        var second = Sample(3000, cpu: 4_000_000, mem: 5120, limit: 10240, quota: 2);
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(TelemetryState.OK, result.State);
        Assert.Equal(0.0, result.CpuQuotaPercent!.Value);
    }

    [Fact]
    public void ParserReadsCgroupV2KeysAndComposesWithCalculator()
    {
        var first = LinuxV2(1000, cpu: 1_000_000, mib: 512);
        var second = LinuxV2(3000, cpu: 3_000_000, mib: 512);
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(2.0, first.QuotaCores);
        Assert.Equal(1_073_741_824L, first.MemoryLimitBytes);
        Assert.Equal(536_870_912L, first.MemoryBytes);
        Assert.Equal(TelemetryState.OK, result.State);
        Assert.Equal(50.0, result.CpuQuotaPercent!.Value);
        Assert.Equal(50.0, result.MemoryPercent!.Value);
    }

    [Fact]
    public void ParserTreatsMaxSentinelAndUnlimitedV1LimitAsUnknown()
    {
        var unlimited = TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["os"] = TelemetryParser.PlatformLinux,
            ["cpuQuotaV2"] = "max 100000",
            ["memoryLimitV2"] = "max"
        }, 1000, SessA);
        Assert.Null(unlimited.QuotaCores);
        Assert.Null(unlimited.MemoryLimitBytes);

        var v1 = TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["os"] = TelemetryParser.PlatformLinux,
            ["cpuQuotaMicrosV1"] = "400000",
            ["cpuPeriodMicrosV1"] = "100000",
            ["memoryLimitV1"] = "9223372036854771712",
            ["memoryUsageV1"] = "1000"
        }, 1000, SessA);
        Assert.Equal(4.0, v1.QuotaCores);
        Assert.Null(v1.MemoryLimitBytes);
        Assert.Equal(1000, v1.MemoryBytes);

        Assert.Equal(TelemetryState.MemoryLimitUnknown, TelemetryCalculator.Compute(
            v1 with { ObservedAt = 1000, CumulativeCpuUsec = 0 },
            v1 with { ObservedAt = 3000, CumulativeCpuUsec = 2_000_000 }).State);
    }

    [Fact]
    public void ExplicitV2UnlimitedMemoryIsAuthoritativeOverMixedV1Facts()
    {
        var parsed = TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["os"] = TelemetryParser.PlatformLinux,
            ["memoryLimitV2"] = "max",
            ["memoryLimitV1"] = "1073741824"
        }, 1000, SessA);

        Assert.Null(parsed.MemoryLimitBytes);
    }

    [Fact]
    public void UnavailableV2FactsFallBackToValidV1LimitsButMalformedV2RemainsAuthoritative()
    {
        var fallback = TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["os"] = TelemetryParser.PlatformLinux,
            ["cpuQuotaV2"] = "unknown",
            ["memoryLimitV2"] = "",
            ["cpuQuotaMicrosV1"] = "200000",
            ["cpuPeriodMicrosV1"] = "100000",
            ["memoryLimitV1"] = "4294967296"
        }, 1000, SessA);
        Assert.Equal(2.0, fallback.QuotaCores);
        Assert.Equal(4_294_967_296L, fallback.MemoryLimitBytes);

        var conflicting = TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["os"] = TelemetryParser.PlatformLinux,
            ["cpuQuotaV2"] = "invalid 100000",
            ["memoryLimitV2"] = "invalid",
            ["cpuQuotaMicrosV1"] = "200000",
            ["cpuPeriodMicrosV1"] = "100000",
            ["memoryLimitV1"] = "4294967296"
        }, 1000, SessA);
        Assert.Null(conflicting.QuotaCores);
        Assert.Null(conflicting.MemoryLimitBytes);
    }

    [Fact]
    public void RawLinuxCgroupFactsAreIgnoredOutsideLinuxContract()
    {
        var facts = new Dictionary<string, string>
        {
            ["cpuQuotaV2"] = "200000 100000",
            ["memoryCurrentV2"] = "512",
            ["memoryLimitV2"] = "1024",
            ["cpuQuotaMicrosV1"] = "100000",
            ["cpuPeriodMicrosV1"] = "100000",
            ["memoryLimitV1"] = "2048"
        };

        var unknown = TelemetryParser.Parse(facts, 1000, SessA);
        var macos = TelemetryParser.Parse(new Dictionary<string, string>(facts) { ["os"] = "Darwin" }, 1000, SessA);

        Assert.Equal(TelemetryParser.PlatformUnknown, unknown.Platform);
        Assert.Null(unknown.QuotaCores);
        Assert.Null(unknown.MemoryBytes);
        Assert.Null(unknown.MemoryLimitBytes);
        Assert.Equal(TelemetryParser.PlatformMacos, macos.Platform);
        Assert.Null(macos.QuotaCores);
        Assert.Null(macos.MemoryBytes);
        Assert.Null(macos.MemoryLimitBytes);
    }

    [Fact]
    public void ParserIgnoresNonNumericAndUnknownKeys()
    {
        var parsed = TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["os"] = TelemetryParser.PlatformLinux,
            ["cpuSetV2"] = "0-3",
            ["executionScope"] = "container",
            ["notAKey"] = "present"
        }, 1000, SessA);

        Assert.Null(parsed.CumulativeCpuUsec);
        Assert.Null(parsed.MemoryBytes);
        Assert.Null(parsed.QuotaCores);
        Assert.Equal("linux", parsed.Platform);
    }

    [Fact]
    public void ParserAcceptsTabSeparatedProbeLines()
    {
        var sample = TelemetryParser.ParseLines("os\tLinux\naccumCpuUsec\t12345\ncpuQuotaV2\t100000 200000\nlogicalCores\t16\n", 5000, SessA);

        Assert.Equal(5000, sample.ObservedAt);
        Assert.Equal(SessA, sample.Identity);
        Assert.Equal(12345, sample.CumulativeCpuUsec);
        Assert.Equal(0.5, sample.QuotaCores);
        Assert.Equal(16, sample.HostLogicalCores);
    }

    [Fact]
    public void ParserRejectsNegativeCpuAndMemoryValues()
    {
        var parsed = TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["accumCpuUsec"] = "-1",
            ["memoryCurrentBytes"] = "-2",
            ["memoryLimitBytes"] = "-3",
            ["cpuQuotaMicrosV1"] = "-4",
            ["cpuPeriodMicrosV1"] = "100000"
        }, 1000, SessA);

        Assert.Null(parsed.CumulativeCpuUsec);
        Assert.Null(parsed.MemoryBytes);
        Assert.Null(parsed.MemoryLimitBytes);
        Assert.Null(parsed.QuotaCores);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void ParserRejectsNonFiniteQuota(string rawQuota)
    {
        var parsed = TelemetryParser.Parse(new Dictionary<string, string>
        {
            ["cpuQuotaCores"] = rawQuota
        }, 1000, SessA);

        Assert.Null(parsed.QuotaCores);
    }

    [Fact]
    public void CalculatorRejectsInvalidDirectRecordInputs()
    {
        var invalid = Sample(1000, cpu: -1, mem: -2, limit: -3, quota: double.NaN);
        var result = TelemetryCalculator.Compute(null, invalid);

        Assert.Equal(TelemetryState.InvalidInput, result.State);
        Assert.Null(result.CpuQuotaPercent);
        Assert.Null(result.CpuCoreUsage);
        Assert.Null(result.MemoryPercent);
    }

    [Fact]
    public void CalculatorRejectsNonFiniteDirectQuota()
    {
        var first = Sample(1000, cpu: 0, mem: 1, limit: 2, quota: double.PositiveInfinity);
        var second = Sample(2000, cpu: 1_000, mem: 1, limit: 2, quota: double.PositiveInfinity);
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(TelemetryState.InvalidInput, result.State);
        Assert.Null(result.CpuQuotaPercent);
    }

    [Fact]
    public void QuotaChangeSuppressesCurrentQuotaNormalization()
    {
        var first = Sample(1000, cpu: 0, mem: 1, limit: 2, quota: 1);
        var second = Sample(2000, cpu: 1_000_000, mem: 1, limit: 2, quota: 2);
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(TelemetryState.QuotaUnavailable, result.State);
        Assert.Null(result.CpuQuotaPercent);
        Assert.Equal(1.0, result.CpuCoreUsage);
        Assert.Contains("changed between samples", result.Note);
    }

    [Fact]
    public void QuotaBecomingAvailableDoesNotNormalizeTheWholeInterval()
    {
        var first = Sample(1000, cpu: 0, mem: 1, limit: 2, quota: null);
        var second = Sample(2000, cpu: 1_000_000, mem: 1, limit: 2, quota: 2);
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(TelemetryState.QuotaUnavailable, result.State);
        Assert.Null(result.CpuQuotaPercent);
        Assert.Equal(1.0, result.CpuCoreUsage);
        Assert.Contains("changed between samples", result.Note);
    }

    [Fact]
    public void QuotaBecomingUnavailableDoesNotNormalizeTheWholeInterval()
    {
        var first = Sample(1000, cpu: 0, mem: 1, limit: 2, quota: 2);
        var second = Sample(2000, cpu: 1_000_000, mem: 1, limit: 2, quota: null);
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(TelemetryState.QuotaUnavailable, result.State);
        Assert.Null(result.CpuQuotaPercent);
        Assert.Equal(1.0, result.CpuCoreUsage);
        Assert.Contains("changed between samples", result.Note);
    }

    [Fact]
    public void ExtremeValidSamplesRemainFiniteWithoutOverflow()
    {
        var first = Sample(0, cpu: 0, mem: long.MaxValue, limit: long.MaxValue, quota: 1);
        var second = Sample(long.MaxValue, cpu: long.MaxValue, mem: long.MaxValue, limit: long.MaxValue, quota: 1);
        var result = TelemetryCalculator.Compute(first, second, new TelemetryCalculatorOptions(long.MaxValue));

        Assert.Equal(TelemetryState.OK, result.State);
        Assert.True(double.IsFinite(result.CpuCoreUsage!.Value));
        Assert.True(double.IsFinite(result.CpuQuotaPercent!.Value));
        Assert.True(double.IsFinite(result.MemoryPercent!.Value));
    }

    [Fact]
    public void TinyQuotaThatOverflowsRatioIsExplicitlyNonFinite()
    {
        var first = Sample(1000, cpu: 0, mem: 1, limit: 2, quota: double.Epsilon);
        var second = Sample(2000, cpu: long.MaxValue, mem: 1, limit: 2, quota: double.Epsilon);
        var result = TelemetryCalculator.Compute(first, second);

        Assert.Equal(TelemetryState.NonFiniteResult, result.State);
        Assert.Null(result.CpuQuotaPercent);
    }

    [Fact]
    public void NegativeTimestampIsExplicitlyInvalidInsteadOfOverflowing()
    {
        var first = Sample(long.MinValue, cpu: 0, quota: 1);
        var second = Sample(long.MaxValue, cpu: 1, quota: 1);
        var result = TelemetryCalculator.Compute(first, second, new TelemetryCalculatorOptions(long.MaxValue));

        Assert.Equal(TelemetryState.InvalidInput, result.State);
        Assert.Null(result.CpuQuotaPercent);
    }
}
