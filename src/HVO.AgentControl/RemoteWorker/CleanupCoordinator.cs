using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Scoped cleanup for failed profile builds and failed provisioning attempts.
/// Both paths are deliberately narrow: a build image can be removed only when the
/// controller can prove no enrollment, frozen managed approval or active/applied
/// employee rebuild still names its digest, and provisioning cleanup refuses while
/// an employee rebuild is active for the worker. Nothing here removes a resource it
/// cannot prove this controller owns, and no path ever removes an image as part of
/// provisioning cleanup.
/// </summary>
public sealed class CleanupCoordinator(
    AcpControlHost control,
    IRemoteWorkerProvisioner remote,
    RemoteWorkerProvisioningCoordinator provisioning,
    IOptions<WorkerControlOptions> configured,
    ILogger<CleanupCoordinator> logger)
{
    private readonly WorkerControlOptions _options = configured.Value;
    private readonly ExecutionTargetResolver _targets = new(control, configured);

    /// <summary>
    /// Removes one failed or rejected profile build's result tag and image. A
    /// live, built or uncertain build is refused: a live build may still produce an
    /// image, a built/verified image is provisionable, and an uncertain result must
    /// be reconciled first. The digest is refused when any enrollment, frozen
    /// managed approval or active/applied employee rebuild names it, so an image in
    /// use is never removed. Removal is idempotent (an already-absent image is
    /// tolerated) and only the store row is marked <c>removed</c> after the
    /// transport confirms the effect. An uncertain transport leaves the row
    /// non-removed and surfaces a recovery obligation so the operator retries.
    /// </summary>
    public async Task<ProfileBuildRecord> CleanupProfileBuildAsync(string profileBuildId, string requestedBy, CancellationToken cancellationToken)
    {
        RequireEnabled();
        var store = Store();
        var build = store.GetProfileBuild(profileBuildId)
            ?? throw new OrganizationNotFoundException($"Profile build '{profileBuildId}' does not exist.");

        if (build.State == ProfileBuildStates.Uncertain)
            throw new WorkerRecoveryRequiredException("A profile build with an unknown result cannot be removed until it is reconciled by tag.", "profile-build-uncertain");
        if (build.State is not (ProfileBuildStates.Failed or ProfileBuildStates.Rejected))
            throw new OrganizationConcurrencyException($"Profile build '{profileBuildId}' is {build.State}; only a failed or rejected build can be removed.");

        var host = _targets.Resolve(build.HostId);

        // Claim the removal durably before any remote effect. The claim revalidates
        // the shared-tag and in-use checks in one transaction and reserves the
        // (host, tag) pair, so a concurrent build cannot reuse the tag between the
        // preflight and the removal. The claim is released on finalize or failure.
        var claimed = store.ClaimProfileBuildRemoval(build.Id, build.Revision, requestedBy);
        var removed = new List<string> { claimed.ResultTag };

        try
        {
            // Resolution and the digest guards run inside the claim so any refusal
            // releases it and leaves the state retryable, while a transport failure
            // keeps the claim for reconciliation.
            var tagDigest = await remote.InspectImageAsync(host, claimed.ResultTag, cancellationToken).ConfigureAwait(false);
            if (tagDigest is not null) EnsureTagTargetRemovable(tagDigest);
            if (claimed.ImageDigest is { } recordedDigest) EnsureImageNotInUse(recordedDigest);

            await remote.RemoveImageAsync(host, claimed.ResultTag, cancellationToken).ConfigureAwait(false);
            var digestReference = tagDigest ?? claimed.ImageDigest;
            if (digestReference is { } imageDigest && !string.Equals(imageDigest, claimed.ResultTag, StringComparison.Ordinal))
            {
                EnsureImageNotInUse(imageDigest);
                removed.Add(imageDigest);
                await remote.RemoveImageAsync(host, imageDigest, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RemoteWorkerUnavailableException exception) when (exception.Transport)
        {
            // The effect is unknown: the tag may or may not be gone. Keep the claim
            // and do not mark the row removed; a retry is idempotent and tolerates
            // not-found.
            throw new WorkerRecoveryRequiredException("Image removal transport failed; the build remains claimed and unremoved until cleanup is retried.", "profile-build-remove-uncertain");
        }
        catch (OrganizationValidationException)
        {
            // A guard refused the removal, so nothing destructive happened (or only
            // an untag did). Release the claim so the state stays retryable and the
            // build is not marked removed.
            store.FailProfileBuildRemoval(claimed.Id, claimed.Revision, "image removal was refused by the in-use guard after the claim");
            throw;
        }

        var evidence = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', claimed.Id, "remove-profile-build", string.Join('\n', removed))))).ToLowerInvariant();
        var result = store.FinalizeProfileBuildRemoval(claimed.Id, claimed.Revision, requestedBy, evidence);
        logger.LogInformation("Removed failed profile build {BuildId}; references {References}.", claimed.Id, string.Join(", ", removed));
        return result;
    }

    /// <summary>
    /// Removes exactly the resources this controller owns for one worker. It is the
    /// existing label-exact cleanup path with one added guard: an active employee
    /// rebuild holds the worker, so its container and volumes must not be deleted
    /// out from under it. Provisioning cleanup never removes an image.
    /// </summary>
    public Task CleanupFailedProvisioningAsync(string workerId, CancellationToken cancellationToken)
    {
        RequireEnabled();
        var store = Store();
        var enrollment = store.GetWorkerEnrollment(workerId) ?? throw new KeyNotFoundException("Worker not found.");
        if (store.GetActiveEmployeeRebuild(enrollment.WorkerId) is { } active)
            throw new OrganizationConcurrencyException($"The worker has an active employee rebuild in state {active.State}; it must finish or be reconciled before provisioning cleanup.");
        return provisioning.CleanupAsync(workerId, cancellationToken);
    }

    /// <summary>
    /// Refuses cleanup when <paramref name="imageDigest"/> is still named by an
    /// enrollment, a frozen managed approval, or an active or applied rebuild. The
    /// message names only the reason categories, never the digest or any record.
    /// </summary>
    public void EnsureImageNotInUse(string imageDigest)
    {
        var usage = Store().ImageDigestUsage(imageDigest);
        if (usage.Count > 0)
            throw new OrganizationValidationException($"The image is still in use ({string.Join(", ", usage)}); it cannot be removed.");
    }

    /// <summary>
    /// Refuses to remove a tag whose target must survive: the configured approved
    /// base image or any digest an enrollment, frozen approval or rebuild names.
    /// Removing the tag could delete the image when no other reference remains, so
    /// the target is protected before the tag is touched.
    /// </summary>
    private void EnsureTagTargetRemovable(string imageDigest)
    {
        if (string.Equals(imageDigest, _options.ApprovedImageDigest, StringComparison.Ordinal))
            throw new OrganizationValidationException("The build's tag points at the approved base image; removing it could delete the base.");
        EnsureImageNotInUse(imageDigest);
    }

    private void RequireEnabled()
    {
        if (!_options.Enabled) throw new WorkerControlDisabledException();
        if (_options.Validate().Count != 0) throw new WorkerControlConfigurationException("Remote worker configuration is invalid.");
    }

    private OrganizationStore Store() => control.Organization ?? throw new OrganizationStoreException("Organization store unavailable.");
}
