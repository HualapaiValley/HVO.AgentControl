using System.Text.Json.Serialization;

namespace HVO.AgentControl.Core;

public static class RuntimeEnvironmentKind
{
    public const string LegacySsh = "LegacySsh", ExistingMachine = "ExistingMachine", ManagedDevcontainer = "ManagedDevcontainer";
}

// Owner configuration, separate from evidence of the actual machine/container and its lifecycle authority.
public sealed class RuntimeEnvironmentRecord
{
    public string RuntimeId { get; set; } = "";
    public string? HostId { get; set; }
    public string Kind { get; set; } = RuntimeEnvironmentKind.LegacySsh;
    public string? ConfigurationProjectId { get; set; }
    public string? DevcontainerPath { get; set; }
    public string ConnectionFingerprint { get; set; } = "";
    public long Revision { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfigureRuntimeEnvironmentInput(string RequestId, long ExpectedRevision, long ExpectedRuntimeRevision,
    string HostId, string Kind, string? ConfigurationProjectId = null, string? DevcontainerPath = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResetRuntimeEnvironmentInput(string RequestId, long ExpectedRevision, long ExpectedRuntimeRevision);

public sealed record RuntimeEnvironmentView(string RuntimeId, string RuntimeName, long RuntimeRevision, long Revision,
    string? RequestedHostId, string RequestedKind, string? ConfigurationProjectId, string? DevcontainerPath,
    string State, int? MaxWorkerRegistrations, int ActiveTaskCapacity, bool PlacementVerified,
    long? CreatedAt, long? UpdatedAt);

public sealed record RuntimeEnvironmentPage(List<RuntimeEnvironmentView> Items, string? NextAfter);
