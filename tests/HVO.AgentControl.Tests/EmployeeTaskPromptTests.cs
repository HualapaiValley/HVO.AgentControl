using System.Text;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The host-generated task prompt is canonical and bounded and can never carry
/// an internal control identity or a secret, because it is rendered only from the
/// closed task specification.
/// </summary>
public sealed class EmployeeTaskPromptTests
{
    private static WorkerTaskSpec Spec(
        string description = "Add a bounded health endpoint",
        string workspaceRoot = "/workspace/project",
        IReadOnlyList<string>? allowedPaths = null,
        IReadOnlyList<string>? allowedTools = null,
        IReadOnlyList<string>? forbiddenActions = null,
        int maximumSeconds = 300,
        string? testRecipeId = WorkerTaskTestRecipes.DotnetTestRelease) => new(
            description,
            workspaceRoot,
            allowedPaths ?? ["src", "tests"],
            allowedTools ?? [WorkerTaskTools.Read, WorkerTaskTools.Edit, WorkerTaskTools.Test],
            forbiddenActions ?? ["network egress", "secret access"],
            maximumSeconds,
            testRecipeId,
            Version: 1,
            MaximumTurns: 1);

    [Fact]
    public void RenderedPromptCarriesEveryConstraintAndExplicitHardRules()
    {
        var prompt = WorkerTaskPrompt.Render(Spec());

        Assert.Contains("Add a bounded health endpoint", prompt, StringComparison.Ordinal);
        Assert.Contains("/workspace/project", prompt, StringComparison.Ordinal);
        Assert.Contains("src, tests", prompt, StringComparison.Ordinal);
        Assert.Contains("edit, read, test", prompt, StringComparison.Ordinal);
        Assert.Contains("network egress, secret access", prompt, StringComparison.Ordinal);
        Assert.Contains("300 seconds", prompt, StringComparison.Ordinal);
        Assert.Contains("exactly one turn", prompt, StringComparison.Ordinal);
        Assert.Contains(WorkerTaskTestRecipes.DotnetTestRelease, prompt, StringComparison.Ordinal);
        Assert.Contains(".task-nuget", prompt, StringComparison.Ordinal);
        Assert.Contains("obj/project.assets.json", prompt, StringComparison.Ordinal);
        Assert.Contains("--no-restore", prompt, StringComparison.Ordinal);
        Assert.Contains("no network", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not place credentials", prompt, StringComparison.Ordinal);
        Assert.Contains("organization and employee orientation", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not commit", prompt, StringComparison.Ordinal);
        Assert.Contains("do not push", prompt, StringComparison.Ordinal);
        Assert.Contains("GitHub", prompt, StringComparison.Ordinal);
        Assert.Contains("credentials", prompt, StringComparison.Ordinal);
        Assert.Contains("changedPaths", prompt, StringComparison.Ordinal);
        Assert.Contains("deniedAction", prompt, StringComparison.Ordinal);
        Assert.Contains("noSideEffect", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not include markdown fences", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedPromptIsDeterministicAndContainsNoInternalIdentitiesOrSecrets()
    {
        var first = WorkerTaskPrompt.Render(Spec());
        var second = WorkerTaskPrompt.Render(Spec());
        Assert.Equal(first, second);

        foreach (var marker in new[] { "wrk-", "rtb-", "ses-", "acps-", "req-", "tsk-", "turn-", "control.db", "owner", "password", "bridge.sock" })
        {
            Assert.DoesNotContain(marker, first, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RenderedPromptIsBoundedToTheByteLimit()
    {
        // The description and path arrays are each individually bounded, but the
        // rendered prompt must still refuse to exceed its own byte ceiling rather
        // than truncate a security constraint silently.
        var longPath = new string('a', 700);
        var spec = Spec(allowedPaths: Enumerable.Range(0, 32).Select(i => $"{longPath}{i}").ToArray());

        var exception = Assert.Throws<OrganizationValidationException>(() => WorkerTaskPrompt.Render(spec));
        Assert.Contains("bytes", exception.Message, StringComparison.OrdinalIgnoreCase);

        var normal = WorkerTaskPrompt.Render(Spec());
        Assert.True(Encoding.UTF8.GetByteCount(normal) <= WorkerTaskPrompt.MaxPromptBytes);
    }

    [Theory]
    [InlineData(WorkerTaskStates.Requested, EmployeeTaskDisplayStates.Requested)]
    [InlineData(WorkerTaskStates.Running, EmployeeTaskDisplayStates.Running)]
    [InlineData(WorkerTaskStates.Completed, EmployeeTaskDisplayStates.Completed)]
    [InlineData(WorkerTaskStates.Verified, EmployeeTaskDisplayStates.Verified)]
    [InlineData(WorkerTaskStates.Failed, EmployeeTaskDisplayStates.Failed)]
    [InlineData(WorkerTaskStates.Cancelled, EmployeeTaskDisplayStates.Cancelled)]
    [InlineData(WorkerTaskStates.Uncertain, EmployeeTaskDisplayStates.Uncertain)]
    public void DisplayStateIsAPureProjectionOfThePersistedTaskState(string taskState, string expected)
    {
        Assert.Equal(expected, EmployeeTaskDisplayStates.ForTaskState(taskState));
    }
}
