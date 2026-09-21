using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HVO.AgentControl.Organization;

/// <summary>Base type for a store condition that must fault the host rather than be repaired.</summary>
public class OrganizationStoreException : Exception
{
    public OrganizationStoreException(string message)
        : base(message)
    {
    }

    public OrganizationStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The store could not be opened or is not the expected shape/version. Never repaired in place.</summary>
public sealed class OrganizationStoreCorruptException : OrganizationStoreException
{
    public OrganizationStoreCorruptException(string message)
        : base(message)
    {
    }

    public OrganizationStoreCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A host-owned update was rejected by validation.</summary>
public sealed class OrganizationValidationException : OrganizationStoreException
{
    public OrganizationValidationException(string message)
        : base(message)
    {
    }

    public OrganizationValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A host-owned update lost an optimistic-concurrency race.</summary>
public sealed class OrganizationConcurrencyException : OrganizationStoreException
{
    public OrganizationConcurrencyException(string message)
        : base(message)
    {
    }
}

/// <summary>The referenced record does not exist.</summary>
public sealed class OrganizationNotFoundException : OrganizationStoreException
{
    public OrganizationNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Authoritative organization store backed by a single SQLite database owned by
/// the controller identity. The database is the source of truth after the first
/// adoption; <c>runtime.json</c> is retained as evidence and is never used to
/// overwrite a persisted organization identity.
/// </summary>
/// <remarks>
/// <para>
/// Startup is fail-closed. A brand-new file is created and seeded in one
/// transaction. An existing file must already carry the current schema version,
/// the expected tables and columns, a passing <c>PRAGMA quick_check</c>, and
/// exactly one organization; anything else faults the host instead of seeding,
/// resetting or repairing the store.
/// </para>
/// <para>
/// Concurrency: a bounded cross-process advisory lock (<c>control.db.lock</c>,
/// <c>FileShare.None</c>) allows exactly one writer; every connection enables
/// WAL, <c>synchronous=FULL</c>, foreign keys and a busy timeout. Writes within
/// the store are serialized and use optimistic revisions for host-owned updates.
/// </para>
/// </remarks>
public sealed partial class OrganizationStore : IDisposable
{
    /// <summary>
    /// Schema 13 adds the durable task specification, model report and host
    /// verification domain (#220 slice A). <c>worker_tasks</c> is rebuilt with the
    /// bounded specification, the optional model-report triplet and a bounded
    /// failure detail, preserving every row and its timestamps/revision; the
    /// additive <c>worker_task_verifications</c> table records the host-only path
    /// from a completed task to <c>Verified</c>. Schema 12 added the durable
    /// employee-rebuild operation record (additive only): it records the exact
    /// source revision/digest a worker is running, the verified target
    /// revision/build/digest and host, the destructive-reset authorization, and
    /// one fixed state machine from <c>Intent</c> to <c>Applied</c>/<c>Failed</c>.
    /// Schema 11 permitted managed hiring against the controller-local Docker
    /// daemon; it rebuilt <c>execution_hosts</c> with a constrained
    /// <c>transport_kind</c>.
    /// </summary>
    public const int CurrentSchemaVersion = 13;

    public const string DatabaseFileName = "control.db";
    public const string LockFileName = "control.db.lock";
    public const string AdoptionBackupFileName = "runtime.pre-database.json";
    public const string AdoptionBackupHashFileName = "runtime.pre-database.sha256";
    public const string SchemaV1BackupFileName = "control.schema-v1.db";
    public const string SchemaV1BackupHashFileName = "control.schema-v1.sha256";
    public const string SchemaV2BackupFileName = "control.schema-v2.db";
    public const string SchemaV2BackupHashFileName = "control.schema-v2.sha256";
    public const string SchemaV3BackupFileName = "control.schema-v3.db";
    public const string SchemaV3BackupHashFileName = "control.schema-v3.sha256";
    public const string SchemaV4BackupFileName = "control.schema-v4.db";
    public const string SchemaV4BackupHashFileName = "control.schema-v4.sha256";
    public const string SchemaV5BackupFileName = "control.schema-v5.db";
    public const string SchemaV5BackupHashFileName = "control.schema-v5.sha256";
    public const string SchemaV6BackupFileName = "control.schema-v6.db";
    public const string SchemaV6BackupHashFileName = "control.schema-v6.sha256";
    public const string SchemaV7BackupFileName = "control.schema-v7.db";
    public const string SchemaV7BackupHashFileName = "control.schema-v7.sha256";
    public const string SchemaV8BackupFileName = "control.schema-v8.db";
    public const string SchemaV8BackupHashFileName = "control.schema-v8.sha256";
    public const string SchemaV9BackupFileName = "control.schema-v9.db";
    public const string SchemaV9BackupHashFileName = "control.schema-v9.sha256";
    public const string SchemaV10BackupFileName = "control.schema-v10.db";
    public const string SchemaV10BackupHashFileName = "control.schema-v10.sha256";
    public const string SchemaV11BackupFileName = "control.schema-v11.db";
    public const string SchemaV11BackupHashFileName = "control.schema-v11.sha256";
    public const string SchemaV12BackupFileName = "control.schema-v12.db";
    public const string SchemaV12BackupHashFileName = "control.schema-v12.sha256";

    /// <summary>Maximum accepted organization display-name length.</summary>
    public const int MaxDisplayNameLength = 128;

    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] SchemaV1Statements =
    [
        """
        CREATE TABLE schema_version (
            version INTEGER NOT NULL
        )
        """,
        """
        CREATE TABLE organizations (
            id TEXT PRIMARY KEY,
            slug TEXT NOT NULL UNIQUE,
            display_name TEXT NOT NULL,
            description TEXT NOT NULL,
            basic_instructions TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            revision INTEGER NOT NULL
        )
        """,
        """
        CREATE TABLE departments (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
            slug TEXT NOT NULL,
            display_name TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            revision INTEGER NOT NULL,
            UNIQUE (organization_id, slug),
            UNIQUE (id, organization_id)
        )
        """,
        """
        CREATE TABLE roles (
            id TEXT PRIMARY KEY,
            department_id TEXT NOT NULL REFERENCES departments(id) ON DELETE RESTRICT,
            slug TEXT NOT NULL,
            display_name TEXT NOT NULL,
            instruction_profile TEXT NOT NULL,
            permission_profile TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            revision INTEGER NOT NULL,
            UNIQUE (department_id, slug),
            UNIQUE (id, department_id)
        )
        """,
        """
        CREATE TABLE employees (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
            department_id TEXT NOT NULL,
            role_id TEXT NOT NULL REFERENCES roles(id) ON DELETE RESTRICT,
            slug TEXT NOT NULL,
            display_name TEXT NOT NULL,
            purpose TEXT NOT NULL,
            instructions TEXT NOT NULL,
            rules TEXT NOT NULL,
            restrictions TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            revision INTEGER NOT NULL,
            UNIQUE (organization_id, slug),
            FOREIGN KEY (department_id, organization_id) REFERENCES departments(id, organization_id),
            FOREIGN KEY (role_id, department_id) REFERENCES roles(id, department_id)
        )
        """,
        """
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
        )
        """,
        """
        CREATE TABLE acp_sessions (
            id TEXT PRIMARY KEY,
            employee_id TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT,
            native_session_id TEXT NOT NULL,
            title TEXT,
            status TEXT NOT NULL CHECK (status IN ('adopted', 'active', 'superseded', 'closed')),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE (employee_id, native_session_id),
            UNIQUE (id, employee_id)
        )
        """,
        """
        CREATE UNIQUE INDEX one_active_session_per_employee
            ON acp_sessions (employee_id)
            WHERE status = 'active'
        """,
        """
        CREATE TABLE adoption_audit (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
            employee_id TEXT REFERENCES employees(id) ON DELETE RESTRICT,
            source TEXT NOT NULL,
            authorization_reference TEXT NOT NULL,
            adopted_at TEXT NOT NULL,
            notes TEXT
        )
        """,
    ];

    private static readonly string RuntimeBindingsV2Statement =
        """
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
            credential_set_id TEXT,
            provider_config_version TEXT,
            provider_profile_id TEXT,
            provider_config_status TEXT NOT NULL DEFAULT 'unconfigured' CHECK (provider_config_status IN ('unconfigured', 'configured', 'unavailable', 'revoked')),
            model_catalog_version TEXT,
            policy_lane_id TEXT,
            configured_provider_id TEXT,
            configured_model_id TEXT,
            configured_variant TEXT,
            observed_provider_id TEXT,
            observed_model_id TEXT,
            observed_variant TEXT,
            observed_at TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            revision INTEGER NOT NULL,
            FOREIGN KEY (session_ref, employee_id) REFERENCES acp_sessions(id, employee_id) ON DELETE RESTRICT
        )
        """;

    // Explicit object-for-object V2 schema. Index 5 is intentionally the sole
    // changed object; deriving this with substring replacement could silently
    // replace another statement that happened to mention runtime_bindings.
    private static readonly string[] SchemaV2Statements =
    [
        SchemaV1Statements[0],
        SchemaV1Statements[1],
        SchemaV1Statements[2],
        SchemaV1Statements[3],
        SchemaV1Statements[4],
        RuntimeBindingsV2Statement,
        SchemaV1Statements[6],
        SchemaV1Statements[7],
        SchemaV1Statements[8],
    ];

    /// <summary>
    /// Canonical, normalized definition of every table and explicit index this
    /// build creates, keyed by object type and name. An existing store is
    /// accepted only when its <c>sqlite_master</c> definitions match this
    /// signature exactly (after whitespace normalization), so a same-column
    /// rebuild that quietly drops a NOT NULL, UNIQUE, CHECK, FOREIGN KEY or a
    /// partial index fails closed instead of being read as the accepted schema.
    /// Inline UNIQUE/PK constraints create <c>sqlite_autoindex_*</c> entries with
    /// no SQL, which are excluded; the named partial index is explicit.
    /// </summary>
    private static readonly string[] OrientationSchemaV3Statements =
    [
        """
        CREATE TABLE orientation_fragments (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
            layer TEXT NOT NULL CHECK (layer IN ('organization', 'department', 'role', 'employee')),
            scope_id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            content TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            active INTEGER NOT NULL CHECK (active IN (0, 1)),
            created_at TEXT NOT NULL,
            UNIQUE (layer, scope_id, revision),
            UNIQUE (id, organization_id)
        )
        """,
        """
        CREATE UNIQUE INDEX one_current_orientation_fragment
            ON orientation_fragments (layer, scope_id)
            WHERE active = 1
        """,
        """
        CREATE TABLE orientation_facts (
            fragment_id TEXT NOT NULL REFERENCES orientation_fragments(id) ON DELETE RESTRICT,
            category TEXT NOT NULL CHECK (category IN ('identity', 'department', 'reporting', 'duty', 'restriction', 'escalation')),
            value TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            PRIMARY KEY (fragment_id, category, ordinal),
            UNIQUE (fragment_id, category, value)
        )
        """,
        """
        CREATE TABLE permission_policies (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
            version TEXT NOT NULL UNIQUE,
            revision INTEGER NOT NULL,
            summary TEXT NOT NULL,
            created_at TEXT NOT NULL,
            active INTEGER NOT NULL CHECK (active IN (0, 1)),
            UNIQUE (id, revision)
        )
        """,
        """
        CREATE UNIQUE INDEX one_active_permission_policy
            ON permission_policies (organization_id)
            WHERE active = 1
        """,
        """
        CREATE TABLE permission_restrictions (
            id TEXT PRIMARY KEY,
            policy_id TEXT NOT NULL REFERENCES permission_policies(id) ON DELETE RESTRICT,
            fragment_id TEXT REFERENCES orientation_fragments(id) ON DELETE RESTRICT,
            layer TEXT NOT NULL CHECK (layer IN ('host', 'organization', 'department', 'role', 'employee')),
            stable_key TEXT NOT NULL,
            tool_pattern TEXT NOT NULL,
            resource_pattern TEXT NOT NULL,
            description TEXT NOT NULL,
            waivable INTEGER NOT NULL CHECK (waivable IN (0, 1)),
            revision INTEGER NOT NULL,
            UNIQUE (policy_id, stable_key),
            UNIQUE (id, policy_id)
        )
        """,
        """
        CREATE TABLE orientation_assignments (
            id TEXT PRIMARY KEY,
            employee_id TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT,
            runtime_binding_id TEXT NOT NULL REFERENCES runtime_bindings(id) ON DELETE RESTRICT,
            session_id TEXT REFERENCES acp_sessions(id) ON DELETE RESTRICT,
            policy_id TEXT NOT NULL REFERENCES permission_policies(id) ON DELETE RESTRICT,
            orientation_version TEXT NOT NULL,
            artifact_file_name TEXT NOT NULL,
            artifact_bytes INTEGER NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('Assigned', 'Delivered', 'Acknowledged', 'Comprehended', 'Failed', 'TimedOut', 'Rejected', 'Stale', 'Uncertain')),
            assigned_at TEXT NOT NULL,
            delivered_at TEXT,
            acknowledged_at TEXT,
            comprehended_at TEXT,
            evidence_hash TEXT,
            evidence_summary TEXT,
            evidence_source TEXT CHECK (evidence_source IN ('owner-submitted', 'live-model')),
            required_runtime_generation INTEGER,
            loaded_runtime_generation INTEGER,
            last_error TEXT,
            revision INTEGER NOT NULL,
            UNIQUE (id, employee_id),
            FOREIGN KEY (session_id, employee_id) REFERENCES acp_sessions(id, employee_id) ON DELETE RESTRICT
        )
        """,
        """
        CREATE UNIQUE INDEX one_current_orientation_assignment
            ON orientation_assignments (employee_id)
            WHERE state <> 'Stale'
        """,
        """
        CREATE TABLE orientation_assignment_fragments (
            assignment_id TEXT NOT NULL REFERENCES orientation_assignments(id) ON DELETE RESTRICT,
            fragment_id TEXT NOT NULL REFERENCES orientation_fragments(id) ON DELETE RESTRICT,
            ordinal INTEGER NOT NULL,
            PRIMARY KEY (assignment_id, ordinal),
            UNIQUE (assignment_id, fragment_id)
        )
        """,
        """
        CREATE TABLE orientation_evidence (
            id TEXT PRIMARY KEY,
            assignment_id TEXT NOT NULL REFERENCES orientation_assignments(id) ON DELETE RESTRICT,
            employee_id TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT,
            session_id TEXT NOT NULL REFERENCES acp_sessions(id) ON DELETE RESTRICT,
            orientation_version TEXT NOT NULL,
            evidence_hash TEXT NOT NULL,
            sanitized_summary TEXT NOT NULL,
            evidence_source TEXT NOT NULL CHECK (evidence_source IN ('owner-submitted', 'live-model')),
            outcome TEXT NOT NULL CHECK (outcome IN ('Comprehended', 'Rejected', 'TimedOut', 'Failed')),
            created_at TEXT NOT NULL,
            UNIQUE (assignment_id, evidence_hash)
        )
        """,
        """
        CREATE TABLE dispatch_holds (
            id TEXT PRIMARY KEY,
            runtime_binding_id TEXT NOT NULL REFERENCES runtime_bindings(id) ON DELETE RESTRICT,
            reason TEXT NOT NULL CHECK (reason IN ('orientation-unacknowledged', 'stale', 'failed', 'policy-update', 'orientation-reload-required', 'manual', 'task-verification')),
            active INTEGER NOT NULL CHECK (active IN (0, 1)),
            detail TEXT,
            created_at TEXT NOT NULL,
            cleared_at TEXT,
            revision INTEGER NOT NULL,
            UNIQUE (runtime_binding_id, reason)
        )
        """,
        """
        CREATE TABLE permission_grants (
            id TEXT PRIMARY KEY,
            organization_id TEXT NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
            employee_id TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT,
            restriction_id TEXT NOT NULL REFERENCES permission_restrictions(id) ON DELETE RESTRICT,
            policy_id TEXT NOT NULL REFERENCES permission_policies(id) ON DELETE RESTRICT,
            policy_revision INTEGER NOT NULL,
            tool TEXT NOT NULL,
            resource TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            revoked_at TEXT,
            idempotency_key TEXT NOT NULL,
            created_at TEXT NOT NULL,
            revision INTEGER NOT NULL,
            UNIQUE (employee_id, idempotency_key)
        )
        """,
        """
        CREATE TABLE permission_requests (
            id TEXT PRIMARY KEY,
            employee_id TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT,
            runtime_binding_id TEXT NOT NULL REFERENCES runtime_bindings(id) ON DELETE RESTRICT,
            session_id TEXT REFERENCES acp_sessions(id) ON DELETE RESTRICT,
            generation INTEGER NOT NULL,
            tool TEXT NOT NULL,
            resource_hash TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('rejected')),
            created_at TEXT NOT NULL,
            decided_at TEXT NOT NULL,
            UNIQUE (runtime_binding_id, generation, id)
        )
        """,
        """
        CREATE TABLE permission_audit (
            id TEXT PRIMARY KEY,
            permission_request_id TEXT REFERENCES permission_requests(id) ON DELETE RESTRICT,
            employee_id TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT,
            policy_id TEXT NOT NULL REFERENCES permission_policies(id) ON DELETE RESTRICT,
            policy_revision INTEGER NOT NULL,
            tool TEXT NOT NULL,
            resource_hash TEXT NOT NULL,
            decision TEXT NOT NULL CHECK (decision IN ('allowed', 'rejected', 'cancelled', 'uncertain')),
            restriction_id TEXT REFERENCES permission_restrictions(id) ON DELETE RESTRICT,
            grant_id TEXT REFERENCES permission_grants(id) ON DELETE RESTRICT,
            summary TEXT NOT NULL,
            matched_restriction_ids TEXT NOT NULL,
            created_at TEXT NOT NULL
        )
        """,
    ];

    private static readonly string[] SchemaV3Statements =
        [.. SchemaV2Statements, .. OrientationSchemaV3Statements];

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV1 =
        BuildExpectedSchema(SchemaV1Statements);

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV2 =
        BuildExpectedSchema(SchemaV2Statements);

    private static readonly string[] SchemaV4Statements =
        [.. SchemaV3Statements, .. RemoteWorkerSchemaV4Statements];

    private static readonly string[] SchemaV5Statements =
        [.. SchemaV3Statements, .. RemoteWorkerSchemaV5Statements];

    private static readonly string[] SchemaV6Statements =
        [.. SchemaV3Statements, .. RemoteWorkerSchemaV6Statements];

    private static readonly string[] SchemaV7Statements =
        [.. SchemaV6Statements, .. HireRequestSchemaV7Statements];

    // v8 = v6 + profile tables (referenced by the rebuilt hire_requests) + the
    // rebuilt hire tables + immutability triggers. The v7 hire statements stay
    // frozen in SchemaV7Statements for exact-signature migration.
    private static readonly string[] SchemaV8Statements =
        [.. SchemaV6Statements, .. ContainerProfileSchemaV8Statements, .. HireRequestSchemaV8Statements, .. ContainerProfileImmutabilityV8Statements];

    private static readonly string[] SchemaV9Statements =
        [.. SchemaV8Statements, .. ProfileBuildSchemaV9Statements];

    // v10 = v6 + profile tables + the rebuilt v10 hire tables + profile
    // immutability + profile builds + approval/resources tables and triggers. The
    // v8/v9 hire and profile statements stay frozen for exact-signature migration.
    private static readonly string[] SchemaV10Statements =
        [.. SchemaV6Statements, .. ContainerProfileSchemaV8Statements, .. HireRequestSchemaV10Statements, .. ContainerProfileImmutabilityV8Statements, .. ProfileBuildSchemaV9Statements, .. HireApprovalSchemaV10Statements];

    // v11 = v6 with the rebuilt execution_hosts (via RemoteWorkerSchemaV11Statements)
    // + profile tables + the v10 hire tables + profile immutability + profile builds
    // + approval/resources tables. The frozen v4 execution_hosts statement stays in
    // RemoteWorkerSchemaV4Statements for exact v3-v10 signature migration.
    private static readonly string[] SchemaV11Statements =
        [.. SchemaV3Statements, .. RemoteWorkerSchemaV11Statements, .. ContainerProfileSchemaV8Statements, .. HireRequestSchemaV10Statements, .. ContainerProfileImmutabilityV8Statements, .. ProfileBuildSchemaV9Statements, .. HireApprovalSchemaV10Statements];

    // v12 = v11 + the additive durable employee-rebuild operation record (table,
    // partial active index and immutability/no-delete/no-replace triggers). The
    // frozen v11 statements above stay intact for exact-signature migration.
    private static readonly string[] SchemaV12Statements =
        [.. SchemaV11Statements, .. RebuildSchemaV12Statements];

    // v13 rebuilds worker_tasks in place with the bounded task specification and
    // model report, and adds the host verification table. The frozen v12
    // statements stay intact for exact-signature migration; the v13 remote-worker
    // list replaces only the worker_tasks statement.
    private static readonly string[] RemoteWorkerSchemaV13Statements =
        [
            ExecutionHostsSchemaV11Statement,
            .. RemoteWorkerSchemaV6Statements[1..5],
            WorkerTasksSchemaV13Statement,
            .. RemoteWorkerSchemaV6Statements[6..],
        ];

    private static readonly string[] SchemaV13Statements =
        [.. SchemaV3Statements, .. RemoteWorkerSchemaV13Statements, .. ContainerProfileSchemaV8Statements, .. HireRequestSchemaV10Statements, .. ContainerProfileImmutabilityV8Statements, .. ProfileBuildSchemaV9Statements, .. HireApprovalSchemaV10Statements, .. RebuildSchemaV12Statements, .. WorkerTaskVerificationSchemaV13Statements];

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV3 =
        BuildExpectedSchema(SchemaV3Statements);

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV4 =
        BuildExpectedSchema(SchemaV4Statements);

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV5 =
        BuildExpectedSchema(SchemaV5Statements);

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV6 =
        BuildExpectedSchema(SchemaV6Statements);

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV7 =
        BuildExpectedSchema(SchemaV7Statements);

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV8 =
        BuildExpectedSchema(SchemaV8Statements);

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV9 =
        BuildExpectedSchema(SchemaV9Statements);

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV10 =
        BuildExpectedSchema(SchemaV10Statements);

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV11 =
        BuildExpectedSchema(SchemaV11Statements);

    /// <summary>The exact frozen v12 signature, retained as the accepted pre-v13 shape.</summary>
    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchemaV12 =
        BuildExpectedSchema(SchemaV12Statements);

    private static readonly IReadOnlyDictionary<(string Type, string Name), string> ExpectedSchema =
        BuildExpectedSchema(SchemaV13Statements);

    private static IReadOnlyDictionary<(string Type, string Name), string> BuildExpectedSchema(
        IEnumerable<string> statements)
    {
        var expected = new Dictionary<(string Type, string Name), string>();
        foreach (var statement in statements)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                statement,
                @"^\s*CREATE\s+(?:UNIQUE\s+)?(?<type>TABLE|INDEX|TRIGGER)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    "Every schema statement must begin with CREATE TABLE, CREATE [UNIQUE] INDEX or CREATE TRIGGER.");
            }

            var type = match.Groups["type"].Value.ToLowerInvariant();
            var name = match.Groups["name"].Value;
            if (!expected.TryAdd((type, name), NormalizeSchemaSql(statement)))
            {
                throw new InvalidOperationException(
                    $"Schema statements contain duplicate {type} object '{name}'.");
            }
        }

        return expected;
    }

    private static string NormalizeSchemaSql(string sql) =>
        // SQLite renders a table that was renamed into place with its name quoted
        // (`CREATE TABLE "execution_hosts"`). Identifier quotes are not part of the
        // definition, so they are removed before comparison; schema string literals
        // use single quotes and are left intact.
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(sql.Trim(), "\"", string.Empty),
            @"\s+",
            " ").Trim();

    private readonly string _databasePath;
    private readonly TimeSpan _lockTimeout;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private FileStream? _lock;
    private bool _disposed;

    private string? _employeeId;
    private string? _bindingId;

    public OrganizationStore(string databasePath, ILogger? logger = null, TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _logger = logger;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
    }

    /// <summary>Absolute path of the authoritative database file.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// Deterministic fault seam invoked with the seeded temporary database path
    /// immediately before it is published as <see cref="DatabasePath"/>. Tests
    /// throw from it to prove that a crash before publication leaves no
    /// authoritative path and that the temporary created by that attempt is
    /// cleaned up. Production never sets it.
    /// </summary>
    internal Action<string>? BeforeFirstPublication { get; set; }

    /// <summary>
    /// Deterministic fault seam that forces the seed checkpoint to be evaluated
    /// as a busy <c>(1, 0, 0)</c> result. Tests set it to prove that a checkpoint
    /// that did not truncate the WAL faults the first start and never publishes
    /// an authoritative database. Production never sets it.
    /// </summary>
    internal bool ForceBusyCheckpointForTest { get; set; }

    /// <summary>Fault seam after the migration transaction starts and before commit.</summary>
    internal Action? BeforeMigrationCommit { get; set; }

    /// <summary>Fault seam after the verified backup exists and before migration begins.</summary>
    internal Action? AfterMigrationBackup { get; set; }

    /// <summary>
    /// Restricts the process creation mask so files the runtime creates (the
    /// database, its WAL/SHM sidecars and the writer lock) are controller-only
    /// from the moment SQLite opens them, closing the window before the explicit
    /// post-open chmod runs. Linux-only: the shipped image is Linux, and the
    /// <c>libc</c> import is not portable to every host the tests may run on.
    /// </summary>
    public static void RestrictProcessFileCreation()
    {
        if (OperatingSystem.IsLinux())
        {
            _ = UnixUmask(0x3F); // octal 077: clear group/other read, write and execute.
        }
    }

    [DllImport("libc", EntryPoint = "umask", SetLastError = true)]
    private static extern uint UnixUmask(uint mask);

    /// <summary>
    /// Opens the store and, for a new database, seeds the initial organization
    /// in a single transaction. An existing database is validated and returned
    /// as-is; it is never reseeded or reset.
    /// </summary>
    /// <param name="organizationNameFromConfiguration">Fallback name for a fresh organization.</param>
    /// <param name="adoptionAuthorizationReference">Recorded owner authorization reference for the seed.</param>
    /// <param name="adoptionSource">Persisted runtime.json identity to adopt, or null for a fresh organization.</param>
    /// <param name="adoptionSourceDescription">Human-readable source identity for the audit record.</param>
    public OrganizationRuntimeIdentity OpenAndAdopt(
        string organizationNameFromConfiguration,
        string adoptionAuthorizationReference,
        PersistedRuntimeState? adoptionSource,
        string adoptionSourceDescription,
        byte[]? adoptionSourceBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationNameFromConfiguration);
        ThrowIfDisposed();
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _lock ??= AcquireLock();

            var existed = File.Exists(_databasePath);
            OrganizationRuntimeIdentity identity;
            if (existed)
            {
                RequireNonEmptyExistingDatabase();
                using var connection = OpenConnection(_databasePath);
                PrepareExistingStore(connection);
                identity = ReadIdentity(connection, created: false, organizationNameFromConfiguration);
            }
            else
            {
                if (adoptionSource is not null)
                {
                    if (adoptionSourceBytes is null)
                    {
                        throw new OrganizationStoreException(
                            "A persisted adoption source requires its byte-exact input for verified backup evidence.");
                    }

                    EnsureAdoptionBackup(adoptionSourceBytes);
                }

                identity = CreateSeedAndPublish(
                    organizationNameFromConfiguration,
                    adoptionAuthorizationReference,
                    adoptionSource,
                    adoptionSourceDescription);
            }

            _employeeId = identity.EmployeeId;
            _bindingId = identity.RuntimeBindingId;
            RestrictFileMode(_databasePath);
            return identity;
        }
    }

    /// <summary>
    /// Creates or verifies the byte-exact pre-database JSON backup and its hash
    /// before the first database is built. A retry may complete missing hash
    /// evidence only when the retained bytes still match exactly; conflicting
    /// evidence fails closed and is never replaced.
    /// </summary>
    private void EnsureAdoptionBackup(byte[] sourceBytes)
    {
        var directory = Path.GetDirectoryName(_databasePath) ?? ".";
        var backupPath = Path.Combine(directory, AdoptionBackupFileName);
        var hashPath = Path.Combine(directory, AdoptionBackupHashFileName);
        var expectedHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();

        if (File.Exists(backupPath))
        {
            var retained = File.ReadAllBytes(backupPath);
            if (!retained.AsSpan().SequenceEqual(sourceBytes))
            {
                throw new OrganizationStoreCorruptException(
                    $"The pre-database backup '{backupPath}' does not match the runtime state being adopted. Refusing to replace conflicting evidence.");
            }
        }
        else
        {
            PublishEvidenceFile(backupPath, sourceBytes);
        }

        if (File.Exists(hashPath))
        {
            var recordedHash = File.ReadAllText(hashPath).Trim();
            if (!string.Equals(recordedHash, expectedHash, StringComparison.Ordinal))
            {
                throw new OrganizationStoreCorruptException(
                    $"The pre-database backup hash '{hashPath}' does not match '{backupPath}'. Refusing to replace conflicting evidence.");
            }
        }
        else
        {
            PublishEvidenceFile(hashPath, Encoding.ASCII.GetBytes(expectedHash + "\n"));
        }

        RestrictFileMode(backupPath);
        RestrictFileMode(hashPath);
    }

    private static void PublishEvidenceFile(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        var temporary = Path.Combine(
            directory,
            "." + Path.GetFileName(path) + "." + RandomNumberGenerator.GetHexString(8) + ".tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            RestrictFileMode(temporary);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// A brand-new <c>control.db</c> is never seeded in place. An interrupted
    /// direct seed would leave an empty or header-only file at the authoritative
    /// path, and every later start would have to fail closed on the deployment's
    /// own boot. The seed is therefore built in a uniquely named, controller-only
    /// temporary beside the authoritative path, checkpointed, closed, cleared
    /// from the connection pool, restricted to <c>0600</c> (with its sidecars),
    /// and only then atomically renamed into place. A crash before publication
    /// leaves no <c>control.db</c> at all; the next start retries the seed.
    /// </summary>
    private OrganizationRuntimeIdentity CreateSeedAndPublish(
        string organizationNameFromConfiguration,
        string adoptionAuthorizationReference,
        PersistedRuntimeState? adoptionSource,
        string adoptionSourceDescription)
    {
        var directory = Path.GetDirectoryName(_databasePath) ?? ".";
        var temporary = Path.Combine(
            directory,
            "." + Path.GetFileName(_databasePath) + ".seed-" + RandomNumberGenerator.GetHexString(8) + ".tmp");

        try
        {
            using (var connection = OpenConnection(temporary))
            {
                CreateAndSeed(
                    connection,
                    organizationNameFromConfiguration,
                    adoptionAuthorizationReference,
                    adoptionSource,
                    adoptionSourceDescription);

                // Fold the committed WAL back into the main file and truncate it
                // so the published database is self-contained. A reader of the
                // renamed file must never need a sidecar that was left behind
                // under the temporary name.
                Checkpoint(connection, temporary);
            }

            ClearPoolFor(temporary);
            RemoveSidecars(temporary);
            RestrictFileMode(temporary);

            BeforeFirstPublication?.Invoke(temporary);

            // No overwrite: if a competing authoritative file appeared between
            // the existence check and here (a second writer, a restored backup
            // or a failed prior attempt), this must fail rather than replace it.
            // File.Move without overwrite is that guarantee.
            File.Move(temporary, _databasePath);

            ClearPoolFor(_databasePath);
            RemoveSidecars(_databasePath);
            RestrictFileMode(_databasePath);

            // Re-open and validate what was actually published rather than
            // trusting the in-memory seed.
            using var published = OpenConnection(_databasePath);
            ValidateExistingStore(published);
            return ReadIdentity(published, created: true, organizationNameFromConfiguration);
        }
        catch
        {
            // Clean up only the temporary this attempt created, and only when it
            // was never published. Nothing else in the directory is touched: a
            // temporary left by a different, crashed start is not ours to unlink.
            // Drop the pooled handle first so a checkpoint failure that never
            // reached the normal clear does not keep the sidecars alive.
            ClearPoolFor(temporary);
            if (File.Exists(temporary))
            {
                RemoveSidecars(temporary);
                File.Delete(temporary);
            }

            throw;
        }
    }

    /// <summary>Refuses to treat an existing zero-length file as a store to adopt or reseed.</summary>
    private void RequireNonEmptyExistingDatabase()
    {
        if (new FileInfo(_databasePath).Length == 0)
        {
            throw new OrganizationStoreCorruptException(
                $"The control database '{_databasePath}' is zero-length (an interrupted or empty store). Refusing to seed over or repair it in place; restore a verified backup or remove the empty file deliberately.");
        }
    }

    /// <summary>
    /// Persists the established ACP session for the adopted employee in one
    /// transaction. Called after the session handshake, before any bootstrap
    /// prompt, so a crash cannot lose the organization/session mapping. A native
    /// session already recorded for the employee keeps its persisted title: an
    /// existing session is promoted, never retitled, so a later load cannot
    /// overwrite the title that was established with the session.
    /// </summary>
    public void RecordSession(string nativeSessionId, string? title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeSessionId);
        TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                if (_employeeId is null || _bindingId is null)
                {
                    throw new OrganizationStoreException("The store has not been opened; no session can be recorded.");
                }

                using var connection = OpenConnection(_databasePath);
                using var transaction = connection.BeginTransaction();

                var now = Timestamp();
                string? sessionRowId = null;
                using (var find = connection.CreateCommand())
                {
                    find.Transaction = transaction;
                    find.CommandText =
                        "SELECT id FROM acp_sessions WHERE employee_id = $employee AND native_session_id = $native";
                    find.Parameters.AddWithValue("$employee", _employeeId);
                    find.Parameters.AddWithValue("$native", nativeSessionId);
                    sessionRowId = find.ExecuteScalar() as string;
                }

                // Exactly one session per employee is active at a time. The prior
                // active session is demoted in the same transaction that promotes
                // the new one, so a reader can never observe two active sessions and
                // a crash cannot leave the binding pointing at an active row beside
                // a stale second active row.
                Execute(
                    connection,
                    transaction,
                    """
                UPDATE acp_sessions
                SET status = 'superseded', updated_at = $now
                WHERE employee_id = $employee AND status = 'active' AND native_session_id <> $native
                """,
                    ("$employee", _employeeId),
                    ("$native", nativeSessionId),
                    ("$now", now));

                if (sessionRowId is null)
                {
                    sessionRowId = OrganizationIds.NewSessionId();
                    Execute(
                        connection,
                        transaction,
                        """
                    INSERT INTO acp_sessions (id, employee_id, native_session_id, title, status, created_at, updated_at)
                    VALUES ($id, $employee, $native, $title, 'active', $now, $now)
                    """,
                        ("$id", sessionRowId),
                        ("$employee", _employeeId),
                        ("$native", nativeSessionId),
                        ("$title", title),
                        ("$now", now));
                }
                else
                {
                    // Deliberately not updating the title: the persisted title of an
                    // already-recorded session is authoritative and is preserved.
                    Execute(
                        connection,
                        transaction,
                        """
                    UPDATE acp_sessions
                    SET status = 'active', updated_at = $now
                    WHERE id = $id
                    """,
                        ("$now", now),
                        ("$id", sessionRowId));
                }

                // The runtime binding is the mapping this method exists to update.
                // If it is missing (or more than one row somehow matched) the session
                // promotion must not commit: a transaction that claims a binding it
                // did not actually point at is worse than no write at all.
                var bindingRows = Execute(
                    connection,
                    transaction,
                    """
                UPDATE runtime_bindings
                SET session_ref = $session, updated_at = $now, revision = revision + 1
                WHERE id = $binding
                """,
                    ("$session", sessionRowId),
                    ("$now", now),
                    ("$binding", _bindingId));

                if (bindingRows != 1)
                {
                    throw new OrganizationStoreException(
                        $"Recording the session updated {bindingRows} runtime bindings; exactly one is required. The transaction was rolled back.");
                }

                transaction.Commit();
            }
        });
    }

    /// <summary>
    /// Records non-secret configured request policy separately from native/session
    /// observations. This never records an endpoint, account, key or serving-lane
    /// claim, and it never records a key fingerprint: the raw key stays in the
    /// controller-only secret file and is not persisted here.
    /// </summary>
    public void RecordProviderConfiguration(
        string? credentialSetId,
        string? providerConfigVersion,
        string? providerProfileId,
        string providerConfigStatus,
        string? modelCatalogVersion,
        string? policyLaneId,
        string configuredProviderId,
        string configuredModelId,
        string? configuredVariant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerConfigStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredModelId);
        TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                if (_bindingId is null)
                {
                    throw new OrganizationStoreException("The store has not been opened; provider configuration cannot be recorded.");
                }

                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var affected = Execute(
                    connection,
                    transaction,
                    """
                    UPDATE runtime_bindings
                    SET credential_set_id = $credential,
                        provider_config_version = $configVersion,
                        provider_profile_id = $profile,
                        provider_config_status = $status,
                        model_catalog_version = $catalogVersion,
                        policy_lane_id = $lane,
                        configured_provider_id = $provider,
                        configured_model_id = $model,
                        configured_variant = $variant,
                        updated_at = CASE WHEN
                            credential_set_id IS $credential
                            AND provider_config_version IS $configVersion
                            AND provider_profile_id IS $profile
                            AND provider_config_status = $status
                            AND model_catalog_version IS $catalogVersion
                            AND policy_lane_id IS $lane
                            AND configured_provider_id = $provider
                            AND configured_model_id = $model
                            AND configured_variant IS $variant
                            THEN updated_at ELSE $now END,
                        revision = CASE WHEN
                            credential_set_id IS $credential
                            AND provider_config_version IS $configVersion
                            AND provider_profile_id IS $profile
                            AND provider_config_status = $status
                            AND model_catalog_version IS $catalogVersion
                            AND policy_lane_id IS $lane
                            AND configured_provider_id = $provider
                            AND configured_model_id = $model
                            AND configured_variant IS $variant
                            THEN revision ELSE revision + 1 END
                    WHERE id = $binding
                    """,
                    ("$credential", credentialSetId),
                    ("$configVersion", providerConfigVersion),
                    ("$profile", providerProfileId),
                    ("$status", providerConfigStatus),
                    ("$catalogVersion", modelCatalogVersion),
                    ("$lane", policyLaneId),
                    ("$provider", configuredProviderId),
                    ("$model", configuredModelId),
                    ("$variant", configuredVariant),
                    ("$now", Timestamp()),
                    ("$binding", _bindingId));
                if (affected != 1)
                {
                    throw new OrganizationStoreException("Provider configuration did not update exactly one runtime binding.");
                }

                transaction.Commit();
            }
        });
    }

    /// <summary>
    /// Persists an authoritative native model observation independently from the
    /// configured policy lane. Identical observations are a no-op so a status
    /// poll does not create write churn. A missing variant remains SQL NULL.
    /// </summary>
    public void RecordObservedModel(string providerId, string modelId, string? variant, DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                if (_bindingId is null)
                {
                    throw new OrganizationStoreException("The store has not been opened; an observed model cannot be recorded.");
                }

                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var timestamp = observedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                var affected = Execute(
                    connection,
                    transaction,
                    """
                    UPDATE runtime_bindings
                    SET observed_provider_id = $provider,
                        observed_model_id = $model,
                        observed_variant = $variant,
                        observed_at = $observed,
                        updated_at = $observed,
                        revision = revision + 1
                    WHERE id = $binding
                        AND NOT (
                            observed_provider_id IS $provider
                            AND observed_model_id IS $model
                            AND observed_variant IS $variant)
                    """,
                    ("$provider", providerId),
                    ("$model", modelId),
                    ("$variant", variant),
                    ("$observed", timestamp),
                    ("$binding", _bindingId));
                if (affected is < 0 or > 1)
                {
                    throw new OrganizationStoreException("Observed model persistence updated an unexpected number of runtime bindings.");
                }

                transaction.Commit();
            }
        });
    }

    /// <summary>Returns the minimal organization overview read model.</summary>
    public OrganizationOverview GetOverview()
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                var organization = ReadSingleOrganization(connection);
                return BuildOverview(connection, organization);
            }
        });
    }

    /// <summary>
    /// Renames the organization display name under an optimistic revision check.
    /// A stale revision is a conflict; an unknown organization is not found.
    /// </summary>
    public OrganizationOverview UpdateOrganizationDisplayName(
        string organizationId,
        string displayName,
        int expectedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var validated = ValidateDisplayName(displayName);
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();

            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();

                int currentRevision;
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT revision FROM organizations WHERE id = $id";
                    read.Parameters.AddWithValue("$id", organizationId);
                    var value = read.ExecuteScalar();
                    if (value is null)
                    {
                        throw new OrganizationNotFoundException($"Organization '{organizationId}' does not exist.");
                    }

                    currentRevision = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }

                if (currentRevision != expectedRevision)
                {
                    throw new OrganizationConcurrencyException(
                        $"Organization '{organizationId}' was revised to {currentRevision}, expected {expectedRevision}.");
                }

                var now = Timestamp();
                var affected = Execute(
                    connection,
                    transaction,
                    """
                    UPDATE organizations
                    SET display_name = $name, updated_at = $now, revision = revision + 1
                    WHERE id = $id AND revision = $revision
                    """,
                    ("$name", validated),
                    ("$now", now),
                    ("$id", organizationId),
                    ("$revision", expectedRevision));

                if (affected != 1)
                {
                    throw new OrganizationConcurrencyException(
                        $"Organization '{organizationId}' was modified concurrently; retry with the current revision.");
                }

                RefreshOrganizationFragment(connection, transaction, organizationId);
                MarkCurrentAssignmentsStale(
                    connection,
                    transaction,
                    organizationId,
                    "Organization display name changed.");
                transaction.Commit();

                var organization = ReadSingleOrganization(connection);
                return BuildOverview(connection, organization);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock?.Dispose();
        _lock = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private FileStream AcquireLock()
    {
        var lockPath = Path.Combine(
            Path.GetDirectoryName(_databasePath) ?? ".",
            LockFileName);
        var deadline = DateTime.UtcNow + _lockTimeout;
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                RestrictFileMode(lockPath);
                return stream;
            }
            catch (IOException exception)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new OrganizationStoreException(
                        $"Another process holds the writer lock '{lockPath}'. The control database is single-writer; refusing to open it concurrently.",
                        exception);
                }

                Thread.Sleep(100);
            }
        }
    }

    private SqliteConnection OpenConnection() => OpenConnection(_databasePath);

    private SqliteConnection OpenConnection(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            ConfigureConnection(connection);
            RestrictFileMode(path);
            RestrictSidecarModes(path);
        }
        catch (SqliteException exception)
        {
            connection.Dispose();
            throw new OrganizationStoreCorruptException(
                $"The control database '{path}' could not be opened as SQLite: {exception.Message}",
                exception);
        }

        return connection;
    }

    /// <summary>
    /// Opens retained evidence read-only without pooling and without applying the
    /// writer/WAL contract. Verification must never mutate the backup or create
    /// WAL/SHM sidecars merely by inspecting it.
    /// </summary>
    private static SqliteConnection OpenReadOnlyEvidenceConnection(string path)
    {
        var immutableUri = new UriBuilder(Uri.UriSchemeFile, string.Empty)
        {
            Path = Path.GetFullPath(path),
            Query = "immutable=1",
        }.Uri.AbsoluteUri;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = immutableUri,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        ExecutePragma(connection, "foreign_keys = ON");
        ExecutePragma(connection, "busy_timeout = 5000");
        return connection;
    }

    /// <summary>
    /// Applies the writer contract to every connection: WAL, full synchronous
    /// durability, foreign keys and a bounded busy timeout.
    /// </summary>
    internal static void ConfigureConnection(SqliteConnection connection)
    {
        ExecutePragma(connection, "journal_mode = WAL");
        ExecutePragma(connection, "synchronous = FULL");
        ExecutePragma(connection, "foreign_keys = ON");
        ExecutePragma(connection, "busy_timeout = 5000");
    }

    /// <summary>
    /// Folds the WAL back into the main database and truncates the journal so a
    /// closed database file is complete on its own. The checkpoint result is
    /// load-bearing: publishing and deleting the sidecars is only safe when the
    /// TRUNCATE fully succeeded, so a busy or partial checkpoint throws before
    /// the store is published rather than leaving a database that depends on a
    /// discarded WAL.
    /// </summary>
    private void Checkpoint(SqliteConnection connection, string path)
    {
        var rows = new List<(long Busy, long Log, long Checkpointed)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }

        if (ForceBusyCheckpointForTest)
        {
            rows = [(1, 0, 0)];
        }

        ValidateCheckpointResult(rows);
        RestrictFileMode(path);
        RestrictSidecarModes(path);
    }

    /// <summary>
    /// Validates a <c>wal_checkpoint(TRUNCATE)</c> result tuple set. Exactly one
    /// row is expected; a busy checkpoint means another reader held the WAL and
    /// the TRUNCATE did not complete, and <c>checkpointed &lt; log</c> means some
    /// frames were not folded back. Either is a failure to publish.
    /// </summary>
    internal static void ValidateCheckpointResult(IReadOnlyList<(long Busy, long Log, long Checkpointed)> rows)
    {
        if (rows.Count != 1)
        {
            throw new OrganizationStoreException(
                $"PRAGMA wal_checkpoint(TRUNCATE) returned {rows.Count} rows; exactly one result row is required before publishing.");
        }

        var (busy, log, checkpointed) = rows[0];
        if (busy != 0)
        {
            throw new OrganizationStoreException(
                "PRAGMA wal_checkpoint(TRUNCATE) reported a busy checkpoint; the WAL was not truncated and the seed must not be published.");
        }

        if (checkpointed != log)
        {
            throw new OrganizationStoreException(
                $"PRAGMA wal_checkpoint(TRUNCATE) checkpointed {checkpointed} of {log} WAL frames; the WAL was not fully folded back and the seed must not be published.");
        }
    }

    /// <summary>
    /// Drops this exact database path from the pool so its connection is really
    /// closed (and the WAL/SHM released) before the file is renamed into place.
    /// </summary>
    private static void ClearPoolFor(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };

        using var connection = new SqliteConnection(builder.ToString());
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>Removes the WAL/SHM sidecars of one specific database path.</summary>
    private static void RemoveSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// Restricts the WAL/SHM sidecars of one database path when they exist. The
    /// process creation mask closes the creation window; this makes the final
    /// mode explicit even when the runtime inherits a permissive mask (for
    /// example single-identity host development).
    /// </summary>
    private static void RestrictSidecarModes(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = path + suffix;
            if (File.Exists(sidecar))
            {
                File.SetUnixFileMode(sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private static void ExecutePragma(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        command.ExecuteNonQuery();
    }

    private void PrepareExistingStore(SqliteConnection connection)
    {
        ValidateIntegrity(connection);
        var version = ReadSchemaVersion(connection);
        if (version == 1)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV1);
            EnsureSchemaV1Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV1ToV2(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV2);
            version = ReadSchemaVersion(connection);
        }

        if (version == 2)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV2);
            EnsureSchemaV2Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV2ToV3(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV3);
            version = ReadSchemaVersion(connection);
        }

        if (version == 3)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV3);
            EnsureSchemaV3Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV3ToV4(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV4);
            version = ReadSchemaVersion(connection);
        }

        if (version == 4)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV4);
            EnsureSchemaV4Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV4ToV5(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV5);
            version = ReadSchemaVersion(connection);
        }

        if (version == 5)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV5);
            EnsureSchemaV5Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV5ToV6(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV6);
            version = ReadSchemaVersion(connection);
        }

        if (version == 6)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV6);
            EnsureSchemaV6Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV6ToV7(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV7);
            version = ReadSchemaVersion(connection);
        }

        if (version == 7)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV7);
            EnsureSchemaV7Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV7ToV8(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV8);
            version = ReadSchemaVersion(connection);
        }

        if (version == 8)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV8);
            EnsureSchemaV8Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV8ToV9(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV9);
            version = ReadSchemaVersion(connection);
        }

        if (version == 9)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV9);
            EnsureSchemaV9Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV9ToV10(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV10);
            version = ReadSchemaVersion(connection);
        }

        if (version == 10)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV10);
            EnsureSchemaV10Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV10ToV11(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV11);
            version = ReadSchemaVersion(connection);
        }

        if (version == 11)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV11);
            EnsureSchemaV11Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV11ToV12(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchemaV12);
            version = ReadSchemaVersion(connection);
        }

        if (version == 12)
        {
            ValidateSchemaSignature(connection, ExpectedSchemaV12);
            EnsureSchemaV12Backup(connection);
            AfterMigrationBackup?.Invoke();
            MigrateV12ToV13(connection);
            ValidateIntegrity(connection);
            ValidateSchemaSignature(connection, ExpectedSchema);
            version = ReadSchemaVersion(connection);
        }
        else if (version != CurrentSchemaVersion)
        {
            throw new OrganizationStoreCorruptException(
                $"The control database '{_databasePath}' records schema version {version}; this build requires {CurrentSchemaVersion}. Refusing to start on an unknown or newer store.");
        }

        ValidateExistingStore(connection);
    }

    private void ValidateIntegrity(SqliteConnection connection)
    {
        // Integrity first: a damaged file must not be read as identity.
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                var result = reader.GetString(0);
                if (!string.Equals(result, "ok", StringComparison.Ordinal))
                {
                    throw new OrganizationStoreCorruptException(
                        $"The control database '{_databasePath}' failed PRAGMA quick_check: {result}. Refusing to start on a corrupt store.");
                }
            }
        }

        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using var reader = foreignKeys.ExecuteReader();
            if (reader.Read())
            {
                throw new OrganizationStoreCorruptException(
                    $"The control database '{_databasePath}' failed PRAGMA foreign_key_check at table '{reader.GetString(0)}'. Refusing to start on invalid references.");
            }
        }

    }

    private int ReadSchemaVersion(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM schema_version LIMIT 2;";
            var values = new List<int>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                values.Add(reader.GetInt32(0));
            }

            if (values.Count != 1)
            {
                throw new OrganizationStoreCorruptException(
                    $"The control database '{_databasePath}' has {values.Count} schema_version rows; exactly one is expected.");
            }

            return values[0];
        }
        catch (SqliteException exception)
        {
            throw new OrganizationStoreCorruptException(
                $"The control database '{_databasePath}' does not carry a readable schema version. Refusing to infer or repair it.",
                exception);
        }
    }

    private void EnsureSchemaV1Backup(SqliteConnection source)
    {
        var directory = Path.GetDirectoryName(_databasePath) ?? ".";
        var backupPath = Path.Combine(directory, SchemaV1BackupFileName);
        var hashPath = Path.Combine(directory, SchemaV1BackupHashFileName);
        if (!File.Exists(backupPath))
        {
            var temporary = backupPath + "." + RandomNumberGenerator.GetHexString(8) + ".tmp";
            try
            {
                using (var destination = OpenConnection(temporary))
                {
                    source.BackupDatabase(destination);
                    ValidateIntegrity(destination);
                    if (ReadSchemaVersion(destination) != 1)
                    {
                        throw new OrganizationStoreCorruptException("The pre-migration backup did not preserve schema version 1.");
                    }

                    ValidateSchemaSignature(destination, ExpectedSchemaV1);
                    Checkpoint(destination, temporary);
                }

                ClearPoolFor(temporary);
                RemoveSidecars(temporary);
                RestrictFileMode(temporary);
                File.Move(temporary, backupPath);
            }
            finally
            {
                ClearPoolFor(temporary);
                RemoveSidecars(temporary);
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        var bytesBeforeVerification = File.ReadAllBytes(backupPath);
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytesBeforeVerification)).ToLowerInvariant();
        if (File.Exists(hashPath))
        {
            if (!string.Equals(File.ReadAllText(hashPath).Trim(), expectedHash, StringComparison.Ordinal))
            {
                throw new OrganizationStoreCorruptException("The schema-v1 backup hash does not match the retained backup.");
            }
        }
        else
        {
            PublishEvidenceFile(hashPath, Encoding.ASCII.GetBytes(expectedHash + "\n"));
        }

        using (var verify = OpenReadOnlyEvidenceConnection(backupPath))
        {
            ValidateIntegrity(verify);
            if (ReadSchemaVersion(verify) != 1)
            {
                throw new OrganizationStoreCorruptException("The retained pre-migration backup is not schema version 1.");
            }

            ValidateSchemaSignature(verify, ExpectedSchemaV1);
            var sourceDigest = ComputeLogicalContentDigest(source);
            var backupDigest = ComputeLogicalContentDigest(verify);
            if (!sourceDigest.AsSpan().SequenceEqual(backupDigest))
            {
                throw new OrganizationStoreCorruptException(
                    "The retained schema-v1 backup is valid but does not match the current schema-v1 source. Refusing to reuse mismatched recovery evidence.");
            }
        }

        var bytesAfterVerification = File.ReadAllBytes(backupPath);
        if (!bytesBeforeVerification.AsSpan().SequenceEqual(bytesAfterVerification)
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(bytesAfterVerification)).ToLowerInvariant(),
                expectedHash,
                StringComparison.Ordinal))
        {
            throw new OrganizationStoreCorruptException("The retained schema-v1 backup changed while it was being verified.");
        }

        if (File.Exists(backupPath + "-wal") || File.Exists(backupPath + "-shm"))
        {
            throw new OrganizationStoreCorruptException("Verifying the retained schema-v1 backup created an unexpected SQLite sidecar.");
        }

        RestrictFileMode(backupPath);
        RestrictFileMode(hashPath);
    }

    private static byte[] ComputeLogicalContentDigest(SqliteConnection connection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        static void Append(IncrementalHash hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        var tables = new List<(string Name, string Sql)>();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText =
                "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name COLLATE BINARY";
            using var reader = schema.ExecuteReader();
            while (reader.Read())
            {
                tables.Add((reader.GetString(0), NormalizeSchemaSql(reader.GetString(1))));
            }
        }

        foreach (var (table, sql) in tables)
        {
            Append(hash, "table");
            Append(hash, table);
            Append(hash, sql);

            var columns = new List<string>();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    columns.Add(reader.GetString(1));
                }
            }

            var quotedColumns = string.Join(", ", columns.Select(column => $"\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
            using var rows = connection.CreateCommand();
            rows.CommandText = $"SELECT {quotedColumns} FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" ORDER BY {quotedColumns}";
            using var rowReader = rows.ExecuteReader();
            while (rowReader.Read())
            {
                Append(hash, "row");
                for (var index = 0; index < rowReader.FieldCount; index++)
                {
                    var value = rowReader.GetValue(index);
                    switch (value)
                    {
                        case DBNull:
                            Append(hash, "null");
                            break;
                        case byte[] blob:
                            Append(hash, "blob");
                            hash.AppendData(BitConverter.GetBytes(blob.Length));
                            hash.AppendData(blob);
                            break;
                        default:
                            Append(hash, value.GetType().FullName ?? "value");
                            Append(hash, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                            break;
                    }
                }
            }
        }

        return hash.GetHashAndReset();
    }

    private void MigrateV1ToV2(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "ALTER TABLE runtime_bindings RENAME TO runtime_bindings_v1");
        Execute(connection, transaction, RuntimeBindingsV2Statement);
        Execute(
            connection,
            transaction,
            """
            INSERT INTO runtime_bindings (
                id, employee_id, placement, container_ref, volume_ref, home_ref,
                workspace_ref, session_ref, tmux_owner_token, ownership_epoch,
                created_at, updated_at, revision)
            SELECT id, employee_id, placement, container_ref, volume_ref, home_ref,
                   workspace_ref, session_ref, tmux_owner_token, ownership_epoch,
                   created_at, updated_at, revision
            FROM runtime_bindings_v1
            """);
        Execute(connection, transaction, "DROP TABLE runtime_bindings_v1");
        Execute(connection, transaction, "UPDATE schema_version SET version = 2 WHERE version = 1");
        BeforeMigrationCommit?.Invoke();
        transaction.Commit();
    }

    private void EnsureSchemaV2Backup(SqliteConnection source) =>
        EnsureSchemaBackup(
            source,
            sourceVersion: 2,
            SchemaV2BackupFileName,
            SchemaV2BackupHashFileName,
            ExpectedSchemaV2);

    private void EnsureSchemaBackup(
        SqliteConnection source,
        int sourceVersion,
        string backupFileName,
        string hashFileName,
        IReadOnlyDictionary<(string Type, string Name), string> expectedSchema)
    {
        var directory = Path.GetDirectoryName(_databasePath) ?? ".";
        var backupPath = Path.Combine(directory, backupFileName);
        var hashPath = Path.Combine(directory, hashFileName);
        if (!File.Exists(backupPath))
        {
            var temporary = backupPath + "." + RandomNumberGenerator.GetHexString(8) + ".tmp";
            try
            {
                using (var destination = OpenConnection(temporary))
                {
                    source.BackupDatabase(destination);
                    ValidateIntegrity(destination);
                    if (ReadSchemaVersion(destination) != sourceVersion)
                    {
                        throw new OrganizationStoreCorruptException($"The pre-migration backup did not preserve schema version {sourceVersion}.");
                    }
                    ValidateSchemaSignature(destination, expectedSchema);
                    Checkpoint(destination, temporary);
                }
                ClearPoolFor(temporary);
                RemoveSidecars(temporary);
                RestrictFileMode(temporary);
                File.Move(temporary, backupPath);
            }
            finally
            {
                ClearPoolFor(temporary);
                RemoveSidecars(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        var bytesBefore = File.ReadAllBytes(backupPath);
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytesBefore)).ToLowerInvariant();
        if (File.Exists(hashPath))
        {
            if (!string.Equals(File.ReadAllText(hashPath).Trim(), expectedHash, StringComparison.Ordinal))
                throw new OrganizationStoreCorruptException($"The schema-v{sourceVersion} backup hash does not match the retained backup.");
        }
        else
        {
            PublishEvidenceFile(hashPath, Encoding.ASCII.GetBytes(expectedHash + "\n"));
        }

        using (var verify = OpenReadOnlyEvidenceConnection(backupPath))
        {
            ValidateIntegrity(verify);
            if (ReadSchemaVersion(verify) != sourceVersion)
                throw new OrganizationStoreCorruptException($"The retained pre-migration backup is not schema version {sourceVersion}.");
            ValidateSchemaSignature(verify, expectedSchema);
            if (!ComputeLogicalContentDigest(source).AsSpan().SequenceEqual(ComputeLogicalContentDigest(verify)))
                throw new OrganizationStoreCorruptException($"The retained schema-v{sourceVersion} backup does not match the current source.");
        }

        var bytesAfter = File.ReadAllBytes(backupPath);
        if (!bytesBefore.AsSpan().SequenceEqual(bytesAfter)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(bytesAfter)).ToLowerInvariant(), expectedHash, StringComparison.Ordinal))
            throw new OrganizationStoreCorruptException($"The retained schema-v{sourceVersion} backup changed while it was being verified.");
        if (File.Exists(backupPath + "-wal") || File.Exists(backupPath + "-shm"))
            throw new OrganizationStoreCorruptException($"Verifying the retained schema-v{sourceVersion} backup created an unexpected SQLite sidecar.");
        RestrictFileMode(backupPath);
        RestrictFileMode(hashPath);
    }

    private void MigrateV2ToV3(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var statement in OrientationSchemaV3Statements)
        {
            Execute(connection, transaction, statement);
        }
        SeedOrientationV3(connection, transaction);
        Execute(connection, transaction, "UPDATE schema_version SET version = 3 WHERE version = 2");
        BeforeMigrationCommit?.Invoke();
        transaction.Commit();
    }

    private void EnsureSchemaV3Backup(SqliteConnection source) =>
        EnsureSchemaBackup(source, 3, SchemaV3BackupFileName, SchemaV3BackupHashFileName, ExpectedSchemaV3);

    private void MigrateV3ToV4(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var statement in RemoteWorkerSchemaV4Statements)
        {
            Execute(connection, transaction, statement);
        }
        Execute(connection, transaction, "UPDATE schema_version SET version = 4 WHERE version = 3");
        BeforeMigrationCommit?.Invoke();
        transaction.Commit();
    }

    private void EnsureSchemaV4Backup(SqliteConnection source) =>
        EnsureSchemaBackup(source, 4, SchemaV4BackupFileName, SchemaV4BackupHashFileName, ExpectedSchemaV4);

    private void MigrateV4ToV5(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        // Rename the referencing table first. SQLite rewrites its foreign-key
        // target to the renamed obligation table, allowing both old tables to be
        // retained until all rows have been copied into the exact v5 definitions.
        Execute(connection, transaction, "ALTER TABLE worker_recovery_audit RENAME TO worker_recovery_audit_v4");
        Execute(connection, transaction, "ALTER TABLE worker_recovery_obligations RENAME TO worker_recovery_obligations_v4");
        Execute(connection, transaction, WorkerRecoveryObligationsSchemaV5Statement);
        Execute(connection, transaction, WorkerRecoveryAuditSchemaV5Statement);
        Execute(connection, transaction, "INSERT INTO worker_recovery_obligations SELECT * FROM worker_recovery_obligations_v4");
        Execute(connection, transaction, "INSERT INTO worker_recovery_audit SELECT * FROM worker_recovery_audit_v4");
        Execute(connection, transaction, "DROP TABLE worker_recovery_audit_v4");
        Execute(connection, transaction, "DROP TABLE worker_recovery_obligations_v4");
        Execute(connection, transaction, "UPDATE schema_version SET version = 5 WHERE version = 4");
        BeforeMigrationCommit?.Invoke();
        transaction.Commit();
    }

    private void EnsureSchemaV5Backup(SqliteConnection source) =>
        EnsureSchemaBackup(source, 5, SchemaV5BackupFileName, SchemaV5BackupHashFileName, ExpectedSchemaV5);

    private void MigrateV5ToV6(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "ALTER TABLE worker_recovery_audit RENAME TO worker_recovery_audit_v5");
        Execute(connection, transaction, WorkerRecoveryAuditSchemaV6Statement);
        Execute(connection, transaction, "INSERT INTO worker_recovery_audit SELECT * FROM worker_recovery_audit_v5");
        Execute(connection, transaction, "DROP TABLE worker_recovery_audit_v5");
        Execute(connection, transaction, "UPDATE schema_version SET version = 6 WHERE version = 5");
        BeforeMigrationCommit?.Invoke();
        transaction.Commit();
    }

    private void EnsureSchemaV6Backup(SqliteConnection source) =>
        EnsureSchemaBackup(source, 6, SchemaV6BackupFileName, SchemaV6BackupHashFileName, ExpectedSchemaV6);

    private void MigrateV6ToV7(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var statement in HireRequestSchemaV7Statements)
        {
            Execute(connection, transaction, statement);
        }
        Execute(connection, transaction, "UPDATE schema_version SET version = 7 WHERE version = 6");
        BeforeMigrationCommit?.Invoke();
        transaction.Commit();
    }

    private void EnsureSchemaV7Backup(SqliteConnection source) =>
        EnsureSchemaBackup(source, 7, SchemaV7BackupFileName, SchemaV7BackupHashFileName, ExpectedSchemaV7);

    private void MigrateV7ToV8(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var statement in ContainerProfileSchemaV8Statements)
        {
            Execute(connection, transaction, statement);
        }

        // Rebuild hire_requests once with the nullable profile-revision reference.
        // The referencing events table is renamed first so SQLite rewrites its
        // foreign key to the renamed source, then both are copied into the exact
        // v8 definitions; every existing hire survives with a NULL revision.
        Execute(connection, transaction, "ALTER TABLE hire_request_events RENAME TO hire_request_events_v7");
        Execute(connection, transaction, "ALTER TABLE hire_requests RENAME TO hire_requests_v7");
        Execute(connection, transaction, HireRequestsSchemaV8Statement);
        Execute(connection, transaction, HireRequestEventsSchemaV8Statement);
        Execute(connection, transaction,
            """
            INSERT INTO hire_requests (
                id, organization_id, requested_by_employee_id, requested_by_kind, idempotency_key,
                requested_display_name, purpose, department_id, role_id, placement, cpu_limit,
                memory_limit_mib, pids_limit, state, request_version_hash, approved_request_version,
                owner_approval, container_profile_revision_id, revision, created_at, updated_at)
            SELECT id, organization_id, requested_by_employee_id, requested_by_kind, idempotency_key,
                   requested_display_name, purpose, department_id, role_id, placement, cpu_limit,
                   memory_limit_mib, pids_limit, state, request_version_hash, approved_request_version,
                   owner_approval, NULL, revision, created_at, updated_at
            FROM hire_requests_v7
            """);
        Execute(connection, transaction, "INSERT INTO hire_request_events SELECT * FROM hire_request_events_v7");
        Execute(connection, transaction, "DROP TABLE hire_request_events_v7");
        Execute(connection, transaction, "DROP TABLE hire_requests_v7");
        foreach (var statement in ContainerProfileImmutabilityV8Statements)
        {
            Execute(connection, transaction, statement);
        }
        SeedContainerProfilesV8(connection, transaction);
        Execute(connection, transaction, "UPDATE schema_version SET version = 8 WHERE version = 7");
        BeforeMigrationCommit?.Invoke();
        transaction.Commit();
    }

    private void EnsureSchemaV8Backup(SqliteConnection source) =>
        EnsureSchemaBackup(source, 8, SchemaV8BackupFileName, SchemaV8BackupHashFileName, ExpectedSchemaV8);

    private void MigrateV8ToV9(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var statement in ProfileBuildSchemaV9Statements)
        {
            Execute(connection, transaction, statement);
        }
        Execute(connection, transaction, "UPDATE schema_version SET version = 9 WHERE version = 8");
        BeforeMigrationCommit?.Invoke();
        transaction.Commit();
    }

    private void EnsureSchemaV9Backup(SqliteConnection source) =>
        EnsureSchemaBackup(source, 9, SchemaV9BackupFileName, SchemaV9BackupHashFileName, ExpectedSchemaV9);

    private void MigrateV9ToV10(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        // Rebuild hire_requests once more to add the bounded nullable
        // status_detail. The referencing events table is renamed first so SQLite
        // rewrites its foreign key to the renamed source, then both are copied
        // into the exact v10 definitions; every existing hire survives with a
        // NULL detail. The approval tables are created afterwards because they
        // reference the rebuilt hire_requests.
        Execute(connection, transaction, "ALTER TABLE hire_request_events RENAME TO hire_request_events_v9");
        Execute(connection, transaction, "ALTER TABLE hire_requests RENAME TO hire_requests_v9");
        foreach (var statement in HireRequestSchemaV10Statements)
        {
            Execute(connection, transaction, statement);
        }
        Execute(connection, transaction,
            """
            INSERT INTO hire_requests (
                id, organization_id, requested_by_employee_id, requested_by_kind, idempotency_key,
                requested_display_name, purpose, department_id, role_id, placement, cpu_limit,
                memory_limit_mib, pids_limit, state, request_version_hash, approved_request_version,
                owner_approval, container_profile_revision_id, status_detail, revision, created_at, updated_at)
            SELECT id, organization_id, requested_by_employee_id, requested_by_kind, idempotency_key,
                   requested_display_name, purpose, department_id, role_id, placement, cpu_limit,
                   memory_limit_mib, pids_limit, state, request_version_hash, approved_request_version,
                   owner_approval, container_profile_revision_id, NULL, revision, created_at, updated_at
            FROM hire_requests_v9
            """);
        Execute(connection, transaction, "INSERT INTO hire_request_events SELECT * FROM hire_request_events_v9");
        Execute(connection, transaction, "DROP TABLE hire_request_events_v9");
        Execute(connection, transaction, "DROP TABLE hire_requests_v9");
        foreach (var statement in HireApprovalSchemaV10Statements)
        {
            Execute(connection, transaction, statement);
        }
        Execute(connection, transaction, "UPDATE schema_version SET version = 10 WHERE version = 9");
        BeforeMigrationCommit?.Invoke();
        transaction.Commit();
    }

    private void EnsureSchemaV10Backup(SqliteConnection source) =>
        EnsureSchemaBackup(source, 10, SchemaV10BackupFileName, SchemaV10BackupHashFileName, ExpectedSchemaV10);

    private void EnsureSchemaV11Backup(SqliteConnection source) =>
        EnsureSchemaBackup(source, 11, SchemaV11BackupFileName, SchemaV11BackupHashFileName, ExpectedSchemaV11);

    private void EnsureSchemaV12Backup(SqliteConnection source) =>
        EnsureSchemaBackup(source, 12, SchemaV12BackupFileName, SchemaV12BackupHashFileName, ExpectedSchemaV12);

    /// <summary>
    /// Adds the durable employee-rebuild operation record. The migration is
    /// additive: no existing table is rebuilt, so every profile build, approval,
    /// enrollment and host row survives byte-for-byte. The table, its partial
    /// active-worker index and its immutability triggers are created in one
    /// transaction with the version bump.
    /// </summary>
    private void MigrateV11ToV12(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var statement in RebuildSchemaV12Statements)
        {
            Execute(connection, transaction, statement);
        }
        Execute(connection, transaction, "UPDATE schema_version SET version = 12 WHERE version = 11");
        BeforeMigrationCommit?.Invoke();
        transaction.Commit();
    }

    /// <summary>
    /// Rebuilds <c>worker_tasks</c> with the bounded v13 specification and model
    /// report and adds the host verification table. <c>worker_requests</c>
    /// references <c>worker_tasks(id)</c>, so the replacement is built under a
    /// staging name, the existing rows are copied with a deterministic legacy
    /// specification derived from each row's description hash, the old table is
    /// dropped and the staging table is renamed into place while foreign keys are
    /// briefly disabled. Every child reference keeps naming <c>worker_tasks</c>
    /// and resolves to the rebuilt table; <c>PRAGMA foreign_key_check</c> proves
    /// it before the migration is accepted.
    /// </summary>
    private void MigrateV12ToV13(SqliteConnection connection)
    {
        ExecutePragma(connection, "foreign_keys = OFF");
        using var transaction = connection.BeginTransaction();
        try
        {
            var staging = WorkerTasksSchemaV13Statement.Replace(
                "CREATE TABLE worker_tasks (",
                "CREATE TABLE worker_tasks_v13 (",
                StringComparison.Ordinal);
            Execute(connection, transaction, staging);

            // Copy every existing task with a deterministic, bounded legacy
            // specification. The spec is derived from the description hash so the
            // migration is reproducible; no migrated row is left with an empty or
            // invented free-form prompt. The old table has no spec/report columns,
            // so each row is read and re-inserted explicitly.
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    "SELECT id, employee_id, runtime_binding_id, worker_id, description_hash, state, created_at, updated_at, revision FROM worker_tasks";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    var (json, hash) = LegacyTaskSpecForMigration(reader.GetString(4));
                    Execute(connection, transaction,
                        """
                        INSERT INTO worker_tasks_v13 (
                            id, employee_id, runtime_binding_id, worker_id, description_hash,
                            task_spec_json, task_spec_hash, model_report_json, model_report_hash,
                            model_reported_at, failure_detail, state, created_at, updated_at, revision)
                        VALUES ($id, $employee, $binding, $worker, $description, $json, $hash,
                            NULL, NULL, NULL, NULL, $state, $created, $updated, $revision)
                        """,
                        ("$id", reader.GetString(0)), ("$employee", reader.GetString(1)),
                        ("$binding", reader.GetString(2)), ("$worker", reader.GetString(3)),
                        ("$description", reader.GetString(4)), ("$json", json), ("$hash", hash),
                        ("$state", reader.GetString(5)), ("$created", reader.GetString(6)),
                        ("$updated", reader.GetString(7)), ("$revision", reader.GetInt32(8)));
                }
            }

            Execute(connection, transaction, "DROP TABLE worker_tasks");
            Execute(connection, transaction, "ALTER TABLE worker_tasks_v13 RENAME TO worker_tasks");
            foreach (var statement in WorkerTaskVerificationSchemaV13Statements)
            {
                Execute(connection, transaction, statement);
            }
            Execute(connection, transaction, "UPDATE schema_version SET version = 13 WHERE version = 12");
            BeforeMigrationCommit?.Invoke();
            transaction.Commit();
        }
        finally
        {
            ExecutePragma(connection, "foreign_keys = ON");
        }
    }

    /// <summary>The v11 execution_hosts definition created under a staging name during migration.</summary>
    private static string ExecutionHostsV11StagingStatement =>
        ExecutionHostsSchemaV11Statement.Replace("CREATE TABLE execution_hosts (", "CREATE TABLE execution_hosts_v11 (", StringComparison.Ordinal);

    /// <summary>
    /// Rebuilds <c>execution_hosts</c> to admit the controller-local Docker
    /// transport and seeds its reserved row. Seven tables reference
    /// <c>execution_hosts(id)</c>, and renaming the old table would make SQLite
    /// rewrite those references to the temporary name. The replacement is
    /// therefore built under a staging name, the rows are copied, the old table is
    /// dropped, and the staging table is renamed into <c>execution_hosts</c>, so
    /// every child foreign key keeps naming <c>execution_hosts</c> and resolves to
    /// the new table. Foreign keys are switched off only for that swap and
    /// restored before <c>PRAGMA foreign_key_check</c> proves every reference.
    /// </summary>
    private void MigrateV10ToV11(SqliteConnection connection)
    {
        // Seven tables reference execution_hosts(id). Renaming the old table makes
        // SQLite rewrite those foreign keys to the temporary name, so instead the
        // replacement is built under a temporary name, the old table is dropped and
        // the replacement is renamed into "execution_hosts". Every reference still
        // names "execution_hosts" throughout and resolves to the new table, which
        // foreign_key_check proves after commit. Foreign keys are disabled only for
        // the switch (dropping the referenced old table would otherwise violate the
        // immediate constraints of its children) and restored before the check.
        ExecutePragma(connection, "foreign_keys = OFF");
        using var transaction = connection.BeginTransaction();
        try
        {
            Execute(connection, transaction, ExecutionHostsV11StagingStatement);
            Execute(connection, transaction,
                """
                INSERT INTO execution_hosts_v11 (
                    id, slug, display_name, transport_kind, endpoint_host, endpoint_port, endpoint_user,
                    known_hosts_path, host_key_algorithm, host_key_fingerprint, known_hosts_hash, docker_version,
                    docker_api_version, os, architecture, storage_driver, backing_filesystem, shared_storage,
                    free_bytes, memory_bytes, cpu_count, limits_supported, image_platform, capability_status,
                    last_probe_utc, enabled, enrolled, status, created_at, updated_at, revision)
                SELECT id, slug, display_name, transport_kind, endpoint_host, endpoint_port, endpoint_user,
                       known_hosts_path, host_key_algorithm, host_key_fingerprint, known_hosts_hash, docker_version,
                       docker_api_version, os, architecture, storage_driver, backing_filesystem, shared_storage,
                       free_bytes, memory_bytes, cpu_count, limits_supported, image_platform, capability_status,
                       last_probe_utc, enabled, enrolled, status, created_at, updated_at, revision
                FROM execution_hosts
                """);
            Execute(connection, transaction, "DROP TABLE execution_hosts");
            Execute(connection, transaction, "ALTER TABLE execution_hosts_v11 RENAME TO execution_hosts");
            SeedLocalDockerExecutionHostV11(connection, transaction);
            Execute(connection, transaction, "UPDATE schema_version SET version = 11 WHERE version = 10");
            BeforeMigrationCommit?.Invoke();
            transaction.Commit();
        }
        finally
        {
            ExecutePragma(connection, "foreign_keys = ON");
        }
    }

    /// <summary>
    /// Seeds the single reserved controller-local Docker execution host. The row
    /// is structural: it is the only execution target a managed hire may use, so a
    /// store missing it is partial. Re-running is idempotent and never overwrites
    /// an existing probed row.
    /// </summary>
    private static void SeedLocalDockerExecutionHostV11(SqliteConnection connection, SqliteTransaction transaction)
    {
        var now = Timestamp();
        Execute(connection, transaction,
            """
            INSERT INTO execution_hosts (
                id, slug, display_name, transport_kind, endpoint_host, endpoint_port, endpoint_user,
                known_hosts_path, os, capability_status, enabled, enrolled, status, created_at, updated_at, revision)
            VALUES ($id, $slug, $name, 'local-docker', NULL, NULL, NULL, NULL, 'linux', 'unprobed', 1, 0, 'registered', $now, $now, 1)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", ExecutionHosts.LocalDockerId),
            ("$slug", ExecutionHosts.LocalDockerSlug),
            ("$name", ExecutionHosts.LocalDockerDisplayName),
            ("$now", now));
    }

    private void ValidateExistingStore(SqliteConnection connection)
    {
        ValidateIntegrity(connection);
        var version = ReadSchemaVersion(connection);
        try
        {
            ValidateSchemaSignature(connection, ExpectedSchema);
        }
        catch (OrganizationStoreCorruptException exception) when (version == CurrentSchemaVersion)
        {
            throw new OrganizationStoreCorruptException(
                $"Unsupported schema {CurrentSchemaVersion} signature. Restore a current authoritative schema-v{CurrentSchemaVersion} backup or source. No schema-v{CurrentSchemaVersion} backup is created automatically; the retained schema-v12 file is pre-migration evidence only and restoring it would lose task specifications, model reports and host verifications recorded after migration. {exception.Message}",
                exception);
        }
        if (version != CurrentSchemaVersion)
        {
            throw new OrganizationStoreCorruptException(
                $"The control database '{_databasePath}' records schema version {version}; this build requires {CurrentSchemaVersion}. Refusing to start on an unknown or newer store.");
        }

        int organizationCount;
        using (var organizations = connection.CreateCommand())
        {
            organizations.CommandText = "SELECT COUNT(*) FROM organizations;";
            organizationCount = Convert.ToInt32(organizations.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (organizationCount != 1)
        {
            throw new OrganizationStoreCorruptException(
                $"The control database '{_databasePath}' holds {organizationCount} organizations; exactly one is expected. An initialized store missing its organization is corrupt, not an invitation to reseed.");
        }

        // The three seed departments are structural: an initialized store
        // missing one is partial and must fail closed rather than be repaired.
        using (var departments = connection.CreateCommand())
        {
            departments.CommandText =
                """
                SELECT COUNT(DISTINCT slug) FROM departments
                WHERE organization_id = (SELECT id FROM organizations LIMIT 1)
                  AND slug IN ($operations, $development, $qa)
                """;
            departments.Parameters.AddWithValue("$operations", OrganizationSeed.OperationsSlug);
            departments.Parameters.AddWithValue("$development", OrganizationSeed.DevelopmentSlug);
            departments.Parameters.AddWithValue("$qa", OrganizationSeed.QaSlug);
            var departmentCount = Convert.ToInt32(departments.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (departmentCount != 3)
            {
                throw new OrganizationStoreCorruptException(
                    $"The control database '{_databasePath}' holds {departmentCount} of the required Operations/Development/QA departments. It is partial; refusing to reseed or reset it.");
            }
        }
    }

    /// <summary>
    /// Proves the store carries exactly the load-bearing schema this build
    /// writes: the same tables and explicit indexes with matching normalized
    /// definitions. Column names alone are not enough because a rebuilt table
    /// can keep every column while dropping a PRIMARY KEY, NOT NULL, UNIQUE,
    /// CHECK or FOREIGN KEY; the full definition comparison catches all of
    /// them, including the partial active-session index.
    /// </summary>
    private void ValidateSchemaSignature(
        SqliteConnection connection,
        IReadOnlyDictionary<(string Type, string Name), string> expectedSchema)
    {
        var actual = new Dictionary<(string Type, string Name), string>();
        var unexpected = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                // A table/index always carries its defining SQL. A NULL SQL is
                // an internal auto-index (already excluded by name) or an
                // object this build did not create; either way it is not ours.
                if (reader.IsDBNull(2))
                {
                    unexpected.Add($"{type} {name} (no definition)");
                    continue;
                }

                actual[(type, name)] = NormalizeSchemaSql(reader.GetString(2));
            }
        }

        var differences = new List<string>();
        foreach (var (key, expectedSql) in expectedSchema)
        {
            if (!actual.TryGetValue(key, out var actualSql))
            {
                differences.Add($"missing {key.Type} {key.Name}");
            }
            else if (!string.Equals(expectedSql, actualSql, StringComparison.Ordinal))
            {
                differences.Add($"changed {key.Type} {key.Name}");
            }
        }

        foreach (var key in actual.Keys)
        {
            if (!expectedSchema.ContainsKey(key))
            {
                differences.Add($"unexpected {key.Type} {key.Name}");
            }
        }

        differences.AddRange(unexpected);
        if (differences.Count > 0)
        {
            throw new OrganizationStoreCorruptException(
                $"The control database '{_databasePath}' has an unexpected table shape. The load-bearing schema does not match this build: {string.Join("; ", differences)}. Refusing to start on an unexpected store.");
        }
    }

    private OrganizationRuntimeIdentity CreateAndSeed(
        SqliteConnection connection,
        string organizationNameFromConfiguration,
        string adoptionAuthorizationReference,
        PersistedRuntimeState? adoptionSource,
        string adoptionSourceDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adoptionAuthorizationReference);

        var now = Timestamp();
        var organizationId = string.IsNullOrWhiteSpace(adoptionSource?.OrganizationId)
            ? OrganizationIds.NewOrganizationId()
            : adoptionSource!.OrganizationId;
        var organizationName = string.IsNullOrWhiteSpace(adoptionSource?.OrganizationName)
            ? organizationNameFromConfiguration
            : adoptionSource!.OrganizationName;

        var operationsId = OrganizationIds.NewDepartmentId();
        var developmentId = OrganizationIds.NewDepartmentId();
        var qaId = OrganizationIds.NewDepartmentId();
        var roleId = OrganizationIds.NewRoleId();
        var employeeId = OrganizationIds.NewEmployeeId();
        var bindingId = OrganizationIds.NewRuntimeBindingId();
        var auditId = OrganizationIds.NewAuditId();
        var tmuxOwnerToken = string.IsNullOrWhiteSpace(adoptionSource?.TmuxOwnerToken)
            ? OrganizationIds.NewOwnerToken()
            : adoptionSource!.TmuxOwnerToken;
        var sessionId = string.IsNullOrWhiteSpace(adoptionSource?.SessionId) ? null : adoptionSource!.SessionId;
        var sessionTitle = string.IsNullOrWhiteSpace(adoptionSource?.SessionTitle) ? null : adoptionSource!.SessionTitle;
        string? sessionRowId = null;

        using var transaction = connection.BeginTransaction();

        foreach (var statement in SchemaV13Statements)
        {
            Execute(connection, transaction, statement);
        }

        Execute(
            connection,
            transaction,
            "INSERT INTO schema_version (version) VALUES ($version)",
            ("$version", CurrentSchemaVersion));

        SeedLocalDockerExecutionHostV11(connection, transaction);

        Execute(
            connection,
            transaction,
            """
            INSERT INTO organizations (id, slug, display_name, description, basic_instructions, created_at, updated_at, revision)
            VALUES ($id, $slug, $name, $description, $instructions, $now, $now, 1)
            """,
            ("$id", organizationId),
            ("$slug", OrganizationSeed.OrganizationSlug),
            ("$name", organizationName),
            ("$description", OrganizationSeed.OrganizationDescription),
            ("$instructions", OrganizationSeed.OrganizationInstructions),
            ("$now", now));

        foreach (var (id, slug, display) in new[]
        {
            (operationsId, OrganizationSeed.OperationsSlug, OrganizationSeed.OperationsDisplayName),
            (developmentId, OrganizationSeed.DevelopmentSlug, OrganizationSeed.DevelopmentDisplayName),
            (qaId, OrganizationSeed.QaSlug, OrganizationSeed.QaDisplayName),
        })
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO departments (id, organization_id, slug, display_name, created_at, updated_at, revision)
                VALUES ($id, $organization, $slug, $display, $now, $now, 1)
                """,
                ("$id", id),
                ("$organization", organizationId),
                ("$slug", slug),
                ("$display", display),
                ("$now", now));
        }

        Execute(
            connection,
            transaction,
            """
            INSERT INTO roles (id, department_id, slug, display_name, instruction_profile, permission_profile, created_at, updated_at, revision)
            VALUES ($id, $department, $slug, $display, $instruction, $permission, $now, $now, 1)
            """,
            ("$id", roleId),
            ("$department", operationsId),
            ("$slug", OrganizationSeed.OperationsItRoleSlug),
            ("$display", OrganizationSeed.OperationsItRoleDisplayName),
            ("$instruction", OrganizationSeed.OperationsItRoleInstructionProfile),
            ("$permission", OrganizationSeed.OperationsItRolePermissionProfile),
            ("$now", now));

        Execute(
            connection,
            transaction,
            """
            INSERT INTO employees (id, organization_id, department_id, role_id, slug, display_name, purpose, instructions, rules, restrictions, created_at, updated_at, revision)
            VALUES ($id, $organization, $department, $role, $slug, $display, $purpose, $instructions, $rules, $restrictions, $now, $now, 1)
            """,
            ("$id", employeeId),
            ("$organization", organizationId),
            ("$department", operationsId),
            ("$role", roleId),
            ("$slug", OrganizationSeed.AdoptedEmployeeSlug),
            ("$display", OrganizationSeed.AdoptedEmployeeDisplayName),
            ("$purpose", OrganizationSeed.AdoptedEmployeePurpose),
            ("$instructions", OrganizationSeed.AdoptedEmployeeInstructions),
            ("$rules", OrganizationSeed.AdoptedEmployeeRules),
            ("$restrictions", OrganizationSeed.AdoptedEmployeeRestrictions),
            ("$now", now));

        if (sessionId is not null)
        {
            sessionRowId = OrganizationIds.NewSessionId();
            Execute(
                connection,
                transaction,
                """
                INSERT INTO acp_sessions (id, employee_id, native_session_id, title, status, created_at, updated_at)
                VALUES ($id, $employee, $native, $title, 'adopted', $now, $now)
                """,
                ("$id", sessionRowId),
                ("$employee", employeeId),
                ("$native", sessionId),
                ("$title", sessionTitle),
                ("$now", now));
        }

        Execute(
            connection,
            transaction,
            """
            INSERT INTO runtime_bindings (id, employee_id, placement, container_ref, volume_ref, home_ref, workspace_ref, session_ref, tmux_owner_token, created_at, updated_at, revision)
            VALUES ($id, $employee, $placement, 'agentcontrol-v2-control', NULL, '/data/home', '/data/workspace', $session, $token, $now, $now, 1)
            """,
            ("$id", bindingId),
            ("$employee", employeeId),
            ("$placement", RuntimePlacements.InternalSharedContainer),
            ("$session", sessionRowId),
            ("$token", tmuxOwnerToken),
            ("$now", now));

        var source = string.IsNullOrWhiteSpace(adoptionSourceDescription)
            ? "seed://fresh-organization"
            : adoptionSourceDescription;
        var notes = adoptionSource is null
            ? "Fresh organization seed: exactly one combined Operations/IT employee bound to the internal shared control runtime."
            : "Adopted the existing persisted control runtime identity without forging a historical hire transition.";
        SeedOrientationV3(connection, transaction);
        SeedContainerProfilesV8(connection, transaction);

        Execute(
            connection,
            transaction,
            """
            INSERT INTO adoption_audit (id, organization_id, employee_id, source, authorization_reference, adopted_at, notes)
            VALUES ($id, $organization, $employee, $source, $authorization, $now, $notes)
            """,
            ("$id", auditId),
            ("$organization", organizationId),
            ("$employee", employeeId),
            ("$source", source),
            ("$authorization", adoptionAuthorizationReference),
            ("$now", now),
            ("$notes", notes));

        transaction.Commit();

        _logger?.LogInformation(
            "Created the authoritative control store '{Path}' with {Departments} departments and one adopted Operations/IT employee.",
            _databasePath,
            3);

        return new OrganizationRuntimeIdentity(
            organizationId,
            OrganizationSeed.OrganizationSlug,
            organizationName,
            employeeId,
            OrganizationSeed.AdoptedEmployeeDisplayName,
            bindingId,
            tmuxOwnerToken,
            sessionId,
            sessionTitle,
            Created: true,
            DisplayNameDiffersFromConfiguration: !string.Equals(
                organizationName,
                organizationNameFromConfiguration,
                StringComparison.Ordinal));
    }

    private OrganizationRuntimeIdentity ReadIdentity(
        SqliteConnection connection,
        bool created,
        string organizationNameFromConfiguration)
    {
        var organization = ReadSingleOrganization(connection);

        string? employeeId = null;
        string? employeeName = null;
        string? bindingId = null;
        string? token = null;
        string? sessionRef = null;
        using (var binding = connection.CreateCommand())
        {
            binding.CommandText =
                """
                SELECT e.id, e.display_name, b.id, b.tmux_owner_token, b.session_ref
                FROM runtime_bindings b
                JOIN employees e ON e.id = b.employee_id
                WHERE b.placement = $placement
                ORDER BY e.created_at
                LIMIT 2
                """;
            binding.Parameters.AddWithValue("$placement", RuntimePlacements.InternalSharedContainer);
            using var reader = binding.ExecuteReader();
            var rows = 0;
            while (reader.Read())
            {
                rows++;
                if (rows > 1)
                {
                    break;
                }

                employeeId = reader.GetString(0);
                employeeName = reader.GetString(1);
                bindingId = reader.GetString(2);
                token = reader.GetString(3);
                sessionRef = reader.IsDBNull(4) ? null : reader.GetString(4);
            }

            if (rows != 1 || employeeId is null || bindingId is null || token is null)
            {
                throw new OrganizationStoreCorruptException(
                    $"The control database '{_databasePath}' does not hold exactly one internal shared runtime binding. Refusing to start on an unexpected organization shape.");
            }
        }

        string? sessionId = null;
        string? sessionTitle = null;
        if (sessionRef is not null)
        {
            using var session = connection.CreateCommand();
            session.CommandText = "SELECT native_session_id, title FROM acp_sessions WHERE id = $id";
            session.Parameters.AddWithValue("$id", sessionRef);
            using var reader = session.ExecuteReader();
            if (reader.Read())
            {
                sessionId = reader.GetString(0);
                sessionTitle = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        return new OrganizationRuntimeIdentity(
            organization.Id,
            organization.Slug,
            organization.DisplayName,
            employeeId,
            employeeName!,
            bindingId,
            token,
            sessionId,
            sessionTitle,
            created,
            DisplayNameDiffersFromConfiguration: !string.Equals(
                organization.DisplayName,
                organizationNameFromConfiguration,
                StringComparison.Ordinal));
    }

    private (string Id, string Slug, string DisplayName, string Description, string BasicInstructions, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
        ReadSingleOrganization(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, slug, display_name, description, basic_instructions, revision, created_at, updated_at FROM organizations ORDER BY created_at LIMIT 2;";
        using var reader = command.ExecuteReader();
        var rows = 0;
        string? id = null;
        string? slug = null;
        string? displayName = null;
        string? description = null;
        string? basicInstructions = null;
        var revision = 0;
        var createdAt = DateTimeOffset.MinValue;
        var updatedAt = DateTimeOffset.MinValue;
        while (reader.Read())
        {
            rows++;
            if (rows > 1)
            {
                break;
            }

            id = reader.GetString(0);
            slug = reader.GetString(1);
            displayName = reader.GetString(2);
            description = reader.GetString(3);
            basicInstructions = reader.GetString(4);
            revision = reader.GetInt32(5);
            createdAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            updatedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (rows != 1 || id is null || slug is null || displayName is null || description is null || basicInstructions is null)
        {
            throw new OrganizationStoreCorruptException(
                $"The control database '{_databasePath}' does not hold exactly one organization. Refusing to start on an unexpected store shape.");
        }

        return (id, slug, displayName, description, basicInstructions, revision, createdAt, updatedAt);
    }

    /// <summary>
    /// Extracts the authoritative body of the active department orientation
    /// fragment. A department without a persisted fragment is reported as null
    /// by the caller; a fragment missing the expected heading is returned as
    /// persisted rather than invented.
    /// </summary>
    private static string ReadDepartmentStandingInstructions(string fragmentContent)
    {
        const string heading = "# Department";
        var normalized = fragmentContent
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        var lines = normalized.Split('\n');
        var headingLine = Array.FindIndex(lines, line => string.Equals(line, heading, StringComparison.Ordinal));
        return headingLine < 0
            ? normalized
            : string.Join('\n', lines[(headingLine + 1)..]).Trim();
    }

    private static string ReadRoleStandingInstructions(string fragmentContent)
    {
        const string marker = "Standing instructions:\n";
        const string terminator = "\n\nOperating behavior:";
        var start = fragmentContent.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new OrganizationStoreCorruptException(
                "The active role fragment does not contain authoritative standing instructions.");
        }

        start += marker.Length;
        var end = fragmentContent.IndexOf(terminator, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new OrganizationStoreCorruptException(
                "The active role fragment does not delimit authoritative standing instructions.");
        }

        return fragmentContent[start..end].Trim();
    }

    private OrganizationOverview BuildOverview(
        SqliteConnection connection,
        (string Id, string Slug, string DisplayName, string Description, string BasicInstructions, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt) organization)
    {
        var departments = new List<DepartmentSummary>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT d.id, d.slug, d.display_name,
                       (SELECT COUNT(*) FROM employees e WHERE e.department_id = d.id),
                       d.revision,
                       (SELECT f.content FROM orientation_fragments f
                        WHERE f.layer = 'department' AND f.scope_id = d.id AND f.active = 1)
                FROM departments d
                WHERE d.organization_id = $organization
                ORDER BY d.slug
                """;
            command.Parameters.AddWithValue("$organization", organization.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                departments.Add(new DepartmentSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : ReadDepartmentStandingInstructions(reader.GetString(5))));
            }
        }

        var roles = new List<RoleSummary>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT r.id, r.department_id, r.slug, r.display_name, r.instruction_profile,
                       r.permission_profile, f.content, f.revision
                FROM roles r
                JOIN orientation_fragments f ON f.layer = 'role' AND f.scope_id = r.id AND f.active = 1
                JOIN departments d ON d.id = r.department_id
                WHERE d.organization_id = $organization
                ORDER BY r.slug
                """;
            command.Parameters.AddWithValue("$organization", organization.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                roles.Add(new RoleSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    ReadRoleStandingInstructions(reader.GetString(6)),
                    reader.GetInt32(7)));
            }
        }

        var employees = new List<EmployeeSummary>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT e.id, e.slug, e.display_name, e.purpose, e.instructions, e.rules, e.restrictions, e.organization_id,
                        d.id, d.slug, d.display_name,
                       r.id, r.slug, r.display_name,
                       b.id, b.placement, s.id, s.native_session_id, s.title, e.revision
                FROM employees e
                JOIN departments d ON d.id = e.department_id
                JOIN roles r ON r.id = e.role_id
                JOIN runtime_bindings b ON b.employee_id = e.id
                LEFT JOIN acp_sessions s ON s.id = b.session_ref
                WHERE e.organization_id = $organization
                ORDER BY e.created_at
                """;
            command.Parameters.AddWithValue("$organization", organization.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                employees.Add(new EmployeeSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.GetString(13),
                    reader.GetString(14),
                    reader.GetString(15),
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    reader.GetInt32(19)));
            }
        }

        for (var index = 0; index < employees.Count; index++)
        {
            var employee = employees[index];
            employees[index] = employee with
            {
                Orientation = TryGetOrientationStatus(connection, employee.Id),
            };
        }

        var audit = new List<AdoptionAuditSummary>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, organization_id, employee_id, source, authorization_reference, adopted_at
                FROM adoption_audit
                WHERE organization_id = $organization
                ORDER BY adopted_at
                """;
            command.Parameters.AddWithValue("$organization", organization.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                audit.Add(new AdoptionAuditSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }

        return new OrganizationOverview(
            organization.Id,
            organization.Slug,
            organization.DisplayName,
            organization.Description,
            organization.BasicInstructions,
            organization.Revision,
            organization.CreatedAt,
            organization.UpdatedAt,
            departments,
            roles,
            employees,
            audit);
    }

    /// <summary>
    /// Validates a display name for a host-owned rename. The store is the single
    /// authority, so the rule lives here rather than only at the API edge.
    /// </summary>
    public static string ValidateDisplayName(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var trimmed = displayName.Trim();
        if (trimmed.Length == 0)
        {
            throw new OrganizationValidationException("A display name is required.");
        }

        if (trimmed.Length > MaxDisplayNameLength)
        {
            throw new OrganizationValidationException(
                $"A display name must be at most {MaxDisplayNameLength} characters.");
        }

        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                throw new OrganizationValidationException("A display name must not contain control characters.");
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Translates raw storage faults from a read or update into the store's own
    /// exception contract so callers and the HTTP endpoints can answer a single
    /// 503 ProblemDetails instead of a leaked 500. Store-defined exceptions
    /// (concurrency, not-found, validation, corruption) pass through unchanged;
    /// the wrapped message is generic and never re-exposes the underlying
    /// database or path detail to a client.
    /// </summary>
    private static T TranslateStoreFaults<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (OrganizationStoreException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            throw new OrganizationStoreException(
                "The authoritative organization store could not be read or updated.", exception);
        }
    }

    private static void TranslateStoreFaults(Action operation)
    {
        TranslateStoreFaults(() =>
        {
            operation();
            return true;
        });
    }

    private static bool IsStorageFault(Exception exception) =>
        exception is SqliteException
            or InvalidOperationException
            or ObjectDisposedException
            or IOException
            or FormatException
            or OverflowException
            or NotSupportedException;

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    private static string Timestamp() =>
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static void RestrictFileMode(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        // Controller-only: no group or other access, matching the 0700 store directory.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
