using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The authoritative SQLite store: deterministic seed, verified adoption,
/// fail-closed recovery, referential integrity and optimistic concurrency.
/// These tests run without Docker; the controller-only ownership boundary is
/// additionally asserted by the real alternate-UID container suite.
/// </summary>
public sealed class OrganizationStoreTests
{
    [Fact]
    public void FreshStoreSeedsExactlyTheApprovedBaseline()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var identity = store.OpenAndAdopt(
            "AgentControl Development",
            "owner-approved:test",
            adoptionSource: null,
            adoptionSourceDescription: "seed://fresh-organization");

        Assert.True(identity.Created);
        Assert.False(identity.DisplayNameDiffersFromConfiguration);

        var overview = store.GetOverview();
        Assert.Equal("agentcontrol-development", overview.Slug);
        Assert.Equal("AgentControl Development", overview.DisplayName);

        // The seeded organization text is the exact approved contract, not a
        // paraphrase that could drift from what the agent is oriented with.
        Assert.Equal(OrganizationSeed.OrganizationDescription, overview.Description);
        Assert.Equal(OrganizationSeed.OrganizationInstructions, overview.BasicInstructions);

        // Exactly Operations, Development and QA; no Finance.
        Assert.Equal(
            new[] { "development", "operations", "qa" },
            overview.Departments.Select(department => department.Slug).ToArray());
        Assert.DoesNotContain(overview.Departments, department => department.Slug == "finance");

        // Exactly one combined Operations/IT role and one adopted employee in Operations.
        var role = Assert.Single(overview.Roles);
        Assert.Equal("operations-it", role.Slug);
        Assert.Equal(OrganizationSeed.OperationsItRoleDisplayName, role.DisplayName);
        Assert.Equal(OrganizationSeed.OperationsItRoleInstructionProfile, role.InstructionProfile);
        Assert.Equal(OrganizationSeed.OperationsItRolePermissionProfile, role.PermissionProfile);
        Assert.Contains(OrganizationSeed.RoleOrientation, role.StandingInstructions, StringComparison.Ordinal);
        Assert.Equal(1, role.Revision);
        var employee = Assert.Single(overview.Employees);
        Assert.Equal("operations", employee.DepartmentSlug);
        Assert.Equal("operations-it", employee.RoleSlug);
        Assert.Equal(RuntimePlacements.InternalSharedContainer, employee.Placement);

        // The employee's purpose, instructions, rules and restrictions are the
        // exact seeded contract rather than empty placeholders.
        Assert.Equal(OrganizationSeed.AdoptedEmployeePurpose, employee.Purpose);
        Assert.Equal(OrganizationSeed.AdoptedEmployeeInstructions, employee.Instructions);
        Assert.Equal(OrganizationSeed.AdoptedEmployeeRules, employee.Rules);
        Assert.Equal(OrganizationSeed.AdoptedEmployeeRestrictions, employee.Restrictions);

        // No fake Dev/QA employees: those departments are empty.
        Assert.Equal(0, overview.Departments.Single(d => d.Slug == "development").EmployeeCount);
        Assert.Equal(0, overview.Departments.Single(d => d.Slug == "qa").EmployeeCount);
        var operations = overview.Departments.Single(d => d.Slug == "operations");
        Assert.Equal(1, operations.EmployeeCount);

        // Department detail data is authoritative: the Operations department has
        // a persisted orientation fragment and a revision, while the unstaffed
        // seed departments truthfully report no standing instructions.
        Assert.Equal(1, operations.Revision);
        Assert.Contains(OrganizationSeed.DepartmentOrientation, operations.StandingInstructions, StringComparison.Ordinal);
        Assert.Null(overview.Departments.Single(d => d.Slug == "development").StandingInstructions);
        Assert.Null(overview.Departments.Single(d => d.Slug == "qa").StandingInstructions);

        // One adoption audit record carrying the authorization reference.
        var audit = Assert.Single(overview.AdoptionAudit);
        Assert.Equal("owner-approved:test", audit.AuthorizationReference);
        Assert.Equal(employee.Id, audit.EmployeeId);
    }

    [Fact]
    public void DepartmentStandingInstructionsUseAnExactHeadingLine()
    {
        var method = typeof(OrganizationStore).GetMethod(
            "ReadDepartmentStandingInstructions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        Assert.Equal(
            "Department body with a later # Department mention.",
            method.Invoke(null, new object[] { "# Department\r\n\r\nDepartment body with a later # Department mention." }));
        Assert.Equal(
            "Preface containing # Department inline.\n\nBody remains intact.",
            method.Invoke(null, new object[] { "Preface containing # Department inline.\n\nBody remains intact." }));
        Assert.Equal(
            "Authoritative body.",
            method.Invoke(null, new object[] { "Preface\n# Department\n\nAuthoritative body." }));
    }

    [Fact]
    public void ReopeningAnExistingStoreIsIdempotentAndNeverDuplicates()
    {
        using var root = new TempStore();
        string organizationId;
        string employeeId;
        string bindingId;

        using (var first = Open(root))
        {
            var identity = first.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            organizationId = identity.OrganizationId;
            employeeId = identity.EmployeeId;
            bindingId = identity.RuntimeBindingId;
        }

        using var second = Open(root);
        var reopened = second.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.False(reopened.Created);
        Assert.Equal(organizationId, reopened.OrganizationId);
        Assert.Equal(employeeId, reopened.EmployeeId);
        Assert.Equal(bindingId, reopened.RuntimeBindingId);

        var overview = second.GetOverview();
        Assert.Single(overview.Employees);
        Assert.Single(overview.Roles);
        Assert.Equal(3, overview.Departments.Count);
        Assert.Single(overview.AdoptionAudit);
    }

    [Fact]
    public void SchemaVersionsHaveEqualCountsAndExactlyOneChangedObject()
    {
        var type = typeof(OrganizationStore);
        var v1 = (string[])type.GetField("SchemaV1Statements", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var v2 = (string[])type.GetField("SchemaV2Statements", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

        Assert.Equal(9, v1.Length);
        Assert.Equal(v1.Length, v2.Length);
        var changed = v1.Zip(v2).Where(pair => !string.Equals(pair.First, pair.Second, StringComparison.Ordinal)).ToArray();
        var only = Assert.Single(changed);
        Assert.Contains("CREATE TABLE runtime_bindings", only.First, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE runtime_bindings", only.Second, StringComparison.Ordinal);
        Assert.DoesNotContain("credential_set_id", only.First, StringComparison.Ordinal);
        Assert.Contains("credential_set_id", only.Second, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaV6WithAnyDifferentSignatureIsUnsupported()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        ExecuteRaw(root.Path, "ALTER TABLE orientation_assignments ADD COLUMN branch_only_value TEXT;");
        using var reopened = Open(root);
        var exception = Assert.Throws<OrganizationStoreCorruptException>(() =>
            reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));

        Assert.Contains("load-bearing schema", exception.Message, StringComparison.Ordinal);
        Assert.Equal(8, RawScalar(root.Path, "SELECT version FROM schema_version;"));
    }

    [Fact]
    public void ExactSchemaV4MigratesThroughV5ToV6WithVerifiedCreateOnceBackupsAndPreservesRecoveryRows()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        DowngradeToCanonicalV4(root.Path);
        SeedV4RecoveryRows(root.Path);

        using (var migrated = Open(root))
        {
            migrated.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            var obligation = Assert.Single(migrated.ListWorkerRecoveryObligations("wrk-v4"));
            var audit = Assert.Single(migrated.ListWorkerRecoveryAudit("wrk-v4"));
            Assert.Equal("ownership-changed", obligation.Kind);
            Assert.Equal(obligation.Id, audit.ObligationId);
        }

        Assert.Equal(8, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.Contains("session-reconciliation", RawText(root.Path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='worker_recovery_obligations'"), StringComparison.Ordinal);
        Assert.Contains("request-uncertain", RawText(root.Path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='worker_recovery_audit'"), StringComparison.Ordinal);
        var v4Backup = Path.Combine(root.Directory, OrganizationStore.SchemaV4BackupFileName);
        var v4Hash = Path.Combine(root.Directory, OrganizationStore.SchemaV4BackupHashFileName);
        var v5Backup = Path.Combine(root.Directory, OrganizationStore.SchemaV5BackupFileName);
        var v5Hash = Path.Combine(root.Directory, OrganizationStore.SchemaV5BackupHashFileName);
        Assert.True(File.Exists(v4Backup));
        Assert.True(File.Exists(v5Backup));
        AssertNoBackupSidecars(v4Backup);
        AssertNoBackupSidecars(v5Backup);
        Assert.Equal(4, RawScalar(v4Backup, "SELECT version FROM schema_version;"));
        Assert.Equal(5, RawScalar(v5Backup, "SELECT version FROM schema_version;"));
        Assert.Equal(1, RawScalar(v4Backup, "SELECT COUNT(*) FROM worker_recovery_obligations;"));
        Assert.Equal(1, RawScalar(v5Backup, "SELECT COUNT(*) FROM worker_recovery_audit;"));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(v4Backup))).ToLowerInvariant(), File.ReadAllText(v4Hash).Trim());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(v5Backup))).ToLowerInvariant(), File.ReadAllText(v5Hash).Trim());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(v4Backup));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(v4Hash));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(v5Backup));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(v5Hash));
        }

        var retainedV4 = File.ReadAllBytes(v4Backup);
        var retainedV5 = File.ReadAllBytes(v5Backup);
        using var restarted = Open(root);
        restarted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.Equal(retainedV4, File.ReadAllBytes(v4Backup));
        Assert.Equal(retainedV5, File.ReadAllBytes(v5Backup));
    }

    [Fact]
    public void ExactSchemaV5MigratesToV6WithVerifiedCreateOnceBackup()
    {
        using var root = new TempStore();
        using (var store = Open(root))
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        DowngradeToCanonicalV5(root.Path);

        using (var migrated = Open(root))
            migrated.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

        Assert.Equal(8, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.Contains("request-uncertain", RawText(root.Path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='worker_recovery_audit'"), StringComparison.Ordinal);
        var backup = Path.Combine(root.Directory, OrganizationStore.SchemaV5BackupFileName);
        var hash = Path.Combine(root.Directory, OrganizationStore.SchemaV5BackupHashFileName);
        Assert.True(File.Exists(backup));
        AssertNoBackupSidecars(backup);
        Assert.Equal(5, RawScalar(backup, "SELECT version FROM schema_version;"));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant(), File.ReadAllText(hash).Trim());
        var retained = File.ReadAllBytes(backup);
        using var restarted = Open(root);
        restarted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.Equal(retained, File.ReadAllBytes(backup));
    }

    [Fact]
    public void UnknownSchemaV5ShapeFailsBeforeBackupOrMigration()
    {
        using var root = new TempStore();
        using (var store = Open(root))
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        DowngradeToCanonicalV5(root.Path);
        ExecuteRaw(root.Path, "ALTER TABLE worker_recovery_audit ADD COLUMN unknown_v5_value TEXT;");

        using var reopened = Open(root);
        Assert.Throws<OrganizationStoreCorruptException>(() => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Equal(5, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.False(File.Exists(Path.Combine(root.Directory, OrganizationStore.SchemaV5BackupFileName)));
    }

    [Fact]
    public void UnknownSchemaV4ShapeFailsBeforeBackupOrMigration()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }
        DowngradeToCanonicalV4(root.Path);
        ExecuteRaw(root.Path, "ALTER TABLE worker_recovery_obligations ADD COLUMN unknown_v4_value TEXT;");

        using var reopened = Open(root);
        Assert.Throws<OrganizationStoreCorruptException>(() => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Equal(4, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.False(File.Exists(Path.Combine(root.Directory, OrganizationStore.SchemaV4BackupFileName)));
    }

    [Fact]
    public void BuildExpectedSchemaRejectsDuplicateObjectKeys()
    {
        var method = typeof(OrganizationStore).GetMethod(
            "BuildExpectedSchema",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method.Invoke(null, new object[] { new[] { "CREATE TABLE duplicate (id TEXT)", "CREATE TABLE duplicate (id TEXT)" } }));
        Assert.IsType<InvalidOperationException>(invocation.InnerException);
        Assert.Contains("duplicate", invocation.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaV1MigratesTransactionallyWithVerifiedCreateOnceBackupAndPreservesData()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            store.RecordSession("ses_preserved", "Preserved");
        }

        DowngradeToCanonicalV1(root.Path);
        var before = SnapshotCoreData(root.Path);
        using (var migrated = Open(root))
        {
            var identity = migrated.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            Assert.Equal("ses_preserved", identity.SessionId);
            Assert.Equal(before, SnapshotCoreData(root.Path));
        }

        Assert.Equal(8, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM pragma_table_info('runtime_bindings') WHERE name = 'credential_set_id';"));
        var v1Backup = Path.Combine(root.Directory, OrganizationStore.SchemaV1BackupFileName);
        var v1Hash = Path.Combine(root.Directory, OrganizationStore.SchemaV1BackupHashFileName);
        Assert.True(File.Exists(v1Backup));
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(v1Backup))).ToLowerInvariant(),
            File.ReadAllText(v1Hash).Trim());
        Assert.Equal(1, RawScalar(v1Backup, "SELECT version FROM schema_version;"));

        var backup = Path.Combine(root.Directory, OrganizationStore.SchemaV2BackupFileName);
        var hash = Path.Combine(root.Directory, OrganizationStore.SchemaV2BackupHashFileName);
        Assert.True(File.Exists(backup));
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant(),
            File.ReadAllText(hash).Trim());
        Assert.Equal(2, RawScalar(backup, "SELECT version FROM schema_version;"));

        var backupBytes = File.ReadAllBytes(backup);
        using var restarted = Open(root);
        restarted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
    }

    /// <summary>
    /// A canonical v1 database migrates through v2 to v3 in one process with
    /// source-bound backups at both boundaries, preserving identity and history.
    /// </summary>
    [Fact]
    public void ChainedV1ToV3MigrationPreservesDataAndRetainsBothSourceBoundBackups()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            store.RecordSession("ses_chain", "Chained");
        }

        DowngradeToCanonicalV1(root.Path);
        Assert.Equal(1, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        var before = SnapshotCoreData(root.Path);

        using (var migrated = Open(root))
        {
            var identity = migrated.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            Assert.Equal("ses_chain", identity.SessionId);
            Assert.Equal("Chained", identity.SessionTitle);
        }

        Assert.Equal(8, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.Equal(before, SnapshotCoreData(root.Path));
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM pragma_table_info('runtime_bindings') WHERE name = 'credential_set_id';"));
        Assert.Equal(4, RawScalar(root.Path, "SELECT COUNT(*) FROM orientation_fragments WHERE active = 1;"));
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM permission_policies WHERE active = 1;"));

        // Both migration boundaries retained verified, source-bound evidence.
        foreach (var (backupName, hashName, expectedVersion) in new[]
        {
            (OrganizationStore.SchemaV1BackupFileName, OrganizationStore.SchemaV1BackupHashFileName, 1),
            (OrganizationStore.SchemaV2BackupFileName, OrganizationStore.SchemaV2BackupHashFileName, 2),
        })
        {
            var backup = Path.Combine(root.Directory, backupName);
            var hash = Path.Combine(root.Directory, hashName);
            Assert.True(File.Exists(backup), $"{backupName} should be retained");

            // The store must not have left WAL/SHM sidecars beside the evidence.
            AssertNoBackupSidecars(backup);
            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant(),
                File.ReadAllText(hash).Trim());
            Assert.Equal(expectedVersion, RawScalar(backup, "SELECT version FROM schema_version;"));
        }
    }

    [Fact]
    public void FaultAfterBackupBeforeMigrationLeavesImmutableBackupAcrossTwoRestarts()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        DowngradeToCanonicalV1(root.Path);
        using (var faulted = Open(root))
        {
            faulted.AfterMigrationBackup = () => throw new InvalidOperationException("simulated fault after backup");
            Assert.Throws<InvalidOperationException>(() =>
                faulted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        }

        var backup = Path.Combine(root.Directory, OrganizationStore.SchemaV1BackupFileName);
        var hash = Path.Combine(root.Directory, OrganizationStore.SchemaV1BackupHashFileName);
        var retained = File.ReadAllBytes(backup);
        var retainedHash = File.ReadAllBytes(hash);
        AssertNoBackupSidecars(backup);

        for (var restart = 0; restart < 2; restart++)
        {
            using var store = Open(root);
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            Assert.Equal(retained, File.ReadAllBytes(backup));
            Assert.Equal(retainedHash, File.ReadAllBytes(hash));
            AssertNoBackupSidecars(backup);
        }
    }

    [Fact]
    public void DifferentValidSchemaV1BackupIsRejectedAndSourceRemainsUnchanged()
    {
        using var source = new TempStore();
        using (var store = Open(source))
        {
            store.OpenAndAdopt("Source Organization", "owner-approved:test", null, "seed://fresh");
        }
        DowngradeToCanonicalV1(source.Path);
        var sourceBefore = SnapshotCoreData(source.Path);

        using var other = new TempStore();
        using (var store = Open(other))
        {
            store.OpenAndAdopt("Different Organization", "owner-approved:test", null, "seed://fresh");
        }
        DowngradeToCanonicalV1(other.Path);
        var backup = Path.Combine(source.Directory, OrganizationStore.SchemaV1BackupFileName);
        CopySqliteDatabase(other.Path, backup);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(source.Directory, OrganizationStore.SchemaV1BackupHashFileName), hash + "\n");

        using var reopened = Open(source);
        var exception = Assert.Throws<OrganizationStoreCorruptException>(() =>
            reopened.OpenAndAdopt("Source Organization", "owner-approved:test", null, "seed://fresh"));
        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, RawScalar(source.Path, "SELECT version FROM schema_version;"));
        Assert.Equal(sourceBefore, SnapshotCoreData(source.Path));
    }

    [Fact]
    public void SchemaV1WriteAfterFailedMigrationInvalidatesRetainedBackup()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }
        DowngradeToCanonicalV1(root.Path);
        using (var faulted = Open(root))
        {
            faulted.BeforeMigrationCommit = () => throw new InvalidOperationException("simulated migration crash");
            Assert.Throws<InvalidOperationException>(() =>
                faulted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        }

        ExecuteRaw(root.Path, "UPDATE organizations SET display_name = 'Changed after failed migration', revision = revision + 1;");
        var sourceBefore = SnapshotCoreData(root.Path);
        using var retry = Open(root);
        var exception = Assert.Throws<OrganizationStoreCorruptException>(() =>
            retry.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.Equal(sourceBefore, SnapshotCoreData(root.Path));
    }

    [Fact]
    public void MigrationFaultRollsBackAndRestartCompletesFromUnchangedV1()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        DowngradeToCanonicalV1(root.Path);
        using (var faulted = Open(root))
        {
            faulted.BeforeMigrationCommit = () => throw new InvalidOperationException("simulated migration crash");
            Assert.Throws<InvalidOperationException>(() =>
                faulted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        }

        Assert.Equal(1, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.Equal(0, RawScalar(root.Path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'orientation_assignments';"));
        using var retry = Open(root);
        retry.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.Equal(8, RawScalar(root.Path, "SELECT version FROM schema_version;"));
    }

    [Fact]
    public void NonCanonicalV1FailsClosedBeforeBackupOrMigration()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        DowngradeToCanonicalV1(root.Path);
        ExecuteRaw(root.Path, "ALTER TABLE organizations ADD COLUMN partial TEXT;");
        using var reopened = Open(root);
        Assert.Throws<OrganizationStoreCorruptException>(() =>
            reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.False(File.Exists(Path.Combine(root.Directory, OrganizationStore.SchemaV1BackupFileName)));
        Assert.Equal(1, RawScalar(root.Path, "SELECT version FROM schema_version;"));
    }

    [Fact]
    public void ProviderConfigurationRecordsOnlySanitizedConfiguredMetadataIdempotently()
    {
        using var root = new TempStore();
        using var store = Open(root);
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        var revision = RawScalar(root.Path, "SELECT revision FROM runtime_bindings;");

        store.RecordProviderConfiguration(
            "agentcontrol-system-phase1",
            "opencode-1.18.30-openai-compatible-v1",
            "agentcontrol-control-phase1-v1",
            "configured",
            "cliproxy-phase1-2026-09-14-v1",
            "gpt-6-astra",
            "cliproxy",
            "gpt-6-astra",
            "medium");
        Assert.Equal("gpt-6-astra", RawScalarString(root.Path, "SELECT policy_lane_id FROM runtime_bindings;"));
        Assert.Equal("medium", RawScalarString(root.Path, "SELECT configured_variant FROM runtime_bindings;"));
        Assert.Equal("agentcontrol-control-phase1-v1", RawScalarString(root.Path, "SELECT provider_profile_id FROM runtime_bindings;"));
        Assert.Equal(revision + 1, RawScalar(root.Path, "SELECT revision FROM runtime_bindings;"));

        store.RecordProviderConfiguration(
            "agentcontrol-system-phase1",
            "opencode-1.18.30-openai-compatible-v1",
            "agentcontrol-control-phase1-v1",
            "configured",
            "cliproxy-phase1-2026-09-14-v1",
            "gpt-6-astra",
            "cliproxy",
            "gpt-6-astra",
            "medium");
        Assert.Equal(revision + 1, RawScalar(root.Path, "SELECT revision FROM runtime_bindings;"));
        Assert.Equal(0, RawScalar(root.Path, "SELECT COUNT(*) FROM pragma_table_info('runtime_bindings') WHERE name LIKE '%endpoint%' OR name LIKE '%key%' OR name LIKE '%secret%' OR name LIKE '%fingerprint%';"));
    }

    [Fact]
    public void ObservedNativeModelIsSeparateNullableAndIdempotent()
    {
        using var root = new TempStore();
        using var store = Open(root);
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordProviderConfiguration(
            "agentcontrol-system-phase1",
            "opencode-1.18.30-openai-compatible-v1",
            "agentcontrol-control-phase1-v1",
            "configured",
            "cliproxy-phase1-2026-09-14-v1",
            "default",
            "cliproxy",
            "default",
            "medium");
        var revision = RawScalar(root.Path, "SELECT revision FROM runtime_bindings;");
        var firstObservedAt = DateTimeOffset.Parse("2026-09-14T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        store.RecordObservedModel("native-provider", "served-model", null, firstObservedAt);
        Assert.Equal("cliproxy", RawScalarString(root.Path, "SELECT configured_provider_id FROM runtime_bindings;"));
        Assert.Equal("default", RawScalarString(root.Path, "SELECT configured_model_id FROM runtime_bindings;"));
        Assert.Equal("medium", RawScalarString(root.Path, "SELECT configured_variant FROM runtime_bindings;"));
        Assert.Equal("native-provider", RawScalarString(root.Path, "SELECT observed_provider_id FROM runtime_bindings;"));
        Assert.Equal("served-model", RawScalarString(root.Path, "SELECT observed_model_id FROM runtime_bindings;"));
        Assert.True(RawIsNull(root.Path, "SELECT observed_variant FROM runtime_bindings;"));
        Assert.Equal(firstObservedAt.ToString("O"), RawScalarString(root.Path, "SELECT observed_at FROM runtime_bindings;"));
        Assert.Equal(revision + 1, RawScalar(root.Path, "SELECT revision FROM runtime_bindings;"));

        store.RecordObservedModel("native-provider", "served-model", null, firstObservedAt.AddMinutes(1));
        Assert.Equal(revision + 1, RawScalar(root.Path, "SELECT revision FROM runtime_bindings;"));
        Assert.Equal(firstObservedAt.ToString("O"), RawScalarString(root.Path, "SELECT observed_at FROM runtime_bindings;"));

        store.RecordObservedModel("native-provider", "served-model", "high", firstObservedAt.AddMinutes(2));
        Assert.Equal("high", RawScalarString(root.Path, "SELECT observed_variant FROM runtime_bindings;"));
        Assert.Equal(revision + 2, RawScalar(root.Path, "SELECT revision FROM runtime_bindings;"));
    }

    [Fact]
    public void AdoptionPreservesPersistedIdentityAndReportsTheNameDifference()
    {
        using var root = new TempStore();
        var source = RuntimeStateStore.CreateNew("Contoso Development", () => "org-legacy-0042");
        source.SessionId = "ses_existing";
        source.SessionTitle = "Contoso Development AgentControl";
        source.TmuxOwnerToken = "token-existing";

        using var store = Open(root);
        var identity = store.OpenAndAdopt(
            "AgentControl Development",
            "owner-approved:test",
            source,
            "/control-data/runtime.json",
            "byte-exact-adoption-source"u8.ToArray());

        // The existing stable id, session, title and tmux token are preserved exactly.
        Assert.Equal("org-legacy-0042", identity.OrganizationId);
        Assert.Equal("ses_existing", identity.SessionId);
        Assert.Equal("Contoso Development AgentControl", identity.SessionTitle);
        Assert.Equal("token-existing", identity.TmuxOwnerToken);
        Assert.True(identity.DisplayNameDiffersFromConfiguration);

        // The persisted name wins over configuration; it is reported, not renamed.
        Assert.Equal("Contoso Development", identity.OrganizationDisplayName);
        var overview = store.GetOverview();
        Assert.Equal("Contoso Development", overview.DisplayName);
        var employee = Assert.Single(overview.Employees);
        Assert.Equal("ses_existing", employee.SessionId);
        Assert.Equal("Contoso Development AgentControl", employee.SessionTitle);

        var backup = Path.Combine(root.Directory, OrganizationStore.AdoptionBackupFileName);
        var hash = Path.Combine(root.Directory, OrganizationStore.AdoptionBackupHashFileName);
        Assert.Equal("byte-exact-adoption-source"u8.ToArray(), File.ReadAllBytes(backup));
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant(),
            File.ReadAllText(hash).Trim());
    }

    [Fact]
    public void AdoptionBackupExistsAndIsVerifiedBeforeDatabasePublication()
    {
        using var root = new TempStore();
        var source = RuntimeStateStore.CreateNew("AgentControl Development", () => "org-existing");
        var bytes = "exact-runtime-json"u8.ToArray();
        using (var store = Open(root))
        {
            store.BeforeFirstPublication = _ =>
            {
                Assert.True(File.Exists(Path.Combine(root.Directory, OrganizationStore.AdoptionBackupFileName)));
                Assert.True(File.Exists(Path.Combine(root.Directory, OrganizationStore.AdoptionBackupHashFileName)));
                Assert.False(File.Exists(root.Path));
            };

            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", source, "/runtime.json", bytes);
        }

        // Conflicting evidence is never overwritten on a retry or operator error.
        File.Delete(root.Path);
        using var retry = Open(root);
        var conflict = Assert.Throws<OrganizationStoreCorruptException>(() =>
            retry.OpenAndAdopt(
                "AgentControl Development",
                "owner-approved:test",
                source,
                "/runtime.json",
                "different-runtime-json"u8.ToArray()));
        Assert.Contains("does not match", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root.Directory, OrganizationStore.AdoptionBackupFileName)));
        Assert.False(File.Exists(root.Path));
    }

    [Fact]
    public void RenameKeepsStableIdsAndRejectsAStaleRevision()
    {
        using var root = new TempStore();
        using var store = Open(root);
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        var before = store.GetOverview();

        var renamed = store.UpdateOrganizationDisplayName(before.Id, "  Renamed Org  ", before.Revision);

        Assert.Equal("Renamed Org", renamed.DisplayName);
        Assert.Equal(before.Revision + 1, renamed.Revision);
        Assert.Equal(identity.OrganizationId, renamed.Id);

        // Stable ids are unchanged by a rename.
        Assert.Equal(before.Employees.Single().Id, renamed.Employees.Single().Id);
        Assert.Equal(before.Employees.Single().RuntimeBindingId, renamed.Employees.Single().RuntimeBindingId);

        // A stale revision is a conflict, and the store is left unchanged.
        Assert.Throws<OrganizationConcurrencyException>(
            () => store.UpdateOrganizationDisplayName(renamed.Id, "Second Rename", before.Revision));
        Assert.Equal("Renamed Org", store.GetOverview().DisplayName);
        Assert.Throws<OrganizationNotFoundException>(
            () => store.UpdateOrganizationDisplayName("org-missing", "Nope", 1));
        Assert.Throws<OrganizationValidationException>(
            () => store.UpdateOrganizationDisplayName(renamed.Id, "   ", renamed.Revision));
    }

    [Fact]
    public void RecordSessionIsTransactionalAndIdempotent()
    {
        using var root = new TempStore();
        using var store = Open(root);
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

        store.RecordSession("ses_a", "First");
        var first = Assert.Single(store.GetOverview().Employees);
        Assert.Equal("ses_a", first.SessionId);
        Assert.Equal("First", first.SessionTitle);
        Assert.Equal(1, CountActiveSessions(root.Path));

        // Re-recording the same native session does not create a duplicate and
        // does not disturb the single-active invariant.
        store.RecordSession("ses_a", "First");
        Assert.Single(store.GetOverview().Employees);
        Assert.Equal(1, CountSessions(root.Path));
        Assert.Equal(1, CountActiveSessions(root.Path));

        // A new native session becomes the authoritative binding session, and
        // the prior active session is demoted in the same transaction: exactly
        // one session is ever active.
        store.RecordSession("ses_b", "Second");
        var second = Assert.Single(store.GetOverview().Employees);
        Assert.Equal("ses_b", second.SessionId);
        Assert.Equal(2, CountSessions(root.Path));
        Assert.Equal(1, CountActiveSessions(root.Path));
        Assert.Equal("ses_b", ActiveNativeSession(root.Path));
        Assert.Equal("superseded", SessionStatus(root.Path, "ses_a"));
        Assert.Equal("active", SessionStatus(root.Path, "ses_b"));
    }

    [Fact]
    public void SessionStatusIsConstrainedAndOnlyOneActiveRowIsAllowed()
    {
        using var root = new TempStore();
        using var store = Open(root);
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        store.RecordSession("ses_a", "First");

        // The schema constrains status to the known lifecycle values.
        var invalidStatus = Assert.Throws<SqliteException>(() => ExecuteRaw(
            root.Path,
            """
            INSERT INTO acp_sessions (id, employee_id, native_session_id, title, status, created_at, updated_at)
            SELECT 'acps-bad', id, 'ses_bad', NULL, 'bogus', created_at, updated_at FROM employees LIMIT 1;
            """));
        Assert.Contains("CHECK", invalidStatus.Message, StringComparison.OrdinalIgnoreCase);

        // The partial unique index makes a second active session impossible even
        // if application code regressed.
        var secondActive = Assert.Throws<SqliteException>(() => ExecuteRaw(
            root.Path,
            """
            INSERT INTO acp_sessions (id, employee_id, native_session_id, title, status, created_at, updated_at)
            SELECT 'acps-second', id, 'ses_second', NULL, 'active', created_at, updated_at FROM employees LIMIT 1;
            """));
        Assert.Contains("UNIQUE", secondActive.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountActiveSessions(root.Path));
    }

    [Fact]
    public void MalformedDatabaseFailsClosedWithoutReseeding()
    {
        using var root = new TempStore();
        File.WriteAllBytes(root.Path, "this is not a sqlite database"u8.ToArray());
        using var store = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Throws<OrganizationStoreCorruptException>(
            () => store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
    }

    [Fact]
    public void EmptyExistingFileFailsClosedWithoutSeeding()
    {
        using var root = new TempStore();
        File.WriteAllBytes(root.Path, []);
        using var store = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Throws<OrganizationStoreCorruptException>(
            () => store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
    }

    [Fact]
    public void PartialDatabaseFailsClosedWithoutRepair()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        ExecuteRaw(root.Path, "DROP TABLE adoption_audit;");
        using var reopened = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));
        var exception = Assert.Throws<OrganizationStoreCorruptException>(
            () => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Contains("adoption_audit", exception.Message, StringComparison.Ordinal);

        // A refused start does not reseed or repair: the surviving tables and the
        // single organization are exactly what they were.
        Assert.Equal(0, RawScalar(root.Path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'adoption_audit';"));
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'roles';"));
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM organizations;"));
    }

    [Fact]
    public void UnexpectedTablesAndColumnsFailClosed()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        ExecuteRaw(root.Path, "CREATE TABLE unexpected_table (id TEXT); ALTER TABLE organizations ADD COLUMN unexpected_column TEXT;");
        using var reopened = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));
        var exception = Assert.Throws<OrganizationStoreCorruptException>(
            () => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Contains("unexpected table shape", exception.Message, StringComparison.Ordinal);
        Assert.Contains("current authoritative schema-v8 backup or source", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No schema-v8 backup is created automatically", exception.Message, StringComparison.Ordinal);
        Assert.Contains("schema-v7 file is pre-migration evidence only", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lose container profiles recorded after migration", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Restore the verified schema-v7 backup", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSeedDepartmentFailsClosedRatherThanBeingReseeded()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        ExecuteRaw(
            root.Path,
            "DELETE FROM permission_audit; DELETE FROM permission_requests; DELETE FROM permission_grants; DELETE FROM dispatch_holds; DELETE FROM orientation_evidence; DELETE FROM orientation_assignment_fragments; DELETE FROM orientation_assignments; DELETE FROM permission_restrictions; DELETE FROM permission_policies; DELETE FROM orientation_facts; DELETE FROM orientation_fragments; DELETE FROM adoption_audit; DELETE FROM runtime_bindings; DELETE FROM acp_sessions; DELETE FROM employees; DELETE FROM roles; DELETE FROM departments WHERE slug = 'qa';");

        using var reopened = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));
        var exception = Assert.Throws<OrganizationStoreCorruptException>(
            () => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Contains("Operations/Development/QA", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewerSchemaVersionFailsClosed()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        ExecuteRaw(root.Path, "UPDATE schema_version SET version = 999;");
        using var reopened = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));
        var exception = Assert.Throws<OrganizationStoreCorruptException>(
            () => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Contains("schema version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreWithoutAnOrganizationFailsClosedRatherThanReseeding()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        ExecuteRaw(root.Path, "DELETE FROM permission_audit; DELETE FROM permission_requests; DELETE FROM permission_grants; DELETE FROM dispatch_holds; DELETE FROM orientation_evidence; DELETE FROM orientation_assignment_fragments; DELETE FROM orientation_assignments; DELETE FROM permission_restrictions; DELETE FROM permission_policies; DELETE FROM orientation_facts; DELETE FROM orientation_fragments; DELETE FROM adoption_audit; DELETE FROM runtime_bindings; DELETE FROM acp_sessions; DELETE FROM employees; DELETE FROM roles; DELETE FROM departments; DROP TRIGGER container_profile_revisions_no_delete; DROP TRIGGER container_profiles_no_delete; DELETE FROM container_profile_revisions; DELETE FROM container_profiles; DELETE FROM organizations; CREATE TRIGGER container_profile_revisions_no_delete BEFORE DELETE ON container_profile_revisions BEGIN SELECT RAISE(ABORT, 'container profile revisions are never deleted'); END; CREATE TRIGGER container_profiles_no_delete BEFORE DELETE ON container_profiles BEGIN SELECT RAISE(ABORT, 'container profiles are retired, never deleted'); END;");
        using var reopened = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));
        var exception = Assert.Throws<OrganizationStoreCorruptException>(
            () => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Contains("organizations", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptPageFailsClosedOnIntegrityCheck()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        // Corrupt almost the whole file while leaving the SQLite magic intact so
        // the open path reaches the integrity/shape validation rather than the
        // raw "not a database" error. The WAL sidecars are removed first so the
        // corrupt main file is actually consulted.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = root.Path + suffix;
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        var bytes = File.ReadAllBytes(root.Path);
        Assert.True(bytes.Length > 16);
        Array.Clear(bytes, 16, bytes.Length - 16);
        File.WriteAllBytes(root.Path, bytes);
        using var reopened = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));
        Assert.ThrowsAny<OrganizationStoreException>(
            () => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
    }

    [Fact]
    public void SecondWriterIsRefusedByTheBoundedCrossProcessLock()
    {
        using var root = new TempStore();
        using var first = Open(root);
        first.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

        using var second = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(250));
        var exception = Assert.Throws<OrganizationStoreException>(
            () => second.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Contains("single-writer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateSlugsAndIdentifiersAreRejected()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        // Duplicate organization slug. Every NOT NULL column is supplied so the
        // rejection is the intended UNIQUE constraint, not a missing-value error.
        var duplicateSlug = Assert.Throws<SqliteException>(() => ExecuteRaw(
            root.Path,
            """
            INSERT INTO organizations (id, slug, display_name, description, basic_instructions, created_at, updated_at, revision)
            VALUES ('org-dup', 'agentcontrol-development', 'Duplicate', 'description', 'instructions', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00', 1);
            """));
        Assert.Contains("UNIQUE", duplicateSlug.Message, StringComparison.OrdinalIgnoreCase);

        // Duplicate primary key, likewise fully populated.
        var duplicateId = Assert.Throws<SqliteException>(() => ExecuteRaw(
            root.Path,
            """
            INSERT INTO organizations (id, slug, display_name, description, basic_instructions, created_at, updated_at, revision)
            SELECT id, 'org-other-slug', display_name, description, basic_instructions, created_at, updated_at, revision FROM organizations;
            """));
        Assert.Contains("UNIQUE", duplicateId.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidReferencesAreRejectedByForeignKeys()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        // An employee whose department does not exist violates the referential
        // contract. All NOT NULL columns are supplied so the failure is the
        // intended FOREIGN KEY, never a missing-value error.
        var missingDepartment = Assert.Throws<SqliteException>(() => ExecuteRaw(
            root.Path,
            """
            INSERT INTO employees (id, organization_id, department_id, role_id, slug, display_name, purpose, instructions, rules, restrictions, created_at, updated_at, revision)
            SELECT 'emp-ghost', id, 'dept-does-not-exist', (SELECT id FROM roles LIMIT 1), 'ghost', 'Ghost', 'purpose', 'instructions', 'rules', 'restrictions', created_at, updated_at, 1
            FROM organizations;
            """));
        Assert.Contains("FOREIGN KEY", missingDepartment.Message, StringComparison.OrdinalIgnoreCase);

        // A role from a different department cannot be assigned even when both
        // records belong to the same organization: the composite
        // (role_id, department_id) reference is what fails.
        var crossDepartment = Assert.Throws<SqliteException>(() => ExecuteRaw(
            root.Path,
            """
            INSERT INTO roles (id, department_id, slug, display_name, instruction_profile, permission_profile, created_at, updated_at, revision)
            SELECT 'role-qa', id, 'qa-role', 'QA role', 'role/qa@1', 'policy/qa@1', created_at, updated_at, 1
            FROM departments WHERE slug = 'qa';
            UPDATE employees SET role_id = 'role-qa' WHERE slug = 'operations-it';
            """));
        Assert.Contains("FOREIGN KEY", crossDepartment.Message, StringComparison.OrdinalIgnoreCase);

        // A runtime binding for a nonexistent employee is refused too.
        var missingEmployee = Assert.Throws<SqliteException>(() => ExecuteRaw(
            root.Path,
            """
            INSERT INTO runtime_bindings (id, employee_id, placement, tmux_owner_token, created_at, updated_at, revision)
            VALUES ('rtb-ghost', 'emp-missing', 'InternalSharedContainer', 'token', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00', 1);
            """));
        Assert.Contains("FOREIGN KEY", missingEmployee.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedRenameLeavesTheStoreUnchanged()
    {
        using var root = new TempStore();
        using var store = Open(root);
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        var before = store.GetOverview();

        Assert.Throws<OrganizationValidationException>(
            () => store.UpdateOrganizationDisplayName(before.Id, new string('x', OrganizationStore.MaxDisplayNameLength + 1), before.Revision));
        Assert.Throws<OrganizationConcurrencyException>(
            () => store.UpdateOrganizationDisplayName(before.Id, "Renamed", before.Revision + 5));

        var after = store.GetOverview();
        Assert.Equal(before.DisplayName, after.DisplayName);
        Assert.Equal(before.Revision, after.Revision);
    }

    [Fact]
    public void DatabaseFileIsRestrictedToTheControllerIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempStore();
        using var store = Open(root);
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

        // A write keeps the WAL/SHM sidecars in the pool, so their actual modes
        // are asserted, not just the main file and lock.
        store.RecordSession("ses_permissions", null);

        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, File.GetUnixFileMode(root.Path));
        Assert.Equal(expected, File.GetUnixFileMode(Path.Combine(root.Directory, OrganizationStore.LockFileName)));

        // WAL mode is enabled, so the write-ahead log must exist and be
        // controller-only; the shared-memory file too when present. Relying on
        // the 0700 parent would not be enough if the volume were widened.
        Assert.True(File.Exists(root.Path + "-wal"), "WAL mode should leave a -wal sidecar after a write");
        foreach (var sidecar in new[] { root.Path + "-wal", root.Path + "-shm" })
        {
            if (File.Exists(sidecar))
            {
                Assert.Equal(expected, File.GetUnixFileMode(sidecar));
            }
        }
    }

    /// <summary>
    /// A crash before the seed is published must leave no authoritative
    /// <c>control.db</c>: the next start is a clean first start rather than one
    /// failing closed on its own half-written boot.
    /// </summary>
    [Fact]
    public void CrashBeforeFirstPublicationLeavesNoAuthoritativePath()
    {
        using var root = new TempStore();
        using var store = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromSeconds(5));
        store.BeforeFirstPublication = _ =>
            throw new InvalidOperationException("simulated crash before the seed is published");

        Assert.Throws<InvalidOperationException>(
            () => store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));

        Assert.False(File.Exists(root.Path));

        // Only the temporary this attempt created is cleaned up; no stray seed
        // files remain to collide with a later unique name.
        Assert.Empty(Directory.GetFileSystemEntries(
            root.Directory,
            "." + OrganizationStore.DatabaseFileName + ".seed-*"));

        // The retry really seeds: no half-state blocks the second attempt.
        store.BeforeFirstPublication = null;
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.True(identity.Created);
        Assert.True(File.Exists(root.Path));
        Assert.Single(store.GetOverview().Employees);
    }

    /// <summary>
    /// The authoritative path appears only after a committed, validated seed:
    /// during the seed there is no <c>control.db</c>, and after publication the
    /// reopened store validates rather than trusting the in-memory seed.
    /// </summary>
    [Fact]
    public void AuthoritativePathAppearsOnlyAfterACommittedValidSeed()
    {
        using var root = new TempStore();
        using var store = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromSeconds(5));

        var existedDuringSeed = true;
        store.BeforeFirstPublication = temporaryPath =>
        {
            // The hook runs after the seed is committed and checkpointed but
            // before the atomic rename, so the authoritative path must not exist.
            existedDuringSeed = File.Exists(root.Path);
            Assert.True(File.Exists(temporaryPath));
        };

        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

        Assert.False(existedDuringSeed);
        Assert.True(identity.Created);
        Assert.True(File.Exists(root.Path));
        Assert.Equal(3, store.GetOverview().Departments.Count);
    }

    [Fact]
    public void HeaderOnlyExistingFileFailsClosedWithoutReseeding()
    {
        using var root = new TempStore();

        // The SQLite magic with no usable schema is exactly what a crash between
        // file creation and the first page would leave. It must fail closed and
        // not be reseeded or replaced in place.
        var original = "SQLite format 3\0"u8.ToArray();
        File.WriteAllBytes(root.Path, original);

        using var store = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));
        Assert.ThrowsAny<OrganizationStoreException>(
            () => store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));

        Assert.Equal(original, File.ReadAllBytes(root.Path));
    }

    [Fact]
    public void StoreConnectionsEnableWalFullSyncForeignKeysAndBusyTimeout()
    {
        using var root = new TempStore();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = root.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        OrganizationStore.ConfigureConnection(connection);

        Assert.Equal("wal", ReadPragma(connection, "journal_mode").ToLowerInvariant());
        Assert.Equal(2, int.Parse(ReadPragma(connection, "synchronous"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, int.Parse(ReadPragma(connection, "foreign_keys"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(5000, int.Parse(ReadPragma(connection, "busy_timeout"), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A binding may reference only a session owned by the same employee. The
    /// composite <c>(session_ref, employee_id)</c> foreign key enforces this even
    /// though a plain session id is globally unique; a second employee, a second
    /// session and a raw cross-employee UPDATE must all be rejected.
    /// </summary>
    [Fact]
    public void ARuntimeBindingCannotReferenceAnotherEmployeesSession()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        var exception = Assert.Throws<SqliteException>(() => ExecuteRaw(
            root.Path,
            """
            INSERT INTO employees (id, organization_id, department_id, role_id, slug, display_name, purpose, instructions, rules, restrictions, created_at, updated_at, revision)
            SELECT 'emp-second', organization_id, department_id, role_id, 'second', 'Second', 'purpose', 'instructions', 'rules', 'restrictions', created_at, updated_at, 1 FROM employees LIMIT 1;
            INSERT INTO acp_sessions (id, employee_id, native_session_id, title, status, created_at, updated_at)
            VALUES ('acps-second', 'emp-second', 'ses_second', NULL, 'closed', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
            UPDATE runtime_bindings SET session_ref = 'acps-second';
            """));
        Assert.Contains("FOREIGN KEY", exception.Message, StringComparison.OrdinalIgnoreCase);

        // The binding still points at its own employee (or no session), never the
        // other employee's session.
        Assert.Equal(0, RawScalar(
            root.Path,
            "SELECT COUNT(*) FROM runtime_bindings b JOIN acp_sessions s ON s.id = b.session_ref WHERE s.employee_id <> b.employee_id;"));
    }

    /// <summary>
    /// Dropping the partial active-session index leaves every column intact but
    /// removes the single-active-session invariant, so the store must fail closed
    /// instead of accepting it and it must not recreate the index.
    /// </summary>
    [Fact]
    public void RemovedActiveSessionIndexFailsClosedEvenThoughColumnsMatch()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        ExecuteRaw(root.Path, "DROP INDEX one_active_session_per_employee;");

        using var reopened = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));
        var exception = Assert.Throws<OrganizationStoreCorruptException>(
            () => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Contains("one_active_session_per_employee", exception.Message, StringComparison.Ordinal);

        // Refused starts never repair: the index stays gone and the store is intact.
        Assert.Equal(0, RawScalar(
            root.Path,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'one_active_session_per_employee';"));
        Assert.Equal(7, RawScalar(root.Path, "SELECT COUNT(*) FROM pragma_table_info('acp_sessions');"));
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM organizations;"));
    }

    /// <summary>
    /// A table rebuilt with exactly the same columns but a weakened definition
    /// (here the status CHECK removed) must be rejected: column presence alone is
    /// not proof of the load-bearing schema.
    /// </summary>
    [Fact]
    public void WeakenedTableDefinitionWithIdenticalColumnsFailsClosed()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        RewriteSchemaSql(
            root.Path,
            "acp_sessions",
            """
            CREATE TABLE acp_sessions (
                id TEXT PRIMARY KEY,
                employee_id TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT,
                native_session_id TEXT NOT NULL,
                title TEXT,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE (employee_id, native_session_id),
                UNIQUE (id, employee_id)
            )
            """);

        // The column set is unchanged; only the CHECK is missing.
        Assert.Equal(7, RawScalar(root.Path, "SELECT COUNT(*) FROM pragma_table_info('acp_sessions');"));

        using var reopened = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromMilliseconds(200));
        var exception = Assert.Throws<OrganizationStoreCorruptException>(
            () => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Contains("changed table acp_sessions", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A session promotion is only committed when the runtime binding it points
    /// at is actually updated. A missing binding faults the transaction and the
    /// inserted session is rolled back with it.
    /// </summary>
    [Fact]
    public void RecordSessionFailsAndRollsBackWhenTheBindingIsMissing()
    {
        using var root = new TempStore();
        using var store = Open(root);
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

        ExecuteRaw(root.Path, "DELETE FROM runtime_bindings;");

        var exception = Assert.Throws<OrganizationStoreException>(() => store.RecordSession("ses_orphan", "Orphan"));
        Assert.Contains("runtime binding", exception.Message, StringComparison.Ordinal);

        // The promotion transaction rolled back: no session row survives.
        Assert.Equal(0, CountSessions(root.Path));
        Assert.Equal(0, RawScalar(root.Path, "SELECT COUNT(*) FROM runtime_bindings;"));
    }

    /// <summary>
    /// A loaded (already recorded) session is promoted without being retitled:
    /// the persisted title is authoritative and survives a mismatched or null
    /// title argument.
    /// </summary>
    [Fact]
    public void RecordingAnExistingSessionPreservesItsPersistedTitle()
    {
        using var root = new TempStore();
        var source = RuntimeStateStore.CreateNew("Contoso", () => "org-title");
        source.SessionId = "ses_titled";
        source.SessionTitle = "Persisted Title";

        using var store = Open(root);
        store.OpenAndAdopt(
            "AgentControl Development",
            "owner-approved:test",
            source,
            "/runtime.json",
            "byte-exact-title-source"u8.ToArray());

        Assert.Equal("Persisted Title", Assert.Single(store.GetOverview().Employees).SessionTitle);

        store.RecordSession("ses_titled", "Ignored Retitle");
        Assert.Equal("Persisted Title", Assert.Single(store.GetOverview().Employees).SessionTitle);
        Assert.Equal("active", SessionStatus(root.Path, "ses_titled"));

        // A null title for a loaded session must not erase the persisted title.
        store.RecordSession("ses_titled", null);
        Assert.Equal("Persisted Title", Assert.Single(store.GetOverview().Employees).SessionTitle);
    }

    [Fact]
    public void CheckpointValidationRejectsBusyPartialAndNonSingleResults()
    {
        // A busy checkpoint, a partial checkpoint, no row and more than one row
        // are all publication-blocking failures.
        Assert.Throws<OrganizationStoreException>(
            () => OrganizationStore.ValidateCheckpointResult([(1, 10, 10)]));
        Assert.Throws<OrganizationStoreException>(
            () => OrganizationStore.ValidateCheckpointResult([(0, 10, 4)]));
        Assert.Throws<OrganizationStoreException>(
            () => OrganizationStore.ValidateCheckpointResult([]));
        Assert.Throws<OrganizationStoreException>(
            () => OrganizationStore.ValidateCheckpointResult([(0, 0, 0), (0, 0, 0)]));

        // Successful shapes: a fully truncated WAL and a complete passive checkpoint.
        OrganizationStore.ValidateCheckpointResult([(0, 0, 0)]);
        OrganizationStore.ValidateCheckpointResult([(0, 8, 8)]);
    }

    [Fact]
    public void BusyCheckpointFaultsTheFirstStartAndNeverPublishes()
    {
        using var root = new TempStore();
        using var store = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromSeconds(5));
        store.ForceBusyCheckpointForTest = true;

        Assert.Throws<OrganizationStoreException>(
            () => store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));

        // Nothing was published and the attempt's temporary was cleaned up.
        Assert.False(File.Exists(root.Path));
        Assert.Empty(Directory.GetFileSystemEntries(
            root.Directory,
            "." + OrganizationStore.DatabaseFileName + ".seed-*"));

        // Without the fault the same store seeds cleanly.
        store.ForceBusyCheckpointForTest = false;
        var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.True(identity.Created);
        Assert.True(File.Exists(root.Path));
    }

    /// <summary>
    /// A competing authoritative file that appears before publication must never
    /// be replaced, and the seed attempt's temporary must be cleaned up.
    /// </summary>
    [Fact]
    public void FirstPublicationNeverOverwritesACompetingAuthoritativeFile()
    {
        using var root = new TempStore();
        using var store = new OrganizationStore(root.Path, lockTimeout: TimeSpan.FromSeconds(5));
        var competing = "competing-authoritative-bytes"u8.ToArray();
        store.BeforeFirstPublication = _ => File.WriteAllBytes(root.Path, competing);

        Assert.ThrowsAny<IOException>(
            () => store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));

        // The competing file is untouched and no seed temporary is orphaned.
        Assert.Equal(competing, File.ReadAllBytes(root.Path));
        Assert.Empty(Directory.GetFileSystemEntries(
            root.Directory,
            "." + OrganizationStore.DatabaseFileName + ".seed-*"));
    }

    [Fact]
    public void ExactSchemaV6MigratesToV7WithVerifiedCreateOnceBackupAndPreservesRows()
    {
        using var root = new TempStore();
        using (var store = Open(root))
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        DowngradeToCanonicalV6(root.Path);
        var before = SnapshotCoreData(root.Path);

        using (var migrated = Open(root))
        {
            migrated.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            Assert.Equal(before, SnapshotCoreData(root.Path));
            Assert.Empty(migrated.ListHireRequests());
        }

        Assert.Equal(8, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        var backup = Path.Combine(root.Directory, OrganizationStore.SchemaV6BackupFileName);
        var hash = Path.Combine(root.Directory, OrganizationStore.SchemaV6BackupHashFileName);
        Assert.True(File.Exists(backup));
        AssertNoBackupSidecars(backup);
        Assert.Equal(6, RawScalar(backup, "SELECT version FROM schema_version;"));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant(), File.ReadAllText(hash).Trim());
        var retained = File.ReadAllBytes(backup);
        using var restarted = Open(root);
        restarted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.Equal(retained, File.ReadAllBytes(backup));
    }

    [Fact]
    public void ExactSchemaV7MigratesToV8WithVerifiedCreateOnceBackupSeedsGenericProfileAndPreservesHires()
    {
        using var root = new TempStore();
        string hireId;
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            var overview = store.GetOverview();
            var department = overview.Departments.Single(x => x.Slug == OrganizationSeed.OperationsSlug);
            hireId = store.CreateHireRequest(new HireRequestCreate("v7-hire", "Kept Hire", "Survives migration.", department.Id, overview.Roles.Single().Id, RuntimePlacements.DeveloperContainer, 2, 2048, 256), null).Id;
        }
        DowngradeToCanonicalV7(root.Path);
        Assert.Equal(7, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        var before = SnapshotCoreData(root.Path);

        using (var migrated = Open(root))
        {
            migrated.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            Assert.Equal(before, SnapshotCoreData(root.Path));
            var hire = Assert.Single(migrated.ListHireRequests());
            Assert.Equal(hireId, hire.Id);
            Assert.Null(hire.ContainerProfileRevisionId);
            Assert.Equal(HireRequestStates.Requested, hire.State);
            var profile = Assert.Single(migrated.ListContainerProfiles());
            Assert.Equal(ContainerProfileSeed.GenericEmployeeSlug, profile.Slug);
            Assert.Equal(1, profile.CurrentRevisionNumber);
            Assert.Equal(ContainerProfileBuildStatuses.Unbuilt, profile.CurrentBuildStatus);
            Assert.Equal("seed", Assert.Single(migrated.GetContainerProfile(profile.Id)!.Revisions).CreatedBy);
        }

        Assert.Equal(8, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.Equal(1, RawScalar(root.Path, $"SELECT COUNT(*) FROM hire_request_events WHERE hire_request_id = '{hireId}';"));
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM pragma_table_info('hire_requests') WHERE name = 'container_profile_revision_id';"));
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM pragma_foreign_key_list('hire_requests') WHERE \"table\" = 'container_profile_revisions';"));
        Assert.Equal(3, RawScalar(root.Path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'container_profile%';"));
        var backup = Path.Combine(root.Directory, OrganizationStore.SchemaV7BackupFileName);
        var hash = Path.Combine(root.Directory, OrganizationStore.SchemaV7BackupHashFileName);
        Assert.True(File.Exists(backup));
        AssertNoBackupSidecars(backup);
        Assert.Equal(7, RawScalar(backup, "SELECT version FROM schema_version;"));
        Assert.Equal(0, RawScalar(backup, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'container_profiles';"));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant(), File.ReadAllText(hash).Trim());
        var retained = File.ReadAllBytes(backup);
        using var restarted = Open(root);
        restarted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.Equal(retained, File.ReadAllBytes(backup));
        Assert.Single(restarted.ListContainerProfiles());
    }

    [Fact]
    public void UnknownSchemaV7ShapeFailsBeforeBackupOrMigration()
    {
        using var root = new TempStore();
        using (var store = Open(root)) store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        DowngradeToCanonicalV7(root.Path);
        ExecuteRaw(root.Path, "ALTER TABLE hire_requests ADD COLUMN unknown_v7_value TEXT;");
        using var reopened = Open(root);
        Assert.Throws<OrganizationStoreCorruptException>(() => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Equal(7, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.False(File.Exists(Path.Combine(root.Directory, OrganizationStore.SchemaV7BackupFileName)));
    }

    [Fact]
    public void GenericEmployeeSeedIsIdempotentAndNeverOverwritesAnExistingSlug()
    {
        using var root = new TempStore();
        using (var store = Open(root))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            var seeded = Assert.Single(store.ListContainerProfiles());
            // An owner revision on the seeded profile must survive a migration re-run.
            store.CreateContainerProfileRevision(seeded.Id, new ContainerProfileRevisionCreate(seeded.Revision, """{"image":"agentcontrol-worker-base","name":"Owner tweak"}""", null));
        }
        // Simulate a v7 store whose v8 tables already exist with the owner's data (crash after migration, before version bump is impossible
        // transactionally; this instead proves the seed guard itself by re-running the seed against the existing slug).
        ExecuteRaw(root.Path, "UPDATE schema_version SET version = 7;");
        using var reopened = Open(root);
        var exception = Record.Exception(() => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        // A v7 version with v8 tables present is an unexpected shape and must fail closed, not re-seed.
        Assert.IsType<OrganizationStoreCorruptException>(exception);
        Assert.Equal(2, RawScalar(root.Path, "SELECT COUNT(*) FROM container_profile_revisions;"));
        Assert.Equal(1, RawScalar(root.Path, "SELECT COUNT(*) FROM container_profiles;"));
    }

    [Fact]
    public void UnknownSchemaV6ShapeFailsBeforeBackupOrMigration()
    {
        using var root = new TempStore();
        using (var store = Open(root)) store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        DowngradeToCanonicalV6(root.Path);
        ExecuteRaw(root.Path, "ALTER TABLE organizations ADD COLUMN unknown_v6_value TEXT;");
        using var reopened = Open(root);
        Assert.Throws<OrganizationStoreCorruptException>(() => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Equal(6, RawScalar(root.Path, "SELECT version FROM schema_version;"));
        Assert.False(File.Exists(Path.Combine(root.Directory, OrganizationStore.SchemaV6BackupFileName)));
    }

    [Fact]
    public void HireRequestsAreImmutableIdempotentRelationshipBoundAndRevisionRejected()
    {
        using var root = new TempStore();
        using var store = Open(root);
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        var overview = store.GetOverview();
        var department = overview.Departments.Single(x => x.Slug == OrganizationSeed.OperationsSlug);
        var role = Assert.Single(overview.Roles);
        var input = new HireRequestCreate("hire-key-1", "Developer One", "Build bounded product changes.", department.Id, role.Id, RuntimePlacements.DeveloperContainer, 2, 2048, 256);

        var created = store.CreateHireRequest(input, null);
        var duplicate = store.CreateHireRequest(input, string.Empty);
        Assert.Equal(created, duplicate);
        Assert.Equal(HireRequestStates.Requested, created.State);
        Assert.StartsWith("sha256:", created.RequestVersionHash, StringComparison.Ordinal);
        Assert.Single(store.ListHireRequests());
        Assert.Throws<OrganizationValidationException>(() => store.CreateHireRequest(input, "different-header-key"));
        Assert.Throws<OrganizationValidationException>(() => store.CreateHireRequest(input with { IdempotencyKey = null }, null));
        Assert.Throws<OrganizationConcurrencyException>(() => store.CreateHireRequest(input with { Purpose = "Different" }, "hire-key-1"));
        Assert.Throws<OrganizationValidationException>(() => store.CreateHireRequest(input with { CpuLimit = 0, IdempotencyKey = "other" }, "other"));
        Assert.Throws<OrganizationValidationException>(() => store.CreateHireRequest(input with { DepartmentId = overview.Departments.Single(x => x.Slug == OrganizationSeed.QaSlug).Id, IdempotencyKey = "relationship" }, "relationship"));

        var headerOnly = store.CreateHireRequest(input with { IdempotencyKey = null, RequestedDisplayName = "Header Only" }, "header-only-key");
        Assert.Equal("header-only-key", headerOnly.IdempotencyKey);
        Assert.Equal(2, store.ListHireRequests().Count);

        var rejected = store.RejectHireRequest(created.Id, created.Revision);
        Assert.Equal(HireRequestStates.Rejected, rejected.State);
        Assert.Equal(created.Revision + 1, rejected.Revision);
        Assert.Throws<OrganizationConcurrencyException>(() => store.RejectHireRequest(created.Id, created.Revision));
        Assert.Equal(2, RawScalar(root.Path, $"SELECT COUNT(*) FROM hire_request_events WHERE hire_request_id = '{created.Id}';"));
    }

    /// <summary>
    /// A store that opens but faults during a read (for example a table dropped
    /// underneath it) is still a store contract failure, not a raw SqliteException
    /// leaking to callers or an HTTP 500.
    /// </summary>
    [Fact]
    public void ReadFaultsAreTranslatedIntoTheStoreContract()
    {
        using var root = new TempStore();
        using var store = Open(root);
        store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

        ExecuteRaw(root.Path, "DROP TABLE adoption_audit;");

        var exception = Assert.Throws<OrganizationStoreException>(() => store.GetOverview());
        Assert.IsNotType<OrganizationStoreCorruptException>(exception);
        Assert.IsType<SqliteException>(exception.InnerException);
        Assert.DoesNotContain(root.Path, exception.Message, StringComparison.Ordinal);
    }

    private static void DowngradeToCanonicalV1(string path)
    {
        ExecuteRaw(
            path,
            """
            PRAGMA foreign_keys = OFF;
            DROP TRIGGER container_profile_revisions_immutable;
            DROP TRIGGER container_profile_revisions_no_delete;
            DROP TRIGGER container_profiles_no_delete;
            DROP TABLE hire_request_events;
            DROP TABLE hire_requests;
            DROP TABLE container_profile_revisions;
            DROP TABLE container_profiles;
            DROP TABLE worker_event_retention;
            DROP TABLE remote_terminal_viewers;
            DROP TABLE worker_recovery_audit;
            DROP TABLE worker_pending_permissions;
            DROP TABLE worker_events;
            DROP TABLE worker_recovery_obligations;
            DROP TABLE resource_records;
            DROP TABLE provisioning_operations;
            DROP TABLE worker_cancellations;
            DROP TABLE worker_requests;
            DROP TABLE worker_tasks;
            DROP TABLE worker_cursors;
            DROP TABLE worker_enrollments;
            DROP TABLE execution_hosts;
            DROP TABLE permission_audit;
            DROP TABLE permission_requests;
            DROP TABLE permission_grants;
            DROP TABLE dispatch_holds;
            DROP TABLE orientation_evidence;
            DROP TABLE orientation_assignment_fragments;
            DROP INDEX one_current_orientation_assignment;
            DROP TABLE orientation_assignments;
            DROP TABLE permission_restrictions;
            DROP INDEX one_active_permission_policy;
            DROP TABLE permission_policies;
            DROP TABLE orientation_facts;
            DROP INDEX one_current_orientation_fragment;
            DROP TABLE orientation_fragments;
            ALTER TABLE runtime_bindings RENAME TO runtime_bindings_v2;
            CREATE TABLE runtime_bindings (
                id TEXT PRIMARY KEY,
                employee_id TEXT NOT NULL UNIQUE REFERENCES employees(id) ON DELETE RESTRICT,
                placement TEXT NOT NULL CHECK (placement IN ('InternalSharedContainer', 'DeveloperContainer')),
                container_ref TEXT,
                volume_ref TEXT,
                home_ref TEXT,
                workspace_ref TEXT,
                session_ref TEXT,
                tmux_owner_token TEXT NOT NULL,
                ownership_epoch INTEGER,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                revision INTEGER NOT NULL,
                FOREIGN KEY (session_ref, employee_id) REFERENCES acp_sessions(id, employee_id) ON DELETE RESTRICT
            );
            INSERT INTO runtime_bindings (
                id, employee_id, placement, container_ref, volume_ref, home_ref,
                workspace_ref, session_ref, tmux_owner_token, ownership_epoch,
                created_at, updated_at, revision)
            SELECT id, employee_id, placement, container_ref, volume_ref, home_ref,
                   workspace_ref, session_ref, tmux_owner_token, ownership_epoch,
                   created_at, updated_at, revision
            FROM runtime_bindings_v2;
            DROP TABLE runtime_bindings_v2;
            UPDATE schema_version SET version = 1;
            PRAGMA foreign_keys = ON;
            """);
    }

    private static void DowngradeToCanonicalV7(string path)
    {
        ExecuteRaw(path,
            """
            PRAGMA foreign_keys = OFF;
            DROP TRIGGER container_profile_revisions_immutable;
            DROP TRIGGER container_profile_revisions_no_delete;
            DROP TRIGGER container_profiles_no_delete;
            ALTER TABLE hire_request_events RENAME TO hire_request_events_v8;
            ALTER TABLE hire_requests RENAME TO hire_requests_v8;
            CREATE TABLE hire_requests (
                id TEXT PRIMARY KEY,
                organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
                requested_by_employee_id TEXT REFERENCES employees(id) ON DELETE RESTRICT,
                requested_by_kind TEXT NOT NULL CHECK (requested_by_kind IN ('owner')),
                idempotency_key TEXT NOT NULL UNIQUE CHECK (length(idempotency_key) BETWEEN 1 AND 128),
                requested_display_name TEXT NOT NULL CHECK (length(requested_display_name) BETWEEN 1 AND 128),
                purpose TEXT NOT NULL CHECK (length(purpose) BETWEEN 1 AND 2048),
                department_id TEXT NOT NULL,
                role_id TEXT NOT NULL,
                placement TEXT NOT NULL CHECK (placement IN ('InternalSharedContainer', 'DeveloperContainer')),
                cpu_limit INTEGER NOT NULL CHECK (cpu_limit BETWEEN 1 AND 64),
                memory_limit_mib INTEGER NOT NULL CHECK (memory_limit_mib BETWEEN 256 AND 131072),
                pids_limit INTEGER NOT NULL CHECK (pids_limit BETWEEN 16 AND 4096),
                state TEXT NOT NULL CHECK (state IN ('Requested', 'Approved', 'Provisioning', 'Orienting', 'Ready', 'Rejected', 'Failed', 'Interrupted', 'Uncertain')),
                request_version_hash TEXT NOT NULL CHECK (length(request_version_hash) = 71 AND substr(request_version_hash, 1, 7) = 'sha256:'),
                approved_request_version TEXT,
                owner_approval TEXT,
                revision INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (department_id, organization_id) REFERENCES departments(id, organization_id) ON DELETE RESTRICT,
                FOREIGN KEY (role_id, department_id) REFERENCES roles(id, department_id) ON DELETE RESTRICT
            );
            CREATE TABLE hire_request_events (
                id TEXT PRIMARY KEY,
                hire_request_id TEXT NOT NULL REFERENCES hire_requests(id) ON DELETE RESTRICT,
                state TEXT NOT NULL CHECK (state IN ('Requested', 'Approved', 'Provisioning', 'Orienting', 'Ready', 'Rejected', 'Failed', 'Interrupted', 'Uncertain')),
                revision INTEGER NOT NULL,
                detail_hash TEXT NOT NULL CHECK (length(detail_hash) = 71 AND substr(detail_hash, 1, 7) = 'sha256:'),
                created_at TEXT NOT NULL,
                UNIQUE (hire_request_id, revision)
            );
            INSERT INTO hire_requests SELECT id, organization_id, requested_by_employee_id, requested_by_kind, idempotency_key,
                requested_display_name, purpose, department_id, role_id, placement, cpu_limit, memory_limit_mib, pids_limit,
                state, request_version_hash, approved_request_version, owner_approval, revision, created_at, updated_at FROM hire_requests_v8;
            INSERT INTO hire_request_events SELECT * FROM hire_request_events_v8;
            DROP TABLE hire_request_events_v8;
            DROP TABLE hire_requests_v8;
            DROP TABLE container_profile_revisions;
            DROP TABLE container_profiles;
            UPDATE schema_version SET version = 7;
            PRAGMA foreign_keys = ON;
            """);
    }

    private static void DowngradeToCanonicalV6(string path)
    {
        ExecuteRaw(path,
            """
            PRAGMA foreign_keys = OFF;
            DROP TRIGGER container_profile_revisions_immutable;
            DROP TRIGGER container_profile_revisions_no_delete;
            DROP TRIGGER container_profiles_no_delete;
            DROP TABLE hire_request_events;
            DROP TABLE hire_requests;
            DROP TABLE container_profile_revisions;
            DROP TABLE container_profiles;
            UPDATE schema_version SET version = 6;
            PRAGMA foreign_keys = ON;
            """);
    }

    private static void DowngradeToCanonicalV5(string path)
    {
        ExecuteRaw(
            path,
            """
            PRAGMA foreign_keys = OFF;
            DROP TRIGGER container_profile_revisions_immutable;
            DROP TRIGGER container_profile_revisions_no_delete;
            DROP TRIGGER container_profiles_no_delete;
            DROP TABLE hire_request_events;
            DROP TABLE hire_requests;
            DROP TABLE container_profile_revisions;
            DROP TABLE container_profiles;
            DROP TABLE worker_recovery_audit;
            CREATE TABLE worker_recovery_audit (id TEXT PRIMARY KEY, obligation_id TEXT NOT NULL REFERENCES worker_recovery_obligations(id) ON DELETE RESTRICT, worker_id TEXT NOT NULL REFERENCES worker_enrollments(worker_id) ON DELETE RESTRICT, kind TEXT NOT NULL CHECK(kind IN('replay-gap','ownership-changed','session-reconciliation')), marker_hash TEXT NOT NULL, evidence_hash TEXT NOT NULL CHECK(length(evidence_hash)=71 AND substr(evidence_hash,1,7)='sha256:'), disposition TEXT NOT NULL CHECK(disposition='acknowledged-after-external-reconciliation'), recorded_at TEXT NOT NULL);
            UPDATE schema_version SET version = 5;
            PRAGMA foreign_keys = ON;
            """);
    }

    private static void DowngradeToCanonicalV4(string path)
    {
        ExecuteRaw(
            path,
            """
            PRAGMA foreign_keys = OFF;
            DROP TRIGGER container_profile_revisions_immutable;
            DROP TRIGGER container_profile_revisions_no_delete;
            DROP TRIGGER container_profiles_no_delete;
            DROP TABLE hire_request_events;
            DROP TABLE hire_requests;
            DROP TABLE container_profile_revisions;
            DROP TABLE container_profiles;
            DROP TABLE worker_recovery_audit;
            DROP TABLE worker_recovery_obligations;
            CREATE TABLE worker_recovery_obligations (id TEXT PRIMARY KEY, worker_id TEXT NOT NULL REFERENCES worker_enrollments(worker_id) ON DELETE RESTRICT, source TEXT NOT NULL CHECK(source IN('controller','worker')), kind TEXT NOT NULL CHECK(kind IN('replay-gap','replay-loss','replay-ack-uncertain','journal-failure','request-uncertain','permission-pending','process-interrupted','ownership-changed')), marker_hash TEXT NOT NULL, worker_generation INTEGER NOT NULL CHECK(worker_generation>=0), sequence INTEGER NOT NULL CHECK(sequence>=0), active INTEGER NOT NULL CHECK(active IN(0,1)), detail_hash TEXT, marker_json TEXT CHECK(marker_json IS NULL OR length(marker_json)<=8192), created_at TEXT NOT NULL, cleared_at TEXT, revision INTEGER NOT NULL, UNIQUE(worker_id,kind,marker_hash));
            CREATE TABLE worker_recovery_audit (id TEXT PRIMARY KEY, obligation_id TEXT NOT NULL REFERENCES worker_recovery_obligations(id) ON DELETE RESTRICT, worker_id TEXT NOT NULL REFERENCES worker_enrollments(worker_id) ON DELETE RESTRICT, kind TEXT NOT NULL CHECK(kind IN('replay-gap','ownership-changed')), marker_hash TEXT NOT NULL, evidence_hash TEXT NOT NULL CHECK(length(evidence_hash)=71 AND substr(evidence_hash,1,7)='sha256:'), disposition TEXT NOT NULL CHECK(disposition='acknowledged-after-external-reconciliation'), recorded_at TEXT NOT NULL);
            UPDATE schema_version SET version = 4;
            PRAGMA foreign_keys = ON;
            """);
    }

    private static void SeedV4RecoveryRows(string path)
    {
        ExecuteRaw(
            path,
            """
            INSERT INTO execution_hosts(id,slug,display_name,transport_kind,endpoint_host,endpoint_port,endpoint_user,known_hosts_path,os,capability_status,enabled,enrolled,status,created_at,updated_at,revision)
            VALUES('host-v4','host-v4','Host V4','ssh-docker','worker.example',22,'docker','/known','linux','valid',1,1,'ready','2026-09-16T00:00:00.0000000+00:00','2026-09-16T00:00:00.0000000+00:00',1);
            INSERT INTO worker_enrollments(worker_id,runtime_binding_id,host_id,organization_id,container_name,control_volume_name,home_volume_name,workspace_volume_name,session_volume_name,resource_labels_hash,expected_image_digest,expected_platform,controller_id,key_file_path,key_id,bridge_socket_path,lifecycle_status,worker_generation,process_generation,ownership_epoch,enabled,created_at,updated_at,revision)
            SELECT 'wrk-v4',b.id,'host-v4',e.organization_id,'container-v4','control-v4','home-v4','workspace-v4','session-v4','sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','linux/amd64','controller-v4','/control/key','sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','/control/bridge.sock','enrolled',1,1,1,1,'2026-09-16T00:00:00.0000000+00:00','2026-09-16T00:00:00.0000000+00:00',1
            FROM runtime_bindings b JOIN employees e ON e.id=b.employee_id LIMIT 1;
            INSERT INTO worker_recovery_obligations VALUES('rec-v4','wrk-v4','controller','ownership-changed','sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',1,7,0,NULL,NULL,'2026-09-16T00:00:00.0000000+00:00','2026-09-16T00:01:00.0000000+00:00',2);
            INSERT INTO worker_recovery_audit VALUES('audit-v4','rec-v4','wrk-v4','ownership-changed','sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd','sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee','acknowledged-after-external-reconciliation','2026-09-16T00:01:00.0000000+00:00');
            """);
    }

    private static string SnapshotCoreData(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT group_concat(value, '|') FROM (
                SELECT 'org:' || id || ':' || display_name AS value FROM organizations
                UNION ALL SELECT 'dept:' || id || ':' || slug FROM departments
                UNION ALL SELECT 'role:' || id || ':' || slug FROM roles
                UNION ALL SELECT 'emp:' || id || ':' || slug FROM employees
                UNION ALL SELECT 'binding:' || id || ':' || tmux_owner_token || ':' || ifnull(session_ref, '') FROM runtime_bindings
                UNION ALL SELECT 'session:' || id || ':' || native_session_id || ':' || status FROM acp_sessions
                UNION ALL SELECT 'audit:' || id || ':' || authorization_reference FROM adoption_audit
                ORDER BY value
            );
            """;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void RewriteSchemaSql(string path, string name, string sql)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (var writable = connection.CreateCommand())
        {
            writable.CommandText = "PRAGMA writable_schema = ON;";
            writable.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE sqlite_master SET sql = $sql WHERE type = 'table' AND name = $name";
            update.Parameters.AddWithValue("$sql", sql);
            update.Parameters.AddWithValue("$name", name);
            update.ExecuteNonQuery();
        }

        using (var writable = connection.CreateCommand())
        {
            writable.CommandText = "PRAGMA writable_schema = OFF;";
            writable.ExecuteNonQuery();
        }
    }

    private static string ReadPragma(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static OrganizationStore Open(TempStore root) =>
        new(root.Path, lockTimeout: TimeSpan.FromSeconds(5));

    private static void ExecuteRaw(string path, string sql)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AssertNoBackupSidecars(string backupPath)
    {
        Assert.False(File.Exists(backupPath + "-wal"));
        Assert.False(File.Exists(backupPath + "-shm"));
    }

    private static void CopySqliteDatabase(string sourcePath, string destinationPath)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        using var source = new SqliteConnection(sourceBuilder.ToString());
        using var destination = new SqliteConnection(destinationBuilder.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static int CountSessions(string path) => RawScalar(path, "SELECT COUNT(*) FROM acp_sessions;");

    private static int CountActiveSessions(string path) =>
        RawScalar(path, "SELECT COUNT(*) FROM acp_sessions WHERE status = 'active';");

    private static string ActiveNativeSession(string path) =>
        RawScalarString(path, "SELECT native_session_id FROM acp_sessions WHERE status = 'active';");

    private static string SessionStatus(string path, string nativeSessionId) =>
        RawScalarString(path, $"SELECT status FROM acp_sessions WHERE native_session_id = '{nativeSessionId}';");

    private static string RawText(string path, string sql) => RawScalarString(path, sql);

    private static int RawScalar(string path, string sql)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool RawIsNull(string path, string sql)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() is DBNull or null;
    }

    private static string RawScalarString(string path, string sql)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private sealed class TempStore : IDisposable
    {
        public TempStore()
        {
            Directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agentcontrol-store-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, OrganizationStore.DatabaseFileName);
        }

        public string Directory { get; }

        public string Path { get; }

        public void Dispose()
        {
            // SQLite connection pooling can hold file handles briefly on Linux;
            // clear the pools so the recursive delete is reliable under CI.
            SqliteConnection.ClearAllPools();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
