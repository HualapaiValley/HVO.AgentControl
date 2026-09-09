using System.Reflection;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RuntimeSupervisorReconciliationTests
{
    [Fact]
    public async Task DeniedToolRequiresOrderedIdleRecheckAndFinishesAfterRestart()
    {
        string data, secrets;
        await using (var app = new TestApp())
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var command = await AddPrompt(app, worker);
            await Reconcile(app, worker, Snapshot(worker, command, DeniedTool(worker, command)));
            Assert.Equal(Delivery.Accepted, (await app.Store.Detail(worker.Id)).Commands.Single().State);
            data = app.DataPath; secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var savedWorker = (await restarted.Store.Snapshot()).Workers.Single();
        var savedCommand = (await restarted.Store.Detail(savedWorker.Id)).Commands.Single();
        await Reconcile(restarted, savedWorker, Snapshot(savedWorker, savedCommand, DeniedTool(savedWorker, savedCommand)) with
        {
            IdleToolFailureMessageId = "msg_denied"
        });
        var detail = await restarted.Store.Detail(savedWorker.Id);
        Assert.Equal(Delivery.Finished, detail.Commands.Single().State);
        Assert.Equal("NeedsReview", detail.Worker.Outcome);
        Assert.Contains("Permission was rejected", (await restarted.Store.Command(savedCommand.Id)).ResultJson);
    }

    [Theory]
    [InlineData("busy", "msg_denied")]
    [InlineData("idle", "a-different-turn")]
    public async Task ContinuedOrUnmatchedDenialCannotFinishFromRecheck(string status, string boundary)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = await AddPrompt(app, worker);
        await Reconcile(app, worker, Snapshot(worker, command, DeniedTool(worker, command)) with
        {
            Status = status,
            IdleToolFailureMessageId = boundary
        });

        Assert.NotEqual(Delivery.Finished, (await app.Store.Detail(worker.Id)).Commands.Single().State);
    }

    [Fact]
    public async Task LaterFinalAfterContinuedDenialWinsOverEarlierStoppedToolCandidate()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = await AddPrompt(app, worker);
        var denied = Snapshot(worker, command, DeniedTool(worker, command));
        await Reconcile(app, worker, denied with
        {
            Messages = [.. denied.Messages, FinalAssistant(5, "Final response after permission denial")],
            IdleToolFailureMessageId = "msg_denied"
        });

        var result = (await app.Store.Detail(worker.Id)).Commands.Single();
        Assert.Equal(Delivery.Finished, result.State);
        Assert.Contains("Final response after permission denial", ControlStore.ResponseText((await app.Store.Command(result.Id)).ResultJson));
    }

    [Fact]
    public async Task ToolBoundaryWithoutFinalMessageStaysActiveAcrossRestartUntilFinalArrives()
    {
        string data, secrets;
        await using (var app = new TestApp())
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var command = await AddPrompt(app, worker);
            var snapshot = Snapshot(worker, command, FinalAssistant(completed: null, text: ""));
            await Reconcile(app, worker, snapshot with { Messages = snapshot.Messages[..^1] });
            var retained = (await app.Store.Detail(worker.Id)).Commands.Single();
            Assert.Equal(Delivery.Accepted, retained.State);
            Assert.Equal(command.ResultJson, (await app.Store.Command(retained.Id)).ResultJson);
            data = app.DataPath; secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var restartedWorker = (await restarted.Store.Snapshot()).Workers.Single();
        var restartedCommand = (await restarted.Store.Detail(restartedWorker.Id)).Commands.Single();
        var boundary = Snapshot(restartedWorker, restartedCommand, FinalAssistant(completed: null, text: ""));
        await Reconcile(restarted, restartedWorker, boundary with { Messages = boundary.Messages[..^1] });
        Assert.Equal(Delivery.Accepted, (await restarted.Store.Detail(restartedWorker.Id)).Commands.Single().State);
        await Reconcile(restarted, restartedWorker, Snapshot(restartedWorker, restartedCommand, FinalAssistant(completed: 4, text: "Final review evidence")));
        var finished = (await restarted.Store.Detail(restartedWorker.Id)).Commands.Single();
        Assert.Equal(Delivery.Finished, finished.State);
        Assert.Contains("Final review evidence", ControlStore.ResponseText((await restarted.Store.Command(finished.Id)).ResultJson));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tool-calls")]
    public async Task CompletedNativeErrorWithToolPartsFinishesDeliveryAndRetainsFailure(string? finish)
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = await AddPrompt(app, worker);
        var info = new Dictionary<string, object>
        {
            ["id"] = "msg_final",
            ["role"] = "assistant",
            ["parentID"] = command.NativeMessageId!,
            ["sessionID"] = worker.NativeSessionId,
            ["time"] = new { created = 3L, completed = 4L },
            ["error"] = new { name = "APIError", data = new { message = "Provider failed" } }
        };
        if (finish is not null) info["finish"] = finish;
        var failed = JsonSerializer.SerializeToElement(new
        {
            info,
            parts = new[] { new { type = "tool", tool = "bash", state = new { status = "error", metadata = new { interrupted = true } } } }
        });

        await Reconcile(app, worker, Snapshot(worker, command, failed));
        var detail = await app.Store.Detail(worker.Id);
        Assert.Equal(Delivery.Finished, detail.Commands.Single().State);
        Assert.Equal("Failed", detail.Worker.Outcome);
        Assert.Contains("Provider failed", (await app.Store.Command(command.Id)).ResultJson);
    }

    [Fact]
    public async Task IncompleteLatestAssistantResponseDoesNotFinishBeforeOrAfterRestart()
    {
        string data, secrets;
        await using (var app = new TestApp())
        {
            var worker = await PersistenceTests.SeedWorker(app.Store);
            var command = await AddPrompt(app, worker);
            var incomplete = Snapshot(worker, command, FinalAssistant(completed: null, text: ""));

            await Reconcile(app, worker, incomplete);
            Assert.Equal(Delivery.Accepted, (await app.Store.Detail(worker.Id)).Commands.Single().State);
            data = app.DataPath;
            secrets = app.SecretPath;
        }

        await using var restarted = new TestApp(data, secrets);
        var restartedWorker = (await restarted.Store.Snapshot()).Workers.Single();
        var restartedCommand = (await restarted.Store.Detail(restartedWorker.Id)).Commands.Single();
        await Reconcile(restarted, restartedWorker, Snapshot(restartedWorker, restartedCommand, FinalAssistant(completed: null, text: "")));
        Assert.Equal(Delivery.Accepted, (await restarted.Store.Detail(restartedWorker.Id)).Commands.Single().State);

        await Reconcile(restarted, restartedWorker, Snapshot(restartedWorker, restartedCommand, FinalAssistant(completed: 4, text: "APPROVED receipt")));
        var completed = (await restarted.Store.Detail(restartedWorker.Id)).Commands.Single();
        Assert.Equal(Delivery.Finished, completed.State);
        Assert.Contains("APPROVED receipt", ControlStore.ResponseText((await restarted.Store.Command(completed.Id)).ResultJson));
    }

    [Fact]
    public async Task CompletedLatestAssistantWithRunningToolDoesNotFinish()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = await AddPrompt(app, worker);
        var pendingTool = JsonSerializer.SerializeToElement(new
        {
            info = new { id = "msg_final", role = "assistant", parentID = command.NativeMessageId, sessionID = worker.NativeSessionId, time = new { created = 3L, completed = 4L }, finish = "stop" },
            parts = new[] { new { type = "tool", tool = "bash", state = new { status = "running" } } }
        });

        await Reconcile(app, worker, Snapshot(worker, command, pendingTool));
        Assert.Equal(Delivery.Accepted, (await app.Store.Detail(worker.Id)).Commands.Single().State);
    }

    [Fact]
    public async Task IncompleteLatestAssistantErrorDoesNotFinishTheTurn()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = await AddPrompt(app, worker);
        var failed = JsonSerializer.SerializeToElement(new
        {
            info = new { id = "msg_final", role = "assistant", parentID = command.NativeMessageId, sessionID = worker.NativeSessionId, error = new { name = "Error", message = "native failure" }, time = new { created = 3L } },
            parts = Array.Empty<object>()
        });

        await Reconcile(app, worker, Snapshot(worker, command, failed));
        Assert.Equal(Delivery.Accepted, (await app.Store.Detail(worker.Id)).Commands.Single().State);
    }

    [Fact]
    public async Task ObservedRouteMismatchFailsOutcomeAndRecordsNativeEvidence()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = await AddPrompt(app, worker);
        await app.Store.Write(async db =>
        {
            var saved = (await db.Commands.FindAsync(command.Id))!;
            saved.ExecutionPayload = Json.Write(new PromptInput("task", "High risk task", worker.Revision,
                "openai", "gpt-5.6-sol", RiskLevel: TaskRiskLevels.High,
                RiskPolicyVersion: "risk-floor-v1", RiskRouteMaximum: TaskRiskLevels.High));
            return true;
        });
        var mismatch = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg_final",
                role = "assistant",
                parentID = command.NativeMessageId,
                sessionID = worker.NativeSessionId,
                providerID = "openai",
                modelID = "gpt-5.6-luna",
                time = new { created = 3L, completed = 4L },
                finish = "stop"
            },
            parts = new[] { new { type = "text", text = "Executed on unexpected route" } }
        });

        await Reconcile(app, worker, Snapshot(worker, command, mismatch) with { Status = "busy" });
        Assert.Equal(Delivery.Running, (await app.Store.Detail(worker.Id)).Commands.Single().State);

        var final = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg_final_expected",
                role = "assistant",
                parentID = command.NativeMessageId,
                sessionID = worker.NativeSessionId,
                providerID = "openai",
                modelID = "gpt-5.6-sol",
                time = new { created = 5L, completed = 6L },
                finish = "stop"
            },
            parts = new[] { new { type = "text", text = "Final response after bounded history changed" } }
        });
        await Reconcile(app, worker, Snapshot(worker, command, final));

        var detail = await app.Store.Detail(worker.Id);
        Assert.Equal(Delivery.Finished, detail.Commands.Single().State);
        Assert.Equal("Failed", detail.Worker.Outcome);
        Assert.Contains("different provider/model", detail.Commands.Single().Detail);
        var evidence = await app.Store.Read(db => db.Events.SingleAsync(x =>
            x.Type == "TaskRiskRouteMismatch" && x.CommandId == command.Id));
        Assert.Contains("gpt-5.6-sol", evidence.Payload);
        Assert.Contains("gpt-5.6-luna", evidence.Payload);
    }

    [Fact]
    public void ContradictoryNativeRouteMetadataIsUntrusted()
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                providerID = "openai",
                modelID = "gpt-5.6-sol",
                model = new { providerID = "openai", id = "gpt-5.6-luna" }
            }
        });

        var route = NativeTurnEvidence.ObservedRoute(message);

        Assert.NotNull(route);
        Assert.True(route.Contradictory);
    }

    [Fact]
    public async Task LateRouteMismatchCorrectsFinishedAssignmentOutcome()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = await AddPrompt(app, worker);
        await app.Store.Write(async db =>
    {
        var saved = (await db.Commands.FindAsync(command.Id))!;
        saved.ExecutionPayload = Json.Write(new PromptInput("task", "High risk task", worker.Revision,
            "openai", "gpt-5.6-sol", RiskLevel: TaskRiskLevels.High,
            RiskPolicyVersion: "risk-floor-v1", RiskRouteMaximum: TaskRiskLevels.High));
        db.Assignments.Add(new AssignmentRecord { Id = command.Id, WorkerId = worker.Id, Prompt = "High risk task" });
        return true;
    });
        JsonElement Final(string model) => JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg_final",
                role = "assistant",
                parentID = command.NativeMessageId,
                sessionID = worker.NativeSessionId,
                providerID = "openai",
                modelID = model,
                time = new { created = 3L, completed = 4L },
                finish = "stop"
            },
            parts = new[] { new { type = "text", text = "Terminal result" } }
        });

        await Reconcile(app, worker, Snapshot(worker, command, Final("gpt-5.6-sol")));
        Assert.Equal("NeedsReview", (await app.Store.Detail(worker.Id)).Assignments.Single().Outcome);

        await Reconcile(app, worker, Snapshot(worker, command, Final("gpt-5.6-luna")));

        var detail = await app.Store.Detail(worker.Id);
        Assert.Equal("Failed", detail.Assignments.Single().Outcome);
        Assert.Contains("different provider/model", detail.Commands.Single().Detail);
        Assert.Single(await app.Store.Read(db => db.Events.Where(x =>
            x.Type == "TaskRiskRouteMismatch" && x.CommandId == command.Id).ToListAsync()));
    }

    [Fact]
    public async Task AcceptedAbortStillFinishesWhenNativeIsIdle()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var abort = new CommandRecord { Id = Guid.NewGuid().ToString(), RuntimeId = worker.RuntimeId, WorkerId = worker.Id, Kind = "Abort", State = Delivery.Accepted };
        await app.Store.Write(db => { db.Commands.Add(abort); return Task.FromResult(true); });
        var snapshot = new NativeSnapshot(JsonSerializer.SerializeToElement(new { }), [], "idle", JsonSerializer.SerializeToElement(new { }), [], []);

        await Reconcile(app, worker, snapshot);
        Assert.Equal(Delivery.Finished, (await app.Store.Detail(worker.Id)).Commands.Single().State);
    }

    private static async Task<CommandRecord> AddPrompt(TestApp app, WorkerRecord worker)
    {
        var command = new CommandRecord
        {
            Id = Guid.NewGuid().ToString(),
            RuntimeId = worker.RuntimeId,
            WorkerId = worker.Id,
            Kind = "Prompt",
            State = Delivery.Accepted,
            NativeMessageId = "msg_user",
            Payload = Json.Write(new PromptInput(Guid.NewGuid().ToString(), "Task", worker.Revision))
        };
        await app.Store.Write(db => { db.Commands.Add(command); return Task.FromResult(true); });
        return command;
    }

    private static NativeSnapshot Snapshot(WorkerRecord worker, CommandRecord command, JsonElement final) => new(
        JsonSerializer.SerializeToElement(new { }),
        [
            JsonSerializer.SerializeToElement(new { info = new { id = command.NativeMessageId, role = "user", sessionID = worker.NativeSessionId, time = new { created = 1L } }, parts = Array.Empty<object>() }),
            JsonSerializer.SerializeToElement(new { info = new { id = "msg_tool", role = "assistant", parentID = command.NativeMessageId, sessionID = worker.NativeSessionId, time = new { created = 2L, completed = 3L }, finish = "tool-calls" }, parts = new[] { new { type = "tool", tool = "bash", state = new { status = "completed" } } } }),
            final
        ],
        "idle", JsonSerializer.SerializeToElement(new { }), [], []);

    private static JsonElement FinalAssistant(long? completed, string text) => JsonSerializer.SerializeToElement(new
    {
        info = completed.HasValue
            ? new Dictionary<string, object> { ["id"] = "msg_final", ["role"] = "assistant", ["parentID"] = "msg_user", ["sessionID"] = "ses_fixture", ["time"] = new { created = 3L, completed }, ["finish"] = "stop" }
            : new Dictionary<string, object> { ["id"] = "msg_final", ["role"] = "assistant", ["parentID"] = "msg_user", ["sessionID"] = "ses_fixture", ["time"] = new { created = 3L } },
        parts = new[] { new { type = "text", text } }
    });

    private static JsonElement DeniedTool(WorkerRecord worker, CommandRecord command) => JsonSerializer.SerializeToElement(new
    {
        info = new { id = "msg_denied", role = "assistant", parentID = command.NativeMessageId, sessionID = worker.NativeSessionId, time = new { created = 3L, completed = 4L }, finish = "tool-calls" },
        parts = new[] { new { type = "tool", tool = "bash", state = new { status = "error", error = "Permission was rejected" } } }
    });

    private static Task<bool> Reconcile(TestApp app, WorkerRecord worker, NativeSnapshot snapshot)
    {
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("Reconcile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<bool>)method.Invoke(supervisor, [worker.Id, snapshot])!;
    }
}
