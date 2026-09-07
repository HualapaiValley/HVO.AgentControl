using System.Globalization;
using HVO.AgentControl.Core;

namespace HVO.AgentControl.Ssh;

public sealed record CapabilitySnapshot(long ObservedAt, string Source, string Scope, Dictionary<string, string> Facts);

public static class CapabilityProbe
{
    public static CapabilitySnapshot Parse(string output, string directory)
    {
        var facts = new Dictionary<string, string>();
        foreach (var line in output.Split('\n').Take(80))
        {
            var split = line.IndexOf('\t');
            if (split < 1 || split > 80) continue;
            var value = line[(split + 1)..].Trim();
            facts[line[..split]] = value.Length == 0 ? "unknown" : value[..Math.Min(value.Length, 500)];
        }
        facts["effectiveCpuCores"] = EffectiveCpuCores(facts);
        facts["effectiveMemoryBytes"] = EffectiveMemoryBytes(facts);
        return new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "probe", directory, facts);
    }

    /// <summary>Effective CPU quota/cpuset/host minimum normalized from raw probe facts; fractional quotas allowed. "unknown" when no container constraint is verifiable.</summary>
    public static string EffectiveCpuCores(Dictionary<string, string> facts)
    {
        var host = TryNonUnknown(facts, "logicalCores", out var rawLogical)
            ? ParsePositiveDecimal(rawLogical)
            : null;
        if (!IsContainerLike(facts))
            return host is { } hostOnly ? Format(hostOnly) : "unknown";

        var quota = CpuQuotaCores(facts);
        var setV2 = CpuSetCount(facts, "cpuSetV2");
        var setV1 = CpuSetCount(facts, "cpuSetV1");
        if (quota.Status == Constraint.Unavailable && setV2.Status == Constraint.Unavailable && setV1.Status == Constraint.Unavailable)
            return "unknown";

        var candidates = new List<decimal>();
        if (host is { } hostCores) candidates.Add(hostCores);
        if (quota.Status == Constraint.Constrained) candidates.Add(quota.Cores);
        if (setV2.Status == Constraint.Constrained) candidates.Add(setV2.Count);
        if (setV1.Status == Constraint.Constrained) candidates.Add(setV1.Count);
        return candidates.Count == 0 ? "unknown" : Format(candidates.Min());
    }

    /// <summary>Effective memory in bytes normalized from host-visible memory and the cgroup limit; "unknown" when no container limit is verifiable.</summary>
    public static string EffectiveMemoryBytes(Dictionary<string, string> facts)
    {
        var host = HostMemoryBytes(facts);
        if (!IsContainerLike(facts))
            return host is { } hostOnly ? Format(hostOnly) : "unknown";

        var limit = MemoryLimitBytes(facts);
        if (limit.Status == Constraint.Unavailable) return "unknown";

        var candidates = new List<long>();
        if (host is { } hostBytes) candidates.Add(hostBytes);
        if (limit.Status == Constraint.Constrained) candidates.Add(limit.Bytes);
        return candidates.Count == 0 ? "unknown" : Format(candidates.Min());
    }

    private static bool IsContainerLike(Dictionary<string, string> facts) =>
        string.Equals(facts.GetValueOrDefault("executionScope"), "container", StringComparison.Ordinal)
        || facts.ContainsKey("cpuQuotaV2") || facts.ContainsKey("cpuSetV2") || facts.ContainsKey("cpuSetV1")
        || facts.ContainsKey("cpuQuotaMicrosV1") || facts.ContainsKey("cpuPeriodMicrosV1")
        || facts.ContainsKey("memoryLimitV2") || facts.ContainsKey("memoryLimitV1");

    private static bool TryNonUnknown(Dictionary<string, string> facts, string key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        if (facts.TryGetValue(key, out value) && value.Length != 0 && value != "unknown") return true;
        value = null;
        return false;
    }

    private static (Constraint Status, decimal Cores) CpuQuotaCores(Dictionary<string, string> facts)
    {
        if (TryNonUnknown(facts, "cpuQuotaV2", out var max))
        {
            var parts = max.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && ParsePositiveLong(parts[1]) is { } period)
            {
                if (parts[0] is "max" or "-1") return (Constraint.Unlimited, 0);
                if (ParsePositiveDecimal(parts[0]) is { } quota) return (Constraint.Constrained, quota / period);
            }
            return (Constraint.Unavailable, 0);
        }

        if (TryNonUnknown(facts, "cpuQuotaMicrosV1", out var quotaMicros)
            && TryNonUnknown(facts, "cpuPeriodMicrosV1", out var periodMicros))
        {
            if (ParsePositiveLong(periodMicros) is not { } v1Period) return (Constraint.Unavailable, 0);
            if (quotaMicros == "-1") return (Constraint.Unlimited, 0);
            if (ParsePositiveDecimal(quotaMicros) is { } v1Quota) return (Constraint.Constrained, v1Quota / v1Period);
            return (Constraint.Unavailable, 0);
        }
        return (Constraint.Unavailable, 0);
    }

    private static (Constraint Status, long Count) CpuSetCount(Dictionary<string, string> facts, string key)
    {
        if (!TryNonUnknown(facts, key, out var set)) return (Constraint.Unavailable, 0);
        var intervals = new List<(long Lo, long Hi)>();
        foreach (var token in set.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (ParseNonNegativeLong(token) is not { } single) return (Constraint.Unavailable, 0);
                intervals.Add((single, single));
            }
            else
            {
                var low = ParseNonNegativeLong(token[..dash]);
                var high = ParseNonNegativeLong(token[(dash + 1)..]);
                if (low is not { } lo || high is not { } hi || hi < lo) return (Constraint.Unavailable, 0);
                if (hi - lo + 1 < 0) return (Constraint.Unavailable, 0);
                intervals.Add((lo, hi));
            }
        }
        intervals.Sort();
        for (var i = 1; i < intervals.Count; i++)
            if (intervals[i].Lo <= intervals[i - 1].Hi) return (Constraint.Unavailable, 0);
        long total = 0;
        foreach (var (lo, hi) in intervals)
        {
            var count = hi - lo + 1;
            if (count < 0 || long.MaxValue - total < count) return (Constraint.Unavailable, 0);
            total += count;
        }
        return total > 0 ? (Constraint.Constrained, total) : (Constraint.Unavailable, 0);
    }

    private static (Constraint Status, long Bytes) MemoryLimitBytes(Dictionary<string, string> facts)
    {
        if (TryNonUnknown(facts, "memoryLimitV2", out var max))
        {
            if (max is "max" or "-1") return (Constraint.Unlimited, 0);
            if (ParsePositiveLong(max) is { } value) return (Constraint.Constrained, value);
            return (Constraint.Unavailable, 0);
        }
        if (TryNonUnknown(facts, "memoryLimitV1", out var bytes))
        {
            if (!long.TryParse(bytes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)) return (Constraint.Unavailable, 0);
            return limit <= 0 || limit >= (1L << 62) ? (Constraint.Unlimited, 0) : (Constraint.Constrained, limit);
        }
        return (Constraint.Unavailable, 0);
    }

    private static long? HostMemoryBytes(Dictionary<string, string> facts)
    {
        if (TryNonUnknown(facts, "memoryKiB", out var kiB)
            && long.TryParse(kiB, NumberStyles.None, CultureInfo.InvariantCulture, out var ki)
            && ki > 0 && ki <= long.MaxValue / 1024)
            return ki * 1024;
        if (TryNonUnknown(facts, "memoryBytes", out var bytes)
            && long.TryParse(bytes, NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b > 0)
            return b;
        return null;
    }

    private static decimal? ParsePositiveDecimal(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static long? ParsePositiveLong(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static long? ParseNonNegativeLong(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

    private static string Format(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private enum Constraint { Unavailable, Unlimited, Constrained }

    // No installations, credentials, external network requests or unbounded hardware enumeration.
    public static string Script(string directory) => "cd " + BootstrapScript.Quote(directory) + " || exit 1\n" + """
        emit() { printf '%s\t%s\n' "$1" "$2"; }
        emit os "$(uname -s 2>/dev/null)"
        emit osRelease "$(uname -r 2>/dev/null)"
        emit architecture "$(uname -m 2>/dev/null)"
        emit logicalCores "$(getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.logicalcpu 2>/dev/null)"
        if test -r /proc/cpuinfo; then
          emit cpuType "$(awk -F ': ' '/model name|Hardware/ {print $2; exit}' /proc/cpuinfo)"
          emit memoryKiB "$(awk '/MemTotal:/ {print $2}' /proc/meminfo)"
          emit availableMemoryKiB "$(awk '/MemAvailable:/ {print $2}' /proc/meminfo)"
        else
          emit cpuType "$(sysctl -n machdep.cpu.brand_string 2>/dev/null)"
          emit memoryBytes "$(sysctl -n hw.memsize 2>/dev/null)"
        fi
        emit workspaceFreeKiB "$(df -Pk . 2>/dev/null | awk 'NR==2 {print $4}')"
        scope=unknown
        if test -e /.dockerenv || test -e /run/.containerenv; then scope=container; fi
        emit executionScope "$scope"
        emit cpuQuotaV2 "$(cat /sys/fs/cgroup/cpu.max 2>/dev/null)"
        emit cpuSetV2 "$(cat /sys/fs/cgroup/cpuset.cpus.effective 2>/dev/null)"
        emit cpuSetV1 "$(cat /sys/fs/cgroup/cpuset/cpuset.cpus.effective 2>/dev/null || cat /sys/fs/cgroup/cpuset/cpuset.cpus 2>/dev/null)"
        emit memoryLimitV2 "$(cat /sys/fs/cgroup/memory.max 2>/dev/null)"
        emit memoryCurrentV2 "$(cat /sys/fs/cgroup/memory.current 2>/dev/null)"
        emit cpuQuotaMicrosV1 "$(cat /sys/fs/cgroup/cpu/cpu.cfs_quota_us 2>/dev/null)"
        emit cpuPeriodMicrosV1 "$(cat /sys/fs/cgroup/cpu/cpu.cfs_period_us 2>/dev/null)"
        emit memoryLimitV1 "$(cat /sys/fs/cgroup/memory/memory.limit_in_bytes 2>/dev/null)"
        for tool in gh az aws gcloud terraform kubectl git dotnet node npm python3 docker podman clang swift xcodebuild xcrun ffmpeg convert magick nvidia-smi rocminfo ollama; do
          if command -v "$tool" >/dev/null 2>&1; then emit "tool.$tool" present; else emit "tool.$tool" absent; fi
        done
        emit dockerDaemonAccess unknown
        emit gpuUsable unknown
        emit imageGeneration unknown
        emit videoGeneration unknown
        emit iosBuildAccess unknown
        emit signingAccess unknown
        """;
}
