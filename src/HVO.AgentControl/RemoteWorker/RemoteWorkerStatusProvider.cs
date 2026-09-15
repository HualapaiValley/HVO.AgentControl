using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;

namespace HVO.AgentControl.RemoteWorker;

public sealed record RemoteWorkerSnapshot(string EmployeeId, string RuntimeBindingId, string WorkerId, string HostId, string LifecycleStatus, bool EnrollmentEnabled, string ConnectionState, string ProcessState, string? SessionRecordId, string? NativeSessionId, long OwnershipEpoch, long ProcessGeneration, bool Held, bool ViewerSupported, bool ViewerAvailable, IReadOnlyList<WorkerPendingPermissionRecord> PendingWorkerPermissions, string? Detail)
{
    public RemoteWorkerSnapshot(string employeeId, string runtimeBindingId, string workerId, string hostId, string lifecycleStatus, bool enrollmentEnabled, string connectionState, string processState, string? sessionId, long ownershipEpoch, long processGeneration, bool held, bool viewerSupported, bool viewerAvailable, IReadOnlyList<WorkerPendingPermissionRecord> pendingWorkerPermissions, string? detail)
        : this(employeeId, runtimeBindingId, workerId, hostId, lifecycleStatus, enrollmentEnabled, connectionState, processState, sessionId, sessionId, ownershipEpoch, processGeneration, held, viewerSupported, viewerAvailable, pendingWorkerPermissions, detail) { }

    public string? SessionId { get => NativeSessionId; init => NativeSessionId = value; }
}

public interface IRemoteWorkerStatusProvider
{
    IReadOnlyDictionary<string, RemoteWorkerSnapshot> Snapshot(OrganizationOverview overview);
}

/// <summary>Builds a request-safe snapshot exclusively from the controller store; it performs no network I/O.</summary>
public sealed class RemoteWorkerStatusProvider(AcpControlHost control) : IRemoteWorkerStatusProvider
{
    public IReadOnlyDictionary<string, RemoteWorkerSnapshot> Snapshot(OrganizationOverview overview)
    {
        var store = control.Organization;
        if (store is null) return new Dictionary<string, RemoteWorkerSnapshot>(StringComparer.Ordinal);
        var cursors = store.ListWorkerCursors().ToDictionary(x => x.WorkerId, StringComparer.Ordinal);
        var recoveries = store.ListWorkerRecoveryObligations(activeOnly: true).GroupBy(x => x.WorkerId).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        var permissions = store.ListWorkerPendingPermissions().Where(x => x.State is "pending" or "uncertain").GroupBy(x => x.WorkerId).ToDictionary(x => x.Key, x => x.Take(16).ToArray(), StringComparer.Ordinal);
        var employees = overview.Employees.ToDictionary(x => x.RuntimeBindingId, StringComparer.Ordinal);
        var result = new Dictionary<string, RemoteWorkerSnapshot>(StringComparer.Ordinal);
        foreach (var enrollment in store.ListWorkerEnrollments())
        {
            if (!employees.TryGetValue(enrollment.RuntimeBindingId, out var employee)) continue;
            cursors.TryGetValue(enrollment.WorkerId, out var cursor); recoveries.TryGetValue(enrollment.WorkerId, out var obligations); obligations ??= []; permissions.TryGetValue(enrollment.WorkerId, out var pending); pending ??= [];
            var held = enrollment.LifecycleStatus is "held" or "failed" || cursor?.ConnectionState == "held" || cursor?.HoldSummary is not null || obligations.Length > 0;
            var viewerSupported = cursor?.ViewerSupported == true;
            var viewerAvailable = viewerSupported && cursor?.ViewerAvailable == true && enrollment.LifecycleStatus == "enrolled" && cursor is { ConnectionState: "authenticated", Status: "running", HoldSummary: null } && employee.SessionRecordId is not null && employee.NativeSessionId is not null && obligations.Length == 0;
            if (!result.TryAdd(employee.Id, new(employee.Id, enrollment.RuntimeBindingId, enrollment.WorkerId, enrollment.HostId, enrollment.LifecycleStatus, enrollment.Enabled, cursor?.ConnectionState ?? "disconnected", cursor?.Status ?? "unknown", employee.SessionRecordId, employee.NativeSessionId, cursor?.ObservedOwnershipEpoch ?? enrollment.OwnershipEpoch, cursor?.ObservedProcessGeneration ?? enrollment.ProcessGeneration, held, viewerSupported, viewerAvailable, pending, obligations.FirstOrDefault()?.Kind)))
                throw new OrganizationStoreCorruptException("Multiple worker enrollments resolved to the same employee.");
        }
        return result;
    }
}
