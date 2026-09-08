namespace HVO.AgentControl.Components.Pages;

internal sealed class ConversationSelection
{
    private long generation;

    public (string? WorkerId, long Generation) Change(string? workerId) => (workerId, Interlocked.Increment(ref generation));
    public (string? WorkerId, long Generation) Capture(string? workerId) => (workerId, Volatile.Read(ref generation));
    public void Invalidate() => Interlocked.Increment(ref generation);
    public bool IsCurrent((string? WorkerId, long Generation) selection) => selection.Generation == Volatile.Read(ref generation);
}
