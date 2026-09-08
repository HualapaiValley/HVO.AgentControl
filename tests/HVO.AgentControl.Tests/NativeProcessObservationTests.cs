using System.Reflection;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using HVO.AgentControl.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class NativeProcessObservationTests
{
    private const string OldMarker = "boot-a:100";
    private const string NewMarker = "boot-a:200";

    [Fact]
    public void BootstrapCapturesAndRetainsProcessBoundaryBeforeRespawn()
    {
        var runtime = new RuntimeRecord { StateDirectory = "/state", ManagedServerId = "managed", ApiPort = 4096 };
        var script = BootstrapScript.Create(runtime);
        Assert.True(script.IndexOf("mkdir bootstrap.lock", StringComparison.Ordinal) <
            script.IndexOf("previous=$(cat \"$observation\"", StringComparison.Ordinal));
        Assert.True(script.IndexOf("previous=$(cat \"$observation\"", StringComparison.Ordinal) <
            script.IndexOf("respawn-pane", StringComparison.Ordinal));
        Assert.Contains("/proc/$pid/stat", script);
        Assert.Contains("/proc/sys/kernel/random/boot_id", script);
        Assert.Contains("native-process-replacements", script);
        Assert.Contains("tail -n 32", script);
        Assert.DoesNotContain("previous=$(cat startup-options", script);
        Assert.DoesNotContain("oom_kill", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeneratedBootstrapRemainsValidPosixShell()
    {
        var runtime = new RuntimeRecord { StateDirectory = "/state", ManagedServerId = "managed", ApiPort = 4096 };
        var start = new System.Diagnostics.ProcessStartInfo("/bin/sh", "-n")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.StandardInput.WriteAsync(BootstrapScript.Create(runtime));
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }

    [Fact]
    public void ProbeParsesReplacementWithoutInferringOomOrContainerRestart()
    {
        var observation = NativeProcessProbe.Parse("managed",
            "OBSERVATION\tObserved\tLinux\t42\tboot-a:200\nREPLACEMENT\t41\tboot-a:100\t42\tboot-a:200\tDeadPaneRespawn\tSignal\t9\tUnknown\n", 123);
        Assert.Equal(NativeProcessObservationState.Observed, observation.State);
        Assert.Equal(42, observation.ProcessId);
        var replacement = Assert.Single(observation.Replacements);
        Assert.Equal(41, replacement.PreviousProcessId);
        Assert.Equal(42, replacement.CurrentProcessId);
        Assert.Equal(NativeProcessExitEvidence.Signal, replacement.ProcessExitEvidence);
        Assert.Equal(9, replacement.ProcessExitCode);
        Assert.Equal("Unknown", replacement.OomEvidence);
        Assert.Equal("Unknown", replacement.EnvironmentRestartEvidence);

        var invalid = NativeProcessProbe.Parse("managed", "OBSERVATION\tObserved\tLinux\t41\tbad marker\n", 124);
        Assert.Equal(NativeProcessObservationState.Unavailable, invalid.State);
        Assert.Empty(invalid.Replacements);
        var impossibleSignal = NativeProcessProbe.Parse("managed",
            "OBSERVATION\tObserved\tLinux\t42\tboot-a:200\nREPLACEMENT\t41\tboot-a:100\t42\tboot-a:200\tDeadPaneRespawn\tSignal\t0\tUnknown\n", 125);
        Assert.Equal(NativeProcessObservationState.Unavailable, impossibleSignal.State);
    }

    [Fact]
    public async Task SameProcessReconnectAndUnsupportedProbeNeverInterruptWork()
    {
        await using var app = new TestApp();
        var (runtime, worker, command) = await InFlightPrompt(app);
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 41));
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 41));
        await app.Store.ObserveNativeProcess(runtime.Id,
            NativeProcessObservation.Unsupported(runtime.ManagedServerId, ControlStore.Now, "TransportUnsupported"));

        var detail = await app.Store.Detail(worker.Id);
        Assert.Equal(Delivery.Running, detail.Commands.Single(x => x.Id == command.Id).State);
        Assert.Equal("Assigned", detail.Assignments.Single(x => x.Id == command.Id).Outcome);
        Assert.Empty(await app.Store.Read(db => db.Events.Where(x => x.Type == "NativeProcessReplaced").ToListAsync()));
        Assert.Contains((await app.Store.NativeProcessObservations(runtime.Id)).Select(x => x.State), x => x == NativeProcessObservationState.Unsupported);
    }

    [Fact]
    public async Task ProvenPidReuseBoundaryInterruptsOnlyUnresolvedPromptAndPreservesIdentityAndArtifacts()
    {
        await using var app = new TestApp();
        var (runtime, worker, command) = await InFlightPrompt(app);
        command.ProgressText = "retained partial tool evidence";
        command.ResultJson = "{\"retained\":true}";
        var finished = await TerminalPrompt(app, worker, Delivery.Finished, "NeedsReview");
        var cancelled = await TerminalPrompt(app, worker, Delivery.Cancelled, "Cancelled");
        await app.Store.Write(async db =>
        {
            var saved = (await db.Commands.FindAsync(command.Id))!;
            saved.ProgressText = command.ProgressText; saved.ResultJson = command.ResultJson;
            return true;
        });
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 41));

        var replacement = Replacement(runtime, 41, OldMarker, 41, NewMarker, NativeProcessExitEvidence.Unknown, null);
        var result = await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, NewMarker, 41, replacement));

        Assert.Equal("Fresh", result.Freshness);
        Assert.Equal(1, result.ReplacementReceipts);
        Assert.Equal(1, result.InterruptedCommands);
        var detail = await app.Store.Detail(worker.Id);
        var interrupted = detail.Commands.Single(x => x.Id == command.Id);
        Assert.Equal(Delivery.Unknown, interrupted.State);
        Assert.Equal(command.NativeMessageId, interrupted.NativeMessageId);
        Assert.Equal(command.ProgressText, interrupted.ProgressText);
        Assert.Equal(command.ResultJson, interrupted.ResultJson);
        Assert.Equal("Interrupted", detail.Assignments.Single(x => x.Id == command.Id).Outcome);
        Assert.Equal(Delivery.Finished, detail.Commands.Single(x => x.Id == finished.Id).State);
        Assert.Equal("NeedsReview", detail.Assignments.Single(x => x.Id == finished.Id).Outcome);
        Assert.Equal(Delivery.Cancelled, detail.Commands.Single(x => x.Id == cancelled.Id).State);
        Assert.Equal("Cancelled", detail.Assignments.Single(x => x.Id == cancelled.Id).Outcome);
        Assert.Equal(worker.NativeSessionId, detail.Worker.NativeSessionId);
        Assert.Equal("Unknown", replacement.OomEvidence);
        Assert.Equal("Unknown", replacement.EnvironmentRestartEvidence);
        Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "NativeProcessCommandInterrupted" && x.CommandId == command.Id).ToListAsync()));

        await Reconcile(app, worker, IncompleteIdle(worker, command));
        detail = await app.Store.Detail(worker.Id);
        Assert.Equal(Delivery.Unknown, detail.Commands.Single(x => x.Id == command.Id).State);
        Assert.Equal("Interrupted", detail.Assignments.Single(x => x.Id == command.Id).Outcome);
        Assert.Equal(3, detail.Commands.Count(x => x.Kind == "Prompt"));

        await Reconcile(app, worker, BusyTerminal(worker, command));
        detail = await app.Store.Detail(worker.Id);
        Assert.Equal(Delivery.Unknown, detail.Commands.Single(x => x.Id == command.Id).State);
        Assert.Equal("Interrupted", detail.Assignments.Single(x => x.Id == command.Id).Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaleOrMismatchedReplacementEvidenceRemainsUnverified(bool stale)
    {
        await using var app = new TestApp();
        var (runtime, worker, command) = await InFlightPrompt(app);
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 41));
        var previous = stale ? OldMarker : "different:100";
        var replacement = Replacement(runtime, 41, previous, 42, NewMarker, NativeProcessExitEvidence.Unknown, null);
        var observedAt = stale ? ControlStore.Now - 60001 : ControlStore.Now;

        var result = await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, NewMarker, 42, replacement, observedAt));

        Assert.Equal(0, result.ReplacementReceipts);
        Assert.Equal(0, result.InterruptedCommands);
        Assert.Equal(Delivery.Running, (await app.Store.Detail(worker.Id)).Commands.Single(x => x.Id == command.Id).State);
        Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "NativeProcessReplacementUnverified").ToListAsync()));
    }

    [Fact]
    public async Task UnverifiedReplacementRemainsRetryableWhenFreshCausalEvidenceArrives()
    {
        await using var app = new TestApp();
        var (runtime, worker, command) = await InFlightPrompt(app);
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 41));

        var mismatched = Replacement(runtime, 99, "other:100", 42, NewMarker, NativeProcessExitEvidence.Unknown, null);
        var first = await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, NewMarker, 42, mismatched));
        Assert.Equal(0, first.ReplacementReceipts);
        Assert.Equal(0, first.InterruptedCommands);

        var matching = Replacement(runtime, 41, OldMarker, 42, NewMarker, NativeProcessExitEvidence.Unknown, null);
        var second = await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, NewMarker, 42, matching));
        Assert.Equal(1, second.ReplacementReceipts);
        Assert.Equal(1, second.InterruptedCommands);
        Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "NativeProcessReplacementUnverified").ToListAsync()));
        Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "NativeProcessReplaced").ToListAsync()));
        Assert.Equal(Delivery.Unknown, (await app.Store.Detail(worker.Id)).Commands.Single(x => x.Id == command.Id).State);
    }

    [Fact]
    public async Task LostBootstrapResponseAndControllerRestartRecoverReceiptExactlyOnce()
    {
        string data, secrets, runtimeId, workerId, commandId, nativeSessionId, nativeMessageId;
        NativeProcessObservation replacementObservation;
        await using (var app = new TestApp())
        {
            var (runtime, worker, command) = await InFlightPrompt(app);
            runtimeId = runtime.Id; workerId = worker.Id; commandId = command.Id;
            nativeSessionId = worker.NativeSessionId; nativeMessageId = command.NativeMessageId!;
            await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 51));
            replacementObservation = NativeProcessProbe.Parse(runtime.ManagedServerId,
                "OBSERVATION\tObserved\tLinux\t52\tboot-a:200\nREPLACEMENT\t51\tboot-a:100\t52\tboot-a:200\tDeadPaneRespawn\tExitStatus\t137\tUnknown\n" +
                "REPLACEMENT\t51\tboot-a:100\t52\tboot-a:200\tDeadPaneRespawn\tExitStatus\t137\tUnknown\n",
                ControlStore.Now);
            data = app.DataPath; secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var first = await restarted.Store.ObserveNativeProcess(runtimeId, replacementObservation with { ObservedAt = ControlStore.Now });
        var replay = await restarted.Store.ObserveNativeProcess(runtimeId, replacementObservation with { ObservedAt = ControlStore.Now });
        Assert.Equal(1, first.ReplacementReceipts);
        Assert.Equal(1, first.InterruptedCommands);
        Assert.Equal(0, replay.ReplacementReceipts);
        Assert.Equal(0, replay.InterruptedCommands);
        var detail = await restarted.Store.Detail(workerId);
        Assert.Equal(nativeSessionId, detail.Worker.NativeSessionId);
        Assert.Equal(nativeMessageId, detail.Commands.Single(x => x.Id == commandId).NativeMessageId);
        Assert.Equal(Delivery.Unknown, detail.Commands.Single(x => x.Id == commandId).State);
        Assert.Equal("Interrupted", detail.Assignments.Single(x => x.Id == commandId).Outcome);
        Assert.Single(await restarted.Store.Read(db => db.Events.Where(x => x.Type == "NativeProcessReplaced").ToListAsync()));
        Assert.Single(await restarted.Store.Read(db => db.Events.Where(x => x.Type == "NativeProcessCommandInterrupted").ToListAsync()));
    }

    [Fact]
    public async Task ControllerRestartWithSameIncarnationDoesNotBecomeProcessReplacement()
    {
        string data, secrets, runtimeId, workerId;
        NativeProcessObservation sameProcess;
        await using (var app = new TestApp())
        {
            var (runtime, worker, _) = await InFlightPrompt(app);
            runtimeId = runtime.Id; workerId = worker.Id;
            sameProcess = Observed(runtime, OldMarker, 61);
            await app.Store.ObserveNativeProcess(runtime.Id, sameProcess);
            data = app.DataPath; secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var result = await restarted.Store.ObserveNativeProcess(runtimeId, sameProcess with { ObservedAt = ControlStore.Now });
        Assert.Equal(0, result.ReplacementReceipts);
        Assert.Equal(0, result.InterruptedCommands);
        Assert.Equal(Delivery.Running, (await restarted.Store.Detail(workerId)).Commands.Single().State);
        Assert.Empty(await restarted.Store.Read(db => db.Events.Where(x => x.Type == "NativeProcessReplaced").ToListAsync()));
    }

    [Fact]
    public async Task DifferentPidWithSameStartMarkerIsAProvenReplacement()
    {
        await using var app = new TestApp();
        var (runtime, worker, command) = await InFlightPrompt(app);
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 81));

        var result = await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 82,
            Replacement(runtime, 81, OldMarker, 82, OldMarker, NativeProcessExitEvidence.Unknown, null)));

        Assert.Equal(1, result.ReplacementReceipts);
        Assert.Equal(1, result.InterruptedCommands);
        Assert.Equal(Delivery.Unknown, (await app.Store.Detail(worker.Id)).Commands.Single(x => x.Id == command.Id).State);
    }

    [Fact]
    public async Task ProcessReplacementPreservesRecordedTerminalOutcome()
    {
        await using var app = new TestApp();
        var (runtime, worker, command) = await InFlightPrompt(app);
        await app.Store.Write(db =>
        {
            var saved = db.Commands.Single(x => x.Id == command.Id);
            saved.ProgressText = "Retained terminal evidence";
            saved.ResultJson = "{\"retained\":true}";
            var assignment = db.Assignments.Single(x => x.Id == command.Id);
            assignment.Outcome = "VerifiedComplete";
            assignment.Evidence = "Owner verified retained task evidence.";
            db.Workers.Single(x => x.Id == worker.Id).Outcome = "VerifiedComplete";
            return Task.FromResult(true);
        });
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 91));

        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, NewMarker, 92,
            Replacement(runtime, 91, OldMarker, 92, NewMarker, NativeProcessExitEvidence.Unknown, null)));

        var detail = await app.Store.Detail(worker.Id);
        var interrupted = detail.Commands.Single(x => x.Id == command.Id);
        Assert.Equal(Delivery.Unknown, interrupted.State);
        Assert.Equal(ControlStore.NativeProcessInterruptedDetail, interrupted.Detail);
        Assert.Equal("Retained terminal evidence", interrupted.ProgressText);
        Assert.Equal("{\"retained\":true}", interrupted.ResultJson);
        Assert.Equal("VerifiedComplete", detail.Worker.Outcome);
        Assert.Equal("VerifiedComplete", detail.Assignments.Single(x => x.Id == command.Id).Outcome);
        Assert.Equal("Owner verified retained task evidence.", detail.Assignments.Single(x => x.Id == command.Id).Evidence);

        await Reconcile(app, worker, IncompleteIdle(worker, command));
        detail = await app.Store.Detail(worker.Id);
        Assert.Equal(Delivery.Unknown, detail.Commands.Single(x => x.Id == command.Id).State);
        Assert.Equal("VerifiedComplete", detail.Worker.Outcome);
        Assert.Equal("VerifiedComplete", detail.Assignments.Single(x => x.Id == command.Id).Outcome);
        Assert.Equal("Owner verified retained task evidence.", detail.Assignments.Single(x => x.Id == command.Id).Evidence);
    }

    [Fact]
    public async Task StaleWorkerOutcomeFromPriorAssignmentDoesNotMaskInterruption()
    {
        await using var app = new TestApp();
        var (runtime, worker, command) = await InFlightPrompt(app);
        await app.Store.Write(async db =>
        {
            (await db.Workers.FindAsync(worker.Id))!.Outcome = "VerifiedComplete";
            return true;
        });
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 101));

        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, NewMarker, 102,
            Replacement(runtime, 101, OldMarker, 102, NewMarker, NativeProcessExitEvidence.Unknown, null)));

        var detail = await app.Store.Detail(worker.Id);
        Assert.Equal(Delivery.Unknown, detail.Commands.Single(x => x.Id == command.Id).State);
        Assert.Equal("Interrupted", detail.Worker.Outcome);
        Assert.Equal("Interrupted", detail.Assignments.Single(x => x.Id == command.Id).Outcome);
    }

    [Fact]
    public async Task RetentionKeepsConfirmedReceiptsAndCurrentProcessBaseline()
    {
        await using var app = new TestApp();
        var (runtime, _, _) = await InFlightPrompt(app);
        await app.Store.ObserveNativeProcess(runtime.Id, Observed(runtime, OldMarker, 71));
        var replacementObservation = Observed(runtime, NewMarker, 72,
            Replacement(runtime, 71, OldMarker, 72, NewMarker, NativeProcessExitEvidence.Unknown, null));
        await app.Store.ObserveNativeProcess(runtime.Id, replacementObservation);
        await app.Store.Write(db =>
        {
            for (var index = 0; index < 110; index++) ControlStore.Event(db, "RetentionFixture", payload: new { index });
            return Task.FromResult(true);
        });

        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions { EventRetention = 100 }), NullLogger<RuntimeSupervisor>.Instance);
        var retain = typeof(RuntimeSupervisor).GetMethod("Retain", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task<bool>)retain.Invoke(supervisor, null)!;

        var observations = await app.Store.NativeProcessObservations(runtime.Id);
        Assert.Equal(NewMarker, Assert.Single(observations).Incarnation);
        var receipt = Assert.Single(await app.Store.Read(db => db.Events.Where(x => x.Type == "NativeProcessReplaced").ToListAsync()));
        Assert.True((await app.Store.Evidence(receipt.Sequence, 100)).Incomplete);
        var replay = await app.Store.ObserveNativeProcess(runtime.Id, replacementObservation with { ObservedAt = ControlStore.Now });
        Assert.Equal(0, replay.ReplacementReceipts);
        Assert.Equal(0, replay.InterruptedCommands);
    }

    private static NativeProcessObservation Observed(RuntimeRecord runtime, string marker, int pid,
        NativeProcessReplacementReceipt? replacement = null, long? observedAt = null) =>
        new(runtime.ManagedServerId, NativeProcessObservationState.Observed, "Linux", pid, marker, observedAt ?? ControlStore.Now,
            "SyntheticTest", "Synthetic process observation.", replacement is null ? [] : [replacement]);

    private static NativeProcessReplacementReceipt Replacement(RuntimeRecord runtime, int previousPid, string previous,
        int currentPid, string current, string exitEvidence, int? exitCode) =>
        new(NativeProcessProbe.ReceiptId(runtime.ManagedServerId, previousPid, previous, currentPid, current), previousPid,
            previous, currentPid, current, "DeadPaneRespawn", exitEvidence, exitCode, "Unknown", "Unknown");

    private static async Task<(RuntimeRecord Runtime, WorkerRecord Worker, CommandRecord Command)> InFlightPrompt(TestApp app)
    {
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var runtime = await app.Store.Read(async db => (await db.Runtimes.FindAsync(worker.RuntimeId))!);
        var command = new CommandRecord
        {
            Id = Guid.NewGuid().ToString(),
            RuntimeId = runtime.Id,
            WorkerId = worker.Id,
            Kind = "Prompt",
            State = Delivery.Running,
            NativeMessageId = "msg_original",
            Payload = Json.Write(new PromptInput(Guid.NewGuid().ToString(), "Original prompt", worker.Revision))
        };
        await app.Store.Write(db =>
        {
            db.Commands.Add(command);
            db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = worker.Id, Prompt = "Original prompt" });
            return Task.FromResult(true);
        });
        return (runtime, worker, command);
    }

    private static async Task<CommandRecord> TerminalPrompt(TestApp app, WorkerRecord worker, string state, string outcome)
    {
        var command = new CommandRecord { Id = Guid.NewGuid().ToString(), RuntimeId = worker.RuntimeId, WorkerId = worker.Id, Kind = "Prompt", State = state, NativeMessageId = "msg_" + state };
        await app.Store.Write(db =>
        {
            db.Commands.Add(command);
            db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = worker.Id, Outcome = outcome });
            return Task.FromResult(true);
        });
        return command;
    }

    private static NativeSnapshot IncompleteIdle(WorkerRecord worker, CommandRecord command) => new(
        JsonSerializer.SerializeToElement(new { }),
        [
            JsonSerializer.SerializeToElement(new { info = new { id = command.NativeMessageId, role = "user", sessionID = worker.NativeSessionId, time = new { created = 1L } }, parts = Array.Empty<object>() }),
            JsonSerializer.SerializeToElement(new { info = new { id = "msg_incomplete", role = "assistant", parentID = command.NativeMessageId, sessionID = worker.NativeSessionId, time = new { created = 2L } }, parts = Array.Empty<object>() })
        ], "idle", JsonSerializer.SerializeToElement(new { }), [], []);

    private static NativeSnapshot BusyTerminal(WorkerRecord worker, CommandRecord command) => new(
        JsonSerializer.SerializeToElement(new { }),
        [
            JsonSerializer.SerializeToElement(new { info = new { id = command.NativeMessageId, role = "user", sessionID = worker.NativeSessionId, time = new { created = 1L } }, parts = Array.Empty<object>() }),
            JsonSerializer.SerializeToElement(new { info = new { id = "msg_terminal", role = "assistant", parentID = command.NativeMessageId, sessionID = worker.NativeSessionId, time = new { created = 2L, completed = 3L }, finish = "stop" }, parts = new[] { new { type = "text", text = "Final-looking response" } } })
        ], "busy", JsonSerializer.SerializeToElement(new { }), [], []);

    private static Task<bool> Reconcile(TestApp app, WorkerRecord worker, NativeSnapshot snapshot)
    {
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("Reconcile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<bool>)method.Invoke(supervisor, [worker.Id, snapshot])!;
    }
}
