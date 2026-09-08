using HVO.AgentControl.Core;
using HVO.AgentControl.GitHub;
using HVO.AgentControl.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class GitHubMergeTests
{
    [Fact]
    public async Task MergeIntentIsIdempotentOnlyForTheSameImmutableIntent()
    {
        await using var app = new TestApp();
        var workers = await SeedWorkers(app.Store);
        var service = app.Services.GetRequiredService<GitHubMergeService>();
        var input = Input(workers[0], workers[1]);

        var first = await service.CreateIntent(input);
        var replay = await service.CreateIntent(input);

        Assert.Equal(first.Id, replay.Id);
        await Assert.ThrowsAsync<ControlException>(() => service.CreateIntent(input with { ReviewedHeadSha = Sha('b') }));
        await Assert.ThrowsAsync<ControlException>(() => service.CreateIntent(input with { ReviewerWorkerId = workers[0] }));
    }

    [Fact]
    public async Task SharedBotReviewCannotAuthorizeMergeIntent()
    {
        await using var app = new TestApp();
        var workers = await SeedWorkers(app.Store);
        var service = app.Services.GetRequiredService<GitHubMergeService>();

        await Assert.ThrowsAsync<ControlException>(() => service.CreateIntent(Input(workers[0], workers[1]) with { ReviewerIdentity = "hvo-agentcontrol[bot]" }));
    }

    private static CreateGitHubMergeIntentInput Input(string author, string reviewer) =>
        new(Guid.NewGuid().ToString("N"), "Owner/Repo", 12, Sha('a'), Sha('a'), Sha('b'), author, reviewer, "reviewer", ["build"]);

    private static string Sha(char value) => new(value, 40);

    private static async Task<string[]> SeedWorkers(ControlStore store)
    {
        var runtime = await store.SaveRuntime(PersistenceTests.Profile());
        var workers = new[]
        {
            new WorkerRecord { Id = "author", RuntimeId = runtime.Id, ManagedServerId = "server-a", NativeSessionId = "session-a", Directory = "/work/a" },
            new WorkerRecord { Id = "reviewer", RuntimeId = runtime.Id, ManagedServerId = "server-b", NativeSessionId = "session-b", Directory = "/work/b" }
        };
        await store.Write(db => { db.Workers.AddRange(workers); return Task.FromResult(true); });
        return workers.Select(x => x.Id).ToArray();
    }
}
