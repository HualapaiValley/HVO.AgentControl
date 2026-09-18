using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HVO.AgentControl.Organization;

/// <summary>
/// Container profile persistence (schema v8). A profile is a mutable label over
/// an append-only chain of immutable revisions; content columns of a revision are
/// never updated and a change always creates the next revision number.
/// </summary>
public sealed partial class OrganizationStore
{
    public const int MaximumProfileDescriptionLength = 1024;
    public const int MaximumProfileSlugLength = 63;

    private static string[] ContainerProfileSchemaV8Statements =>
    [
        """
        CREATE TABLE container_profiles (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
            slug TEXT NOT NULL CHECK (length(slug) BETWEEN 1 AND 63),
            display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 128),
            description TEXT NOT NULL CHECK (length(description) <= 1024),
            status TEXT NOT NULL CHECK (status IN ('active', 'retired')),
            idempotency_key TEXT UNIQUE CHECK (idempotency_key IS NULL OR length(idempotency_key) BETWEEN 1 AND 128),
            current_revision_number INTEGER NOT NULL CHECK (current_revision_number >= 1),
            revision INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE (organization_id, slug)
        )
        """,
        """
        CREATE TABLE container_profile_revisions (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL REFERENCES container_profiles(id) ON DELETE RESTRICT,
            revision_number INTEGER NOT NULL CHECK (revision_number >= 1),
            base_image_reference TEXT NOT NULL CHECK (base_image_reference = 'agentcontrol-worker-base'),
            definition_json TEXT NOT NULL CHECK (length(definition_json) BETWEEN 2 AND 65536),
            dockerfile_fragment TEXT CHECK (dockerfile_fragment IS NULL OR length(dockerfile_fragment) BETWEEN 1 AND 16384),
            content_hash TEXT NOT NULL CHECK (length(content_hash) = 71 AND substr(content_hash, 1, 7) = 'sha256:'),
            build_status TEXT NOT NULL CHECK (build_status IN ('unbuilt', 'building', 'built', 'failed', 'rejected')),
            built_image_digest TEXT CHECK (built_image_digest IS NULL OR (length(built_image_digest) = 71 AND substr(built_image_digest, 1, 7) = 'sha256:')),
            verified INTEGER NOT NULL CHECK (verified IN (0, 1)),
            created_by TEXT NOT NULL CHECK (created_by IN ('seed', 'owner')),
            created_at TEXT NOT NULL,
            UNIQUE (profile_id, revision_number),
            UNIQUE (profile_id, content_hash)
        )
        """,
    ];

    /// <summary>
    /// Database-boundary immutability. Identity and content columns of a revision
    /// can never change and revisions are never deleted; only the build lifecycle
    /// columns (#259) may be updated. Profiles cannot be deleted either, so a
    /// retired profile keeps its history. Triggers are part of the exact schema
    /// signature, so removing one fails the store closed.
    /// </summary>
    private static string[] ContainerProfileImmutabilityV8Statements =>
    [
        """
        CREATE TRIGGER container_profile_revisions_immutable
            BEFORE UPDATE OF id, profile_id, revision_number, base_image_reference, definition_json, dockerfile_fragment, content_hash, created_by, created_at
            ON container_profile_revisions
        BEGIN
            SELECT RAISE(ABORT, 'container profile revisions are immutable');
        END
        """,
        """
        CREATE TRIGGER container_profile_revisions_no_delete
            BEFORE DELETE ON container_profile_revisions
        BEGIN
            SELECT RAISE(ABORT, 'container profile revisions are never deleted');
        END
        """,
        """
        CREATE TRIGGER container_profiles_no_delete
            BEFORE DELETE ON container_profiles
        BEGIN
            SELECT RAISE(ABORT, 'container profiles are retired, never deleted');
        END
        """,
    ];

    public IReadOnlyList<ContainerProfileSummary> ListContainerProfiles()
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadProfiles(connection, null, null);
            }
        });
    }

    public ContainerProfileDetail? GetContainerProfile(string id)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.ContainerProfilePrefix)) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                var profile = ReadProfiles(connection, null, id).SingleOrDefault();
                return profile is null ? null : new ContainerProfileDetail(profile, ReadRevisions(connection, null, id));
            }
        });
    }

    /// <summary>Returns the revision chain newest first, or null when no profile carries the id.</summary>
    public IReadOnlyList<ContainerProfileRevisionSummary>? ListContainerProfileRevisions(string id)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.ContainerProfilePrefix)) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadProfiles(connection, null, id).Count == 0 ? null : ReadRevisions(connection, null, id);
            }
        });
    }

    public ContainerProfileSummary CreateContainerProfile(ContainerProfileCreate request, string? headerIdempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        var idempotencyKey = ValidateIdempotencyKey(headerIdempotencyKey, request.IdempotencyKey);
        var slug = ValidateProfileSlug(request.Slug);
        var displayName = ValidateDisplayName(request.DisplayName ?? string.Empty);
        var description = ValidateOptionalBoundedText(request.Description, MaximumProfileDescriptionLength);
        var definition = ContainerProfileDefinition.Parse(request.Definition, request.DockerfileFragment);
        var createHash = HashHireValue(string.Join('\n', slug, displayName, description, definition.ContentHash));

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var existing = ReadProfileByKey(connection, transaction, idempotencyKey);
                if (existing is not null)
                {
                    // The key is bound to the original create payload, which is the
                    // immutable first revision, not whatever revision is current now.
                    var firstRevisionHash = ReadRevisionContentHash(connection, transaction, existing.Id, 1);
                    if (!string.Equals(ProfileCreateHash(existing, firstRevisionHash), createHash, StringComparison.Ordinal))
                        throw new OrganizationConcurrencyException("The idempotency key is already bound to a different container profile payload.");
                    transaction.Commit();
                    return existing;
                }

                var organizationId = ReadSingleOrganization(connection).Id;
                if (ProfileSlugExists(connection, transaction, organizationId, slug))
                    throw new OrganizationConcurrencyException($"A container profile with slug '{slug}' already exists.");
                var id = OrganizationIds.NewContainerProfileId();
                var now = Timestamp();
                InsertProfile(connection, transaction, id, organizationId, slug, displayName, description, idempotencyKey, now);
                InsertRevision(connection, transaction, id, 1, definition, "owner", now);
                transaction.Commit();
                return ReadProfiles(connection, null, id).Single();
            }
        });
    }

    public ContainerProfileRevisionSummary CreateContainerProfileRevision(string profileId, ContainerProfileRevisionCreate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsBoundedIdentifier(profileId, OrganizationIds.ContainerProfilePrefix) || request.ExpectedProfileRevision < 1)
            throw new OrganizationValidationException("A stable container profile id and current profile revision are required.");
        var definition = ContainerProfileDefinition.Parse(request.Definition, request.DockerfileFragment);

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var profile = ReadProfiles(connection, transaction, profileId).SingleOrDefault()
                    ?? throw new OrganizationNotFoundException($"Container profile '{profileId}' does not exist.");
                if (profile.Revision != request.ExpectedProfileRevision)
                    throw new OrganizationConcurrencyException("The container profile changed; reload and retry with its current revision.");
                if (profile.Status != ContainerProfileStatuses.Active)
                    throw new OrganizationConcurrencyException("A retired container profile cannot receive new revisions.");
                if (string.Equals(profile.CurrentContentHash, definition.ContentHash, StringComparison.Ordinal))
                    throw new OrganizationConcurrencyException("The definition is identical to the current revision; no new revision was created.");
                if (RevisionContentExists(connection, transaction, profileId, definition.ContentHash))
                    throw new OrganizationConcurrencyException("An earlier revision of this profile already has identical content; revisions are immutable and never re-created.");

                var next = profile.CurrentRevisionNumber + 1;
                var now = Timestamp();
                var revisionId = InsertRevision(connection, transaction, profileId, next, definition, "owner", now);
                var affected = Execute(connection, transaction,
                    "UPDATE container_profiles SET current_revision_number = $next, revision = revision + 1, updated_at = $now WHERE id = $id AND revision = $revision",
                    ("$next", next), ("$now", now), ("$id", profileId), ("$revision", request.ExpectedProfileRevision));
                if (affected != 1) throw new OrganizationConcurrencyException("The container profile changed before the revision was recorded.");
                transaction.Commit();
                return ReadRevisions(connection, null, profileId).Single(r => r.Id == revisionId);
            }
        });
    }

    public ContainerProfileSummary RetireContainerProfile(string profileId, int expectedRevision)
    {
        if (!IsBoundedIdentifier(profileId, OrganizationIds.ContainerProfilePrefix) || expectedRevision < 1)
            throw new OrganizationValidationException("A stable container profile id and current revision are required.");
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var profile = ReadProfiles(connection, transaction, profileId).SingleOrDefault()
                    ?? throw new OrganizationNotFoundException($"Container profile '{profileId}' does not exist.");
                if (profile.Revision != expectedRevision || profile.Status != ContainerProfileStatuses.Active)
                    throw new OrganizationConcurrencyException("The container profile changed or is already retired.");
                var now = Timestamp();
                var affected = Execute(connection, transaction,
                    "UPDATE container_profiles SET status = 'retired', revision = revision + 1, updated_at = $now WHERE id = $id AND revision = $revision AND status = 'active'",
                    ("$now", now), ("$id", profileId), ("$revision", expectedRevision));
                if (affected != 1) throw new OrganizationConcurrencyException("The container profile changed before retirement.");
                transaction.Commit();
                return ReadProfiles(connection, null, profileId).Single();
            }
        });
    }

    /// <summary>
    /// Seeds <c>generic-employee</c> when no profile carries that slug. Runs on
    /// fresh stores and on the v7→v8 migration; it never overwrites an existing
    /// profile and never touches an existing slug's revisions.
    /// </summary>
    internal static void SeedContainerProfilesV8(SqliteConnection connection, SqliteTransaction transaction)
    {
        string organizationId;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM organizations ORDER BY created_at LIMIT 1";
            organizationId = command.ExecuteScalar() as string
                ?? throw new OrganizationStoreCorruptException("Container profiles require exactly one organization to seed against.");
        }

        if (ProfileSlugExists(connection, transaction, organizationId, ContainerProfileSeed.GenericEmployeeSlug)) return;
        var definition = ContainerProfileDefinition.Parse(ContainerProfileSeed.GenericEmployeeDefinition, null);
        var now = Timestamp();
        var id = OrganizationIds.NewContainerProfileId();
        InsertProfile(connection, transaction, id, organizationId, ContainerProfileSeed.GenericEmployeeSlug, ContainerProfileSeed.GenericEmployeeDisplayName, ContainerProfileSeed.GenericEmployeeDescription, null, now);
        InsertRevision(connection, transaction, id, 1, definition, "seed", now);
    }

    private static void InsertProfile(SqliteConnection connection, SqliteTransaction transaction, string id, string organizationId, string slug, string displayName, string description, string? idempotencyKey, string now) =>
        Execute(connection, transaction,
            """
            INSERT INTO container_profiles (id, organization_id, slug, display_name, description, status, idempotency_key, current_revision_number, revision, created_at, updated_at)
            VALUES ($id, $organization, $slug, $name, $description, 'active', $key, 1, 1, $now, $now)
            """,
            ("$id", id), ("$organization", organizationId), ("$slug", slug), ("$name", displayName),
            ("$description", description), ("$key", idempotencyKey), ("$now", now));

    private static string InsertRevision(SqliteConnection connection, SqliteTransaction transaction, string profileId, int number, ContainerProfileDefinition definition, string createdBy, string now)
    {
        var id = OrganizationIds.NewContainerProfileRevisionId();
        Execute(connection, transaction,
            """
            INSERT INTO container_profile_revisions (id, profile_id, revision_number, base_image_reference, definition_json, dockerfile_fragment, content_hash, build_status, built_image_digest, verified, created_by, created_at)
            VALUES ($id, $profile, $number, $base, $definition, $fragment, $hash, 'unbuilt', NULL, 0, $by, $now)
            """,
            ("$id", id), ("$profile", profileId), ("$number", number), ("$base", ContainerProfileDefinition.BaseImageReference),
            ("$definition", definition.CanonicalJson), ("$fragment", definition.DockerfileFragment), ("$hash", definition.ContentHash),
            ("$by", createdBy), ("$now", now));
        return id;
    }

    private static bool ProfileSlugExists(SqliteConnection connection, SqliteTransaction transaction, string organizationId, string slug)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM container_profiles WHERE organization_id = $organization AND slug = $slug";
        command.Parameters.AddWithValue("$organization", organizationId);
        command.Parameters.AddWithValue("$slug", slug);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool RevisionContentExists(SqliteConnection connection, SqliteTransaction transaction, string profileId, string contentHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM container_profile_revisions WHERE profile_id = $profile AND content_hash = $hash";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$hash", contentHash);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static ContainerProfileSummary? ReadProfileByKey(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = ProfileReadCommand(connection, transaction);
        command.CommandText += " WHERE p.idempotency_key = $key";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader) : null;
    }

    private static IReadOnlyList<ContainerProfileSummary> ReadProfiles(SqliteConnection connection, SqliteTransaction? transaction, string? id)
    {
        using var command = ProfileReadCommand(connection, transaction);
        if (id is not null)
        {
            command.CommandText += " WHERE p.id = $id";
            command.Parameters.AddWithValue("$id", id);
        }
        command.CommandText += " ORDER BY p.status, p.slug, p.id";
        var result = new List<ContainerProfileSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadProfile(reader));
        return result;
    }

    private static SqliteCommand ProfileReadCommand(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT p.id, p.organization_id, p.slug, p.display_name, p.description, p.status,
                   p.current_revision_number, r.id, r.content_hash, r.build_status,
                   p.revision, p.created_at, p.updated_at
            FROM container_profiles p
            JOIN container_profile_revisions r ON r.profile_id = p.id AND r.revision_number = p.current_revision_number
            """;
        return command;
    }

    private static ContainerProfileSummary ReadProfile(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetInt32(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
        reader.GetInt32(10),
        DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static IReadOnlyList<ContainerProfileRevisionSummary> ReadRevisions(SqliteConnection connection, SqliteTransaction? transaction, string profileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, profile_id, revision_number, base_image_reference, definition_json, dockerfile_fragment,
                   content_hash, build_status, built_image_digest, verified, created_by, created_at
            FROM container_profile_revisions WHERE profile_id = $profile ORDER BY revision_number DESC
            """;
        command.Parameters.AddWithValue("$profile", profileId);
        var result = new List<ContainerProfileRevisionSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ContainerProfileRevisionSummary(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9) == 1, reader.GetString(10),
                DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    private static string ProfileCreateHash(ContainerProfileSummary profile, string firstRevisionContentHash) =>
        HashHireValue(string.Join('\n', profile.Slug, profile.DisplayName, profile.Description, firstRevisionContentHash));

    private static string ReadRevisionContentHash(SqliteConnection connection, SqliteTransaction transaction, string profileId, int revisionNumber)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT content_hash FROM container_profile_revisions WHERE profile_id = $profile AND revision_number = $number";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$number", revisionNumber);
        return command.ExecuteScalar() as string
            ?? throw new OrganizationStoreCorruptException($"Container profile '{profileId}' has no revision {revisionNumber}.");
    }

    private static string ValidateProfileSlug(string? value)
    {
        var slug = value?.Trim() ?? string.Empty;
        if (slug.Length is 0 or > MaximumProfileSlugLength
            || !System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new OrganizationValidationException("A slug of 1-63 lowercase letters, digits and interior hyphens is required.");
        return slug;
    }

    private static string ValidateOptionalBoundedText(string? value, int maximumLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length > maximumLength) throw new OrganizationValidationException($"The value must be at most {maximumLength} characters.");
        if (trimmed.Any(char.IsControl)) throw new OrganizationValidationException("The value must not contain control characters.");
        return trimmed;
    }
}
