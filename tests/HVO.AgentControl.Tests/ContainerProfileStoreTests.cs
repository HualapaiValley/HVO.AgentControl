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
        var profile = _store.ListContainerProfiles().Single();
        var first = _store.GetContainerProfile(profile.Id)!.Revisions.Single();

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

        // Immutability is enforced by the schema as well as the store: no UPDATE path exists and a
        // duplicate content hash cannot be inserted for the same profile.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _store.DatabasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO container_profile_revisions SELECT 'prev-copy', profile_id, 3, base_image_reference, definition_json, dockerfile_fragment, content_hash, build_status, built_image_digest, verified, created_by, created_at FROM container_profile_revisions WHERE revision_number = 2";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
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
        foreach (var (table, expected) in new[] { ("employees", 1L), ("runtime_bindings", 1L), ("execution_hosts", 0L), ("worker_enrollments", 0L), ("hire_requests", 0L), ("container_profiles", 2L) })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            Assert.Equal(expected, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-profiles-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
