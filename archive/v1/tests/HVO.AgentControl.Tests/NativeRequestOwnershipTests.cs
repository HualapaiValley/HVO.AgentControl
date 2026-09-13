using System.Text.Json;
using HVO.AgentControl.OpenCode;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class NativeRequestOwnershipTests
{
    private static JsonElement Session(string id, string? parent, string directory = "/workspace") =>
        JsonSerializer.SerializeToElement(new { id, parentID = parent, directory });
    private static JsonElement Requests(params string[] sessions) =>
        JsonSerializer.SerializeToElement(sessions.Select((id, i) => new { id = "request" + i, sessionID = id }));

    [Fact]
    public async Task IncludesNestedChildApprovalsAndQuestionsButNotOtherWorkers()
    {
        var sessions = new Dictionary<string, JsonElement>
        {
            ["child"] = Session("child", "root"),
            ["grandchild"] = Session("grandchild", "child"),
            ["other-child"] = Session("other-child", "other"),
            ["other"] = Session("other", null)
        };
        var reads = 0;
        var filter = new NativeRequestOwnership("root", "/workspace", (id, _) => { reads++; return Task.FromResult(sessions[id]); });
        var permissions = await filter.Filter(Requests("root", "child", "grandchild", "other-child"), CancellationToken.None);
        Assert.Equal(new[] { "root", "child", "grandchild" }, permissions.Select(x => x.GetProperty("sessionID").GetString()));
        Assert.Equal(4, reads);
        var questions = await filter.Filter(Requests("grandchild", "other-child"), CancellationToken.None);
        Assert.Single(questions); Assert.Equal(4, reads);
    }

    [Fact]
    public async Task RejectsCyclesMismatchedIdentityAndDifferentWorkspace()
    {
        var sessions = new Dictionary<string, JsonElement>
        {
            ["a"] = Session("a", "b"),
            ["b"] = Session("b", "a"),
            ["wrong-id"] = Session("not-requested", "root"),
            ["outside"] = Session("outside", "root", "/different")
        };
        var filter = new NativeRequestOwnership("root", "/workspace", (id, _) => Task.FromResult(sessions[id]));
        Assert.Empty(await filter.Filter(Requests("a", "wrong-id", "outside"), CancellationToken.None));
    }

    [Fact]
    public async Task MissingSessionsAreIgnoredButTransportFailureCannotHidePendingApprovals()
    {
        var missing = new NativeRequestOwnership("root", "/workspace", (_, _) => throw new NativeRejectedException(404));
        Assert.Empty(await missing.Filter(Requests("deleted"), CancellationToken.None));
        var failed = new NativeRequestOwnership("root", "/workspace", (_, _) => throw new HttpRequestException("disconnected"));
        await Assert.ThrowsAsync<HttpRequestException>(() => failed.Filter(Requests("child"), CancellationToken.None));
    }

    [Fact]
    public async Task UnboundedAncestryFailsObservationInsteadOfClaimingNoApprovals()
    {
        var filter = new NativeRequestOwnership("root", "/workspace", (id, _) => Task.FromResult(Session(id, id + "x")));
        await Assert.ThrowsAsync<InvalidDataException>(() => filter.Filter(Requests("child"), CancellationToken.None));
    }
}
