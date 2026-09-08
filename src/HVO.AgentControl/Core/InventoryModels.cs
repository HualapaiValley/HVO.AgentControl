using System.Text.Json.Serialization;

namespace HVO.AgentControl.Core;

// Registration describes owner intent. It is not verified capacity or provisioner authority.
public sealed class HostRecord
{
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Unclassified";
    public string Description { get; set; } = "";
    public bool Archived { get; set; }
    public long Revision { get; set; } = 1;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class ProjectRecord
{
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string BaseBranch { get; set; } = "main";
    public string Description { get; set; } = "";
    public bool Archived { get; set; }
    public long Revision { get; set; } = 1;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

// Separate from worker command delivery: these transactions have no remote side effects.
public sealed class InventoryMutationReceipt
{
    public string RequestId { get; set; } = "";
    public string ResourceKind { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string Action { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string ResultJson { get; set; } = "{}";
    public long CreatedAt { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateHostInput(string RequestId, string Id, string Name, string Kind = "Unclassified", string Description = "");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateHostInput(string RequestId, long ExpectedRevision, string Name, string Description = "");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateProjectInput(string RequestId, string Id, string Name, string RepositoryUrl, string BaseBranch = "main", string Description = "");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProjectInput(string RequestId, long ExpectedRevision, string Name, string BaseBranch, string Description = "");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveInventoryInput(string RequestId, long ExpectedRevision, bool Archived = true);
public sealed record InventoryPage<T>(List<T> Items, long? NextAfter);
public sealed record InventoryMutationStatus(string RequestId, string ResourceKind, string ResourceId, string Action, long CreatedAt);

public sealed class InventoryException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
