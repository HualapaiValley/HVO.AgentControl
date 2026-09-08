using System.Reflection;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using HVO.AgentControl.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RuntimeSupervisorReconciliationTests
{
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
        Assert.Contains("APPROVED receipt", ControlStore.ResponseText(completed.ResultJson!));
    }

    [Fact]
    public async Task CompletedLatestAssistantWithRunningToolDoesNotFinish()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        var command = await AddPrompt(app, worker);
        var pendingTool = JsonSerializer.SerializeToElement(new
        {
            info = new { id = "msg_final", role = "assistant", parentID = command.NativeMessageId, sessionID = worker.NativeSessionId, time = new { created = 3L, completed = 4L } },
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
            JsonSerializer.SerializeToElement(new { info = new { id = "msg_tool", role = "assistant", parentID = command.NativeMessageId, sessionID = worker.NativeSessionId, time = new { created = 2L, completed = 3L } }, parts = new[] { new { type = "tool", tool = "bash", state = new { status = "completed" } } } }),
            final
        ],
        "idle", JsonSerializer.SerializeToElement(new { }), [], []);

    private static JsonElement FinalAssistant(long? completed, string text) => JsonSerializer.SerializeToElement(new
    {
        info = completed.HasValue
            ? new Dictionary<string, object> { ["id"] = "msg_final", ["role"] = "assistant", ["parentID"] = "msg_user", ["sessionID"] = "ses_fixture", ["time"] = new { created = 3L, completed } }
            : new Dictionary<string, object> { ["id"] = "msg_final", ["role"] = "assistant", ["parentID"] = "msg_user", ["sessionID"] = "ses_fixture", ["time"] = new { created = 3L } },
        parts = new[] { new { type = "text", text } }
    });

    private static Task<bool> Reconcile(TestApp app, WorkerRecord worker, NativeSnapshot snapshot)
    {
        var supervisor = new RuntimeSupervisor(app.Store, null!, Options.Create(new ControlOptions()), NullLogger<RuntimeSupervisor>.Instance);
        var method = typeof(RuntimeSupervisor).GetMethod("Reconcile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<bool>)method.Invoke(supervisor, [worker.Id, snapshot])!;
    }
}
