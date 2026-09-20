using HVO.AgentControl.Organization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Container profiles are mutable labels over an immutable, append-only
/// revision chain. These tests pin the seed, idempotent create, revision
/// immutability, retirement, and that nothing in the slice builds or provisions.
/// </summary>
public sealed class ContainerProfileStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly OrganizationStore _store;

    public ContainerProfileStoreTests()
    {
        _store = new OrganizationStore(Path.Combine(_temp.Path, "control.db"));
        _store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
    }

    public void Dispose() { _store.Dispose(); _temp.Dispose(); }

    [Fact]
    public void FreshStoreSeedsExactlyTheGenericEmployeeProfileUnbuilt()
    {
        var profile = Assert.Single(_store.ListContainerProfiles());
        Assert.Equal(ContainerProfileSeed.GenericEmployeeSlug, profile.Slug);
        Assert.Equal(ContainerProfileSeed.GenericEmployeeDisplayName, profile.DisplayName);
        Assert.Equal(ContainerProfileStatuses.Active, profile.Status);
        Assert.Equal(1, profile.CurrentRevisionNumber);
        Assert.Equal(1, profile.Revision);
        Assert.Equal(ContainerProfileBuildStatuses.Unbuilt, profile.CurrentBuildStatus);
        Assert.StartsWith(OrganizationIds.ContainerProfilePrefix, profile.Id, StringComparison.Ordinal);

        var detail = _store.GetContainerProfile(profile.Id)!;
        var revision = Assert.Single(detail.Revisions);
        Assert.StartsWith(OrganizationIds.ContainerProfileRevisionPrefix, revision.Id, StringComparison.Ordinal);
        Assert.Equal(ContainerProfileDefinition.BaseImageReference, revision.BaseImageReference);
        Assert.Equal(ContainerProfileDefinition.Parse(ContainerProfileSeed.GenericEmployeeDefinition, null).ContentHash, revision.ContentHash);
        Assert.Equal(profile.CurrentRevisionId, revision.Id);
        Assert.Null(revision.BuiltImageDigest);
        Assert.False(revision.Verified);
        Assert.Equal("seed", revision.CreatedBy);
        Assert.Null(revision.DockerfileFragment);
    }

    [Fact]
    public void CreateIsIdempotentValidatedAndSlugUnique()
    {
        var input = new ContainerProfileCreate("prof-key-1", "team-a", "Team A", "  Team A tooling  ", """{"image":"agentcontrol-worker-base","name":"Team A"}""", null);
        var created = _store.CreateContainerProfile(input, null);
        var duplicate = _store.CreateContainerProfile(input, "prof-key-1");
        Assert.Equal(created, duplicate);
        Assert.Equal("Team A tooling", created.Description);
        Assert.Equal(2, _store.ListContainerProfiles().Count);

        Assert.Throws<OrganizationConcurrencyException>(() => _store.CreateContainerProfile(input with { DisplayName = "Team A2" }, "prof-key-1"));
        // Replay stays bound to the original payload (revision 1) even after the profile gains revisions.
        _store.CreateContainerProfileRevision(created.Id, new ContainerProfileRevisionCreate(created.Revision, """{"image":"agentcontrol-worker-base","name":"Team A rev 2"}""", null));
        var replayed = _store.CreateContainerProfile(input, null);
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal(2, replayed.CurrentRevisionNumber);
        Assert.Throws<OrganizationConcurrencyException>(() => _store.CreateContainerProfile(input with { Definition = """{"image":"agentcontrol-worker-base","name":"Team A rev 2"}""" }, "prof-key-1"));
        Assert.Throws<OrganizationConcurrencyException>(() => _store.CreateContainerProfile(input with { IdempotencyKey = "prof-key-2" }, null));
        Assert.Throws<OrganizationValidationException>(() => _store.CreateContainerProfile(input with { IdempotencyKey = "k3", Slug = "Bad Slug" }, null));
        Assert.Throws<OrganizationValidationException>(() => _store.CreateContainerProfile(input with { IdempotencyKey = "k4", Slug = "team-b", Definition = """{"image":"ubuntu"}""" }, null));
        Assert.Throws<OrganizationValidationException>(() => _store.CreateContainerProfile(input with { IdempotencyKey = "k5", Slug = "team-b", DisplayName = "" }, null));
        Assert.Throws<OrganizationValidationException>(() => _store.CreateContainerProfile(input with { IdempotencyKey = null, Slug = "team-b" }, null));
        Assert.Throws<OrganizationValidationException>(() => _store.CreateContainerProfile(input with { IdempotencyKey = "body", Slug = "team-b" }, "header"));
        Assert.Throws<OrganizationValidationException>(() => _store.CreateContainerProfile(input with { IdempotencyKey = "k6", Slug = "team-b", Description = new string('d', OrganizationStore.MaximumProfileDescriptionLength + 1) }, null));
        // Nothing invalid was persisted.
        Assert.Equal(2, _store.ListContainerProfiles().Count);
    }

    [Fact]
    public void RevisionsAreAppendOnlyImmutableRevisionBoundAndContentUnique()
    {
        var profile = _store.ListContainerProfiles().Single(p => p.Slug == ContainerProfileSeed.GenericEmployeeSlug);
        var first = _store.GetContainerProfile(profile.Id)!.Revisions.Single();
        // A second profile gives UPDATE OR REPLACE a slug to collide with.
        _store.CreateContainerProfile(new ContainerProfileCreate("victim-key", "victim", "Victim", null, """{"image":"agentcontrol-worker-base","name":"Victim"}""", null), null);

        var second = _store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(profile.Revision, """{"build":{"dockerfile":"Dockerfile"},"name":"Rev 2"}""", "FROM agentcontrol-worker-base\nRUN true\n"));
        Assert.Equal(2, second.RevisionNumber);
        Assert.Equal("owner", second.CreatedBy);
        Assert.Equal("FROM agentcontrol-worker-base\nRUN true\n", second.DockerfileFragment);
        Assert.Equal(ContainerProfileBuildStatuses.Unbuilt, second.BuildStatus);

        var after = _store.GetContainerProfile(profile.Id)!;
        Assert.Equal(2, after.Profile.CurrentRevisionNumber);
        Assert.Equal(second.Id, after.Profile.CurrentRevisionId);
        Assert.Equal(profile.Revision + 1, after.Profile.Revision);
        Assert.Equal([2, 1], after.Revisions.Select(r => r.RevisionNumber).ToArray());
        Assert.Equal(first, after.Revisions.Single(r => r.RevisionNumber == 1));

        // Stale profile revision, identical content, and earlier-revision content are refused.
        Assert.Throws<OrganizationConcurrencyException>(() => _store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(profile.Revision, """{"image":"agentcontrol-worker-base","name":"Stale"}""", null)));
        Assert.Throws<OrganizationConcurrencyException>(() => _store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(after.Profile.Revision, """{"name":"Rev 2","build":{"dockerfile":"Dockerfile"}}""", "FROM agentcontrol-worker-base\r\nRUN true\r\n")));
        Assert.Throws<OrganizationConcurrencyException>(() => _store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(after.Profile.Revision, ContainerProfileSeed.GenericEmployeeDefinition, null)));
        Assert.Throws<OrganizationValidationException>(() => _store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(after.Profile.Revision, """{"image":"agentcontrol-worker-base","privileged":true}""", null)));
        Assert.Throws<OrganizationValidationException>(() => _store.CreateContainerProfileRevision("prof-", new ContainerProfileRevisionCreate(1, ContainerProfileSeed.GenericEmployeeDefinition, null)));
        Assert.Throws<OrganizationNotFoundException>(() => _store.CreateContainerProfileRevision("prof-doesnotexist", new ContainerProfileRevisionCreate(1, """{"image":"agentcontrol-worker-base","name":"X"}""", null)));
        Assert.Equal(2, _store.GetContainerProfile(profile.Id)!.Revisions.Count);

        // Immutability is enforced at the database boundary, not only by the store API:
        // duplicate content cannot be inserted, identity/content columns cannot be
        // updated, and revisions/profiles cannot be deleted. Only the build lifecycle
        // columns reserved for #259 remain writable.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _store.DatabasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        const string profileSnapshot = "SELECT group_concat(line, '|') FROM (SELECT id || ':' || slug || ':' || display_name || ':' || current_revision_number AS line FROM container_profiles ORDER BY id)";
        const string revisionSnapshot = "SELECT group_concat(line, '|') FROM (SELECT id || ':' || content_hash || ':' || definition_json AS line FROM container_profile_revisions ORDER BY revision_number)";
        var beforeProfiles = Raw(connection, profileSnapshot);
        var beforeRows = Raw(connection, revisionSnapshot);
        // Neither table carries an implicit rowid key for a REPLACE conflict to target.
        foreach (var table in new[] { "container_profiles", "container_profile_revisions" })
            Assert.Contains("WITHOUT ROWID", Raw(connection, $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{table}'"), StringComparison.Ordinal);
        foreach (var sql in new[]
        {
            "INSERT INTO container_profile_revisions SELECT 'prev-copy', profile_id, 3, base_image_reference, definition_json, dockerfile_fragment, content_hash, build_status, built_image_digest, verified, created_by, created_at FROM container_profile_revisions WHERE revision_number = 2",
            "UPDATE container_profile_revisions SET definition_json = '{\"image\":\"agentcontrol-worker-base\",\"privileged\":true}' WHERE revision_number = 1",
            "UPDATE container_profile_revisions SET content_hash = 'sha256:' || substr(content_hash, 8, 63) || 'f' WHERE revision_number = 1",
            "UPDATE container_profile_revisions SET dockerfile_fragment = 'FROM ubuntu' WHERE revision_number = 2",
            "UPDATE container_profile_revisions SET base_image_reference = 'agentcontrol-worker-base' WHERE revision_number = 2",
            "UPDATE container_profile_revisions SET created_by = 'owner' WHERE revision_number = 1",
            "UPDATE container_profile_revisions SET revision_number = 9 WHERE revision_number = 2",
            "DELETE FROM container_profile_revisions WHERE revision_number = 2",
            "DELETE FROM container_profiles",
            // INSERT OR REPLACE resolves the conflict with an implicit delete that skips delete
            // triggers unless recursive_triggers is on; the BEFORE INSERT guard must stop it.
            "INSERT OR REPLACE INTO container_profile_revisions SELECT id, profile_id, revision_number, base_image_reference, '{\"image\":\"agentcontrol-worker-base\",\"privileged\":true}', dockerfile_fragment, 'sha256:' || substr(content_hash, 8, 63) || 'e', build_status, built_image_digest, verified, created_by, created_at FROM container_profile_revisions WHERE revision_number = 1",
            "INSERT OR REPLACE INTO container_profile_revisions SELECT 'prev-replaced', profile_id, revision_number, base_image_reference, definition_json, dockerfile_fragment, content_hash, build_status, built_image_digest, verified, created_by, created_at FROM container_profile_revisions WHERE revision_number = 1",
            "REPLACE INTO container_profile_revisions SELECT 'prev-replaced', profile_id, 1, base_image_reference, '{}', NULL, content_hash, build_status, built_image_digest, verified, created_by, created_at FROM container_profile_revisions WHERE revision_number = 2",
            "INSERT OR REPLACE INTO container_profiles SELECT id, organization_id, slug, 'Replaced', description, status, idempotency_key, 1, revision, created_at, updated_at FROM container_profiles",
            "INSERT OR REPLACE INTO container_profiles SELECT 'prof-replaced', organization_id, slug, display_name, description, status, idempotency_key, current_revision_number, revision, created_at, updated_at FROM container_profiles",
            // Profile identity columns and unique-key updates are guarded, so UPDATE OR REPLACE
            // can never reach an implicit delete of another profile.
            "UPDATE OR REPLACE container_profiles SET idempotency_key = 'other-key'",
            "UPDATE container_profiles SET id = 'prof-renamed'",
            "UPDATE container_profiles SET created_at = '2000-01-01T00:00:00.0000000+00:00'",
            "UPDATE OR REPLACE container_profiles SET slug = 'victim' WHERE slug = 'generic-employee'",
            "UPDATE OR REPLACE container_profiles SET slug = 'generic-employee' WHERE slug = 'victim'",
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            var expected = sql.Contains("REPLACE INTO container_profiles ", StringComparison.Ordinal) ? "never replaced"
                : sql.StartsWith("UPDATE OR REPLACE container_profiles SET slug", StringComparison.Ordinal) ? "never replaced"
                : sql.Contains("container_profiles SET", StringComparison.Ordinal) ? "identity is immutable"
                : sql.Contains("REPLACE", StringComparison.Ordinal) ? "immutable"
                : sql.StartsWith("INSERT", StringComparison.Ordinal) ? "immutable"
                : sql.StartsWith("DELETE", StringComparison.Ordinal) ? "never deleted"
                : "immutable";
            Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        }
        Assert.Equal(beforeRows, Raw(connection, revisionSnapshot));
        Assert.Equal(beforeProfiles, Raw(connection, profileSnapshot));

        // The reserved build lifecycle columns stay writable for #259.
        using (var lifecycle = connection.CreateCommand())
        {
            lifecycle.CommandText = "UPDATE container_profile_revisions SET build_status = 'building' WHERE revision_number = 2";
            Assert.Equal(1, lifecycle.ExecuteNonQuery());
            lifecycle.CommandText = "UPDATE container_profile_revisions SET build_status = 'unbuilt' WHERE revision_number = 2";
            Assert.Equal(1, lifecycle.ExecuteNonQuery());
        }
    }

    private static string Raw(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [Fact]
    public void RetirementIsRevisionBoundTerminalAndBlocksNewRevisions()
    {
        var created = _store.CreateContainerProfile(new ContainerProfileCreate("k", "temp", "Temp", null, """{"image":"agentcontrol-worker-base"}""", null), null);
        Assert.Throws<OrganizationConcurrencyException>(() => _store.RetireContainerProfile(created.Id, created.Revision + 1));
        var retired = _store.RetireContainerProfile(created.Id, created.Revision);
        Assert.Equal(ContainerProfileStatuses.Retired, retired.Status);
        Assert.Equal(created.Revision + 1, retired.Revision);
        Assert.Throws<OrganizationConcurrencyException>(() => _store.RetireContainerProfile(created.Id, retired.Revision));
        Assert.Throws<OrganizationConcurrencyException>(() => _store.CreateContainerProfileRevision(created.Id, new ContainerProfileRevisionCreate(retired.Revision, """{"image":"agentcontrol-worker-base","name":"after retire"}""", null)));
        Assert.Throws<OrganizationNotFoundException>(() => _store.RetireContainerProfile("prof-missing", 1));
        // Retired profiles remain readable with their revisions and sort after active ones.
        Assert.Single(_store.GetContainerProfile(created.Id)!.Revisions);
        Assert.Equal([ContainerProfileStatuses.Active, ContainerProfileStatuses.Retired], _store.ListContainerProfiles().Select(p => p.Status).ToArray());
    }

    [Fact]
    public void RevisionListingIsNewestFirstAndDistinguishesUnknownFromMalformed()
    {
        var profile = _store.ListContainerProfiles().Single();
        _store.CreateContainerProfileRevision(profile.Id, new ContainerProfileRevisionCreate(profile.Revision, """{"image":"agentcontrol-worker-base","name":"Two"}""", null));
        var revisions = _store.ListContainerProfileRevisions(profile.Id)!;
        Assert.Equal([2, 1], revisions.Select(r => r.RevisionNumber).ToArray());
        Assert.Equal(_store.GetContainerProfile(profile.Id)!.Revisions, revisions);
        Assert.Null(_store.ListContainerProfileRevisions("prof-doesnotexist"));
        Assert.Null(_store.ListContainerProfileRevisions("not-a-profile"));
    }

    [Fact]
    public void GetRejectsMalformedIdsWithoutTouchingTheStore()
    {
        Assert.Null(_store.GetContainerProfile("emp-abc"));
        Assert.Null(_store.GetContainerProfile("prof-ABC"));
        Assert.Null(_store.GetContainerProfile("../prof-x"));
        Assert.Null(_store.GetContainerProfile("prof-doesnotexist"));
    }

    [Fact]
    public void ProfileSliceCreatesNoEmployeesBindingsHostsOrEnrollments()
    {
        _store.CreateContainerProfile(new ContainerProfileCreate("k", "extra", "Extra", null, """{"image":"agentcontrol-worker-base"}""", null), null);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _store.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        foreach (var (table, expected) in new[] { ("employees", 1L), ("runtime_bindings", 1L), ("execution_hosts", 1L), ("worker_enrollments", 0L), ("hire_requests", 0L), ("container_profiles", 2L) })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            Assert.Equal(expected, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        // The one host is the reserved controller-local Docker row, seeded
        // structural and unprobed: the profile slice never creates a host.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT COUNT(*) FROM execution_hosts WHERE id = '{ExecutionHosts.LocalDockerId}' AND transport_kind = 'local-docker' AND capability_status = 'unprobed';";
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-profiles-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
