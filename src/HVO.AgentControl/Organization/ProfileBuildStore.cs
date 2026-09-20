using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HVO.AgentControl.Organization;

/// <summary>
/// Profile image builds (schema v9). A build is one attempt to turn one immutable
/// profile revision into an image on one approved execution host. Images live on
/// the host that built them, so the result is recorded per (revision, host), and
/// only a build whose image passed the fixed worker-contract verification becomes
/// a provisionable digest for that host.
/// </summary>
public sealed partial class OrganizationStore
{
    private static string[] ProfileBuildSchemaV9Statements =>
    [
        """
        CREATE TABLE profile_builds (
            id TEXT PRIMARY KEY,
            profile_revision_id TEXT NOT NULL REFERENCES container_profile_revisions(id) ON DELETE RESTRICT,
            host_id TEXT NOT NULL REFERENCES execution_hosts(id) ON DELETE RESTRICT,
            base_image_digest TEXT NOT NULL CHECK (length(base_image_digest) = 71 AND substr(base_image_digest, 1, 7) = 'sha256:'),
            platform TEXT NOT NULL CHECK (platform IN ('linux/amd64', 'linux/arm64')),
            context_hash TEXT NOT NULL CHECK (length(context_hash) = 71 AND substr(context_hash, 1, 7) = 'sha256:'),
            result_tag TEXT NOT NULL CHECK (length(result_tag) BETWEEN 1 AND 200),
            state TEXT NOT NULL CHECK (state IN ('queued', 'building', 'verifying', 'built', 'failed', 'rejected', 'uncertain', 'removed')),
            image_digest TEXT CHECK (image_digest IS NULL OR (length(image_digest) = 71 AND substr(image_digest, 1, 7) = 'sha256:')),
            verified INTEGER NOT NULL CHECK (verified IN (0, 1)),
            failure_summary TEXT CHECK (failure_summary IS NULL OR length(failure_summary) <= 512),
            evidence_hash TEXT CHECK (evidence_hash IS NULL OR (length(evidence_hash) = 71 AND substr(evidence_hash, 1, 7) = 'sha256:')),
            requested_by TEXT NOT NULL CHECK (requested_by IN ('owner')),
            revision INTEGER NOT NULL,
            started_at TEXT,
            finished_at TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        ) WITHOUT ROWID
        """,
        """
        CREATE UNIQUE INDEX one_active_profile_build_per_revision_host
            ON profile_builds (profile_revision_id, host_id)
            WHERE state IN ('queued', 'building', 'verifying', 'uncertain')
        """,
        """
        CREATE UNIQUE INDEX one_verified_profile_build_per_revision_host
            ON profile_builds (profile_revision_id, host_id)
            WHERE state = 'built' AND verified = 1
        """,
        """
        CREATE TRIGGER profile_builds_identity_immutable
            BEFORE UPDATE OF id, profile_revision_id, host_id, base_image_digest, platform, context_hash, result_tag, requested_by, created_at
            ON profile_builds
        BEGIN
            SELECT RAISE(ABORT, 'profile build identity is immutable');
        END
        """,
        """
        CREATE TRIGGER profile_builds_no_delete
            BEFORE DELETE ON profile_builds
        BEGIN
            SELECT RAISE(ABORT, 'profile builds are never deleted');
        END
        """,
        """
        CREATE TRIGGER profile_builds_no_replace
            BEFORE INSERT ON profile_builds
            WHEN EXISTS (SELECT 1 FROM profile_builds WHERE id = NEW.id)
        BEGIN
            SELECT RAISE(ABORT, 'profile builds are never replaced');
        END
        """,
    ];

    public IReadOnlyList<ProfileBuildRecord> ListProfileBuilds(string? profileRevisionId = null, string? hostId = null)
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadBuilds(connection, null, profileRevisionId, hostId, null);
            }
        });
    }

    public ProfileBuildRecord? GetProfileBuild(string id)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.ProfileBuildPrefix)) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadBuilds(connection, null, null, null, id).SingleOrDefault();
            }
        });
    }

    /// <summary>
    /// Records the intent to build. Refuses a retired profile, a revision that is
    /// not the profile's current one and a host that is not ready. A verified
    /// build already present for the pair is returned instead of queuing again,
    /// and so is a live one (queued/building/verifying/uncertain): the caller
    /// resumes or reconciles that row rather than creating a second, which the
    /// partial unique index forbids anyway.
    /// </summary>
    public ProfileBuildRecord QueueProfileBuild(string profileRevisionId, string hostId, string baseImageDigest, string platform, string contextHash, string resultTag)
    {
        if (!IsBoundedIdentifier(profileRevisionId, OrganizationIds.ContainerProfileRevisionPrefix)) throw new OrganizationValidationException("A stable container profile revision id is required.");
        if (string.IsNullOrWhiteSpace(hostId)) throw new OrganizationValidationException("An execution host id is required.");
        if (!IsHash(baseImageDigest) || !IsHash(contextHash)) throw new OrganizationValidationException("Build digests must be exact sha256 values.");
        if (platform is not ("linux/amd64" or "linux/arm64")) throw new OrganizationValidationException("Platform is invalid.");
        if (resultTag is not { Length: >= 1 and <= 200 }) throw new OrganizationValidationException("Result tag is invalid.");

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using (var check = connection.CreateCommand())
                {
                    check.Transaction = transaction;
                    check.CommandText =
                        """
                        SELECT p.status, (r.revision_number = p.current_revision_number),
                               (SELECT status FROM execution_hosts h WHERE h.id = $host AND h.enabled = 1)
                        FROM container_profile_revisions r JOIN container_profiles p ON p.id = r.profile_id
                        WHERE r.id = $revision
                        """;
                    check.Parameters.AddWithValue("$revision", profileRevisionId);
                    check.Parameters.AddWithValue("$host", hostId);
                    using var reader = check.ExecuteReader();
                    if (!reader.Read()) throw new OrganizationNotFoundException($"Container profile revision '{profileRevisionId}' does not exist.");
                    if (reader.GetString(0) != ContainerProfileStatuses.Active) throw new OrganizationConcurrencyException("A retired container profile cannot be built.");
                    if (reader.GetInt64(1) != 1) throw new OrganizationConcurrencyException("Only the profile's current revision can be built.");
                    if (reader.IsDBNull(2) || reader.GetString(2) != "ready") throw new OrganizationConcurrencyException("The execution host is not enabled and ready.");
                }

                var existing = ReadBuilds(connection, transaction, profileRevisionId, hostId, null);
                var verified = existing.FirstOrDefault(b => b.State == ProfileBuildStates.Built && b.Verified);
                if (verified is not null) { transaction.Commit(); return verified; }
                var live = existing.FirstOrDefault(b => ProfileBuildStates.IsLive(b.State));
                if (live is not null) { transaction.Commit(); return live; }

                // A tag claimed for removal is in flight: a new build must not reuse
                // it until the claim finalizes, or a cleanup could detach the new
                // image.
                using (var claimed = connection.CreateCommand())
                {
                    claimed.Transaction = transaction;
                    claimed.CommandText = "SELECT EXISTS(SELECT 1 FROM profile_build_removals WHERE host_id = $host AND result_tag = $tag)";
                    claimed.Parameters.AddWithValue("$host", hostId);
                    claimed.Parameters.AddWithValue("$tag", resultTag);
                    if (Convert.ToInt64(claimed.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
                        throw new OrganizationConcurrencyException("The result tag is claimed for removal; reconcile that cleanup before building it again.");
                }

                var id = OrganizationIds.NewProfileBuildId();
                var now = Timestamp();
                Execute(connection, transaction,
                    """
                    INSERT INTO profile_builds (id, profile_revision_id, host_id, base_image_digest, platform, context_hash, result_tag, state, image_digest, verified, failure_summary, evidence_hash, requested_by, revision, started_at, finished_at, created_at, updated_at)
                    VALUES ($id, $revision, $host, $base, $platform, $context, $tag, 'queued', NULL, 0, NULL, NULL, 'owner', 1, NULL, NULL, $now, $now)
                    """,
                    ("$id", id), ("$revision", profileRevisionId), ("$host", hostId), ("$base", baseImageDigest), ("$platform", platform),
                    ("$context", contextHash), ("$tag", resultTag), ("$now", now));
                Execute(connection, transaction, "UPDATE container_profile_revisions SET build_status = 'building' WHERE id = $revision AND build_status IN ('unbuilt', 'failed')", ("$revision", profileRevisionId));
                transaction.Commit();
                return ReadBuilds(connection, null, null, null, id).Single();
            }
        });
    }

    /// <summary>
    /// Advances one build along its fixed state machine. Terminal states record the
    /// digest (built), the sanitized reason (failed/rejected) or the evidence hash
    /// (verified), and the revision-level summary column is folded from the
    /// per-host result: <c>built</c> once any host has a verified image,
    /// <c>rejected</c> if a host rejected the contract, <c>failed</c> otherwise.
    /// </summary>
    public ProfileBuildRecord TransitionProfileBuild(string id, int expectedRevision, string toState, string? imageDigest = null, bool verified = false, string? failureSummary = null, string? evidenceHash = null)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.ProfileBuildPrefix) || expectedRevision < 1) throw new OrganizationValidationException("A stable profile build id and current revision are required.");
        if (imageDigest is not null && !IsHash(imageDigest)) throw new OrganizationValidationException("Image digest must be an exact sha256 value.");
        if (evidenceHash is not null && !IsHash(evidenceHash)) throw new OrganizationValidationException("Evidence hash must be an exact sha256 value.");
        var summary = failureSummary is null ? null : ValidateOptionalBoundedText(failureSummary, 512);

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadBuilds(connection, transaction, null, null, id).SingleOrDefault()
                    ?? throw new OrganizationNotFoundException($"Profile build '{id}' does not exist.");
                if (current.Revision != expectedRevision) throw new OrganizationConcurrencyException("The profile build changed; reload and retry with its current revision.");
                if (!ProfileBuildStates.CanTransition(current.State, toState)) throw new OrganizationConcurrencyException($"A profile build cannot move from {current.State} to {toState}.");
                if (toState == ProfileBuildStates.Built && imageDigest is null) throw new OrganizationValidationException("A built image needs its digest.");
                if (toState == ProfileBuildStates.Built)
                {
                    using var claimed = connection.CreateCommand();
                    claimed.Transaction = transaction;
                    claimed.CommandText = "SELECT EXISTS(SELECT 1 FROM profile_build_removals WHERE host_id = $host AND result_tag = $tag)";
                    claimed.Parameters.AddWithValue("$host", current.HostId);
                    claimed.Parameters.AddWithValue("$tag", current.ResultTag);
                    if (Convert.ToInt64(claimed.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
                        throw new OrganizationConcurrencyException("The result tag is claimed for removal; the build cannot become verified until that cleanup is reconciled.");
                }
                if (verified && (toState != ProfileBuildStates.Built || evidenceHash is null)) throw new OrganizationValidationException("Verification requires a built image and its evidence hash.");
                var now = Timestamp();
                var affected = Execute(connection, transaction,
                    """
                    UPDATE profile_builds
                    SET state = $state, image_digest = COALESCE($digest, image_digest), verified = $verified,
                        failure_summary = $summary, evidence_hash = COALESCE($evidence, evidence_hash),
                        started_at = CASE WHEN $state = 'building' AND started_at IS NULL THEN $now ELSE started_at END,
                        finished_at = CASE WHEN $state IN ('built', 'failed', 'rejected', 'removed') THEN $now ELSE finished_at END,
                        revision = revision + 1, updated_at = $now
                    WHERE id = $id AND revision = $revision
                    """,
                    ("$state", toState), ("$digest", imageDigest), ("$verified", verified ? 1 : 0), ("$summary", summary), ("$evidence", evidenceHash),
                    ("$now", now), ("$id", id), ("$revision", expectedRevision));
                if (affected != 1) throw new OrganizationConcurrencyException("The profile build changed before the transition.");
                FoldRevisionBuildStatus(connection, transaction, current.ProfileRevisionId);
                transaction.Commit();
                return ReadBuilds(connection, null, null, null, id).Single();
            }
        });
    }

    /// <summary>
    /// Explicit scoped cleanup of one non-verified build. Only a <c>failed</c> or
    /// <c>rejected</c> build can be removed: a <c>queued</c>/<c>building</c>/
    /// <c>verifying</c> build is still live, a <c>built</c>/verified image is
    /// provisionable, and an <c>uncertain</c> row must be reconciled first so an
    /// image that was actually produced is never orphaned. The transition is
    /// revision-bound and records the acting owner; the requested evidence hash is
    /// preserved. Identity columns and the no-delete guard are unchanged: removal
    /// is a state, never a row deletion.
    /// </summary>
    public ProfileBuildRecord TransitionProfileBuildToRemoved(string id, int expectedRevision, string requestedBy, string? evidenceHash = null)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.ProfileBuildPrefix) || expectedRevision < 1) throw new OrganizationValidationException("A stable profile build id and current revision are required.");
        if (!string.Equals(requestedBy, "owner", StringComparison.Ordinal)) throw new OrganizationValidationException("Only the owner may remove a failed profile build.");
        if (evidenceHash is not null && !IsHash(evidenceHash)) throw new OrganizationValidationException("Evidence hash must be an exact sha256 value.");

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadBuilds(connection, transaction, null, null, id).SingleOrDefault()
                    ?? throw new OrganizationNotFoundException($"Profile build '{id}' does not exist.");
                if (current.Revision != expectedRevision) throw new OrganizationConcurrencyException("The profile build changed; reload and retry with its current revision.");
                if (!ProfileBuildStates.CanTransition(current.State, ProfileBuildStates.Removed))
                    throw new OrganizationConcurrencyException($"A profile build cannot move from {current.State} to {ProfileBuildStates.Removed}; only failed or rejected builds may be removed.");
                var now = Timestamp();
                var affected = Execute(connection, transaction,
                    """
                    UPDATE profile_builds
                    SET state = $state, evidence_hash = COALESCE($evidence, evidence_hash),
                        finished_at = COALESCE(finished_at, $now),
                        revision = revision + 1, updated_at = $now
                    WHERE id = $id AND revision = $revision
                    """,
                    ("$state", ProfileBuildStates.Removed), ("$evidence", evidenceHash), ("$now", now), ("$id", id), ("$revision", expectedRevision));
                if (affected != 1) throw new OrganizationConcurrencyException("The profile build changed before the transition.");
                // A removed build must never leave a claim behind: nothing could
                // release it afterwards and the (host, tag) pair would be wedged.
                Execute(connection, transaction, "DELETE FROM profile_build_removals WHERE profile_build_id = $id", ("$id", id));
                FoldRevisionBuildStatus(connection, transaction, current.ProfileRevisionId);
                transaction.Commit();
                return ReadBuilds(connection, null, null, null, id).Single();
            }
        });
    }

    /// <summary>
    /// Durably claims one failed/rejected build for image removal. The claim and
    /// the guarding checks run in one transaction: the build must still be
    /// failed/rejected, no other non-removed build on the host may share the result
    /// tag, the build's recorded digest must not be in use, and no existing claim
    /// may already hold the <c>(host, tag)</c> pair. The caller performs the remote
    /// effect only after the claim succeeds and then finalizes or fails it. This is
    /// what closes the preflight race: a concurrent build cannot reuse the tag
    /// while the claim exists.
    /// </summary>
    public ProfileBuildRecord ClaimProfileBuildRemoval(string id, int expectedRevision, string requestedBy)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.ProfileBuildPrefix) || expectedRevision < 1) throw new OrganizationValidationException("A stable profile build id and current revision are required.");
        if (!string.Equals(requestedBy, "owner", StringComparison.Ordinal)) throw new OrganizationValidationException("Only the owner may remove a failed profile build.");

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadBuilds(connection, transaction, null, null, id).SingleOrDefault()
                    ?? throw new OrganizationNotFoundException($"Profile build '{id}' does not exist.");
                if (current.Revision != expectedRevision) throw new OrganizationConcurrencyException("The profile build changed; reload and retry with its current revision.");
                if (current.State is not (ProfileBuildStates.Failed or ProfileBuildStates.Rejected))
                    throw new OrganizationConcurrencyException($"A profile build in state {current.State} cannot be claimed for removal; only failed or rejected builds may be removed.");
                if (current.ImageDigest is { } digest)
                {
                    var usage = ImageDigestUsageIn(connection, transaction, digest);
                    if (usage.Count > 0)
                        throw new OrganizationValidationException($"The build's image is still in use ({string.Join(", ", usage)}); it cannot be removed.");
                }
                using (var shared = connection.CreateCommand())
                {
                    shared.Transaction = transaction;
                    shared.CommandText = "SELECT COUNT(*) FROM profile_builds WHERE host_id = $host AND result_tag = $tag AND id <> $id AND state <> 'removed'";
                    shared.Parameters.AddWithValue("$host", current.HostId);
                    shared.Parameters.AddWithValue("$tag", current.ResultTag);
                    shared.Parameters.AddWithValue("$id", id);
                    if (Convert.ToInt64(shared.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                        throw new OrganizationValidationException("Another non-removed build on this host shares the result tag; removing it could detach an image still in use.");
                }

                // A claim held by a different build for the same (host, tag) means a
                // removal is genuinely in flight elsewhere; refuse. A claim held by
                // THIS build is an idempotent re-claim: a previous attempt failed on
                // transport and kept its claim, so a retry must be able to reuse it
                // rather than deadlock forever.
                using (var existingClaim = connection.CreateCommand())
                {
                    existingClaim.Transaction = transaction;
                    existingClaim.CommandText = "SELECT profile_build_id FROM profile_build_removals WHERE host_id = $host AND result_tag = $tag";
                    existingClaim.Parameters.AddWithValue("$host", current.HostId);
                    existingClaim.Parameters.AddWithValue("$tag", current.ResultTag);
                    if (existingClaim.ExecuteScalar() is string owner)
                    {
                        if (!string.Equals(owner, id, StringComparison.Ordinal))
                            throw new OrganizationConcurrencyException("A removal is already claimed for this build's result tag by another build; reconcile it before retrying.");
                        transaction.Commit();
                        return current;
                    }
                }

                var now = Timestamp();
                using (var claim = connection.CreateCommand())
                {
                    claim.Transaction = transaction;
                    claim.CommandText = "INSERT INTO profile_build_removals (profile_build_id, host_id, result_tag, requested_by, created_at, revision) VALUES ($id, $host, $tag, 'owner', $now, 1)";
                    claim.Parameters.AddWithValue("$id", id);
                    claim.Parameters.AddWithValue("$host", current.HostId);
                    claim.Parameters.AddWithValue("$tag", current.ResultTag);
                    claim.Parameters.AddWithValue("$now", now);
                    try { claim.ExecuteNonQuery(); }
                    catch (SqliteException exception) when (IsUniqueConstraint(exception))
                    {
                        throw new OrganizationConcurrencyException("A removal is already claimed for this build's result tag; reconcile it before retrying.");
                    }
                }

                transaction.Commit();
                return ReadBuilds(connection, null, null, null, id).Single();
            }
        });
    }

    /// <summary>
    /// Finalizes a claimed removal after the transport confirmed the effect. The
    /// state change to <c>removed</c> and the claim release happen in one
    /// transaction, so a crash cannot leave a build marked removed while its claim
    /// still blocks the tag, or a released claim while the build stays failed.
    /// </summary>
    public ProfileBuildRecord FinalizeProfileBuildRemoval(string id, int expectedRevision, string requestedBy, string? evidenceHash = null)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.ProfileBuildPrefix) || expectedRevision < 1) throw new OrganizationValidationException("A stable profile build id and current revision are required.");
        if (!string.Equals(requestedBy, "owner", StringComparison.Ordinal)) throw new OrganizationValidationException("Only the owner may remove a failed profile build.");
        if (evidenceHash is not null && !IsHash(evidenceHash)) throw new OrganizationValidationException("Evidence hash must be an exact sha256 value.");

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadBuilds(connection, transaction, null, null, id).SingleOrDefault()
                    ?? throw new OrganizationNotFoundException($"Profile build '{id}' does not exist.");
                if (current.Revision != expectedRevision) throw new OrganizationConcurrencyException("The profile build changed; reload and retry with its current revision.");
                if (!ProfileBuildStates.CanTransition(current.State, ProfileBuildStates.Removed))
                    throw new OrganizationConcurrencyException($"A profile build cannot move from {current.State} to {ProfileBuildStates.Removed}; only failed or rejected builds may be removed.");
                var now = Timestamp();
                var affected = Execute(connection, transaction,
                    """
                    UPDATE profile_builds
                    SET state = $state, evidence_hash = COALESCE($evidence, evidence_hash),
                        finished_at = COALESCE(finished_at, $now),
                        revision = revision + 1, updated_at = $now
                    WHERE id = $id AND revision = $revision
                    """,
                    ("$state", ProfileBuildStates.Removed), ("$evidence", evidenceHash), ("$now", now), ("$id", id), ("$revision", expectedRevision));
                if (affected != 1) throw new OrganizationConcurrencyException("The profile build changed before the transition.");
                Execute(connection, transaction, "DELETE FROM profile_build_removals WHERE profile_build_id = $id", ("$id", id));
                FoldRevisionBuildStatus(connection, transaction, current.ProfileRevisionId);
                transaction.Commit();
                return ReadBuilds(connection, null, null, null, id).Single();
            }
        });
    }

    /// <summary>
    /// Releases a claimed removal without marking the build removed. The build stays
    /// in its terminal failed/rejected state (only its sanitized reason is updated),
    /// so a retry can re-claim it once the refusal is resolved. Returns the build.
    /// </summary>
    public ProfileBuildRecord FailProfileBuildRemoval(string id, int expectedRevision, string failureSummary)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.ProfileBuildPrefix) || expectedRevision < 1) throw new OrganizationValidationException("A stable profile build id and current revision are required.");
        var summary = ValidateOptionalBoundedText(failureSummary, 512);
        var result = TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadBuilds(connection, transaction, null, null, id).SingleOrDefault()
                    ?? throw new OrganizationNotFoundException($"Profile build '{id}' does not exist.");
                if (current.Revision != expectedRevision) throw new OrganizationConcurrencyException("The profile build changed; reload and retry with its current revision.");
                if (current.State is not (ProfileBuildStates.Failed or ProfileBuildStates.Rejected))
                    throw new OrganizationConcurrencyException($"A profile build in state {current.State} cannot have its removal released.");
                Execute(connection, transaction,
                    "UPDATE profile_builds SET failure_summary = $summary, revision = revision + 1, updated_at = $now WHERE id = $id AND revision = $revision",
                    ("$summary", summary), ("$now", Timestamp()), ("$id", id), ("$revision", expectedRevision));
                Execute(connection, transaction, "DELETE FROM profile_build_removals WHERE profile_build_id = $id", ("$id", id));
                transaction.Commit();
                return ReadBuilds(connection, null, null, null, id).Single();
            }
        });
        return result;
    }

    /// <summary>True when the <c>(host, tag)</c> pair is claimed for removal, so a build may not reuse it.</summary>
    public bool IsResultTagClaimedForRemoval(string hostId, string resultTag)
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM profile_build_removals WHERE host_id = $host AND result_tag = $tag)";
                command.Parameters.AddWithValue("$host", hostId);
                command.Parameters.AddWithValue("$tag", resultTag);
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
            }
        });
    }

    private static IReadOnlyList<string> ImageDigestUsageIn(SqliteConnection connection, SqliteTransaction transaction, string imageDigest)
    {
        var reasons = new List<string>();
        static bool Exists(SqliteConnection c, SqliteTransaction tx, string sql, string digest)
        {
            using var command = c.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$digest", digest);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        if (Exists(connection, transaction, "SELECT EXISTS(SELECT 1 FROM worker_enrollments WHERE expected_image_digest = $digest)", imageDigest)) reasons.Add("enrollment");
        if (Exists(connection, transaction, "SELECT EXISTS(SELECT 1 FROM managed_enrollment_resources WHERE approved_image_digest = $digest)", imageDigest)) reasons.Add("managed-enrollment");
        if (Exists(connection, transaction, "SELECT EXISTS(SELECT 1 FROM employee_rebuilds WHERE (state IN ('Intent', 'Holding', 'Replacing', 'Verifying', 'Uncertain') OR state = 'Applied') AND (from_image_digest = $digest OR to_image_digest = $digest))", imageDigest)) reasons.Add("employee-rebuild");
        return reasons;
    }

    /// <summary>
    /// Builds whose image may be explicitly removed: <c>failed</c> or
    /// <c>rejected</c> rows that have not already been <c>removed</c>. A live,
    /// verified or uncertain build is never listed, so cleanup cannot select an
    /// image that is still in use or whose result is unknown.
    /// </summary>
    public IReadOnlyList<ProfileBuildRecord> ListRemovableProfileBuilds(string? profileRevisionId = null, string? hostId = null)
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadBuilds(connection, null, profileRevisionId, hostId, null)
                    .Where(b => b.State is ProfileBuildStates.Failed or ProfileBuildStates.Rejected).ToArray();
            }
        });
    }

    /// <summary>
    /// The reason categories for which an image digest is still in use, or empty
    /// when it is safe to remove. A digest is in use when an enrollment expects it,
    /// a frozen managed approval pins it, or an active or applied employee rebuild
    /// names it as either the source or the target. Only category names are
    /// returned; no digest, worker, employee or secret is included, so a caller can
    /// refuse cleanup without leaking which record holds it.
    /// </summary>
    public IReadOnlyList<string> ImageDigestUsage(string imageDigest)
    {
        if (!IsHash(imageDigest)) throw new OrganizationValidationException("Image digest must be an exact sha256 value.");
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                var reasons = new List<string>();
                if (Exists(connection, "SELECT EXISTS(SELECT 1 FROM worker_enrollments WHERE expected_image_digest = $digest)", imageDigest)) reasons.Add("enrollment");
                if (Exists(connection, "SELECT EXISTS(SELECT 1 FROM managed_enrollment_resources WHERE approved_image_digest = $digest)", imageDigest)) reasons.Add("managed-enrollment");
                if (Exists(connection, "SELECT EXISTS(SELECT 1 FROM employee_rebuilds WHERE (state IN ('Intent', 'Holding', 'Replacing', 'Verifying', 'Uncertain') OR state = 'Applied') AND (from_image_digest = $digest OR to_image_digest = $digest))", imageDigest)) reasons.Add("employee-rebuild");
                return (IReadOnlyList<string>)reasons;
            }
        });
    }

    /// <summary>
    /// True when any build other than <paramref name="profileBuildId"/> that has not
    /// already been <c>removed</c> shares the same <c>result_tag</c> on the same
    /// host. The tag is derived from the revision and context, so a failed build and
    /// a later verified build of the same revision/context share one tag; removing
    /// that tag from the failed row would detach the image the verified row still
    /// names. Cleanup must refuse in that case rather than guess.
    /// </summary>
    public bool IsResultTagShared(string profileBuildId, string hostId, string resultTag)
    {
        if (!IsBoundedIdentifier(profileBuildId, OrganizationIds.ProfileBuildPrefix) || string.IsNullOrEmpty(resultTag))
            return true;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM profile_builds WHERE host_id = $host AND result_tag = $tag AND id <> $id AND state <> 'removed')";
                command.Parameters.AddWithValue("$host", hostId);
                command.Parameters.AddWithValue("$tag", resultTag);
                command.Parameters.AddWithValue("$id", profileBuildId);
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
            }
        });
    }

    private static bool Exists(SqliteConnection connection, string sql, string digest)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$digest", digest);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// A controller that stops while a build is <c>building</c> or <c>verifying</c>
    /// cannot know whether the host finished the work. On startup those rows are
    /// moved to <c>uncertain</c> so the next run reconciles them by tag instead of
    /// leaving them live forever or rebuilding blindly. Returns the ids moved.
    /// </summary>
    public IReadOnlyList<string> MarkInterruptedProfileBuildsUncertain()
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var interrupted = ReadBuilds(connection, transaction, null, null, null)
                    .Where(b => b.State is ProfileBuildStates.Building or ProfileBuildStates.Verifying).ToArray();
                foreach (var build in interrupted)
                {
                    Execute(connection, transaction,
                        "UPDATE profile_builds SET state = 'uncertain', failure_summary = $summary, revision = revision + 1, updated_at = $now WHERE id = $id AND revision = $revision",
                        ("$summary", $"The controller stopped while the build was {build.State}; the result is unknown until reconciled."), ("$now", Timestamp()), ("$id", build.Id), ("$revision", build.Revision));
                }
                transaction.Commit();
                return interrupted.Select(b => b.Id).ToArray();
            }
        });
    }

    /// <summary>
    /// The digests a host may run: the configured base plus every verified build
    /// on that host. Provisioning consults this instead of the single option.
    /// </summary>
    public IReadOnlyList<string> ListApprovedImageDigests(string hostId, string configuredBaseDigest)
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                var digests = new List<string>();
                if (IsHash(configuredBaseDigest)) digests.Add(configuredBaseDigest);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT image_digest FROM profile_builds WHERE host_id = $host AND state = 'built' AND verified = 1 AND image_digest IS NOT NULL ORDER BY finished_at";
                command.Parameters.AddWithValue("$host", hostId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) digests.Add(reader.GetString(0));
                return digests;
            }
        });
    }

    /// <summary>The verified build for a revision on a host, or null.</summary>
    public ProfileBuildRecord? GetVerifiedProfileBuild(string profileRevisionId, string hostId)
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadBuilds(connection, null, profileRevisionId, hostId, null).SingleOrDefault(b => b.State == ProfileBuildStates.Built && b.Verified);
            }
        });
    }

    private static void FoldRevisionBuildStatus(SqliteConnection connection, SqliteTransaction transaction, string revisionId)
    {
        Execute(connection, transaction,
            """
            UPDATE container_profile_revisions
            SET build_status = (
                    SELECT CASE
                        WHEN EXISTS (SELECT 1 FROM profile_builds b WHERE b.profile_revision_id = $revision AND b.state = 'built' AND b.verified = 1) THEN 'built'
                        WHEN EXISTS (SELECT 1 FROM profile_builds b WHERE b.profile_revision_id = $revision AND b.state IN ('queued', 'building', 'verifying', 'uncertain')) THEN 'building'
                        WHEN EXISTS (SELECT 1 FROM profile_builds b WHERE b.profile_revision_id = $revision AND b.state = 'rejected') THEN 'rejected'
                        WHEN EXISTS (SELECT 1 FROM profile_builds b WHERE b.profile_revision_id = $revision AND b.state = 'failed') THEN 'failed'
                        ELSE 'unbuilt' END),
                built_image_digest = (SELECT b.image_digest FROM profile_builds b WHERE b.profile_revision_id = $revision AND b.state = 'built' AND b.verified = 1 ORDER BY b.finished_at LIMIT 1),
                verified = CASE WHEN EXISTS (SELECT 1 FROM profile_builds b WHERE b.profile_revision_id = $revision AND b.state = 'built' AND b.verified = 1) THEN 1 ELSE 0 END
            WHERE id = $revision
            """,
            ("$revision", revisionId));
    }

    private static IReadOnlyList<ProfileBuildRecord> ReadBuilds(SqliteConnection connection, SqliteTransaction? transaction, string? revisionId, string? hostId, string? id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var where = new List<string>();
        if (revisionId is not null) { where.Add("profile_revision_id = $revision"); command.Parameters.AddWithValue("$revision", revisionId); }
        if (hostId is not null) { where.Add("host_id = $host"); command.Parameters.AddWithValue("$host", hostId); }
        if (id is not null) { where.Add("id = $id"); command.Parameters.AddWithValue("$id", id); }
        command.CommandText =
            "SELECT id, profile_revision_id, host_id, base_image_digest, platform, context_hash, result_tag, state, image_digest, verified, failure_summary, evidence_hash, requested_by, revision, started_at, finished_at, created_at, updated_at FROM profile_builds"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty) + " ORDER BY created_at DESC, id";
        var result = new List<ProfileBuildRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ProfileBuildRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9) == 1, reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetString(12), reader.GetInt32(13),
                reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(17), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return result;
    }
}
