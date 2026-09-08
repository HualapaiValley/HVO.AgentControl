using System.Net;
using System.Net.Http.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class EvidenceCursorTests
{
    [Fact]
    public async Task LostReadResponseIsRecoveredAfterRestartAndOnlyAcknowledgementAdvancesCursor()
    {
        string data, secrets;
        var requestId = Guid.NewGuid().ToString();
        EvidencePage original;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            await AddEvents(app.Store, 3);
            original = await app.Store.ReadEvidence(new("planner-a", requestId, 2));
            var initialCursor = await app.Store.Read(db => db.EvidenceConsumerCursors.SingleAsync(x => x.ConsumerId == "planner-a"));
            Assert.Equal(0, initialCursor.LastConsumedSequence);
        }

        await using var restarted = new TestApp(data, secrets);
        var recovered = await restarted.Store.ReadEvidence(new("planner-a", requestId, 2));
        Assert.Equal(Json.Write(original), Json.Write(recovered));
        await restarted.Store.AcknowledgeEvidence(new("planner-a", requestId, original.AfterSequence));
        var cursor = await restarted.Store.Read(db => db.EvidenceConsumerCursors.SingleAsync(x => x.ConsumerId == "planner-a"));
        Assert.Equal(original.NextSequence, cursor.LastConsumedSequence);
    }

    [Fact]
    public async Task DuplicateReadIsIdempotentAndStaleAcknowledgementIsRejected()
    {
        await using var app = new TestApp();
        await AddEvents(app.Store, 3);
        var requestId = Guid.NewGuid().ToString();
        var input = new EvidenceReadInput("planner-a", requestId, 2);

        var pages = await Task.WhenAll(app.Store.ReadEvidence(input), app.Store.ReadEvidence(input));
        Assert.Equal(Json.Write(pages[0]), Json.Write(pages[1]));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.ReadEvidence(new("planner-a", Guid.NewGuid().ToString(), 2)));
        await Assert.ThrowsAsync<ControlException>(() => app.Store.AcknowledgeEvidence(new("planner-a", requestId, 1)));
        await app.Store.AcknowledgeEvidence(new("planner-a", requestId, pages[0].AfterSequence));
        var replay = await app.Store.AcknowledgeEvidence(new("planner-a", requestId, pages[0].AfterSequence));
        Assert.Equal(Json.Write(pages[0]), Json.Write(replay));
    }

    [Fact]
    public async Task RangeReportsRetentionGapAndBoundsPayloadWithStableReference()
    {
        await using var app = new TestApp();
        await AddEvents(app.Store, 3);
        var oversized = new JournalEvent { Type = "CoordinatorDecisionApplied", Provenance = "service", Payload = new string('x', 20_000) };
        await app.Store.Write(db => { db.Events.Add(oversized); return Task.FromResult(true); });
        await app.Store.Write(async db =>
        {
            db.Events.Remove(await db.Events.OrderBy(x => x.Sequence).FirstAsync());
            return true;
        });

        var page = await app.Store.Evidence(0, 10);
        Assert.True(page.Incomplete);
        Assert.True(page.PayloadOmitted);
        var omitted = Assert.Single(page.Events, x => x.Id == oversized.Id);
        Assert.Null(omitted.Payload);
        Assert.True(omitted.PayloadOmitted);
        Assert.Equal(20_000, omitted.PayloadCharacters);
        Assert.Equal("journal-event:" + oversized.Id, omitted.RetrievalReference);

        using var client = await app.SignIn();
        var response = await client.GetAsync("/api/v1/evidence?after=0&take=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiPage = await response.Content.ReadFromJsonAsync<EvidencePage>();
        Assert.NotNull(apiPage);
        Assert.True(apiPage.PayloadOmitted);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/evidence?take=201")).StatusCode);
    }

    private static Task AddEvents(ControlStore store, int count) => store.Write(db =>
    {
        for (var index = 0; index < count; index++)
            ControlStore.Event(db, "FixtureEvidence", commandId: "command-" + index, payload: new { index }, provenance: "fixture");
        return Task.FromResult(true);
    });
}
