using System.Globalization;

namespace HVO.AgentControl.Telemetry;

/// <summary>
/// Parses unchanged CapabilityProbe-shaped tab-separated facts (and canonical telemetry keys)
/// into a typed <see cref="RuntimeTelemetrySample"/>. Values that are empty, negative, non-numeric, or
/// sentinel "unlimited" markers produce <see langword="null"/> and are preserved as unknown
/// rather than guessed. The input contracts are documented in docs/RUNTIME_TELEMETRY_CONTRACT.md.
/// </summary>
public static class TelemetryParser
{
    public const string PlatformLinux = "linux";
    public const string PlatformMacos = "macos";
    public const string PlatformUnknown = "unknown";

    // Linux cgroup v1 prints a near-max sentinel (e.g. 9223372036854771712) for "no memory limit".
    private const long CgroupV1UnlimitedMemoryThreshold = 1L << 62;

    /// <summary>
    /// Parses a fact dictionary. Both the static CapabilityProbe keys (cpuQuotaV2,
    /// cpuQuotaMicrosV1, cpuPeriodMicrosV1, memoryLimitV2, memoryLimitV1, memoryCurrentV2, os,
    /// logicalCores) and the canonical sampled keys documented in RUNTIME_TELEMETRY_CONTRACT.md are
    /// accepted; unknown keys are ignored.
    /// </summary>
    public static RuntimeTelemetrySample Parse(IReadOnlyDictionary<string, string> facts, long observedAt, string identity)
    {
        var cumulativeCpuUsec = ParseNonNegativeLong(facts, "accumCpuUsec", "cpuUsageUsec", "usageUsec", "procCpuUsec");
        var memoryBytes = ParseNonNegativeLong(facts, "memoryCurrentBytes", "memoryCurrentV2", "memoryUsageV1");
        var memoryLimit = ParseMemoryLimit(facts);
        var quotaCores = ParseQuotaCores(facts);
        var platform = ParsePlatform(facts);
        var hostLogicalCores = ParseInt(facts, "logicalCores");
        return new RuntimeTelemetrySample(observedAt, identity, cumulativeCpuUsec, memoryBytes, memoryLimit, quotaCores, platform, hostLogicalCores);
    }

    /// <summary>
    /// Parses tab-separated probe lines in the same emit() format CapabilityProbe produces.
    /// </summary>
    public static RuntimeTelemetrySample ParseLines(string output, long observedAt, string identity)
    {
        var facts = new Dictionary<string, string>();
        foreach (var line in output.Split('\n').Take(80))
        {
            var split = line.IndexOf('\t');
            if (split < 1) continue;
            facts[line[..split]] = line[(split + 1)..].Trim();
        }
        return Parse(facts, observedAt, identity);
    }

    private static long? ParseMemoryLimit(IReadOnlyDictionary<string, string> facts)
    {
        var canonical = ParseNonNegativeLong(facts, "memoryLimitBytes", "memoryLimitV2");
        if (canonical is not null) return canonical;
        var v1 = ParseNonNegativeLong(facts, "memoryLimitV1");
        return v1 is { } limit && limit < CgroupV1UnlimitedMemoryThreshold ? limit : null;
    }

    private static double? ParseQuotaCores(IReadOnlyDictionary<string, string> facts)
    {
        var canonical = ParseFiniteDouble(facts, "cpuQuotaCores");
        if (canonical is not null) return canonical > 0 ? canonical : null;

        if (facts.TryGetValue("cpuQuotaV2", out var cpuMax))
        {
            var tokens = cpuMax.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 2 && tokens[0] != "max")
            {
                if (double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var quota) &&
                    double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var period) &&
                    double.IsFinite(quota) && double.IsFinite(period) && quota > 0 && period > 0)
                {
                    var cores = quota / period;
                    return double.IsFinite(cores) && cores > 0 ? cores : null;
                }
            }
            return null;
        }

        var quotaUs = ParseNonNegativeLong(facts, "cpuQuotaMicrosV1");
        var periodUs = ParseNonNegativeLong(facts, "cpuPeriodMicrosV1");
        if (quotaUs is { } q && periodUs is { } p && q > 0 && p > 0) return q / (double)p;
        return null;
    }

    private static string ParsePlatform(IReadOnlyDictionary<string, string> facts)
    {
        if (!facts.TryGetValue("os", out var os)) return PlatformUnknown;
        if (os.Contains("darwin", StringComparison.OrdinalIgnoreCase)) return PlatformMacos;
        if (os.Contains("linux", StringComparison.OrdinalIgnoreCase)) return PlatformLinux;
        return PlatformUnknown;
    }

    private static long? ParseNonNegativeLong(IReadOnlyDictionary<string, string> facts, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (facts.TryGetValue(key, out var raw) &&
                long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value >= 0)
                return value;
        }
        return null;
    }

    private static int? ParseInt(IReadOnlyDictionary<string, string> facts, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (facts.TryGetValue(key, out var raw) &&
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value > 0)
                return value;
        }
        return null;
    }

    private static double? ParseFiniteDouble(IReadOnlyDictionary<string, string> facts, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (facts.TryGetValue(key, out var raw) &&
                double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                double.IsFinite(value))
                return value;
        }
        return null;
    }
}
