using System.Text.Json;
using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public const string ControlDirectory = "/var/lib/opencode/workspaces/control";

    public Task<List<ControlServiceView>> ControlServices() => Read(async db =>
    {
        var services = await db.ControlServices.AsNoTracking().ToListAsync();
        var result = new List<ControlServiceView>();
        foreach (var service in services)
            result.Add(new(service, (await db.Runtimes.FindAsync(service.Id))!,
                await db.ControlSessions.AsNoTracking().Where(x => x.ControlServiceId == service.Id).ToListAsync()));
        return result;
    });

    public Task<ControlServiceRecord> RegisterControlService(RegisterControlServiceInput input, ControlServiceIdentity identity) => Write(async db =>
    {
        if (!Guid.TryParseExact(input.Id, "N", out _) || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 120)
            throw new ControlException("Use a new service ID and a name of 1–120 characters.", 400);
        _ = secrets.PathFor(input.PasswordReference);
        if (await db.ControlServices.FindAsync(input.Id) is { } prior)
        {
            var connection = (await db.Runtimes.FindAsync(input.Id))!;
            if (prior.Endpoint != input.Endpoint || prior.InstanceId != input.ExpectedInstanceId || connection.ServerPasswordReference != input.PasswordReference || connection.Name != input.Name)
                throw new ControlException("Service ID already belongs to a different connection.");
            return prior;
        }
        if (await db.Runtimes.FindAsync(input.Id) is not null || await db.ControlServices.AnyAsync(x => x.InstanceId == identity.InstanceId))
            throw new ControlException("This runtime identity or sidecar instance is already registered.");
        if (await db.ControlServices.AnyAsync()) throw new ControlException("This host already owns a control service. Add a workgroup session to that service.");
        var endpoint = new Uri(input.Endpoint);
        var runtime = new RuntimeRecord
        {
            Id = input.Id,
            ConnectionKind = RuntimeConnections.ControlHttp,
            Name = input.Name,
            Host = endpoint.Host,
            ApiPort = endpoint.Port,
            ServerPasswordReference = input.PasswordReference,
            ManagedServerId = identity.InstanceId,
            AllowedRoots = ControlDirectory,
            StateDirectory = "/var/lib/opencode/state",
            Capacity = 2,
            InstallIfMissing = false,
            DesiredConnected = true,
            Revision = 1,
            Diagnostic = "Host-owned control service registered; connecting over its private HTTP endpoint."
        };
        var service = new ControlServiceRecord
        {
            Id = input.Id,
            Endpoint = input.Endpoint,
            InstanceId = identity.InstanceId,
            IncarnationId = identity.IncarnationId,
            StartedAt = identity.StartedAt,
            LastObservedAt = Now,
            Revision = 1
        };
        db.Runtimes.Add(runtime); db.ControlServices.Add(service);
        // There is one host-operations scope in this single-replica host. Its conversation survives UI deployments.
        await QueueControlSession(db, service, new(Guid.NewGuid().ToString(), "HostOperations", "host", "Host operations"));
        Event(db, "ControlServiceRegistered", runtime.Id, payload: new { service.Id, service.Endpoint, service.InstanceId }, provenance: "user");
        return service;
    });

    public Task<ControlSessionBinding> CreateControlSession(string serviceId, CreateControlSessionInput input) => Write(async db =>
    {
        var service = await db.ControlServices.FindAsync(serviceId) ?? throw new ControlException("Control service not found.", 404);
        return await QueueControlSession(db, service, input);
    });

    private async Task<ControlSessionBinding> QueueControlSession(ControlDb db, ControlServiceRecord service, CreateControlSessionInput input)
    {
        ValidateRequestId(input.Id);
        if (input.ProviderId is null || input.ModelId is null || input.Variant is null || input.Agent is null ||
            input.ScopeKind is not ("HostOperations" or "Workgroup") ||
            string.IsNullOrWhiteSpace(input.ScopeId) || input.ScopeId.Length > 120 || input.ScopeId != input.ScopeId.Trim() || input.ScopeId.Any(char.IsControl) ||
            input.ScopeKind == "HostOperations" && input.ScopeId != "host" ||
            string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 120 || input.ProviderId.Length > 120 || input.ModelId.Length > 120 || input.Variant.Length > 40 || input.Agent.Length > 80 ||
            string.IsNullOrEmpty(input.ProviderId) != string.IsNullOrEmpty(input.ModelId))
            throw new ControlException("Choose HostOperations/host or a Workgroup scope and a bounded name; supply both provider and model together.", 400);
        var payload = Json.Write(input);
        if (await db.Commands.FindAsync(input.Id) is { } prior)
        {
            if (prior.Kind is not ("CreateControlSession" or "ControlSessionDiscovery")) throw new ControlException("Request ID belongs to another operation.");
            _ = Same(prior, service.Id, null, prior.Kind, prior.Payload);
            if (Json.Read<CreateControlSessionInput>(prior.Payload) != input)
                throw new ControlException("Request ID was already used with different parameters.");
            return (await db.ControlSessions.FindAsync(prior.ResultId)) ?? throw new ControlException("Control session receipt has no binding.");
        }
        // Scope is the durable semantic key; repeated provisioning discovers its original conversation.
        if (await db.ControlSessions.SingleOrDefaultAsync(x => x.ScopeKind == input.ScopeKind && x.ScopeId == input.ScopeId && x.IsCurrent) is { } existing)
        {
            if (existing.ControlServiceId != service.Id) throw new ControlException("This scope already belongs to another control service.");
            var original = Json.Read<CreateControlSessionInput>((await db.Commands.FindAsync(existing.CreationCommandId))!.Payload);
            if (original with { Id = input.Id } != input) throw new ControlException("Scope already exists with different settings. Edit its session settings instead.");
            var discovery = await Record(db, input.Id, service.Id, null, "ControlSessionDiscovery", payload);
            discovery.State = Delivery.Finished; discovery.ResultId = existing.Id;
            discovery.Detail = "Existing scope discovered with provisioning state " + existing.State + "; no new native creation was requested.";
            return existing;
        }
        if (await db.ControlSessions.CountAsync(x => x.IsCurrent) >= 64) throw new ControlException("Control session registration limit reached.");
        var binding = new ControlSessionBinding
        {
            Id = Guid.NewGuid().ToString("N"),
            ControlServiceId = service.Id,
            ScopeKind = input.ScopeKind,
            ScopeId = input.ScopeId,
            WorkerId = Guid.NewGuid().ToString("N"),
            CreationCommandId = input.Id
        };
        db.ControlSessions.Add(binding);
        var creation = await Record(db, input.Id, service.Id, null, "CreateControlSession", payload);
        creation.ResultId = binding.Id;
        Event(db, "ControlSessionRequested", service.Id, commandId: input.Id, payload: new { binding.Id, binding.ScopeKind, binding.ScopeId }, provenance: "user");
        return binding;
    }

    public Task<ControlSessionBinding> RetryControlSession(string serviceId, string sessionId, RetryControlSessionInput input) => Write(async db =>
    {
        ValidateRequestId(input.Id);
        var binding = await db.ControlSessions.FindAsync(sessionId) ?? throw new ControlException("Control session not found.", 404);
        if (binding.ControlServiceId != serviceId) throw new ControlException("Control session belongs to another service.");
        var payload = Json.Write(new { sessionId, input });
        if (await db.Commands.FindAsync(input.Id) is { } prior)
        { _ = Same(prior, serviceId, null, "RetryControlSession", payload); return binding; }
        if (binding.Revision != input.ExpectedRevision) throw new ControlException("Control session changed; refresh before retrying.");
        var previous = (await db.Commands.FindAsync(binding.CreationCommandId))!;
        if (binding.State == "Ready" || binding.NativeSessionId.Length > 0 ||
            !(previous.State == Delivery.Failed || previous.State == Delivery.Cancelled && previous.Attempts == 0))
            throw new ControlException("Retry requires a known rejected or never-dispatched creation. Uncertain delivery is reconciled without replaying it.");
        var nextId = Guid.NewGuid().ToString();
        var original = Json.Read<CreateControlSessionInput>(previous.Payload);
        var creation = await Record(db, nextId, serviceId, null, "CreateControlSession", Json.Write(original with { Id = nextId }));
        creation.ResultId = binding.Id;
        binding.CreationCommandId = nextId; binding.State = "Queued"; binding.Detail = "Creation retry recorded after a known unsent/rejected attempt."; binding.Revision++;
        var receipt = await Record(db, input.Id, serviceId, null, "RetryControlSession", payload);
        receipt.State = Delivery.Finished; receipt.ResultId = binding.Id; receipt.Detail = binding.Detail;
        Event(db, "ControlSessionCreationRetried", serviceId, commandId: receipt.Id, payload: new { binding.Id, previousCommandId = previous.Id, nextId }, provenance: "user");
        return binding;
    });

    public Task<ControlSessionRenewalResult> RenewControlSession(string serviceId, string sessionId, RenewControlSessionInput input) => Write(async db =>
    {
        ValidateRequestId(input.Id);
        var service = await db.ControlServices.FindAsync(serviceId) ?? throw new ControlException("Control service not found.", 404);
        var predecessor = await db.ControlSessions.FindAsync(sessionId) ?? throw new ControlException("Control session not found.", 404);
        if (predecessor.ControlServiceId != service.Id) throw new ControlException("Control session belongs to another service.");
        var payload = Json.Write(new { sessionId, input });
        if (await db.Commands.FindAsync(input.Id) is { } prior)
        {
            _ = Same(prior, serviceId, null, "RenewControlSession", payload);
            var existing = await db.ControlSessions.FindAsync(prior.ResultId) ?? throw new ControlException("Renewal receipt has no successor binding.");
            return RenewalResult(existing, prior);
        }
        if (predecessor.Revision != input.ExpectedRevision) throw new ControlException("Control session changed; refresh before renewing.");
        if (predecessor.ScopeKind != "Workgroup") throw new ControlException("Only workgroup control sessions can be renewed through coordination migration.");
        if (!predecessor.IsCurrent || predecessor.State != "Ready" || predecessor.NativeSessionId.Length == 0)
            throw new ControlException("Renew the current ready workgroup control session.");
        if (await db.ControlSessions.AnyAsync(x => x.PredecessorId == predecessor.Id && x.State != Delivery.Failed && x.State != Delivery.Cancelled))
            throw new ControlException("This control session already has a successor. Inspect its provisioning state before cutover.");

        var predecessorWorker = await db.Workers.FindAsync(predecessor.WorkerId)
            ?? throw new ControlException("Current control session worker is missing.");
        var successor = await QueueSuccessorGeneration(db, service, predecessor, predecessorWorker, "OwnerRenewal");
        var receipt = await Record(db, input.Id, service.Id, null, "RenewControlSession", payload);
        receipt.State = Delivery.Finished; receipt.ResultId = successor.Id;
        receipt.Detail = "Successor generation recorded. The predecessor remains current until an explicit paused migration.";
        Event(db, "ControlSessionRenewalRequested", service.Id, commandId: receipt.Id,
            payload: new { predecessorId = predecessor.Id, successorId = successor.Id, successor.Generation, creationCommandId = successor.CreationCommandId },
            provenance: "user", generation: successor.Generation);
        return RenewalResult(successor, receipt);
    });

    private async Task<ControlSessionBinding> QueueSuccessorGeneration(ControlDb db, ControlServiceRecord service,
        ControlSessionBinding predecessor, WorkerRecord predecessorWorker, string generationReason,
        string? recoveryIntentId = null, string? recoveryRunId = null, string? recoverySourceCommandId = null,
        long? recoveryOwnerPolicyRevision = null, string? recoveryInstructionHash = null)
    {
        var latestGeneration = await db.ControlSessions.Where(x => x.ScopeKind == predecessor.ScopeKind && x.ScopeId == predecessor.ScopeId)
            .MaxAsync(x => (int?)x.Generation) ?? predecessor.Generation;
        if (latestGeneration == int.MaxValue) throw new ControlException("Control session generation limit reached.");
        var creationId = Guid.NewGuid().ToString();
        var successor = new ControlSessionBinding
        {
            Id = Guid.NewGuid().ToString("N"),
            ControlServiceId = service.Id,
            ScopeKind = predecessor.ScopeKind,
            ScopeId = predecessor.ScopeId,
            WorkerId = Guid.NewGuid().ToString("N"),
            CreationCommandId = creationId,
            Generation = latestGeneration + 1,
            GenerationReason = generationReason,
            PredecessorId = predecessor.Id,
            IsCurrent = false,
            RecoveryIntentId = recoveryIntentId,
            RecoveryRunId = recoveryRunId,
            RecoverySourceCommandId = recoverySourceCommandId,
            RecoveryOwnerPolicyRevision = recoveryOwnerPolicyRevision,
            RecoveryInstructionHash = recoveryInstructionHash,
            Detail = "Successor generation is waiting for the control service. The predecessor remains current."
        };
        db.ControlSessions.Add(successor);
        var creationInput = new CreateControlSessionInput(creationId, predecessor.ScopeKind, predecessor.ScopeId,
            predecessorWorker.Name, predecessorWorker.ProviderId, predecessorWorker.ModelId, predecessorWorker.Variant, predecessorWorker.Agent);
        var creation = await Record(db, creationId, service.Id, null, "CreateControlSession", Json.Write(creationInput));
        creation.ResultId = successor.Id;
        predecessor.Revision++;
        return successor;
    }

    private static ControlSessionRenewalResult RenewalResult(ControlSessionBinding successor, CommandRecord receipt) =>
        new(successor, new(receipt.Id, receipt.State, successor.Id, receipt.CreatedAt));

    public Task<bool> BindControlSession(string commandId, JsonElement native, List<ModelChoice> models) => Write(async db =>
    {
        var command = await db.Commands.FindAsync(commandId) ?? throw new ControlException("Creation command not found.", 404);
        var binding = await db.ControlSessions.SingleAsync(x => x.CreationCommandId == commandId);
        var nativeId = native.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(nativeId) || !nativeId.StartsWith("ses_", StringComparison.Ordinal) || nativeId.Length > 120 ||
            native.GetProperty("directory").GetString() != ControlDirectory || native.GetProperty("title").GetString() != binding.Title)
            throw new ControlException("Native control session does not match its durable creation intent.");
        if (binding.State == "Ready")
        {
            if (binding.NativeSessionId != nativeId) throw new ControlException("Control scope is already bound to a different native session.");
            return false;
        }
        var runtime = (await db.Runtimes.FindAsync(binding.ControlServiceId))!;
        var input = Json.Read<CreateControlSessionInput>(command.Payload);
        db.Workers.Add(new WorkerRecord
        {
            Id = binding.WorkerId,
            RuntimeId = runtime.Id,
            ManagedServerId = runtime.ManagedServerId,
            NativeSessionId = nativeId,
            Directory = ControlDirectory,
            Role = SessionRoles.Coordinator,
            Name = input.Name,
            Project = input.ScopeId,
            Description = "Host-owned " + input.ScopeKind + " control session",
            ProviderId = input.ProviderId,
            ModelId = input.ModelId,
            Agent = input.Agent,
            Variant = input.Variant,
            ModelsJson = Json.Write(models),
            Archived = !binding.IsCurrent
        });
        binding.NativeSessionId = nativeId; binding.State = "Ready";
        binding.Detail = binding.IsCurrent ? "Persistent control conversation is bound." : "Successor generation is ready for an explicit paused migration.";
        binding.Revision++;
        command.State = Delivery.Finished; command.ResultId = binding.Id; command.Detail = binding.Detail; command.UpdatedAt = Now;
        Event(db, "ControlSessionBound", runtime.Id, binding.WorkerId, command.Id,
            new { binding.Id, nativeId, binding.ScopeKind, binding.ScopeId, binding.Generation, binding.PredecessorId }, generation: binding.Generation);
        return true;
    });

    public Task<bool> ObserveControlService(string id, ControlServiceIdentity identity) => Write(async db =>
    {
        var service = await db.ControlServices.FindAsync(id) ?? throw new ControlException("Control service not found.", 404);
        if (service.InstanceId != identity.InstanceId) throw new ControlException("Control service instance changed. Rebinding is required; no prompts were sent.");
        if (service.IncarnationId != identity.IncarnationId)
        {
            var old = service.IncarnationId;
            Event(db, "ControlServiceRestartObserved", id, payload: new { previousIncarnationId = old, identity.IncarnationId, identity.StartedAt });
            foreach (var command in await db.Commands.Where(x => x.RuntimeId == id && x.Kind == "Prompt" &&
                (x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)).ToListAsync())
            {
                command.State = Delivery.Unknown; command.UpdatedAt = Now;
                command.Detail = "Control service restarted during this instruction. Native history will be reconciled; the original instruction will not be replayed.";
                Event(db, "ControlServiceCommandInterrupted", id, command.WorkerId, command.Id, new { previousIncarnationId = old, identity.IncarnationId, command.NativeMessageId });
                var run = await db.CoordinationRuns.SingleOrDefaultAsync(x => x.DecisionCommandId == command.Id && x.CoordinatorWorkerId == command.WorkerId);
                if (run is not null && command.Origin == "coordinator-decision:" + run.Id)
                {
                    // Only routing proposals are side-effect free: OpenCodeClient disables all tools for
                    // this exact origin. Retire the proposal's authority, never claim its native delivery
                    // failed and never replay it. Worker execution commands cannot enter this path.
                    command.State = Delivery.Cancelled;
                    command.Detail = "Routing proposal superseded after a control service restart. Its native delivery may have completed; no routing actions were applied. A fresh decision will use current evidence.";
                    run.DecisionCommandId = null; run.LastObservation = ""; run.Revision++;
                    if (run.State is "Ready" or "Waiting" or "Deciding" or "Recovering")
                        ScheduleCoordinationRecovery(db, run, "Control service restarted. The previous unapplied proposal was retired; request a fresh decision without replaying it.");
                    Event(db, "ControlDecisionAuthorityRetired", id, command.WorkerId, command.Id,
                        new { run.Id, nativeDelivery = "Unknown", replacementIncarnationId = identity.IncarnationId, actionDispatch = "None" });
                }
            }
            service.IncarnationId = identity.IncarnationId; service.StartedAt = identity.StartedAt; service.Revision++;
        }
        service.LastObservedAt = Now;
        return true;
    });

    public Task<CoordinationRun> MigrateControlSession(string runId, MigrateControlSessionInput input) => Write(async db =>
    {
        ValidateRequestId(input.Id);
        var run = await db.CoordinationRuns.FindAsync(runId) ?? throw new ControlException("Coordination not found.", 404);
        var binding = await db.ControlSessions.FindAsync(input.ControlSessionId) ?? throw new ControlException("Control session not found.", 404);
        var target = await db.Workers.FindAsync(binding.WorkerId) ?? throw new ControlException("Wait for the control session to be ready.");
        var payload = Json.Write(new { runId, input });
        if (await db.Commands.FindAsync(input.Id) is { } prior) { _ = Same(prior, target.RuntimeId, target.Id, "MigrateControlSession", payload); return run; }
        if (run.Revision != input.ExpectedRevision || run.State != "Paused") throw new ControlException("Pause the coordination and refresh its revision before migration.");
        var source = await db.Workers.FindAsync(run.CoordinatorWorkerId) ?? throw new ControlException("Coordinator not found.", 404);
        if (target.Id == source.Id) throw new ControlException("Choose a different control session for migration.");
        var sourceBinding = await db.ControlSessions.SingleOrDefaultAsync(x => x.WorkerId == source.Id);
        var generationCutover = sourceBinding is not null && sourceBinding.IsCurrent && !binding.IsCurrent && binding.PredecessorId == sourceBinding.Id;
        var targetRuntime = (await db.Runtimes.FindAsync(target.RuntimeId))!;
        if (!targetRuntime.DesiredConnected || targetRuntime.Transport != "Connected" || targetRuntime.Health != "Healthy" || target.Archived && !generationCutover)
            throw new ControlException("Connect and observe the target control service before migration.");
        ValidateModelOptions(Json.Read<List<ModelChoice>>(target.ModelsJson), target.ProviderId, target.ModelId, target.Agent, target.Variant);
        if (binding.ScopeKind != "Workgroup" || binding.State != "Ready" || (!binding.IsCurrent && !generationCutover) ||
            target.Stale || target.Activity != "Idle" || target.LastObservedAt is null || target.LastObservedAt < Now - 15000 ||
            source.Stale || source.Activity != "Idle" || source.LastObservedAt is null || source.LastObservedAt < Now - 15000)
            throw new ControlException("Choose a ready, freshly observed idle workgroup control session.");
        if (run.DecisionCommandId is { } decisionId && (await db.Commands.FindAsync(decisionId) is not { } decision || decision.State is not (Delivery.Finished or Delivery.Failed or Delivery.Cancelled)))
            throw new ControlException("Resolve the pending decision before migration. Its delivery is unchanged.");
        var cutoverWorkerIds = new[] { target.Id, source.Id };
        if (await db.Requests.AnyAsync(x => cutoverWorkerIds.Contains(x.WorkerId) && (x.State == "Pending" || x.State == "ReplyUnknown")) ||
            await db.CoordinationRuns.AnyAsync(x => x.CoordinatorWorkerId == target.Id && x.Id != runId && x.State != "Completed" && x.State != "Stopped") ||
            await db.Commands.AnyAsync(x => (x.WorkerId == target.Id || x.WorkerId == run.CoordinatorWorkerId) &&
                (x.State == Delivery.Queued || x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)))
            throw new ControlException("Drain both control sessions and resolve pending requests before migration; existing commands and work remain independent.");
        var previousWorkerId = run.CoordinatorWorkerId;
        var previousDecisionCommandId = run.DecisionCommandId;
        run.CoordinatorWorkerId = target.Id; run.DecisionCommandId = null; run.LastObservation = ""; run.InputJson = "{}";
        run.OwnerPolicyRevision++; run.Revision++;
        run.Detail = "Control session migrated. Resume to evaluate fresh evidence; historical decisions and worker assignments are retained.";
        if (generationCutover)
        {
            sourceBinding!.IsCurrent = false; sourceBinding.Detail = "Predecessor generation retained after explicit coordination migration."; sourceBinding.Revision++;
            source.Archived = true; source.Revision++;
            // Release the filtered unique current-scope claim before assigning it to the successor.
            // Both writes remain in this transaction, so readers cannot observe an ownerless scope.
            await db.SaveChangesAsync();
            binding.IsCurrent = true; binding.Detail = "Current persistent control conversation is bound."; binding.Revision++;
            target.Archived = false; target.Revision++;
        }
        var receipt = await Record(db, input.Id, target.RuntimeId, target.Id, "MigrateControlSession", payload);
        receipt.State = Delivery.Finished; receipt.Detail = run.Detail;
        Event(db, "CoordinationControlSessionMigrated", target.RuntimeId, target.Id, receipt.Id,
            new { run.Id, previousWorkerId, previousDecisionCommandId, controlSessionId = binding.Id, binding.NativeSessionId, binding.Generation, predecessorId = sourceBinding?.Id, generationCutover },
            provenance: "user", generation: binding.Generation);
        return run;
    });

    private static void RequireDevelopmentRuntime(RuntimeRecord runtime)
    {
        if (runtime.ConnectionKind != RuntimeConnections.Ssh)
            throw new ControlException("This host-owned control service does not provide development workspaces, SSH terminals, or worker lifecycle operations.", 409);
    }
}
