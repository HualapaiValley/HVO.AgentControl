using HVO.AgentControl.Components.Pages;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ConversationSelectionTests
{
    [Fact]
    public async Task DelayedSuccessCannotReplaceANewerConversation()
    {
        var selection = new ConversationSelection();
        var a = selection.Change("worker-a");
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = Task.Run(async () => { await releaseA.Task; return selection.IsCurrent(a); });

        var b = selection.Change("worker-b");
        Assert.True(selection.IsCurrent(b));
        releaseA.SetResult();

        Assert.False(await stale);
    }

    [Fact]
    public async Task DelayedErrorIsStaleAfterRapidWorkerSelection()
    {
        var selection = new ConversationSelection();
        var a = selection.Change("worker-a");
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleError = Task.Run(async () =>
        {
            try { await releaseA.Task; throw new InvalidOperationException("delayed read failure"); }
            catch (InvalidOperationException) { return !selection.IsCurrent(a); }
        });

        selection.Change("worker-b");
        var latest = selection.Change("worker-a");
        Assert.True(selection.IsCurrent(latest));
        releaseA.SetResult();

        Assert.True(await staleError);
    }

    [Fact]
    public void OnlyTheLatestOfRapidSelectionsCanApply()
    {
        var selection = new ConversationSelection();
        var a = selection.Change("worker-a");
        var b = selection.Change("worker-b");
        var c = selection.Change("worker-c");

        Assert.False(selection.IsCurrent(a));
        Assert.False(selection.IsCurrent(b));
        Assert.True(selection.IsCurrent(c));
    }

    [Fact]
    public void CompletionCannotApplyAfterConversationDisposal()
    {
        var selection = new ConversationSelection();
        var pending = selection.Change("worker-a");

        selection.Invalidate();

        Assert.False(selection.IsCurrent(pending));
    }
}
