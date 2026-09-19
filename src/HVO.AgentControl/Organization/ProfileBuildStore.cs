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
    /// not the profile's current one, a host that is not ready, and a second live
    /// build for the same revision/host (the partial unique index). A verified
    /// build already present for the pair is returned instead of queuing again.
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
                if (existing.Any(b => b.State is ProfileBuildStates.Queued or ProfileBuildStates.Building or ProfileBuildStates.Verifying or ProfileBuildStates.Uncertain))
                    throw new OrganizationConcurrencyException("A build for this revision on this host is already in progress or uncertain; reconcile it before queuing another.");

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
