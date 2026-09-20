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
    ProfileBuildCoordinator builds,
    EmployeeRebuildCoordinator rebuilds,
    HVO.AgentControl.Runtime.AcpControlHost control,
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
            // A provisioning step left applying by a previous process has an unknown
            // remote effect. Mark it uncertain before the manager reconciles so the
            // next apply inspects the effect instead of repeating it blind.
            var store = control.Organization ?? throw new OrganizationStoreException("Organization store unavailable.");
            var interrupted = store.MarkInterruptedProvisioningOperationsUncertain();
            if (interrupted > 0) logger.LogWarning("Marked {Count} interrupted provisioning operation(s) uncertain for reconciliation.", interrupted);

            // A build left building/verifying by a previous process is unknowable now;
            // mark it uncertain so the next build request reconciles it by tag.
            var buildInterrupted = builds.ReconcileInterruptedOnStartup();
            if (buildInterrupted.Count > 0) logger.LogWarning("Marked {Count} interrupted profile build(s) uncertain for reconciliation: {Ids}", buildInterrupted.Count, string.Join(", ", buildInterrupted));

            // Rebuild reconciliation must run before the manager reconnects: it
            // establishes/retains the manual dispatch hold and verifies any target
            // container with a fresh owner lease before normal dispatch can resume.
            var rebuildReconciled = await rebuilds.ReconcileInterruptedOnStartup(stoppingToken).ConfigureAwait(false);
            if (rebuildReconciled.Count > 0) logger.LogWarning("Reconciled {Count} interrupted employee rebuild(s): {Ids}", rebuildReconciled.Count, string.Join(", ", rebuildReconciled));
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
