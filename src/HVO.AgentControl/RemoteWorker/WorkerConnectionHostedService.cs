using HVO.AgentControl.Organization;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Starts the optional remote-worker connection manager. The subsystem is
/// disabled by default and deliberately does not fault the whole control host:
/// an enabled-but-invalid configuration or a control store that is not open yet
/// holds remote execution, and the remote-worker endpoints report that state as
/// a sanitized 409/503 instead of the process dying at startup.
/// </summary>
internal sealed class WorkerConnectionHostedService(
    WorkerConnectionManager manager,
    IOptions<WorkerControlOptions> configured,
    ILogger<WorkerConnectionHostedService> logger) : BackgroundService
{
    private readonly WorkerControlOptions _options = configured.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var configurationErrors = _options.Validate();
        if (configurationErrors.Count > 0)
        {
            logger.LogError(
                "WorkerControl is enabled with invalid configuration; remote worker execution is held until it is corrected: {Errors}",
                string.Join(" ", configurationErrors));
            return;
        }

        try
        {
            await manager.ReconcileStartupAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OrganizationStoreException)
        {
            // The authoritative control store is unavailable: either the control
            // runtime is disabled or it has not finished opening. The host stays
            // up and the first accepted request reconciles startup instead.
            logger.LogWarning(
                "Remote worker startup reconciliation is held because the authoritative control store is unavailable.");
            return;
        }

        if (!_options.HostedManagerEnabled)
        {
            return;
        }

        await manager.RunMaintenanceAsync(stoppingToken).ConfigureAwait(false);
    }
}
