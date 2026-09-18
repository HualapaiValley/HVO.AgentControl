using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace HVO.AgentControl.Organization;

public sealed partial class OrganizationStore
{
    public const int MinimumHireCpuLimit = 1;
    public const int MaximumHireCpuLimit = 64;
    public const int MinimumHireMemoryMiB = 256;
    public const int MaximumHireMemoryMiB = 131072;
    public const int MinimumHirePidsLimit = 16;
    public const int MaximumHirePidsLimit = 4096;
    public const int MaximumHirePurposeLength = 2048;
    public const int MaximumIdempotencyKeyLength = 128;

    private static string[] HireRequestSchemaV7Statements =>
    [
        """
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
        )
        """,
        """
        CREATE TABLE hire_request_events (
            id TEXT PRIMARY KEY,
            hire_request_id TEXT NOT NULL REFERENCES hire_requests(id) ON DELETE RESTRICT,
            state TEXT NOT NULL CHECK (state IN ('Requested', 'Approved', 'Provisioning', 'Orienting', 'Ready', 'Rejected', 'Failed', 'Interrupted', 'Uncertain')),
            revision INTEGER NOT NULL,
            detail_hash TEXT NOT NULL CHECK (length(detail_hash) = 71 AND substr(detail_hash, 1, 7) = 'sha256:'),
            created_at TEXT NOT NULL,
            UNIQUE (hire_request_id, revision)
        )
        """,
    ];

    /// <summary>
    /// Schema v8 rebuilds <c>hire_requests</c> with a nullable reference to the
    /// immutable profile revision an approval will freeze (#260 makes it required
    /// at approval time). The events table is recreated unchanged so its foreign
    /// key targets the rebuilt table. The v7 statements above stay frozen for
    /// exact-signature migration.
    /// </summary>
    private static string HireRequestsSchemaV8Statement =>
        """
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
            container_profile_revision_id TEXT REFERENCES container_profile_revisions(id) ON DELETE RESTRICT,
            revision INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (department_id, organization_id) REFERENCES departments(id, organization_id) ON DELETE RESTRICT,
            FOREIGN KEY (role_id, department_id) REFERENCES roles(id, department_id) ON DELETE RESTRICT
        )
        """;

    private static string HireRequestEventsSchemaV8Statement => HireRequestSchemaV7Statements[1];

    private static string[] HireRequestSchemaV8Statements => [HireRequestsSchemaV8Statement, HireRequestEventsSchemaV8Statement];

    public IReadOnlyList<HireRequestSummary> ListHireRequests()
    {
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadHireRequests(connection, null);
            }
        });
    }

    public HireRequestSummary? GetHireRequest(string id)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.HireRequestPrefix)) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadHireRequests(connection, id).SingleOrDefault();
            }
        });
    }

    public HireRequestSummary CreateHireRequest(HireRequestCreate request, string? headerIdempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        var idempotencyKey = ValidateIdempotencyKey(headerIdempotencyKey, request.IdempotencyKey);
        var displayName = ValidateDisplayName(request.RequestedDisplayName ?? string.Empty);
        var purpose = ValidateBoundedText(request.Purpose, "A purpose is required.", MaximumHirePurposeLength);
        var departmentId = ValidateStableReference(request.DepartmentId, OrganizationIds.DepartmentPrefix, "department");
        var roleId = ValidateStableReference(request.RoleId, OrganizationIds.RolePrefix, "role");
        var placement = request.Placement is RuntimePlacements.InternalSharedContainer or RuntimePlacements.DeveloperContainer
            ? request.Placement
            : throw new OrganizationValidationException("Placement must be InternalSharedContainer or DeveloperContainer.");
        ValidateResources(request.CpuLimit, request.MemoryLimitMiB, request.PidsLimit);
        var requestHash = HashHireRequest(displayName, purpose, departmentId, roleId, placement, request.CpuLimit, request.MemoryLimitMiB, request.PidsLimit);

        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var existing = ReadHireRequestByKey(connection, transaction, idempotencyKey);
                if (existing is not null)
                {
                    if (!string.Equals(existing.RequestVersionHash, requestHash, StringComparison.Ordinal))
                        throw new OrganizationConcurrencyException("The idempotency key is already bound to a different hire request payload.");
                    transaction.Commit();
                    return existing;
                }

                var organizationId = ReadSingleOrganization(connection).Id;
                EnsureHireRelationships(connection, transaction, organizationId, departmentId, roleId);
                var id = OrganizationIds.NewHireRequestId();
                var now = Timestamp();
                Execute(connection, transaction,
                    """
                    INSERT INTO hire_requests (
                        id, organization_id, requested_by_employee_id, requested_by_kind, idempotency_key,
                        requested_display_name, purpose, department_id, role_id, placement, cpu_limit,
                        memory_limit_mib, pids_limit, state, request_version_hash, approved_request_version,
                        owner_approval, container_profile_revision_id, revision, created_at, updated_at)
                    VALUES ($id, $organization, NULL, 'owner', $key, $name, $purpose, $department, $role,
                        $placement, $cpu, $memory, $pids, 'Requested', $hash, NULL, NULL, NULL, 1, $now, $now)
                    """,
                    ("$id", id), ("$organization", organizationId), ("$key", idempotencyKey),
                    ("$name", displayName), ("$purpose", purpose), ("$department", departmentId),
                    ("$role", roleId), ("$placement", placement), ("$cpu", request.CpuLimit),
                    ("$memory", request.MemoryLimitMiB), ("$pids", request.PidsLimit), ("$hash", requestHash),
                    ("$now", now));
                AppendHireRequestEvent(connection, transaction, id, HireRequestStates.Requested, 1, requestHash, now);
                transaction.Commit();
                return ReadHireRequests(connection, id).Single();
            }
        });
    }

    public HireRequestSummary RejectHireRequest(string id, int expectedRevision)
    {
        if (!IsBoundedIdentifier(id, OrganizationIds.HireRequestPrefix) || expectedRevision < 1)
            throw new OrganizationValidationException("A stable hire request id and current revision are required.");
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var current = ReadHireRequest(connection, transaction, id)
                    ?? throw new OrganizationNotFoundException($"Hire request '{id}' does not exist.");
                if (current.Revision != expectedRevision || current.State != HireRequestStates.Requested)
                    throw new OrganizationConcurrencyException("The hire request changed or is no longer requestable.");
                var now = Timestamp();
                var affected = Execute(connection, transaction,
                    """
                    UPDATE hire_requests
                    SET state = 'Rejected', revision = revision + 1, updated_at = $now
                    WHERE id = $id AND revision = $revision AND state = 'Requested'
                    """,
                    ("$now", now), ("$id", id), ("$revision", expectedRevision));
                if (affected != 1) throw new OrganizationConcurrencyException("The hire request changed before rejection.");
                AppendHireRequestEvent(connection, transaction, id, HireRequestStates.Rejected, expectedRevision + 1,
                    HashHireValue($"reject\n{id}\n{expectedRevision + 1}"), now);
                transaction.Commit();
                return ReadHireRequests(connection, id).Single();
            }
        });
    }

    private static void EnsureHireRelationships(SqliteConnection connection, SqliteTransaction transaction, string organizationId, string departmentId, string roleId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*) FROM roles r
            JOIN departments d ON d.id = r.department_id
            WHERE d.organization_id = $organization AND d.id = $department AND r.id = $role
            """;
        command.Parameters.AddWithValue("$organization", organizationId);
        command.Parameters.AddWithValue("$department", departmentId);
        command.Parameters.AddWithValue("$role", roleId);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new OrganizationValidationException("The selected department and role must exist in this organization and the role must belong to the department.");
    }

    private static void AppendHireRequestEvent(SqliteConnection connection, SqliteTransaction transaction, string requestId, string state, int revision, string detailHash, string now) =>
        Execute(connection, transaction,
            "INSERT INTO hire_request_events (id, hire_request_id, state, revision, detail_hash, created_at) VALUES ($id, $request, $state, $revision, $hash, $now)",
            ("$id", OrganizationIds.NewHireRequestEventId()), ("$request", requestId), ("$state", state),
            ("$revision", revision), ("$hash", detailHash), ("$now", now));

    private static HireRequestSummary? ReadHireRequestByKey(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = HireRequestReadCommand(connection, transaction);
        command.CommandText += " WHERE h.idempotency_key = $key ORDER BY h.created_at DESC";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHireRequest(reader) : null;
    }

    private static HireRequestSummary? ReadHireRequest(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = HireRequestReadCommand(connection, transaction);
        command.CommandText += " WHERE h.id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHireRequest(reader) : null;
    }

    private static IReadOnlyList<HireRequestSummary> ReadHireRequests(SqliteConnection connection, string? id)
    {
        using var command = HireRequestReadCommand(connection, null);
        if (id is not null)
        {
            command.CommandText += " WHERE h.id = $id";
            command.Parameters.AddWithValue("$id", id);
        }
        command.CommandText += " ORDER BY h.created_at DESC, h.id";
        var result = new List<HireRequestSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadHireRequest(reader));
        return result;
    }

    private static SqliteCommand HireRequestReadCommand(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT h.id, h.organization_id, h.requested_by_employee_id, h.requested_by_kind,
                   h.idempotency_key, h.requested_display_name, h.purpose, h.department_id,
                   d.display_name, h.role_id, r.display_name, h.placement, h.cpu_limit,
                   h.memory_limit_mib, h.pids_limit, h.state, h.request_version_hash,
                   h.approved_request_version, h.owner_approval, h.revision, h.created_at, h.updated_at,
                   h.container_profile_revision_id
            FROM hire_requests h
            JOIN departments d ON d.id = h.department_id
            JOIN roles r ON r.id = h.role_id
            """;
        return command;
    }

    private static HireRequestSummary ReadHireRequest(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
        reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
        reader.GetString(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14),
        reader.GetString(15), reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.IsDBNull(18) ? null : reader.GetString(18), reader.GetInt32(19),
        DateTimeOffset.Parse(reader.GetString(20), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(21), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(22) ? null : reader.GetString(22));

    private static string ValidateIdempotencyKey(string? header, string? body)
    {
        var normalizedHeader = string.IsNullOrEmpty(header) ? null : header;
        var normalizedBody = string.IsNullOrEmpty(body) ? null : body;
        if (normalizedHeader is not null && normalizedBody is not null
            && !string.Equals(normalizedHeader, normalizedBody, StringComparison.Ordinal))
            throw new OrganizationValidationException("Idempotency-Key header and body value must match exactly when both are supplied.");
        var value = normalizedHeader ?? normalizedBody;
        var validated = ValidateBoundedText(value, "An Idempotency-Key header or body value is required.", MaximumIdempotencyKeyLength);
        if (!string.Equals(value, validated, StringComparison.Ordinal))
            throw new OrganizationValidationException("The idempotency key must not have leading or trailing whitespace.");
        return validated;
    }

    private static string ValidateStableReference(string? value, string prefix, string kind)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (!IsBoundedIdentifier(trimmed, prefix)) throw new OrganizationValidationException($"A stable {kind} id is required.");
        return trimmed;
    }

    private static bool IsBoundedIdentifier(string? value, string prefix) => value is { Length: >= 6 and <= 128 }
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-".AsSpan()) < 0;

    private static string ValidateBoundedText(string? value, string emptyMessage, int maximumLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new OrganizationValidationException(emptyMessage);
        if (trimmed.Length > maximumLength) throw new OrganizationValidationException($"The value must be at most {maximumLength} characters.");
        if (trimmed.Any(char.IsControl)) throw new OrganizationValidationException("The value must not contain control characters.");
        return trimmed;
    }

    private static void ValidateResources(int cpu, int memory, int pids)
    {
        if (cpu is < MinimumHireCpuLimit or > MaximumHireCpuLimit)
            throw new OrganizationValidationException($"CPU limit must be between {MinimumHireCpuLimit} and {MaximumHireCpuLimit} cores.");
        if (memory is < MinimumHireMemoryMiB or > MaximumHireMemoryMiB)
            throw new OrganizationValidationException($"Memory limit must be between {MinimumHireMemoryMiB} and {MaximumHireMemoryMiB} MiB.");
        if (pids is < MinimumHirePidsLimit or > MaximumHirePidsLimit)
            throw new OrganizationValidationException($"PID limit must be between {MinimumHirePidsLimit} and {MaximumHirePidsLimit}.");
    }

    private static string HashHireRequest(string name, string purpose, string department, string role, string placement, int cpu, int memory, int pids) =>
        HashHireValue(string.Join('\n', name, purpose, department, role, placement,
            cpu.ToString(CultureInfo.InvariantCulture), memory.ToString(CultureInfo.InvariantCulture), pids.ToString(CultureInfo.InvariantCulture)));

    private static string HashHireValue(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
