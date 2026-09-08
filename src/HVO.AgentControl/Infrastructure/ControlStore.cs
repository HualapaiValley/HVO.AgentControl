using HVO.AgentControl.Core;
using HVO.AgentControl.Ssh;
using HVO.AgentControl.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.Infrastructure;

// All writes/claims share this gate in the single, file-locked replica. Network calls never hold it.
public sealed partial class ControlStore(IDbContextFactory<ControlDb> factory, IOptions<ControlOptions> options, Secrets secrets)
{
    private const int TelemetryHistoryLimit = 200;
    private readonly SemaphoreSlim gate = new(1);
    public event Action? Changed;
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task<T> Read<T>(Func<ControlDb, Task<T>> read)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await read(db);
    }

    public async Task<T> Write<T>(Func<ControlDb, Task<T>> write)
    {
        await gate.WaitAsync();
        T result;
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            result = await write(db);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        finally { gate.Release(); }
        // Notifications are advisory; a UI subscriber must never invalidate a committed command.
        if (Changed is { } changed)
            foreach (Action subscriber in changed.GetInvocationList())
                try { subscriber(); } catch (ObjectDisposedException) { }
        return result;
    }

    public static JournalEvent Event(ControlDb db, string type, string? runtimeId = null, string? workerId = null,
        string? commandId = null, object? payload = null, string provenance = "service", int generation = 0, string? nativeId = null)
    {
        var journalEvent = new JournalEvent
        {
            Type = type,
            RuntimeId = runtimeId,
            WorkerId = workerId,
            CommandId = commandId,
            Payload = Json.Write(payload ?? new { }),
            Provenance = provenance,
            Generation = generation,
            NativeId = nativeId
        };
        db.Events.Add(journalEvent);
        return journalEvent;
    }

    public Task<RuntimeRecord> SaveRuntime(RuntimeRecord input) => Write(db => SaveRuntime(db, input));

    public Task<RuntimeTelemetryHistoryRecord> RecordTelemetry(string runtimeId, RuntimeTelemetry telemetry) =>
        Write(db => RecordTelemetry(db, runtimeId, telemetry));

    internal static async Task<RuntimeTelemetryHistoryRecord> RecordTelemetry(ControlDb db, string runtimeId, RuntimeTelemetry telemetry)
    {
        if (await db.Runtimes.FindAsync(runtimeId) is null) throw new ControlException("Runtime not found.", 404);
        var record = new RuntimeTelemetryHistoryRecord
        {
            RuntimeId = runtimeId,
            ObservedAt = telemetry.ObservedAt,
            State = telemetry.State.ToString(),
            CpuQuotaPercent = telemetry.CpuQuotaPercent,
            CpuCoreUsage = telemetry.CpuCoreUsage,
            MemoryPercent = telemetry.MemoryPercent,
            QuotaCores = telemetry.QuotaCores,
            CpuWindowMs = telemetry.CpuWindowMs,
            MemoryBytes = telemetry.MemoryBytes,
            MemoryLimitBytes = telemetry.MemoryLimitBytes,
            Note = telemetry.Note
        };
        db.TelemetryHistory.Add(record);
        // Include this observation before retaining the newest rows, even when probes arrive out of order.
        await db.SaveChangesAsync();
        var expired = await db.TelemetryHistory.Where(x => x.RuntimeId == runtimeId)
            .OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.Sequence).Skip(TelemetryHistoryLimit).ToListAsync();
        db.TelemetryHistory.RemoveRange(expired);
        return record;
    }

    public Task<List<RuntimeTelemetryHistoryRecord>> TelemetryHistory(string runtimeId, int limit = 100) => Read(async db =>
    {
        if (limit is < 1 or > TelemetryHistoryLimit) throw new ControlException($"Telemetry history limit must be between 1 and {TelemetryHistoryLimit}.", 400);
        if (await db.Runtimes.FindAsync(runtimeId) is null) throw new ControlException("Runtime not found.", 404);
        return await db.TelemetryHistory.AsNoTracking().Where(x => x.RuntimeId == runtimeId)
            .OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.Sequence).Take(limit).ToListAsync();
    });

    public Task<CommandRecord> SaveRuntimeForSetup(RuntimeRecord input, string requestId) => Write(async db =>
    {
        ValidateRequestId(requestId);
        var payload = Json.Write(input);
        if (await db.Commands.FindAsync(requestId) is { } prior) return Same(prior, input.Id, null, "SaveRuntimeForSetup", payload);
        var runtime = await SaveRuntime(db, input);
        runtime.DesiredConnected = false; runtime.Transport = "Disconnected";
        runtime.InstalledExecutable = input.InstalledExecutable; runtime.Platform = input.Platform; runtime.Health = input.Health; runtime.Diagnostic = input.Diagnostic;
        var command = await Record(db, requestId, runtime.Id, null, "SaveRuntimeForSetup", payload);
        command.State = Delivery.Finished; command.ResultId = runtime.Id;
        command.Detail = "SSH profile saved for setup. OpenCode was not started.";
        return command;
    });

    public Task<CommandRecord> SaveRuntimeAndConnect(RuntimeRecord input, string requestId) => Write(async db =>
    {
        ValidateRequestId(requestId);
        var payload = Json.Write(input);
        if (await db.Commands.FindAsync(requestId) is { } prior) return Same(prior, input.Id, null, "EnsureServer", payload);
        var runtime = await SaveRuntime(db, input);
        var command = await Record(db, requestId, runtime.Id, null, "EnsureServer", payload);
        runtime.DesiredConnected = true;
        Event(db, "RuntimeVerified", runtime.Id, payload: new { runtime.HostKeySha256, runtime.HostKeyAlgorithm, runtime.PureMode, runtime.PrintLogs, runtime.LogLevel }, provenance: "user");
        return command;
    });

    private async Task<RuntimeRecord> SaveRuntime(ControlDb db, RuntimeRecord input)
    {
        RequireDevelopmentRuntime(input);
        if (await db.Runtimes.FindAsync(input.Id) is { } registered) RequireDevelopmentRuntime(registered);
        ValidateRuntime(input);
        var existing = await db.Runtimes.FindAsync(input.Id);
        if (input.Capacity != 1 && await db.RuntimeEnvironments.AnyAsync(x => x.RuntimeId == input.Id && x.Kind == RuntimeEnvironmentKind.ManagedDevcontainer))
            throw new ControlException("Managed devcontainers allow one active task. Keep capacity at one or explicitly change the environment configuration first.");
        if (existing is null && await db.Runtimes.CountAsync() >= options.Value.MaxRuntimes)
            throw new ControlException("Runtime registration limit reached.");
        if (activeTerminals.GetValueOrDefault(input.Id) > 0) throw new ControlException("Close this runtime’s admin terminals before changing its connection profile.");
        if (existing is not null && (existing.DesiredConnected || existing.Transport == "Connected"))
            throw new ControlException("Disconnect the runtime before editing its connection profile.");
        if (existing is not null && existing.Revision != input.Revision) throw new ControlException("Runtime changed; refresh before editing.");
        if (existing is not null && await db.GitHubAccess.AnyAsync(x => x.Id == input.Id && x.State != "Disabled") &&
            (existing.Host != input.Host || existing.Port != input.Port || existing.Username != input.Username ||
             existing.HostKeySha256 != input.HostKeySha256 || existing.HostKeyAlgorithm != input.HostKeyAlgorithm ||
             existing.CredentialReference != input.CredentialReference || existing.PassphraseReference != input.PassphraseReference ||
             existing.Authentication != input.Authentication || existing.StateDirectory != input.StateDirectory))
            throw new ControlException("Disable GitHub credential renewal before changing this runtime's connection identity.");
        if (existing is not null && (await db.Workers.Where(x => x.RuntimeId == input.Id).Select(x => x.Directory).ToListAsync()).Any(path => !Roots(input).Any(root => IsWithin(path, root))))
            throw new ControlException("Allowed roots must still include registered worker workspaces. Disconnect to suspend all dispatch.");
        if (existing is not null && (await db.Workers.AnyAsync(x => x.RuntimeId == input.Id) || await db.Commands.AnyAsync(x => x.RuntimeId == input.Id && (x.State == Delivery.Queued || x.State == Delivery.Unknown || x.State == Delivery.Dispatching))) &&
            (existing.Host != input.Host || existing.Port != input.Port || existing.Username != input.Username ||
             existing.StateDirectory != input.StateDirectory || existing.ApiPort != input.ApiPort))
            throw new ControlException("A runtime with workers cannot be redirected to another server. Register a new runtime.");
        if (existing is null && await db.Commands.AnyAsync(x => x.RuntimeId == input.Id && x.Kind == "DeleteRuntime"))
            throw new ControlException("Use a new runtime identity after deleting a registration.");
        var record = existing ?? new RuntimeRecord { Id = input.Id };
        record.InstalledExecutable = "";
        record.Name = input.Name.Trim(); record.Host = input.Host; record.Port = input.Port;
        record.Username = input.Username; record.HostKeySha256 = input.HostKeySha256;
        record.HostKeyAlgorithm = input.HostKeyAlgorithm;
        record.Authentication = input.Authentication; record.CredentialReference = input.CredentialReference;
        record.PassphraseReference = input.PassphraseReference; record.ServerPasswordReference = input.ServerPasswordReference;
        record.StateDirectory = input.StateDirectory; record.AllowedRoots = input.AllowedRoots;
        record.Executable = input.Executable; record.ApiPort = input.ApiPort; record.InstallIfMissing = input.InstallIfMissing;
        record.PureMode = input.PureMode; record.PrintLogs = input.PrintLogs; record.LogLevel = input.LogLevel;
        record.Capacity = input.Capacity; record.Labels = input.Labels; record.Revision++;
        if (existing is null) db.Runtimes.Add(record);
        Event(db, "RuntimeSaved", record.Id, provenance: "user");
        return record;
    }

    private void ValidateRuntime(RuntimeRecord value)
    {
        _ = StartupOptions.Arguments(value);
        if (!Guid.TryParseExact(value.Id, "N", out _) || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 120 ||
            string.IsNullOrWhiteSpace(value.Host) || value.Host.Length > 253 || value.Host.Any(char.IsWhiteSpace) ||
            string.IsNullOrWhiteSpace(value.Username) || value.Username.Length > 100 ||
            value.Port is < 1 or > 65535 || value.ApiPort is < 1024 or > 65535 || value.Capacity is < 1 or > 16)
            throw new ControlException("Invalid runtime identity, host, user, ports, or capacity.", 400);
        if (!value.HostKeySha256.StartsWith("SHA256:", StringComparison.Ordinal) || value.HostKeySha256.Length != 50)
            throw new ControlException("Use Verify to discover and explicitly trust the host key, or enter a fingerprint verified through a trusted channel.", 400);
        if (value.HostKeyAlgorithm is not ("ssh-ed25519" or "ecdsa-sha2-nistp256" or "rsa-sha2-512"))
            throw new ControlException("Choose a supported SSH host-key algorithm.", 400);
        if (value.Authentication is not ("privateKey" or "password")) throw new ControlException("Unsupported SSH authentication method.", 400);
        _ = secrets.PathFor(value.CredentialReference); _ = secrets.PathFor(value.ServerPasswordReference);
        if (!string.IsNullOrEmpty(value.PassphraseReference)) _ = secrets.PathFor(value.PassphraseReference);
        ValidatePath(value.StateDirectory);
        if (!string.IsNullOrWhiteSpace(value.Executable)) ValidatePath(value.Executable);
        var roots = Roots(value);
        if (roots.Length == 0 || roots.Length > 16) throw new ControlException("Configure 1–16 allowed absolute workspace roots.", 400);
        foreach (var root in roots)
        {
            ValidatePath(root);
            if (root == "/" || IsWithin(value.StateDirectory, root))
                throw new ControlException("Workspace roots must be narrower than / and must not contain the bootstrap state directory.", 400);
        }
    }

    public static string[] Roots(RuntimeRecord runtime) => runtime.AllowedRoots.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public static bool IsWithin(string path, string root) => path == root.TrimEnd('/') || path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);
    public static void ValidatePath(string path)
    {
        if (!path.StartsWith('/') || path.Length > 2048 || path.Contains('\0') || path.Split('/').Any(x => x == ".."))
            throw new ControlException("Remote paths must be absolute, bounded, and contain no parent traversal or NUL.", 400);
    }
    public static void ValidateRequestId(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new ControlException("A UUID application request ID is required.", 400);
    }

    public Task<CommandRecord> RuntimeCommand(string runtimeId, string kind, string id) => Write(async db =>
    {
        var runtime = await db.Runtimes.FindAsync(runtimeId) ?? throw new ControlException("Runtime not found.", 404);
        if (await db.Commands.FindAsync(id) is { } prior) return Same(prior, runtimeId, null, kind, prior.Payload);
        if (kind == "StopManagedServer") RequireDevelopmentRuntime(runtime);
        if (kind is not ("EnsureServer" or "RefreshState" or "DisconnectRuntime" or "StopManagedServer"))
            throw new ControlException("Unsupported runtime lifecycle command.", 400);
        runtime.DesiredConnected = kind is "EnsureServer" or "RefreshState";
        runtime.Revision++;
        OwnedNativeProcess? owned = null;
        if (kind == "StopManagedServer")
        {
            var observed = await db.Events.AsNoTracking().Where(x => x.RuntimeId == runtimeId && x.Type == "NativeProcessObserved")
                .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync();
            if (observed is not null)
            {
                var evidence = Json.Read<NativeProcessObservationEvidence>(observed.Payload);
                if (evidence is { State: NativeProcessObservationState.Observed, Freshness: "Fresh", ProcessId: > 0 } &&
                    evidence.ObservedAt >= Now - 60000 && evidence.ManagedServerId == runtime.ManagedServerId)
                    owned = new(evidence.ManagedServerId, evidence.ProcessId.Value, evidence.Incarnation, evidence.ObservedAt);
            }
        }
        var command = await Record(db, id, runtimeId, null, kind, Json.Write(new RuntimeLifecycleInput(runtime.Revision, owned)));
        foreach (var queued in await db.Commands.Where(x => x.RuntimeId == runtimeId && x.Id != command.Id &&
                     ((x.State == Delivery.Queued && (x.Kind == "EnsureServer" || x.Kind == "RefreshState" || x.Kind == "DisconnectRuntime" || x.Kind == "StopManagedServer")) ||
                      (x.State == Delivery.Dispatching && x.Kind == "StopManagedServer"))).ToListAsync())
        {
            var dispatchedStop = queued.State == Delivery.Dispatching;
            queued.State = dispatchedStop ? Delivery.Unknown : Delivery.Cancelled;
            queued.Detail = dispatchedStop
                ? "A newer runtime lifecycle intent arrived after stop dispatch. The stop outcome is unknown; it will not be repeated."
                : "Superseded by a newer runtime lifecycle intent; no destructive action was performed.";
            queued.UpdatedAt = Now;
            Event(db, "RuntimeLifecycleSuperseded", runtimeId, commandId: queued.Id, payload: new { supersededBy = command.Id, state = queued.State });
        }
        return command;
    });

    public Task<CommandRecord> CreateWorker(CreateWorkerInput input) => Write(async db =>
    {
        var runtime = await db.Runtimes.FindAsync(input.RuntimeId) ?? throw new ControlException("Runtime not found.", 404);
        if (await db.Commands.FindAsync(input.Id) is { } prior) return Same(prior, input.RuntimeId, null, "CreateWorker", Json.Write(input));
        RequireDevelopmentRuntime(runtime);
        await RequireManagedWorkerSlot(db, runtime);
        ValidatePath(input.Directory);
        if (input.Role is not (SessionRoles.Worker or SessionRoles.Coordinator)) throw new ControlException("Choose Worker or Coordinator role.", 400);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 120 || input.Project.Length > 120)
            throw new ControlException("A worker name and bounded project name are required.", 400);
        if (input.Repository is not null) ValidatePath(input.Repository);
        var equivalent = await db.Runtimes.Where(x => x.Host == runtime.Host && x.Port == runtime.Port && x.Username == runtime.Username).Select(x => x.Id).ToListAsync();
        var directory = input.Directory.TrimEnd('/');
        var owner = await db.Workers.FirstOrDefaultAsync(x => equivalent.Contains(x.RuntimeId) && x.Directory == directory);
        if (owner is not null) throw new ControlException($"Workspace is already used by '{owner.Name}'{(owner.Archived ? " (archived)" : "")}. Open or edit that worker to continue its conversation. To replace it, delete its registration first; dismissing a setup card does not delete a worker.");
        if (await db.Workers.CountAsync() >= options.Value.MaxWorkers && !await db.Commands.AnyAsync(x => x.Id == input.Id))
            throw new ControlException("Worker registration limit reached.");
        return await Record(db, input.Id, input.RuntimeId, null, "CreateWorker", Json.Write(input));
    });

    public Task<CommandRecord> InspectWorkspace(InspectWorkspaceInput input) => Write(async db =>
    {
        RequireDevelopmentRuntime(await db.Runtimes.FindAsync(input.RuntimeId) ?? throw new ControlException("Runtime not found.", 404));
        ValidatePath(input.Directory);
        return await Record(db, input.Id, input.RuntimeId, null, "InspectWorkspace", Json.Write(input));
    });

    public Task<CommandRecord> Prompt(string workerId, PromptInput input) => Write(db => EnqueuePrompt(db, workerId, input));

    private async Task<CommandRecord> EnqueuePrompt(ControlDb db, string workerId, PromptInput input, string origin = "owner")
    {
        var worker = await db.Workers.FindAsync(workerId) ?? throw new ControlException("Worker not found.", 404);
        var payload = Json.Write(input);
        if (await db.Commands.FindAsync(input.Id) is { } prior) return Same(prior, worker.RuntimeId, workerId, "Prompt", payload);
        if (worker.Role == SessionRoles.Coordinator && !origin.StartsWith("coordinator-decision:", StringComparison.Ordinal))
            throw new ControlException("Coordinators route work only. Send instructions through Coordination.");
        if (worker.Role != SessionRoles.Coordinator && origin.StartsWith("coordinator-decision:", StringComparison.Ordinal))
            throw new ControlException("Routing decisions require a coordinator session.");
        if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > options.Value.MaxPromptCharacters)
            throw new ControlException($"Prompt must contain 1–{options.Value.MaxPromptCharacters} characters.", 400);
        var rendered = AssignmentGuidance.Render(input, worker.Directory);
        if (rendered.Length > options.Value.MaxPromptCharacters) throw new ControlException("Rendered instruction exceeds the prompt limit.", 400);
        if (worker.Archived) throw new ControlException("Restore this worker before sending instructions.");
        if (worker.Revision != input.ExpectedRevision) throw new ControlException("Worker changed; refresh and review before sending.");
        if (input.StatusInquiry && worker.LastStatusInquiryAt > Now - 60000) throw new ControlException("Status inquiries have a 60-second cooldown; observed state is already available.");
        if (input.StatusInquiry) worker.LastStatusInquiryAt = Now;
        if (await db.Commands.CountAsync(x => x.WorkerId == workerId && x.State == Delivery.Queued) >= options.Value.QueueLimit)
            throw new ControlException("Worker queue is full.");
        var command = await Record(db, input.Id, worker.RuntimeId, workerId, "Prompt", payload);
        command.Origin = origin;
        command.ExecutionPayload = Json.Write(input with
        {
            Text = rendered,
            ProviderId = input.ProviderId ?? worker.ProviderId,
            ModelId = input.ModelId ?? worker.ModelId,
            Agent = input.Agent ?? worker.Agent,
            Variant = input.Variant ?? worker.Variant
        });
        db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = workerId, Prompt = rendered, TemplateVersion = input.IncludeGuidance ? AssignmentGuidance.Version : "manual-v1" });
        worker.Revision++;
        return command;
    }

    public Task<CommandRecord> Abort(string workerId, string id) => Write(async db =>
    {
        var worker = await db.Workers.FindAsync(workerId) ?? throw new ControlException("Worker not found.", 404);
        return await Record(db, id, worker.RuntimeId, workerId, "Abort", "{}");
    });

    public Task<CommandRecord> Reply(ReplyInput input) => Write(db => EnqueueReply(db, input));

    private async Task<CommandRecord> EnqueueReply(ControlDb db, ReplyInput input, string origin = "owner")
    {
        var request = await db.Requests.FindAsync(input.RequestId) ?? throw new ControlException("Request not found.", 404);
        var worker = await db.Workers.FindAsync(request.WorkerId) ?? throw new ControlException("Worker not found.", 404);
        var payload = Json.Write(input);
        if (await db.Commands.FindAsync(input.Id) is { } prior) return Same(prior, worker.RuntimeId, worker.Id, "Reply", payload);
        if (request.State == "NoLongerPending")
            throw new ControlException("This request is no longer pending on the worker. No new reply was sent; its disappearance does not confirm approval.");
        if (request.State != "Pending" || request.ReplyCommandId is not null)
            throw new ControlException("This request already has a recorded or uncertain reply. Refresh its status before responding again.");
        if (request.Kind == "permission" && input.Permission is not ("once" or "always" or "reject")) throw new ControlException("Select a native permission decision.", 400);
        if (request.Kind == "question" && !input.Reject && (input.Answers is null || Json.Write(input.Answers).Length > 16000)) throw new ControlException("Question answers are required and must be bounded.", 400);
        if (request.Kind == "question" && !input.Reject) InteractiveRequests.ValidateAnswers(request.Json, input.Answers);
        var result = await Record(db, input.Id, worker.RuntimeId, worker.Id, "Reply", payload);
        result.Origin = origin;
        request.ReplyCommandId = result.Id;
        return result;
    }

    private static CommandRecord Same(CommandRecord command, string runtimeId, string? workerId, string kind, string payload)
    {
        if (command.RuntimeId != runtimeId || command.WorkerId != workerId || command.Kind != kind || command.Payload != payload)
            throw new ControlException("Request ID was already used for different content or routing.");
        return command;
    }

    private async Task<CommandRecord> Record(ControlDb db, string id, string runtimeId, string? workerId, string kind, string payload)
    {
        ValidateRequestId(id);
        if (await db.Commands.FindAsync(id) is { } prior) return Same(prior, runtimeId, workerId, kind, payload);
        if (await db.Commands.CountAsync() >= options.Value.MaxCommandRecords)
            throw new ControlException("Command audit limit reached. Back up the database and increase Control:MaxCommandRecords deliberately; request IDs are never silently forgotten.");
        var result = new CommandRecord { Id = id, RuntimeId = runtimeId, WorkerId = workerId, Kind = kind, Payload = payload };
        db.Commands.Add(result);
        Event(db, kind + "Recorded", runtimeId, workerId, id, provenance: "user");
        return result;
    }

    public Task<CommandRecord> EditQueue(string id, string action) => Write(async db =>
    {
        var command = await db.Commands.FindAsync(id) ?? throw new ControlException("Command not found.", 404);
        if (action == "resolveUnknown" && command.State == Delivery.Unknown)
        {
            command.State = Delivery.Cancelled;
            command.Detail = "Owner acknowledged uncertain delivery; this command will never be replayed. Inspect native state before sending new work.";
            if (command.Kind == "Reply")
            {
                var request = await db.Requests.SingleOrDefaultAsync(x => x.ReplyCommandId == command.Id);
                if (request is not null && request.State is "Pending" or "ReplyUnknown")
                { request.ReplyCommandId = null; request.State = "Pending"; }
            }
        }
        else
        {
            if (command.State != Delivery.Queued) throw new ControlException("Only undelivered commands can be reordered/cancelled.");
            if (command.Kind is not ("Prompt" or "CreateWorker")) throw new ControlException("Use the dedicated runtime or native request controls for this operation.");
            switch (action)
            {
                case "cancel": command.State = Delivery.Cancelled; break;
                case "first": command.QueueOrder = (await db.Commands.MinAsync(x => (long?)x.QueueOrder) ?? Now) - 1; break;
                default: throw new ControlException("Unknown queue action.", 400);
            }
        }
        command.UpdatedAt = Now;
        if (command.State == Delivery.Cancelled && await db.Assignments.FindAsync(id) is { } assignment) assignment.Outcome = "Cancelled";
        if (command.State == Delivery.Cancelled && command.Kind == "CreateWorker")
            await db.WorkspaceClaims.Where(x => x.CommandId == id && x.WorkerId == null).ExecuteDeleteAsync();
        Event(db, "CommandEdited", command.RuntimeId, command.WorkerId, id, new { action }, "user");
        return command;
    });

    // Navigation needs identities and status only. Project in SQL so transcript,
    // command evidence and model catalogs never enter the sidebar refresh path.
    public Task<ControlSnapshot> NavigationSnapshot() => Read(async db => new ControlSnapshot(
        await db.Events.MaxAsync(x => (long?)x.Sequence) ?? 0,
        await db.Runtimes.AsNoTracking().Select(x => new RuntimeRecord { Id = x.Id, Name = x.Name }).ToListAsync(),
        await db.Workers.AsNoTracking().Select(x => new WorkerRecord
        {
            Id = x.Id,
            RuntimeId = x.RuntimeId,
            Name = x.Name,
            Project = x.Project,
            Description = x.Description,
            Role = x.Role,
            Archived = x.Archived,
            Stale = x.Stale,
            Activity = x.Activity
        }).ToListAsync(), [], []));

    public Task<ControlSnapshot> Snapshot() => Read(async db => new ControlSnapshot(
        await db.Events.MaxAsync(x => (long?)x.Sequence) ?? 0, await db.Runtimes.AsNoTracking().ToListAsync(),
        await db.Workers.AsNoTracking().ToListAsync(),
        await db.Commands.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(500)
            .Union(db.Commands.AsNoTracking().Where(x => x.Kind == "CreateWorker" && !x.Dismissed && x.State != Delivery.Finished)).ToListAsync(),
        await db.Requests.AsNoTracking().Where(x => x.State == "Pending" || x.State == "ReplyUnknown").ToListAsync()));

    public Task<WorkerDetail> Detail(string id, long? before = null) => Read(async db => new WorkerDetail(
        await db.Workers.FindAsync(id) ?? throw new ControlException("Worker not found.", 404),
        await db.Messages.Where(x => x.WorkerId == id && (before == null || x.NativeCreatedAt < before))
            .OrderByDescending(x => x.NativeCreatedAt).Take(options.Value.HistoryLimit).ToListAsync(),
        await db.Commands.Where(x => x.WorkerId == id).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(),
        await db.Requests.Where(x => x.WorkerId == id).ToListAsync(),
        await db.Assignments.Where(x => x.WorkerId == id).ToListAsync()));

    public Task<bool> SetOutcome(string workerId, OutcomeInput input) => Write(async db =>
    {
        if (input.Outcome is not ("ReportedComplete" or "VerifiedComplete" or "Blocked" or "Failed" or "Cancelled"))
            throw new ControlException("Invalid assignment outcome.", 400);
        if (string.IsNullOrWhiteSpace(input.Evidence) || input.Evidence.Length > 16000) throw new ControlException("Record bounded supporting evidence.", 400);
        var worker = await db.Workers.FindAsync(workerId) ?? throw new ControlException("Worker not found.", 404);
        worker.Outcome = input.Outcome; worker.Revision++;
        var command = await db.Commands.Where(x => x.WorkerId == workerId && x.Kind == "Prompt").OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (command is not null && await db.Assignments.FindAsync(command.Id) is { } assignment)
        { assignment.Outcome = input.Outcome; assignment.Evidence = input.Evidence; }
        Event(db, "AssignmentOutcomeRecorded", worker.RuntimeId, workerId, command?.Id, input, "user");
        return true;
    });

    public Task<bool> Recover() => Write(async db =>
    {
        foreach (var runtime in await db.Runtimes.ToListAsync()) { runtime.Transport = "Disconnected"; runtime.Health = "Unknown"; }
        foreach (var worker in await db.Workers.ToListAsync()) { worker.Stale = true; worker.HistoryGap = true; }
        foreach (var command in await db.Commands.Where(x => x.State == Delivery.Dispatching).ToListAsync())
        {
            command.State = command.Kind is "EnsureServer" or "RefreshState" or "DisconnectRuntime" ? Delivery.Queued : Delivery.Unknown;
            command.Detail = "Backend restarted during dispatch; reconcile native evidence before any further mutation.";
        }
        Event(db, "BackendStarted");
        return true;
    });
}
