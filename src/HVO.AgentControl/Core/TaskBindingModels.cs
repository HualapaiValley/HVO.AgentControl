using System.Text.Json.Serialization;

namespace HVO.AgentControl.Core;

public static class TaskBindingState
{
    public const string Active = "Active", Released = "Released";
}

public static class TaskSessionBindingState
{
    public const string Unbound = "Unbound", ActivationPending = "ActivationPending", Creating = "Creating", Bound = "Bound", Released = "Released";
}

public sealed class WorkerSlotRecord
{
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = SessionRoles.Worker;
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public bool Archived { get; set; }
    public long Revision { get; set; } = 1;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class TaskWorkspaceRecord
{
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string WorkerSlotId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Branch { get; set; } = "";
    public string State { get; set; } = TaskBindingState.Active;
    public long Revision { get; set; } = 1;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class TaskSessionBindingRecord
{
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string TaskBindingId { get; set; } = "";
    public string WorkerSlotId { get; set; } = "";
    public string? LegacyWorkerId { get; set; }
    public string? WorkerId { get; set; }
    public string? CreationCommandId { get; set; }
    public string NativeSessionId { get; set; } = "";
    public int Generation { get; set; }
    public string State { get; set; } = TaskSessionBindingState.Unbound;
    public long Revision { get; set; } = 1;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class TaskBindingRecord
{
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string WorkerSlotId { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string SessionBindingId { get; set; } = "";
    public string State { get; set; } = TaskBindingState.Active;
    public bool PlacementVerified { get; set; }
    public long Revision { get; set; } = 1;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateWorkerSlotInput(string RequestId, string Id, string RuntimeId, string Name,
    string Role = SessionRoles.Worker, string ProviderId = "", string ModelId = "");

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTaskBindingInput(string RequestId, string Id, string WorkItemId, string ProjectId,
    string WorkerSlotId, string WorkspaceId, string SessionBindingId, string Directory, string Branch,
    long ExpectedWorkItemRevision, long ExpectedProjectRevision, long ExpectedSlotRevision,
    string? LegacyWorkerId = null, string? NativeSessionId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReleaseTaskBindingInput(string RequestId, long ExpectedRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTaskSessionInput(string Id, string WorkerId, string ExpectedHead, long ExpectedBindingRevision,
    long ExpectedWorkspaceRevision, long ExpectedSessionRevision, long ExpectedWorkItemRevision, long ExpectedProjectRevision,
    long ExpectedSlotRevision, long ExpectedRuntimeRevision, long ExpectedEnvironmentRevision);
public sealed record TaskSessionCreationIntent(string TaskBindingId, CreateTaskSessionInput Input, int Generation, string Title);

public sealed record WorkerSlotPage(List<WorkerSlotRecord> Items, long? NextAfter);
public sealed record TaskBindingPage(List<TaskBindingView> Items, long? NextAfter);
public sealed record TaskBindingView(TaskBindingRecord Binding, TaskWorkspaceRecord Workspace, TaskSessionBindingRecord Session,
    ProjectRecord Project, WorkerSlotRecord WorkerSlot);
