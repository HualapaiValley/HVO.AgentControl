using HVO.AgentControl.Organization;
using System.Diagnostics;

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
        var maximum = timeout ?? Timeout;
        var interval = pollInterval ?? PollInterval;
        if (maximum <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "The organization-store startup timeout must be positive.");
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval), "The organization-store polling interval must be positive.");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (read() is { } store) return store;
            }
            catch (OrganizationStoreException)
            {
                // Store construction/opening can transiently expose its own typed
                // failure while AcpControlHost is still establishing authority.
                // Treat it exactly like null during this bounded startup window;
                // request paths still report immediate sanitized failures.
            }
            var remaining = maximum - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) return null;
            await Task.Delay(remaining < interval ? remaining : interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
