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
        Assert.Equal(1, overview.Departments.Single(d => d.Slug == "operations").EmployeeCount);

        // One adoption audit record carrying the authorization reference.
        var audit = Assert.Single(overview.AdoptionAudit);
        Assert.Equal("owner-approved:test", audit.AuthorizationReference);
        Assert.Equal(employee.Id, audit.EmployeeId);
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
            "DELETE FROM adoption_audit; DELETE FROM runtime_bindings; DELETE FROM acp_sessions; DELETE FROM employees; DELETE FROM roles; DELETE FROM departments WHERE slug = 'qa';");

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

        ExecuteRaw(root.Path, "DELETE FROM adoption_audit; DELETE FROM runtime_bindings; DELETE FROM acp_sessions; DELETE FROM employees; DELETE FROM roles; DELETE FROM departments; DELETE FROM organizations;");
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

    private static int CountSessions(string path) => RawScalar(path, "SELECT COUNT(*) FROM acp_sessions;");

    private static int CountActiveSessions(string path) =>
        RawScalar(path, "SELECT COUNT(*) FROM acp_sessions WHERE status = 'active';");

    private static string ActiveNativeSession(string path) =>
        RawScalarString(path, "SELECT native_session_id FROM acp_sessions WHERE status = 'active';");

    private static string SessionStatus(string path, string nativeSessionId) =>
        RawScalarString(path, $"SELECT status FROM acp_sessions WHERE native_session_id = '{nativeSessionId}';");

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
