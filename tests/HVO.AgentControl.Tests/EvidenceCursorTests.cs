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
    public async Task ConsumerCursorAdvancesAtomicallyAndDoesNotRepeatAfterRestart()
    {
        string data, secrets;
        long firstNextSequence;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            await AddEvents(app.Store, 3);

            var consumed = await app.Store.ConsumeEvidence(new("planner-a", 2));
            Assert.Equal(2, consumed.Events.Length);
            Assert.True(consumed.Truncated);
            Assert.False(consumed.Incomplete);
            firstNextSequence = consumed.NextSequence;

            var cursor = await app.Store.Read(db => db.EvidenceConsumerCursors.SingleAsync(x => x.ConsumerId == "planner-a"));
            Assert.Equal(consumed.NextSequence, cursor.LastConsumedSequence);
        }

        await using var restarted = new TestApp(data, secrets);
        var remaining = await restarted.Store.ConsumeEvidence(new("planner-a", 2));
        Assert.NotEmpty(remaining.Events);
        Assert.All(remaining.Events, x => Assert.True(x.Sequence > firstNextSequence));
    }

    [Fact]
    public async Task ConcurrentConsumersOfOneCursorReceiveDistinctBoundedPages()
    {
        await using var app = new TestApp();
        await AddEvents(app.Store, 3);

        var pages = await Task.WhenAll(
            app.Store.ConsumeEvidence(new("planner-a", 2)),
            app.Store.ConsumeEvidence(new("planner-a", 2)));

        var sequences = pages.SelectMany(x => x.Events).Select(x => x.Sequence).ToArray();
        Assert.NotEmpty(sequences);
        Assert.Equal(sequences.Length, sequences.Distinct().Count());
    }

    [Fact]
    public async Task RangeReportsRetentionGapAndApiUsesBoundedReadOnlyQuery()
    {
        await using var app = new TestApp();
        await AddEvents(app.Store, 3);
        await app.Store.Write(async db =>
        {
            var first = await db.Events.OrderBy(x => x.Sequence).FirstAsync();
            db.Events.Remove(first);
            return true;
        });

        var page = await app.Store.Evidence(0, 1);
        Assert.True(page.Incomplete);
        Assert.True(page.Truncated);
        Assert.Equal(2, page.EarliestAvailableSequence);
        Assert.Equal("fixture", Assert.Single(page.Events).Provenance);

        using var client = await app.SignIn();
        var response = await client.GetAsync("/api/v1/evidence?after=0&take=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiPage = await response.Content.ReadFromJsonAsync<EvidencePage>();
        Assert.NotNull(apiPage);
        Assert.True(apiPage.Incomplete);
        Assert.Single(apiPage.Events);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/evidence?take=201")).StatusCode);
    }

    private static Task AddEvents(ControlStore store, int count) => store.Write(db =>
    {
        for (var index = 0; index < count; index++)
            ControlStore.Event(db, "FixtureEvidence", commandId: "command-" + index, payload: new { index }, provenance: "fixture");
        return Task.FromResult(true);
    });
}
