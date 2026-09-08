using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Core;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<RuntimeEnvironmentView> RuntimeEnvironment(string runtimeId) => Read(async db =>
    {
        var runtime = await RequireEnvironmentRuntime(db, RuntimeEnvironmentId(runtimeId));
        return EnvironmentView(runtime, await db.RuntimeEnvironments.FindAsync(runtime.Id));
    });

    public Task<RuntimeEnvironmentPage> HostRuntimeEnvironments(string hostId, string? after = null, int take = 50) => Read(async db =>
    {
        // Host membership is owner-declared and must never be used as verified physical capacity.
        var id = InventoryId(hostId);
        if (take is < 1 or > 100) throw new InventoryException("validation", "Take must be between 1 and 100.");
        var cursor = after is null ? "" : RuntimeEnvironmentId(after);
        _ = await RequireHost(db, id);
        var rows = await (from environment in db.RuntimeEnvironments.AsNoTracking()
                          join runtime in db.Runtimes.AsNoTracking() on environment.RuntimeId equals runtime.Id
                          where environment.HostId == id && string.Compare(environment.RuntimeId, cursor) > 0
                          orderby environment.RuntimeId
                          select new { runtime, environment }).Take(take + 1).ToListAsync();
        // This resource uses UUID ordering; expose its cursor explicitly rather than a numeric inventory sequence.
        return new RuntimeEnvironmentPage(rows.Take(take).Select(x => EnvironmentView(x.runtime, x.environment)).ToList(),
            rows.Count > take ? rows[take - 1].runtime.Id : null);
    });

    public Task<RuntimeEnvironmentView> ConfigureRuntimeEnvironment(string runtimeId, ConfigureRuntimeEnvironmentInput input) => Write(async db =>
    {
        var id = RuntimeEnvironmentId(runtimeId);
        var hostId = InventoryId(input.HostId);
        if (input.Kind is not (RuntimeEnvironmentKind.ExistingMachine or RuntimeEnvironmentKind.ManagedDevcontainer))
            throw new InventoryException("validation", "Choose ExistingMachine or ManagedDevcontainer; use reset to return to legacy SSH configuration.");
        var projectId = input.ConfigurationProjectId is null ? null : InventoryId(input.ConfigurationProjectId);
        var path = ValidateDevcontainerPath(input.DevcontainerPath);
        if ((projectId is null) != (path is null) || (projectId is not null && input.Kind != RuntimeEnvironmentKind.ManagedDevcontainer))
            throw new InventoryException("validation", "A devcontainer configuration source requires both a project and relative devcontainer.json path on a ManagedDevcontainer runtime.");
        return await MutateInventory(db, input.RequestId, "RuntimeEnvironment", id, "Configure",
            new { input.ExpectedRevision, input.ExpectedRuntimeRevision, hostId, input.Kind, projectId, path }, async () =>
            {
                var runtime = await RequireEnvironmentRuntime(db, id);
                var environment = await db.RuntimeEnvironments.FindAsync(id);
                RequireEnvironmentRevisions(runtime, environment, input.ExpectedRuntimeRevision, input.ExpectedRevision);
                await RequireEnvironmentEditable(db, runtime);
                var host = await RequireHost(db, hostId);
                if (host.Archived) throw InventoryConflict("resource_archived", "Unarchive the host before associating a runtime.");
                if (projectId is not null && (await RequireProject(db, projectId)).Archived)
                    throw InventoryConflict("resource_archived", "Unarchive the configuration project before using it.");
                if (input.Kind == RuntimeEnvironmentKind.ManagedDevcontainer && await db.Workers.CountAsync(x => x.RuntimeId == id) > 1)
                    throw InventoryConflict("worker_limit", "Managed devcontainers allow one worker/coordinator registration, including archived workers. Resolve extra registrations first.");
                if (input.Kind == RuntimeEnvironmentKind.ManagedDevcontainer &&
                await db.WorkerSlots.CountAsync(x => x.RuntimeId == id) + await db.Workers.CountAsync(x => x.RuntimeId == id) > 1)
                    throw InventoryConflict("worker_limit", "Managed devcontainers allow one worker slot or legacy worker/coordinator registration.");
                environment ??= NewEnvironment(db, runtime);
                environment.HostId = hostId; environment.Kind = input.Kind;
                environment.ConfigurationProjectId = projectId; environment.DevcontainerPath = path;
                environment.ConnectionFingerprint = RuntimeConnectionFingerprint(runtime);
                environment.Revision++; environment.UpdatedAt = Now;
                if (input.Kind == RuntimeEnvironmentKind.ManagedDevcontainer) runtime.Capacity = 1;
                runtime.Revision++;
                return EnvironmentView(runtime, environment);
            });
    });

    public Task<RuntimeEnvironmentView> ResetRuntimeEnvironment(string runtimeId, ResetRuntimeEnvironmentInput input) => Write(async db =>
    {
        var id = RuntimeEnvironmentId(runtimeId);
        return await MutateInventory(db, input.RequestId, "RuntimeEnvironment", id, "Reset",
            new { input.ExpectedRevision, input.ExpectedRuntimeRevision }, async () =>
            {
                var runtime = await RequireEnvironmentRuntime(db, id);
                var environment = await db.RuntimeEnvironments.FindAsync(id);
                RequireEnvironmentRevisions(runtime, environment, input.ExpectedRuntimeRevision, input.ExpectedRevision);
                await RequireEnvironmentEditable(db, runtime);
                environment ??= NewEnvironment(db, runtime);
                environment.HostId = null; environment.Kind = RuntimeEnvironmentKind.LegacySsh;
                environment.ConfigurationProjectId = null; environment.DevcontainerPath = null;
                environment.ConnectionFingerprint = ""; environment.Revision++; environment.UpdatedAt = Now;
                // Reset does not silently increase a previously restricted task capacity.
                runtime.Revision++;
                return EnvironmentView(runtime, environment);
            });
    });

    private static RuntimeEnvironmentRecord NewEnvironment(ControlDb db, RuntimeRecord runtime)
    {
        var environment = new RuntimeEnvironmentRecord { RuntimeId = runtime.Id, CreatedAt = Now };
        db.RuntimeEnvironments.Add(environment);
        return environment;
    }

    private static async Task<RuntimeRecord> RequireEnvironmentRuntime(ControlDb db, string id) =>
        await db.Runtimes.SingleOrDefaultAsync(x => x.Id == id) ?? throw new InventoryException("not_found", "Runtime not found.", 404);

    private static string RuntimeEnvironmentId(string? value)
    {
        // Legacy SaveRuntime admits case-sensitive N-format keys, including the all-zero GUID.
        // Preserve those keys in routes, receipts, foreign keys and UUID pagination cursors.
        if (value is null || !Guid.TryParseExact(value, "N", out _))
            throw new InventoryException("validation", "Use the exact 32-character runtime ID returned by runtime inventory.");
        return value;
    }

    private static void RequireEnvironmentRevisions(RuntimeRecord runtime, RuntimeEnvironmentRecord? environment, long runtimeRevision, long revision)
    {
        if (revision < 0 || runtimeRevision < 1) throw new InventoryException("validation", "Use the current non-negative environment revision and positive runtime revision.");
        if (runtime.Revision != runtimeRevision || (environment?.Revision ?? 0) != revision)
            throw InventoryConflict("revision_conflict", "Runtime or environment configuration changed; refresh both revisions before editing.");
    }

    private async Task RequireEnvironmentEditable(ControlDb db, RuntimeRecord runtime)
    {
        if (runtime.DesiredConnected || runtime.Transport != "Disconnected")
            throw InventoryConflict("runtime_connected", "Explicitly disconnect the runtime before changing its environment association.");
        if (activeTerminals.GetValueOrDefault(runtime.Id) > 0)
            throw InventoryConflict("runtime_in_use", "Close the runtime's admin terminals before changing its environment association.");
        if (await Unresolved(db, runtime.Id, null) || await db.WorkspaceClaims.AnyAsync(x => x.RuntimeId == runtime.Id && x.WorkerId == null))
            throw InventoryConflict("runtime_in_use", "Resolve pending or uncertain operations and workspace setup before changing the environment association.");
        var workerIds = await db.Workers.Where(x => x.RuntimeId == runtime.Id).Select(x => x.Id).ToListAsync();
        if (await db.Requests.AnyAsync(x => workerIds.Contains(x.WorkerId) && (x.State == "Pending" || x.State == "ReplyUnknown")) ||
            await db.WorkItems.AnyAsync(x => workerIds.Contains(x.OwnerWorkerId) && x.State != WorkItemState.Released && x.State != WorkItemState.Abandoned))
            throw InventoryConflict("runtime_in_use", "Resolve pending worker requests and release work-item ownership before changing the environment association.");
        var runs = await db.CoordinationRuns.Where(x => x.State != "Completed" && x.State != "Stopped").ToListAsync();
        if (runs.Any(x => workerIds.Contains(x.CoordinatorWorkerId) || Json.Read<string[]>(x.WorkerIdsJson).Any(workerIds.Contains)))
            throw InventoryConflict("runtime_in_use", "Finish or stop coordination using this runtime before changing its environment association.");
    }

    internal static async Task RequireManagedWorkerSlot(ControlDb db, RuntimeRecord runtime)
    {
        if (!await db.RuntimeEnvironments.AnyAsync(x => x.RuntimeId == runtime.Id && x.Kind == RuntimeEnvironmentKind.ManagedDevcontainer)) return;
        if (await db.Workers.AnyAsync(x => x.RuntimeId == runtime.Id) ||
            await db.WorkerSlots.AnyAsync(x => x.RuntimeId == runtime.Id) ||
            await db.WorkspaceClaims.AnyAsync(x => x.RuntimeId == runtime.Id && x.WorkerId == null) ||
            await db.Commands.AnyAsync(x => x.RuntimeId == runtime.Id && x.Kind == "CreateWorker" &&
                (x.State == Delivery.Queued || x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)))
            throw new ControlException("This managed devcontainer already has a worker/coordinator or unresolved worker setup. Resolve or remove it before creating another worker.");
    }

    private static RuntimeEnvironmentView EnvironmentView(RuntimeRecord runtime, RuntimeEnvironmentRecord? environment)
    {
        var kind = environment?.Kind ?? RuntimeEnvironmentKind.LegacySsh;
        var state = kind == RuntimeEnvironmentKind.LegacySsh ? "LegacySsh" :
            environment!.ConnectionFingerprint == RuntimeConnectionFingerprint(runtime) ? "Configured" : "ConnectionProfileChanged";
        return new(runtime.Id, runtime.Name, runtime.Revision, environment?.Revision ?? 0, environment?.HostId, kind,
            environment?.ConfigurationProjectId, environment?.DevcontainerPath, state,
            kind == RuntimeEnvironmentKind.ManagedDevcontainer ? 1 : null, runtime.Capacity, false, environment?.CreatedAt, environment?.UpdatedAt);
    }

    private static string RuntimeConnectionFingerprint(RuntimeRecord runtime) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(new
    {
        runtime.Host,
        runtime.Port,
        runtime.Username,
        runtime.HostKeySha256,
        runtime.HostKeyAlgorithm,
        runtime.Authentication,
        runtime.CredentialReference,
        runtime.PassphraseReference,
        runtime.ServerPasswordReference,
        runtime.StateDirectory,
        runtime.ManagedServerId,
        runtime.ApiPort,
        runtime.AllowedRoots,
        runtime.Executable,
        startup = StartupOptions.Fingerprint(runtime)
    }))));

    private static string? ValidateDevcontainerPath(string? value)
    {
        if (value is null) return null;
        if (value.Length is 0 or > 1024 || value != value.Trim() || value.StartsWith('/') || value.Contains('\\') || value.Contains(':') ||
            value.Any(char.IsControl) || value.Split('/').Any(x => x.Length == 0 || x is "." or "..") ||
            value.Split('/')[^1] != "devcontainer.json" && value != ".devcontainer.json")
            throw new InventoryException("validation", "Use a repository-relative path ending in devcontainer.json without parent traversal, empty segments or control characters.");
        return value;
    }
}
