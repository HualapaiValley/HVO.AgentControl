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
        return new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "probe", directory, facts);
    }

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
