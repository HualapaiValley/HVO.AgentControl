using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<WorkerRecord> ReserveCoordinator(string id, ReserveCoordinatorInput input) => Write(async db =>
    {
        var worker = await db.Workers.FindAsync(id) ?? throw new ControlException("Session not found.", 404);
        var payload = Json.Write(input);
        if (await db.Commands.FindAsync(input.Id) is { } prior)
        { _ = Same(prior, worker.RuntimeId, id, "ReserveCoordinator", payload); return worker; }
        if (worker.SettingsRevision != input.ExpectedRevision) throw new ControlException("Settings changed; refresh before reserving this session.");
        if (worker.Role != SessionRoles.Coordinator)
        {
            if (worker.Archived || worker.Stale || worker.Activity != "Idle" || worker.LastObservedAt is null || worker.LastObservedAt < Now - 15000)
                throw new ControlException("Reserve only a fresh, idle, dedicated conversation.");
            if (await db.Commands.AnyAsync(x => x.WorkerId == id && (x.State == Delivery.Queued || x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)) ||
                await db.Requests.AnyAsync(x => x.WorkerId == id && (x.State == "Pending" || x.State == "ReplyUnknown")) ||
                await db.CoordinationRuns.AnyAsync(x => x.State != "Completed" && x.State != "Stopped"))
                throw new ControlException("Resolve pending work and stop unfinished coordination before reserving this conversation.");
            worker.Role = SessionRoles.Coordinator; worker.Revision++; worker.SettingsRevision++;
        }
        var command = await Record(db, input.Id, worker.RuntimeId, id, "ReserveCoordinator", payload);
        command.State = Delivery.Finished; command.Detail = "Dedicated coordinator role reserved; this conversation cannot become a task worker.";
        return worker;
    });

    public Task<CommandRecord> DiscoverCapabilities(string workerId, RequestId input) => Write(async db =>
    {
        var worker = await db.Workers.FindAsync(workerId) ?? throw new ControlException("Worker not found.", 404);
        return await EnqueueCapabilities(db, worker, input.Id);
    });

    internal async Task<CommandRecord> EnqueueCapabilities(ControlDb db, WorkerRecord worker, string requestId)
    {
        if (await db.Commands.FindAsync(requestId) is { } prior)
        {
            if (prior.WorkerId != worker.Id || prior.Origin != "capability-report") throw new ControlException("Request ID belongs to another operation.");
            return prior;
        }
        if (worker.CapabilityCommandId is { } existing && await db.Commands.FindAsync(existing) is { } pending &&
            (pending.State == Delivery.Queued || Delivery.InFlight(pending.State))) return pending;
        var command = await EnqueuePrompt(db, worker.Id, new(requestId, CapabilityInquiry, worker.Revision), "capability-report");
        worker.CapabilityCommandId = command.Id;
        if (worker.CapabilityReport.Length == 0) worker.CapabilityReportedAt = null;
        return command;
    }

    private const string CapabilityInquiry = """
        Report capabilities available to you in this workspace and execution environment.
        Include OS/architecture, CPU and effective cores, memory and workspace disk space;
        development tools and Docker CLI versus usable daemon access; GPU access;
        image/video generation versus processing; and iOS source editing, build, test, and signing access separately.
        Use already known provider/model information; mark unverified provider details unknown.
        Do not read authentication files, provider configuration files, or environment variable values.
        Identify dependencies on other machines/services.
        Distinguish lightweight checks you performed from assumptions. Mark unknown or unavailable items explicitly.
        Use lightweight read-only checks. Do not install anything, run benchmarks, generate media, or perform signing.
        Give a concise report of at most 6000 characters; mention anything requiring a follow-up. Do not start other work.
        """;
}
