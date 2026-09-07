using System.Collections.Concurrent;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Ssh;
using HVO.AgentControl.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.Services;

public sealed class RuntimeSupervisor(ControlStore store, IRuntimeTransportFactory transports,
    IOptions<ControlOptions> options, ILogger<RuntimeSupervisor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, Task> loops = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.Recover();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var ids = await store.Read(db => db.Runtimes.Select(x => x.Id).ToListAsync(stoppingToken));
                foreach (var removed in loops.Where(x => !ids.Contains(x.Key) && x.Value.IsCompleted).Select(x => x.Key)) loops.TryRemove(removed, out _);
                foreach (var id in ids) _ = loops.GetOrAdd(id, _ => Task.Run(() => RunRuntime(id, stoppingToken), stoppingToken));
                await Retain();
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(loops.Values); }
    }

    private async Task RunRuntime(string id, CancellationToken token)
    {
        IRuntimeTransport? transport = null;
        CancellationTokenSource? streamCancellation = null;
        Task? reader = null;
        var incoming = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var streamOverflow = 0;
        var failures = 0;
        long lastHealth = 0;
        long lastStreamFrame = 0;
        async Task Close()
        {
            if (streamCancellation is not null) await streamCancellation.CancelAsync();
            if (reader is not null) try { await reader; } catch (Exception) { /* classified below or during shutdown */ }
            streamCancellation?.Dispose(); streamCancellation = null; reader = null;
            if (transport is not null) await transport.DisposeAsync();
            transport = null;
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var runtime = await store.Read(async db => await db.Runtimes.FindAsync(id));
                    if (runtime is null) return;
                    if (!runtime.DesiredConnected)
                    {
                        var stoppedOwned = false;
                        var stop = await Claim(id, "StopManagedServer");
                        if (stop is not null)
                        {
                            try
                            {
                                if (transport is null) throw new ControlException("Connect and inspect the owned server before requesting stop.");
                                await transport.StopOwnedServer(token);
                                stoppedOwned = true;
                                await Complete(stop.Id, Delivery.Finished, "Owned server stopped; affected workers require reconciliation on next connect.");
                            }
                            catch (Exception ex) { await Complete(stop.Id, ex is ControlException ? Delivery.Failed : Delivery.Unknown, SafeError(ex)); }
                        }
                        await Close();
                        if (runtime.Transport != "Disconnected") await MarkDisconnected(id, "Disconnected",
                            stoppedOwned ? "Owned server deliberately stopped; its worker sessions are unavailable until reconnect." : "Control transport disconnected; remote work may continue.", stoppedOwned ? "Stopped" : "Unknown");
                        await FinishRuntimeCommands(id, "DisconnectRuntime");
                        await Task.Delay(options.Value.PollMilliseconds, token); continue;
                    }
                    if (transport is null)
                    {
                        await store.Write(async db =>
                        {
                            var record = (await db.Runtimes.FindAsync(id))!;
                            record.Transport = "Connecting"; record.Health = "Starting"; record.Diagnostic = "Validating SSH identity and ensuring the owned tmux server.";
                            ControlStore.Event(db, "BootstrapStarted", id); return true;
                        });
                        transport = await transports.Connect(runtime, token);
                        lastHealth = 0;
                        var capabilities = await transport.ProbeCapabilities(ControlStore.Roots(runtime)[0], token);
                        var version = await transport.Api.Verify(token);
                        var models = await transport.Api.Models(ControlStore.Roots(runtime)[0], token);
                        await store.Write(async db =>
                        {
                            var record = (await db.Runtimes.FindAsync(id))!;
                            record.Transport = "Connected"; record.Health = "Reconciling"; record.Version = version;
                            record.InstalledExecutable = transport.InstalledExecutable; record.Platform = transport.Platform; record.CapabilitiesJson = Json.Write(capabilities); record.Generation++; record.ModelsJson = Json.Write(models);
                            if (RuntimeTelemetryProjection.FromCapabilities(record.CapabilitiesJson, $"{record.Id}:{record.Generation}") is { } telemetry)
                                await ControlStore.RecordTelemetry(db, record.Id, telemetry.Result);
                            record.ProviderState = models.Count == 0 ? "ProviderSetupRequired" : "ModelsAvailable";
                            record.Diagnostic = models.Count == 0 ? "Run opencode auth login in this runtime, then refresh. Provider credentials remain remote." : "Connected; reconciling native sessions.";
                            ControlStore.Event(db, "RuntimeConnected", id, generation: record.Generation); return true;
                        });
                        failures = 0;
                    }
                    if (!transport.Connected) throw new IOException("SSH transport disconnected.");
                    if (reader is { IsCompleted: false } && ControlStore.Now - Interlocked.Read(ref lastStreamFrame) > 45000)
                    {
                        await streamCancellation!.CancelAsync();
                        try { await reader; } catch (OperationCanceledException) { }
                    }
                    if (reader is null || reader.IsCompleted)
                    {
                        if (reader is not null)
                        {
                            try { await reader; } catch (Exception ex) { logger.LogInformation("Runtime {RuntimeId} SSE ended ({Category})", id, ex.GetType().Name); }
                            await MarkGap(id, "SSE reconnected; transient events during the gap may be unavailable.");
                        }
                        streamCancellation?.Dispose(); streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var response = await transport.Api.Subscribe(token); // subscribe before snapshots and dispatch
                        Interlocked.Exchange(ref lastStreamFrame, ControlStore.Now);
                        var streamToken = streamCancellation.Token;
                        reader = Task.Run(async () =>
                        {
                            using (response)
                            await using (var stream = await response.Content.ReadAsStreamAsync(streamToken))
                            {
                                await foreach (var item in SseReader.Read(stream, cancellationToken: streamToken))
                                {
                                    Interlocked.Exchange(ref lastStreamFrame, ControlStore.Now);
                                    if (!incoming.Writer.TryWrite(item)) Interlocked.Exchange(ref streamOverflow, 1);
                                }
                            }
                        }, token);
                    }
                    if (Interlocked.Exchange(ref streamOverflow, 0) != 0) await MarkGap(id, "SSE buffer overflow; reconstructed from native snapshots.");
                    var events = new List<JsonElement>();
                    while (events.Count < 512 && incoming.Reader.TryRead(out var item)) events.Add(item);
                    if (events.Count > 0) await ObserveEvents(id, events);
                    var workers = await store.Read(db => db.Workers.Where(x => x.RuntimeId == id).AsNoTracking().ToListAsync(token));
                    foreach (var worker in workers)
                    {
                        try
                        {
                            var snapshot = await transport.Api.Snapshot(worker, options.Value.HistoryLimit, token);
                            await Reconcile(worker.Id, snapshot);
                        }
                        catch (NativeRejectedException ex) when (ex.Status == 404)
                        {
                            await store.Write(async db =>
                            {
                                var record = await db.Workers.FindAsync(worker.Id);
                                if (record is null) return false;
                                record.Stale = true; record.Activity = "MissingSession"; record.CurrentAction = "Native session is missing. No replacement session or prompt was created.";
                                return true;
                            });
                        }
                    }
                    if (ControlStore.Now - lastHealth > 15000)
                    {
                        _ = await transport.Api.Get("/global/health", token);
                        var models = await transport.Api.Models(ControlStore.Roots(runtime)[0], token);
                        await store.Write(async db =>
                        {
                            var record = (await db.Runtimes.FindAsync(id))!;
                            record.Health = "Healthy"; record.LastHealthyAt = ControlStore.Now;
                            record.ModelsJson = Json.Write(models); record.ProviderState = models.Count == 0 ? "ProviderSetupRequired" : "ModelsAvailable";
                            record.Diagnostic = models.Count == 0 ? "Provider setup required in the remote runtime." : "SSH, API and session reconciliation are healthy.";
                            return true;
                        });
                        lastHealth = ControlStore.Now;
                    }
                    await FinishRuntimeCommands(id, "EnsureServer");
                    await FinishRuntimeCommands(id, "RefreshState");
                    await ReconcileCreation(runtime, transport, token);
                    var next = await Claim(id);
                    if (next is not null) await Dispatch(next, runtime, transport, token);
                    await Task.Delay(options.Value.PollMilliseconds, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    failures++;
                    logger.LogWarning("Runtime {RuntimeId} connection/reconciliation failed ({Category})", id, ex.GetType().Name);
                    await Close();
                    try { await MarkDisconnected(id, "Reconnecting", SafeError(ex)); }
                    catch (Exception storageError) { logger.LogError("Persistence unavailable ({Category}); dispatch remains stopped", storageError.GetType().Name); }
                    var delay = Math.Min(30000, 500 * (1 << Math.Min(failures, 6))) + Random.Shared.Next(100, 500);
                    await Task.Delay(delay, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Close(); }
    }

    private Task<CommandRecord?> Claim(string runtimeId, string? kind = null) => store.Write(async db =>
    {
        var candidates = await db.Commands.Where(x => x.RuntimeId == runtimeId && x.State == Delivery.Queued)
            .OrderBy(x => x.QueueOrder).ThenBy(x => x.Id).ToListAsync();
        foreach (var command in candidates.OrderBy(x => x.Kind is "Abort" or "Reply" ? 0 : 1))
        {
            if (kind is not null ? command.Kind != kind : command.Kind is "EnsureServer" or "RefreshState" or "DisconnectRuntime" or "StopManagedServer") continue;
            if (command.Kind == "Prompt")
            {
                var worker = await db.Workers.FindAsync(command.WorkerId);
                if (worker is null || worker.Archived || worker.Stale || worker.Activity != "Idle") continue;
                var inFlight = await db.Commands.Where(x => x.Kind == "Prompt" && (x.State == Delivery.Dispatching || x.State == Delivery.Accepted || x.State == Delivery.Running || x.State == Delivery.Unknown)).ToListAsync();
                if (inFlight.Any(x => x.WorkerId == worker.Id)) continue;
                var taskWorkers = await db.Workers.Where(x => x.Role == SessionRoles.Worker).Select(x => x.Id).ToListAsync();
                var active = await db.Workers.Where(x => x.Role == SessionRoles.Worker && x.Activity != "Idle" && x.Activity != "Unknown" && x.Activity != "MissingSession").Select(x => x.Id).ToListAsync();
                var occupied = active.Concat(inFlight.Where(x => taskWorkers.Contains(x.WorkerId!)).Select(x => x.WorkerId!)).ToHashSet();
                var runtime = (await db.Runtimes.FindAsync(runtimeId))!;
                var runtimeWorkers = await db.Workers.Where(x => x.RuntimeId == runtimeId).Select(x => x.Id).ToListAsync();
                if (worker.Role == SessionRoles.Worker && (occupied.Count >= options.Value.GlobalCapacity || runtimeWorkers.Count(occupied.Contains) >= runtime.Capacity)) continue;
            }
            if (command.Kind is "Abort" or "Reply" && (await db.Workers.FindAsync(command.WorkerId))?.Stale != false) continue;
            command.State = Delivery.Dispatching; command.Attempts++; command.UpdatedAt = ControlStore.Now;
            ControlStore.Event(db, "CommandDispatching", runtimeId, command.WorkerId, command.Id);
            return command;
        }
        return null;
    });

    private async Task Dispatch(CommandRecord command, RuntimeRecord runtime, IRuntimeTransport transport, CancellationToken token)
    {
        var mutationStarted = false;
        try
        {
            var api = transport.Api;
            var worker = command.WorkerId is null ? null : await store.Read(async db => await db.Workers.FindAsync(command.WorkerId));
            switch (command.Kind)
            {
                case "InspectWorkspace":
                    var inspection = Json.Read<InspectWorkspaceInput>(command.Payload);
                    var inspected = await transport.Workspace(runtime, new(command.Id, runtime.Id, "inspect", "", inspection.Directory, "", ""), token);
                    var discovered = await api.Models(inspected.Directory, token);
                    await store.Write(async db =>
                    {
                        foreach (var existing in await db.Workers.Where(x => x.RuntimeId == runtime.Id && x.Directory == inspected.Directory).ToListAsync())
                            existing.ModelsJson = Json.Write(discovered);
                        return true;
                    });
                    await store.Write(async db =>
                    {
                        var record = (await db.Commands.FindAsync(command.Id))!;
                        record.ResultJson = Json.Write(new { inspected.Directory, inspected.Branch, models = discovered });
                        record.State = Delivery.Finished; record.Detail = "Workspace verified and provider/model discovery completed.";
                        ControlStore.Event(db, "WorkspaceInspected", runtime.Id, commandId: command.Id); return true;
                    });
                    break;
                case "CreateWorker":
                    var input = Json.Read<CreateWorkerInput>(command.Payload);
                    await CreationProgress(command.Id, "Checking workspace and preparing its worktree, when requested.");
                    // Worktree provisioning is itself a mutation; a lost SSH response must not repeat it.
                    mutationStarted = input.Repository is not null;
                    var workspace = await transport.Workspace(runtime, input, token);
                    if (await WorkspaceOwner(runtime, workspace.Directory) is { } workspaceOwner)
                        throw new ControlException($"Workspace is already used by '{workspaceOwner.Name}'{(workspaceOwner.Archived ? " (archived)" : "")}. Open or edit that worker, or delete its registration before replacing it. Use a separate workspace for another worker.");
                    await CreationProgress(command.Id, "Workspace checked. Discovering available models.");
                    var models = await api.Models(workspace.Directory, token);
                    if (!models.Any(x => x.ProviderId == input.ProviderId && x.ModelId == input.ModelId)) throw new ControlException("Selected provider/model is unavailable in this workspace; configure remote provider authentication.");
                    await ClaimWorkspace(command, runtime, workspace.Directory);
                    mutationStarted = true;
                    await CreationProgress(command.Id, "Creating the persistent OpenCode conversation.");
                    var session = await api.CreateSession(workspace.Directory, input.Name + " [hvo:" + command.Id + "]", token);
                    await SaveCreated(command, runtime, input, workspace, session, models);
                    break;
                case "Prompt":
                    if ((worker!.Role == SessionRoles.Coordinator) != command.Origin.StartsWith("coordinator-decision:", StringComparison.Ordinal))
                        throw new ControlException("Session role does not permit this instruction.");
                    if (command.Origin == "capability-report")
                    {
                        var facts = await transport.ProbeCapabilities(worker.Directory, token);
                        await store.Write(async db => { (await db.Workers.FindAsync(worker.Id))!.CapabilitiesJson = Json.Write(facts); return true; });
                    }
                    var prompt = Json.Read<PromptInput>(string.IsNullOrEmpty(command.ExecutionPayload) ? command.Payload : command.ExecutionPayload);
                    var choices = await api.Models(worker!.Directory, token);
                    if (!choices.Any(x => x.ProviderId == (prompt.ProviderId ?? worker.ProviderId) && x.ModelId == (prompt.ModelId ?? worker.ModelId)))
                        throw new ControlException("Provider/model unavailable; configure authentication on the runtime and submit a new request when ready.");
                    ControlStore.ValidateModelOptions(choices, prompt.ProviderId ?? worker.ProviderId, prompt.ModelId ?? worker.ModelId, prompt.Agent ?? "", prompt.Variant ?? "");
                    var beforePrompt = await api.Snapshot(worker, options.Value.HistoryLimit, token);
                    if (beforePrompt.Status != "idle" || beforePrompt.Questions.Length > 0 || beforePrompt.Permissions.Length > 0)
                    {
                        await Complete(command.Id, Delivery.Queued, "Native state changed before submission; waiting for idle.");
                        break;
                    }
                    command.NativeMessageId = OpenCodeClient.NewMessageId(beforePrompt);
                    await store.Write(async db => { (await db.Commands.FindAsync(command.Id))!.NativeMessageId = command.NativeMessageId; return true; });
                    mutationStarted = true;
                    await api.Prompt(worker, command, prompt, token);
                    await Complete(command.Id, Delivery.Accepted, "OpenCode accepted asynchronous submission; completion is not yet known.");
                    break;
                case "Abort":
                    if (!api.Capabilities.CanAbort) throw new ControlException("This adapter cannot abort.");
                    mutationStarted = true;
                    await api.Abort(worker!, token);
                    await Complete(command.Id, Delivery.Accepted, "Cancellation requested; waiting to observe native idle. Subprocess termination is not guaranteed by this receipt.");
                    break;
                case "Reply":
                    var reply = Json.Read<ReplyInput>(command.Payload);
                    var request = await store.Read(async db => await db.Requests.FindAsync(reply.RequestId)) ?? throw new ControlException("Request missing.");
                    var current = await api.Snapshot(worker!, options.Value.HistoryLimit, token);
                    var pending = request.Kind == "permission" ? current.Permissions : current.Questions;
                    if (!pending.Any(x => x.GetProperty("id").GetString() == request.NativeId))
                    {
                        await store.RecordUnavailableReply(command.Id);
                        break;
                    }
                    mutationStarted = true;
                    await api.Reply(worker!, request, reply, token);
                    await Complete(command.Id, Delivery.Finished, "Reply accepted for the identified native request.");
                    break;
                default: throw new ControlException("Unsupported command.");
            }
        }
        catch (OperationCanceledException) when (command.Kind == "Prompt" && !mutationStarted && token.IsCancellationRequested)
        {
            // Shutdown interrupted only read-only checks; no native submission needs reconciliation.
            await store.Write(async db =>
            {
                var pending = (await db.Commands.FindAsync(command.Id))!;
                pending.State = Delivery.Queued; pending.Attempts = Math.Max(0, pending.Attempts - 1);
                pending.Detail = "Backend stopped during read-only preflight; instruction remains queued and was not submitted.";
                pending.UpdatedAt = ControlStore.Now;
                ControlStore.Event(db, "CommandPreflightInterrupted", runtime.Id, command.WorkerId, command.Id);
                return true;
            });
        }
        catch (NativeRejectedException ex) when (ex.Status is >= 400 and < 500)
        { await Complete(command.Id, Delivery.Failed, SafeError(ex)); }
        catch (Exception ex)
        { await Complete(command.Id, mutationStarted ? Delivery.Unknown : Delivery.Failed, SafeError(ex)); }
    }

    private Task<bool> CreationProgress(string id, string detail) => store.Write(async db =>
    {
        var command = (await db.Commands.FindAsync(id))!;
        command.Detail = detail; command.UpdatedAt = ControlStore.Now;
        ControlStore.Event(db, "WorkerSetupProgress", command.RuntimeId, commandId: id, payload: new { detail });
        return true;
    });

    private Task<bool> SaveCreated(CommandRecord command, RuntimeRecord runtime, CreateWorkerInput input, WorkspaceIdentity workspace, JsonElement session, List<ModelChoice>? models = null) => store.Write(async db =>
    {
        if (session.GetProperty("directory").GetString() != workspace.Directory) throw new ControlException("Created session directory differs from verified workspace.");
        var nativeId = session.GetProperty("id").GetString()!;
        var worker = new WorkerRecord
        {
            RuntimeId = runtime.Id,
            ManagedServerId = runtime.ManagedServerId,
            NativeSessionId = nativeId,
            Name = input.Name,
            Role = input.Role,
            Project = input.Project,
            Directory = workspace.Directory,
            Branch = workspace.Branch,
            BaseRef = input.BaseRef ?? "",
            ProviderId = input.ProviderId,
            ModelId = input.ModelId,
            ModelsJson = Json.Write(models ?? new List<ModelChoice>())
        };
        db.Workers.Add(worker);
        var claim = await db.WorkspaceClaims.SingleOrDefaultAsync(x => x.CommandId == command.Id);
        if (claim is not null) claim.WorkerId = worker.Id;
        var record = (await db.Commands.FindAsync(command.Id))!;
        record.ResultId = worker.Id; record.Detail = "Worker created; its persistent conversation is ready."; record.State = Delivery.Finished; record.UpdatedAt = ControlStore.Now;
        ControlStore.Event(db, "WorkerCreated", runtime.Id, worker.Id, command.Id, new { nativeId, workspace.Directory });
        if (input.DiscoverCapabilities && worker.Role == SessionRoles.Worker)
            await store.EnqueueCapabilities(db, worker, Guid.NewGuid().ToString());
        return true;
    });

    private Task<WorkerRecord?> WorkspaceOwner(RuntimeRecord runtime, string directory) => store.Read(async db =>
    {
        var equivalent = await db.Runtimes.Where(x => x.Host == runtime.Host && x.Port == runtime.Port && x.Username == runtime.Username).Select(x => x.Id).ToListAsync();
        return await db.Workers.FirstOrDefaultAsync(x => equivalent.Contains(x.RuntimeId) && x.Directory == directory);
    });

    private Task<bool> ClaimWorkspace(CommandRecord command, RuntimeRecord runtime, string directory) => store.Write(async db =>
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(new { runtime.Host, runtime.Port, runtime.Username, directory }))));
        if (await db.WorkspaceClaims.FindAsync(id) is { } existing)
        {
            if (existing.CommandId != command.Id) throw new ControlException("This canonical workspace is already claimed by another worker or an uncertain creation request.");
            return true;
        }
        if (await db.Workers.CountAsync() + await db.WorkspaceClaims.CountAsync(x => x.WorkerId == null) >= options.Value.MaxWorkers)
            throw new ControlException("Worker limit reached.");
        db.WorkspaceClaims.Add(new WorkspaceClaim { Id = id, CommandId = command.Id, RuntimeId = runtime.Id, Directory = directory });
        return true;
    });

    private async Task ReconcileCreation(RuntimeRecord runtime, IRuntimeTransport transport, CancellationToken token)
    {
        var unknown = await store.Read(db => db.Commands.Where(x => x.RuntimeId == runtime.Id && x.Kind == "CreateWorker" && x.State == Delivery.Unknown).ToListAsync());
        foreach (var command in unknown)
        {
            var input = Json.Read<CreateWorkerInput>(command.Payload);
            var sessions = await transport.Api.Sessions(input.Directory, token);
            var found = sessions.EnumerateArray().Where(x => x.GetProperty("title").GetString() == input.Name + " [hvo:" + command.Id + "]").ToArray();
            if (found.Length != 1) continue;
            var workspace = await transport.Workspace(runtime, input with { Repository = null }, token);
            if (await store.Read(db => db.Workers.AnyAsync(x => x.RuntimeId == runtime.Id && x.Directory == workspace.Directory))) continue;
            await SaveCreated(command, runtime, input, workspace, found[0]);
        }
    }

    private Task<bool> Reconcile(string workerId, NativeSnapshot snapshot) => store.Write(async db =>
    {
        var worker = await db.Workers.FindAsync(workerId);
        if (worker is null) return false;
        var changed = worker.Stale;
        foreach (var message in snapshot.Messages)
        {
            var info = message.GetProperty("info"); var nativeId = info.GetProperty("id").GetString()!;
            var json = message.GetRawText();
            var record = await db.Messages.SingleOrDefaultAsync(x => x.WorkerId == workerId && x.NativeId == nativeId);
            if (record?.Json == json) continue;
            changed = true;
            if (record is null)
            {
                record = new TranscriptMessage { WorkerId = workerId, NativeId = nativeId };
                db.Messages.Add(record);
            }
            record.Json = json; record.Role = info.GetProperty("role").GetString()!;
            record.NativeCreatedAt = info.GetProperty("time").GetProperty("created").GetInt64();
            if (record.Role == "assistant") worker.LastModelAt = ControlStore.Now;
            foreach (var part in message.GetProperty("parts").EnumerateArray())
                if (part.GetProperty("type").GetString() == "tool")
                { worker.LastToolAt = ControlStore.Now; worker.CurrentAction = part.GetProperty("tool").GetString() + ": " + part.GetProperty("state").GetProperty("status").GetString(); }
            ControlStore.Event(db, "MessageSnapshotUpdated", worker.RuntimeId, worker.Id, payload: new { nativeId }, provenance: "native", nativeId: nativeId);
        }
        foreach (var kind in new[] { "permission", "question" })
        {
            var native = kind == "permission" ? snapshot.Permissions : snapshot.Questions;
            var present = native.Select(x => x.GetProperty("id").GetString()!).ToHashSet();
            var records = await db.Requests.Where(x => x.WorkerId == workerId && x.Kind == kind).ToListAsync();
            foreach (var item in native)
            {
                var nativeId = item.GetProperty("id").GetString()!;
                var record = records.SingleOrDefault(x => x.NativeId == nativeId);
                if (record is null)
                {
                    record = new PendingRequest { WorkerId = workerId, NativeId = nativeId, Kind = kind };
                    db.Requests.Add(record); changed = true;
                    ControlStore.Event(db, kind == "question" ? "QuestionRequested" : "PermissionRequested", worker.RuntimeId, workerId, provenance: "native", nativeId: nativeId);
                }
                record.Json = item.GetRawText();
                if (record.ReplyCommandId is not null && await db.Commands.FindAsync(record.ReplyCommandId) is { State: Delivery.Unknown }) record.State = "ReplyUnknown";
            }
            foreach (var record in records.Where(x => !present.Contains(x.NativeId) && x.State is "Pending" or "ReplyUnknown"))
            { record.State = "NoLongerPending"; changed = true; }
        }
        var activity = snapshot.Permissions.Length > 0 ? "WaitingPermission" : snapshot.Questions.Length > 0 ? "WaitingQuestion" : snapshot.Status switch { "idle" => "Idle", "busy" => "Active", "retry" => "Retrying", _ => "Unknown" };
        if (worker.Activity != activity) { worker.Activity = activity; worker.Revision++; changed = true; }
        worker.Stale = false; worker.LastObservedAt = ControlStore.Now;
        var commands = await db.Commands.Where(x => x.WorkerId == workerId && (x.State == Delivery.Dispatching || x.State == Delivery.Unknown || x.State == Delivery.Accepted || x.State == Delivery.Running)).ToListAsync();
        var abortObserved = activity == "Idle" && commands.Any(x => x.Kind == "Abort" && x.State == Delivery.Accepted);
        foreach (var command in commands)
        {
            if (command.Kind == "Abort" && activity == "Idle" && command.State == Delivery.Accepted)
            { command.State = Delivery.Finished; command.Detail = "Native idle observed after cancellation request. Review tool results for subprocess effects."; changed = true; }
            if (command.Kind != "Prompt") continue;
            if (abortObserved && command.State is Delivery.Accepted or Delivery.Running)
            {
                command.State = Delivery.Finished; command.Detail = "Cancellation requested and native idle observed. Review tool effects; subprocess termination is not guaranteed.";
                command.UpdatedAt = ControlStore.Now; worker.Outcome = "Cancelled";
                if (await db.Assignments.FindAsync(command.Id) is { } cancelled) cancelled.Outcome = "Cancelled";
                ControlStore.Event(db, "CancellationObserved", worker.RuntimeId, workerId, command.Id); changed = true;
                continue;
            }
            var user = snapshot.Messages.Any(x => x.GetProperty("info").GetProperty("id").GetString() == command.NativeMessageId);
            if (!user) continue;
            var assistants = NativeTurnEvidence.AssistantMessages(snapshot.Messages, command.NativeMessageId);
            var ended = assistants.Any(x => x.GetProperty("info").GetProperty("time").TryGetProperty("completed", out _));
            var failed = assistants.Any(x => x.GetProperty("info").TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));
            command.AcceptedAt ??= ControlStore.Now;
            var progress = ControlStore.ResponseText(Json.Write(new { messages = assistants }));
            progress = progress.Length <= 6000 ? progress : "[Earlier output omitted]\n" + progress[^5975..];
            if (command.ProgressText != progress && progress.Length > 0)
            { command.ProgressText = progress; command.LastProgressAt = ControlStore.Now; changed = true; }
            if (activity == "Idle" && ended && !failed && progress.Length > 0 && command.Origin == "capability-report")
            { worker.CapabilityReport = progress; worker.CapabilityReportedAt = ControlStore.Now; }
            if (activity == "Idle" && ended) command.ResultJson = Json.Write(new { messages = assistants });
            var state = activity == "Idle" && ended ? Delivery.Finished : activity == "Idle" ? Delivery.Accepted : Delivery.Running;
            if (state == command.State) continue;
            command.State = state; command.UpdatedAt = ControlStore.Now;
            command.Detail = state == Delivery.Finished ? "Native turn ended. Assignment outcome requires evidence and owner review." : "Native caller message identity found in retained history.";
            worker.Outcome = failed ? "Failed" : state == Delivery.Finished ? "NeedsReview" : "Running";
            if (await db.Assignments.FindAsync(command.Id) is { } assignment) assignment.Outcome = worker.Outcome;
            ControlStore.Event(db, "CommandReconciled", worker.RuntimeId, workerId, command.Id, new { state }); changed = true;
        }
        // Keep bounded current transcript pages; authoritative older conversation data stays remote.
        var oldMessages = await db.Messages.Where(x => x.WorkerId == workerId).OrderByDescending(x => x.NativeCreatedAt)
            .Skip(options.Value.HistoryLimit * 2).ToListAsync();
        if (oldMessages.Count > 0) db.Messages.RemoveRange(oldMessages);
        if (changed) ControlStore.Event(db, "HistoryReconciled", worker.RuntimeId, workerId, payload: new { worker.Activity, worker.HistoryGap });
        return true;
    });

    private Task<bool> ObserveEvents(string runtimeId, List<JsonElement> events) => store.Write(async db =>
    {
        var runtime = (await db.Runtimes.FindAsync(runtimeId))!; runtime.LastEventAt = ControlStore.Now;
        var workers = await db.Workers.Where(x => x.RuntimeId == runtimeId).ToListAsync();
        var batch = new HashSet<string>();
        foreach (var envelope in events)
        {
            if (!envelope.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("type", out var nativeType)) continue;
            var type = nativeType.GetString() ?? "unknown";
            if (type.Length > 160) type = "unknown";
            if (type is "server.heartbeat" or "server.connected") continue;
            var directory = envelope.TryGetProperty("directory", out var dir) ? dir.GetString() : null;
            var sessionId = NativeSession(payload);
            var worker = workers.SingleOrDefault(x => x.NativeSessionId == sessionId && x.Directory == directory);
            if (!batch.Add((worker?.Id ?? "runtime") + type)) continue;
            var nativeId = payload.TryGetProperty("id", out var eventId) ? eventId.GetString() : null;
            ControlStore.Event(db, type, runtimeId, worker?.Id, payload: new { directory, sessionId, nativeType = type }, provenance: "native", generation: runtime.Generation, nativeId: nativeId);
            if (worker is null) continue;
            if (type == "session.error") { worker.Outcome = "Failed"; worker.CurrentAction = "Native session error; inspect retained transcript and remote log."; }
        }
        return true;
    });

    public static string? NativeSession(JsonElement payload)
    {
        if (!payload.TryGetProperty("properties", out var props)) return null;
        if (props.TryGetProperty("sessionID", out var session)) return session.GetString();
        foreach (var field in new[] { "info", "part" })
            if (props.TryGetProperty(field, out var value) && value.TryGetProperty("sessionID", out session)) return session.GetString();
        return null;
    }

    private Task<bool> Complete(string id, string state, string detail) => store.Write(async db =>
    {
        var command = (await db.Commands.FindAsync(id))!;
        command.State = state; command.Detail = detail; command.UpdatedAt = ControlStore.Now;
        if (state == Delivery.Failed && command.Kind == "CreateWorker")
            await db.WorkspaceClaims.Where(x => x.CommandId == id && x.WorkerId == null).ExecuteDeleteAsync();
        ControlStore.Event(db, "CommandStateChanged", command.RuntimeId, command.WorkerId, id, new { state, detail });
        return true;
    });
    private Task<bool> FinishRuntimeCommands(string id, string kind) => store.Write(async db =>
    {
        foreach (var command in await db.Commands.Where(x => x.RuntimeId == id && x.Kind == kind && x.State == Delivery.Queued).ToListAsync())
        { command.State = Delivery.Finished; command.Attempts++; command.UpdatedAt = ControlStore.Now; ControlStore.Event(db, kind + "Finished", id, commandId: command.Id); }
        return true;
    });
    private Task<bool> MarkDisconnected(string id, string transport, string diagnostic, string health = "Unknown") => store.Write(async db =>
    {
        var runtime = (await db.Runtimes.FindAsync(id))!; runtime.Transport = transport; runtime.Health = health;
        runtime.Diagnostic = diagnostic; runtime.ReconnectAttempts++;
        foreach (var worker in await db.Workers.Where(x => x.RuntimeId == id).ToListAsync()) { worker.Stale = true; worker.HistoryGap = true; }
        ControlStore.Event(db, "RuntimeDisconnected", id, payload: new { diagnostic }); return true;
    });
    private Task<bool> MarkGap(string id, string reason) => store.Write(async db =>
    {
        foreach (var worker in await db.Workers.Where(x => x.RuntimeId == id).ToListAsync()) { worker.HistoryGap = true; worker.Stale = true; }
        ControlStore.Event(db, "HistoryGap", id, payload: new { reason }); return true;
    });
    private Task<bool> Retain() => store.Write(async db =>
    {
        var threshold = await db.Events.OrderByDescending(x => x.Sequence).Skip(options.Value.EventRetention).Select(x => (long?)x.Sequence).FirstOrDefaultAsync();
        if (threshold is not null) await db.Events.Where(x => x.Sequence <= threshold).ExecuteDeleteAsync();
        return true;
    });
    public static string SafeError(Exception exception) => exception switch
    {
        ControlException => exception.Message,
        NativeRejectedException => exception.Message,
        OperationCanceledException => "Request timed out or the backend stopped; reconcile delivery before retrying any mutation.",
        _ => "Connection or native operation failed (" + exception.GetType().Name + "). Verify trusted host key, mounted credentials, SSH reachability, prerequisites and owned process health."
    };
}
