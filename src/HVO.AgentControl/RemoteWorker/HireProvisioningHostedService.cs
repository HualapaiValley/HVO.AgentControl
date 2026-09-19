using HVO.AgentControl.Organization;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Resumes managed hires left mid-flight by a previous process. It is a restart
/// reconciliation, not an approval trigger: only states that already carry an
/// owner approval are listed (Provisioning, Orienting, Interrupted, Uncertain),
/// while Approved is deliberately excluded because the approval endpoint owns
/// that transition. Each hire is resumed through the same idempotent coordinator
/// path; a failure for one hire is logged and never faults the host or the others.
/// </summary>
internal sealed class HireProvisioningHostedService(
    HireProvisioningCoordinator coordinator,
    HVO.AgentControl.Runtime.AcpControlHost control,
    IOptions<WorkerControlOptions> configured,
    ILogger<HireProvisioningHostedService> logger) : BackgroundService
{
    private static readonly string[] ResumableStates =
    [
        HireRequestStates.Provisioning,
        HireRequestStates.Orienting,
        HireRequestStates.Interrupted,
        HireRequestStates.Uncertain,
    ];

    private readonly WorkerControlOptions _options = configured.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        if (_options.Validate().Count > 0) return;

        OrganizationStore store;
        try
        {
            store = control.Organization ?? throw new OrganizationStoreException("Organization store unavailable.");
        }
        catch (OrganizationStoreException)
        {
            return;
        }

        IReadOnlyList<HireRequestSummary> hires;
        try
        {
            hires = store.ListHireRequests();
        }
        catch (OrganizationStoreException exception)
        {
            logger.LogWarning(exception, "Hire provisioning resume is held because the authoritative control store is unavailable.");
            return;
        }

        foreach (var hire in hires.Where(x => ResumableStates.Contains(x.State, StringComparer.Ordinal)))
        {
            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                await coordinator.ResumeAsync(hire.Id, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // One hire that cannot resume must not stop the others or the host;
                // the coordinator already recorded the sanitized outcome.
                logger.LogWarning(exception, "Hire {HireId} could not be resumed to Orienting and remains for recovery.", hire.Id);
            }
        }
    }
}
