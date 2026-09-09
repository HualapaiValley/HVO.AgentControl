using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed partial class CoordinationTests
{
    [Fact]
    public async Task RepositoryObjectRepairSurvivesRestartAndDispatchesCorrectedReviewerScopeOnce()
    {
        string data, secrets, runId, firstWorkerId, reviewerId, rejectedId, rejectedResult, feedback;
        await using (var app = new TestApp())
        {
            data = app.DataPath; secrets = app.SecretPath;
            var (coordinator, first, reviewer) = await Seed(app.Store);
            firstWorkerId = first.Id; reviewerId = reviewer.Id;
            runId = (await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id,
                "Ask for status and independently review the verified PR head.", [first.Id, reviewer.Id]))).Id;
            await app.Store.CoordinationTick();
            rejectedId = (await app.Store.Coordinations()).Single().DecisionCommandId!;
            var badDecision = Json.Write(new
            {
                summary = "Request status and independent review.",
                complete = false,
                actions = new object[]
                {
                    new { type = "send_prompt", workerId = first.Id, text = "Report status." },
                    new
                    {
                        type = "send_prompt", workerId = reviewer.Id, text = "Review the exact PR head independently.",
                        githubMergeScope = new
                        {
                            version = 1, purpose = "PullRequestMergeAuthority", role = "Reviewer",
                            repository = new { owner = "secret-rejected-value", name = "repository" },
                            pullRequestNumber = 194, headSha = new string('a', 40)
                        }
                    }
                }
            });
            await Finish(app.Store, rejectedId, badDecision);
            rejectedResult = await app.Store.Read(async db => (await db.Commands.FindAsync(rejectedId))!.ResultJson);
            await app.Store.CoordinationTick();
            var run = (await app.Store.Coordinations()).Single();
            var repair = Json.Read<CoordinatorContext>(run.InputJson).Repair!;
            Assert.Equal("Ready", run.State);
            Assert.Equal(1, repair.Attempt);
            Assert.Equal(rejectedId, repair.RejectedCommandId);
            feedback = repair.Feedback!;
            Assert.Contains("JSON string at actions[].githubMergeScope.repository", feedback);
            Assert.DoesNotContain("secret-rejected-value", feedback + run.Detail);
            Assert.InRange(feedback.Length, 1, 300);
            // Parsing rejects the whole batch, including the first action that was already valid.
            Assert.DoesNotContain((await app.Store.Snapshot()).Commands, command => command.Origin == "coordinator:" + runId);
        }

        await using var restarted = new TestApp(data, secrets);
        var retained = (await restarted.Store.Coordinations()).Single();
        Assert.Equal(feedback, Json.Read<CoordinatorContext>(retained.InputJson).Repair!.Feedback);
        Assert.False(await restarted.Store.CoordinationTick());
        await ObserveIdle(restarted.Store);
        await restarted.Store.CoordinationTick();
        var correction = (await restarted.Store.Coordinations()).Single();
        Assert.NotEqual(rejectedId, correction.DecisionCommandId);
        var correctionCommand = await restarted.Store.Read(async db => (await db.Commands.FindAsync(correction.DecisionCommandId))!);
        var input = Json.Read<PromptInput>(correctionCommand.Payload).Text;
        Assert.Contains("JSON STRING containing", input);
        Assert.Contains("githubMergeScope.repository", input);
        Assert.Contains("Reviewer role", input);
        Assert.Equal(feedback, Json.Read<CoordinatorContext>(correction.InputJson).Repair!.Feedback);

        var scope = new GitHubMergeTaskScope(1, "PullRequestMergeAuthority", "Reviewer", "owner/repository", 194, new string('a', 40));
        await FinishDecision(restarted.Store, runId, new("Corrected repository string; retain independent review.",
        [
            new("send_prompt", firstWorkerId, "Report status."),
            new("send_prompt", reviewerId, "Review the exact PR head independently.", GitHubMergeScope: scope)
        ]));
        await restarted.Store.CoordinationTick();
        await restarted.Store.CoordinationTick();
        var dispatched = (await restarted.Store.Snapshot()).Commands.Where(command => command.Origin == "coordinator:" + runId).ToArray();
        Assert.Equal(2, dispatched.Length);
        Assert.Single(dispatched, command => command.WorkerId == firstWorkerId);
        var review = Assert.Single(dispatched, command => command.WorkerId == reviewerId);
        Assert.Equal(scope, Json.Read<PromptInput>(review.Payload).GitHubMergeScope);
        Assert.Equal(rejectedResult, await restarted.Store.Read(async db => (await db.Commands.FindAsync(rejectedId))!.ResultJson));
        var applied = (await restarted.Store.Coordinations()).Single();
        Assert.Null(Json.Read<CoordinatorContext>(applied.InputJson).Repair);
        Assert.Equal(1, await restarted.Store.Read(db => db.Events.CountAsync(item => item.Type == "CoordinatorDecisionApplied")));
    }

    [Theory]
    [InlineData("{\"summary\":{\"secret-value\":true},\"complete\":false,\"actions\":[]}", "JSON string at summary")]
    [InlineData("{\"summary\":\"ok\",\"complete\":\"secret-value\",\"actions\":[]}", "JSON boolean at complete")]
    [InlineData("{\"summary\":\"ok\",\"complete\":false,\"actions\":\"secret-value\"}", "JSON array at actions")]
    [InlineData("secret-value is not JSON", "one JSON decision object")]
    public async Task SchemaRepairFeedbackUsesFixedFieldGuidanceWithoutRejectedContent(string response, string expected)
    {
        await using var app = new TestApp();
        var (coordinator, worker, _) = await Seed(app.Store);
        var run = await app.Store.StartCoordination(new(Guid.NewGuid().ToString(), coordinator.Id, "Coordinate", [worker.Id]));
        await app.Store.CoordinationTick();
        await Finish(app.Store, (await app.Store.Coordinations()).Single().DecisionCommandId!, response);
        await app.Store.CoordinationTick();
        var repaired = (await app.Store.Coordinations()).Single();
        var feedback = Json.Read<CoordinatorContext>(repaired.InputJson).Repair!.Feedback!;
        Assert.Contains(expected, feedback);
        Assert.DoesNotContain("secret-value", feedback + repaired.Detail);
        var rejectedEvent = await app.Store.Read(db => db.Events.SingleAsync(item => item.Type == "CoordinatorDecisionRejected"));
        Assert.DoesNotContain("secret-value", rejectedEvent.Payload);
        Assert.DoesNotContain((await app.Store.Snapshot()).Commands, command => command.Origin == "coordinator:" + run.Id);
    }

    [Fact]
    public void LegacyDecisionRepairWithoutFeedbackRemainsReadable()
    {
        var legacy = Json.Read<DecisionRepair>("{\"attempt\":2,\"rejectedCommandId\":\"retained-command\"}");
        Assert.Equal(2, legacy.Attempt);
        Assert.Equal("retained-command", legacy.RejectedCommandId);
        Assert.Null(legacy.Feedback);
    }
}
