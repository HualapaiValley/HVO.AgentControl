using System.Net;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class TranscriptPaginationTests
{
    [Fact]
    public async Task HistoryRejectsCursorIdWithoutTimestampButKeepsTimestampCompatibility()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(db =>
        {
            db.Messages.Add(new TranscriptMessage { WorkerId = worker.Id, NativeId = "message", NativeCreatedAt = 10 });
            return Task.FromResult(true);
        });

        var error = await Assert.ThrowsAsync<ControlException>(() => app.Store.Detail(worker.Id, beforeId: "message"));
        Assert.Equal(400, error.Status);
        Assert.Single((await app.Store.Detail(worker.Id, before: 11)).Messages);
        using var owner = await app.SignIn();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/v1/workers/{worker.Id}/history?beforeId=message")).StatusCode);
    }

    [Fact]
    public async Task HistoryCursorRetainsTimestampTiesAcrossPagesWithoutRepeatingMessages()
    {
        await using var app = new TestApp();
        var worker = await PersistenceTests.SeedWorker(app.Store);
        await app.Store.Write(db =>
        {
            for (var i = 0; i < 199; i++)
                db.Messages.Add(new TranscriptMessage { WorkerId = worker.Id, NativeId = $"newer-{i:D3}", NativeCreatedAt = 11 + i });
            db.Messages.Add(new TranscriptMessage { WorkerId = worker.Id, NativeId = "tie-a", NativeCreatedAt = 10 });
            db.Messages.Add(new TranscriptMessage { WorkerId = worker.Id, NativeId = "tie-b", NativeCreatedAt = 10 });
            db.Messages.Add(new TranscriptMessage { WorkerId = worker.Id, NativeId = "tie-c", NativeCreatedAt = 10 });
            return Task.FromResult(true);
        });

        var first = await app.Store.Detail(worker.Id);
        var cursor = first.Messages.Last();
        var second = await app.Store.Detail(worker.Id, cursor.NativeCreatedAt, cursor.NativeId);
        var nextCursor = second.Messages.Last();
        var third = await app.Store.Detail(worker.Id, nextCursor.NativeCreatedAt, nextCursor.NativeId);

        Assert.Equal(200, first.Messages.Count);
        Assert.Equal(new[] { "tie-c" }, first.Messages.Where(x => x.NativeCreatedAt == 10).Select(x => x.NativeId));
        Assert.Equal(new[] { "tie-b", "tie-a" }, second.Messages.Select(x => x.NativeId));
        Assert.Equal(202, first.Messages.Concat(second.Messages).Select(x => x.NativeId).Distinct().Count());
        Assert.Empty(third.Messages);
    }
}
