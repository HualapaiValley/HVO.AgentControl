using HVO.AgentControl.Ssh;
using Microsoft.AspNetCore.Components;

namespace HVO.AgentControl.Components;

public partial class CapabilityFacts
{
    [Parameter] public string Json { get; set; } = "{}";
    private CapabilitySnapshot? facts;
    private static string Label(string key) => key switch
    {
        "os" => "Operating system",
        "osRelease" => "OS release",
        "architecture" => "Architecture",
        "logicalCores" => "Visible logical cores",
        "cpuType" => "CPU type",
        "executionScope" => "Execution environment",
        "memoryKiB" => "Visible memory (KiB)",
        "memoryBytes" => "Visible memory (bytes)",
        "availableMemoryKiB" => "Available memory (KiB)",
        "workspaceFreeKiB" => "Workspace free disk (KiB)",
        "cpuQuotaV2" => "CPU quota / period (microseconds)",
        "cpuSetV2" => "Allowed CPU set",
        "memoryLimitV2" => "Container memory limit (bytes; max = unlimited)",
        "memoryCurrentV2" => "Container memory usage (bytes)",
        "cpuQuotaMicrosV1" => "Legacy CPU quota (microseconds)",
        "cpuPeriodMicrosV1" => "Legacy CPU period (microseconds)",
        "memoryLimitV1" => "Legacy memory limit (bytes)",
        "dockerDaemonAccess" => "Docker daemon access",
        "gpuUsable" => "Usable GPU access",
        "imageGeneration" => "Image generation",
        "videoGeneration" => "Video generation",
        "iosBuildAccess" => "iOS build access",
        "signingAccess" => "Signing access",
        _ => key.StartsWith("tool.", StringComparison.Ordinal) ? key[5..] + " installed" : key
    };
    protected override void OnParametersSet()
    {
        facts = Json == "{}" ? null : Core.Json.Read<CapabilitySnapshot>(Json);
    }
}
