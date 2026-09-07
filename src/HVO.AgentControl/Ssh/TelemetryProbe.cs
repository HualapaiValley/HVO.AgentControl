using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Telemetry;

namespace HVO.AgentControl.Ssh;

public static class TelemetryProbe
{
    public static RuntimeTelemetrySample? Parse(string output, string identity)
    {
        if (!output.Split('\n').Contains("scope\tcontainer-cgroup-v2")) return null;
        return TelemetryParser.ParseLines(output, ControlStore.Now, identity);
    }

    // Only the verified container-root cgroup v2 scope is supported initially. Host cgroups,
    // cgroup v1 and macOS need separate collectors; never label their counters as container CPU.
    public const string Script = """
        export LC_ALL=C
        if test "$(uname -s)" != Linux; then exit 0; fi
        if ! test -e /.dockerenv && ! test -e /run/.containerenv; then exit 0; fi
        if ! test -f /sys/fs/cgroup/cgroup.controllers; then exit 0; fi
        if ! awk -F: '$1=="0" && $3=="/" {found=1} END {exit !found}' /proc/self/cgroup; then exit 0; fi
        if ! test -r /sys/fs/cgroup/cpu.stat || ! test -r /sys/fs/cgroup/memory.current; then exit 0; fi
        printf 'scope\tcontainer-cgroup-v2\nos\tLinux\n'
        printf 'accumCpuUsec\t%s\n' "$(awk '$1=="usage_usec" {print $2}' /sys/fs/cgroup/cpu.stat)"
        printf 'cpuQuotaV2\t%s\n' "$(cat /sys/fs/cgroup/cpu.max 2>/dev/null)"
        printf 'memoryCurrentV2\t%s\n' "$(cat /sys/fs/cgroup/memory.current 2>/dev/null)"
        printf 'memoryLimitV2\t%s\n' "$(cat /sys/fs/cgroup/memory.max 2>/dev/null)"
        """;
}
