using System.Diagnostics;
using System.Reflection;
using System.Text;
using HVO.AgentControl.Components.Pages;
using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace HVO.AgentControl.Tests;

public sealed class LightweightSnapshotTests(ITestOutputHelper output)
{
    [Fact]
    public async Task HistoricalCutoffsRetainEveryOutstandingReceiptWithoutMaterializingLargeBodies()
    {
        await using var app = new TestApp();
        var first = await PersistenceTests.SeedWorker(app.Store);
        var second = new WorkerRecord
        {
            Id = "snapshot-second-worker",
            RuntimeId = first.RuntimeId,
            NativeSessionId = "snapshot-second-session",
            Name = "Second worker"
        };
        var payload = Json.Write(new PromptInput("request", new string('p', 900), 7));
        var execution = Json.Write(new PromptInput("request", new string('e', 900), 7, "fixture", "large"));
        var result = Json.Write(new { messages = new[] { new { parts = new[] { new { type = "text", text = new string('r', 142_800) } } } } });
        const string oldId = "old-outstanding";
        await app.Store.Write(db =>
        {
            db.Workers.Add(second);
            db.Commands.Add(Command(oldId, first, Delivery.Unknown, 1, payload, execution, result));
            for (var i = 0; i < 101; i++)
                db.Commands.Add(Command("first-history-" + i, first, Delivery.Cancelled, i + 2, payload, execution, result));
            for (var i = 0; i < 501; i++)
                db.Commands.Add(Command("global-history-" + i, second, Delivery.Finished, i + 103, payload, execution, result));
            return Task.FromResult(true);
        });

        var storedCharacters = await app.Store.Read(db => db.Commands.SumAsync(x =>
            (long)x.Payload.Length + x.ExecutionPayload.Length + x.ResultJson.Length + x.ProgressText.Length));
        var timer = Stopwatch.StartNew();
        var snapshot = await app.Store.Snapshot();
        var snapshotMilliseconds = timer.ElapsedMilliseconds;
        timer.Restart();
        var detail = await app.Store.Detail(first.Id);
        var detailMilliseconds = timer.ElapsedMilliseconds;
        var snapshotBytes = Encoding.UTF8.GetByteCount(Json.Write(snapshot));
        var detailBytes = Encoding.UTF8.GetByteCount(Json.Write(detail));

        Assert.True(storedCharacters > 72_000_000, $"Expected a realistic large-body baseline, observed {storedCharacters} characters.");
        Assert.Equal(501, snapshot.Commands.Count);
        Assert.Equal(101, detail.Commands.Count);
        Assert.Equal(Delivery.Unknown, snapshot.Commands.Single(x => x.Id == oldId).State);
        Assert.Equal(Delivery.Unknown, detail.Commands.Single(x => x.Id == oldId).State);
        Assert.All(snapshot.Commands, AssertLightweight);
        Assert.All(detail.Commands, AssertLightweight);
        Assert.True(snapshotBytes < 1_000_000, $"Snapshot metadata was {snapshotBytes} bytes.");
        Assert.True(detailBytes < 500_000, $"Worker metadata was {detailBytes} bytes.");

        var exact = await app.Store.Command(oldId);
        Assert.Equal(payload, exact.Payload);
        Assert.Equal(execution, exact.ExecutionPayload);
        Assert.Equal(result, exact.ResultJson);
        Assert.Equal(1000, exact.ProgressText.Length);
        Assert.Equal(7, (await app.Store.CommandPrompt(oldId)).ExpectedRevision);
        output.WriteLine($"stored-body-characters={storedCharacters}; snapshot-bytes={snapshotBytes}; detail-bytes={detailBytes}; snapshot-ms={snapshotMilliseconds}; detail-ms={detailMilliseconds}");
    }

    [Fact]
    public async Task WorkerCreationCardsHydrateBodiesBeyondBatchLimit()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(db =>
        {
            for (var index = 0; index < 101; index++)
                db.Commands.Add(new CommandRecord
                {
                    Id = "creation-" + index,
                    RuntimeId = worker.RuntimeId,
                    Kind = "CreateWorker",
                    State = Delivery.Failed,
                    Payload = Json.Write(new CreateWorkerInput("creation-" + index, worker.RuntimeId, "Worker " + index,
                        "Project", "/work/" + index, "openai", "gpt-5.6-sol")),
                    CreatedAt = index,
                    QueueOrder = index
                });
            return Task.FromResult(true);
        });
        var page = new TestWorkers(app.Store);

        await page.Hydrate(await app.Store.Snapshot());

        Assert.Equal(101, page.Cards.Count);
        Assert.All(page.Cards, command => Assert.NotEmpty(command.Payload));
    }

    private static CommandRecord Command(string id, WorkerRecord worker, string state, long createdAt,
        string payload, string execution, string result) => new()
        {
            Id = id,
            RuntimeId = worker.RuntimeId,
            WorkerId = worker.Id,
            Kind = "Prompt",
            State = state,
            Payload = payload,
            ExecutionPayload = execution,
            ResultJson = result,
            ProgressText = new string('x', 1000),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            QueueOrder = createdAt
        };

    private static void AssertLightweight(CommandRecord command)
    {
        Assert.Empty(command.Payload);
        Assert.Empty(command.ExecutionPayload);
        Assert.Empty(command.ResultJson);
        Assert.True(command.ProgressText.Length <= 600);
    }

    private sealed class TestWorkers : Workers
    {
        public TestWorkers(Infrastructure.ControlStore store) => Store = store;
        public Task Hydrate(ControlSnapshot value) { snapshot = value; return SnapshotChanged(); }
        public List<CommandRecord> Cards => ((IEnumerable<CommandRecord>)typeof(Workers)
            .GetProperty("CreationCards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).ToList();
    }
}