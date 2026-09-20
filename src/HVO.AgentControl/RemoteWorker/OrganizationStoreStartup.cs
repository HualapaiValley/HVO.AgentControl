using HVO.AgentControl.Organization;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Bounded startup barrier for optional worker hosted services. The control host
/// opens the authoritative organization store asynchronously after process
/// liveness, so hosted services must not treat an initial null as a permanent
/// disabled state. They wait only for startup, with cancellation and a fixed
/// deadline; API request paths keep their existing immediate 503 behavior.
/// </summary>
internal static class OrganizationStoreStartup
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    internal static async Task<OrganizationStore?> WaitAsync(
        Func<OrganizationStore?> read,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        var deadline = DateTimeOffset.UtcNow + (timeout ?? Timeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read() is { } store) return store;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            await Task.Delay(
                remaining < (pollInterval ?? PollInterval) ? remaining : pollInterval ?? PollInterval,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
