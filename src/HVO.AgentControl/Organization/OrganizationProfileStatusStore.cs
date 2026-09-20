using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HVO.AgentControl.Organization;

/// <summary>
/// Read-only projection of one managed employee's profile revision status (#261
/// slice B). The projection is derived from the frozen owner approval resources,
/// the immutable profile revision chain and the per-host verified builds; it
/// performs no writes. A <c>NewerRevisionAvailable</c> is reported only when the
/// profile has a newer current revision AND a verified build for that revision
/// already exists on the employee's host, so a later UI action can offer a ready
/// target rather than a revision that still needs building.
/// </summary>
public sealed partial class OrganizationStore
{
    /// <summary>
    /// The profile status for the managed employee with <paramref name="employeeId"/>,
    /// or null when the employee does not exist, is not a managed employee, or has
    /// no managed enrollment resources. Never mutates anything.
    /// </summary>
    public EmployeeProfileStatus? GetEmployeeProfileStatus(string employeeId)
    {
        if (!IsBoundedIdentifier(employeeId, OrganizationIds.EmployeePrefix)) return null;
        return TranslateStoreFaults(() =>
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                using var connection = OpenConnection();
                return ReadEmployeeProfileStatus(connection, null, employeeId);
            }
        });
    }

    private static EmployeeProfileStatus? ReadEmployeeProfileStatus(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string employeeId)
    {
        // The managed enrollment resources are the managed-employee marker. A
        // non-managed employee (including the internal seed employee) has no such
        // row for its binding and yields null rather than fabricated fields.
        string bindingId;
        string? workerId;
        string revisionId;
        string imageDigest;
        string hostId;
        string platform;
        string profileId;
        int revisionNumber;
        string displayName;
        int currentRevisionNumber;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT b.id, w.worker_id,
                       m.approved_profile_revision_id, m.approved_image_digest, m.approved_host_id, m.platform,
                       r.profile_id, r.revision_number, p.display_name, p.current_revision_number
                FROM runtime_bindings b
                JOIN managed_enrollment_resources m ON m.runtime_binding_id = b.id
                LEFT JOIN worker_enrollments w ON w.runtime_binding_id = b.id
                LEFT JOIN container_profile_revisions r ON r.id = m.approved_profile_revision_id
                LEFT JOIN container_profiles p ON p.id = r.profile_id
                WHERE b.employee_id = $employee
                ORDER BY b.id
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$employee", employeeId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            bindingId = reader.GetString(0);
            workerId = reader.IsDBNull(1) ? null : reader.GetString(1);
            revisionId = reader.GetString(2);
            imageDigest = reader.GetString(3);
            hostId = reader.GetString(4);
            platform = reader.GetString(5);
            if (reader.IsDBNull(6) || reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9))
                throw new OrganizationStoreCorruptException($"Managed enrollment resources for binding '{bindingId}' do not resolve to a container profile revision.");
            profileId = reader.GetString(6);
            revisionNumber = reader.GetInt32(7);
            displayName = reader.GetString(8);
            currentRevisionNumber = reader.GetInt32(9);
        }

        // The newer target is only offered when the profile moved past the frozen
        // revision and a verified build for the newest revision already exists on
        // the employee's host.
        string? newerRevisionId = null;
        int? newerRevisionNumber = null;
        string? newerVerifiedBuildId = null;
        string? newerVerifiedImageDigest = null;
        if (currentRevisionNumber > revisionNumber)
        {
            var latestRevisionId = ReadProfileRevisionId(connection, transaction, profileId, currentRevisionNumber);
            if (latestRevisionId is not null)
            {
                var verified = ReadBuilds(connection, transaction, latestRevisionId, hostId, null)
                    .FirstOrDefault(b => b.State == ProfileBuildStates.Built && b.Verified && b.ImageDigest is not null);
                if (verified is not null)
                {
                    newerRevisionId = latestRevisionId;
                    newerRevisionNumber = currentRevisionNumber;
                    newerVerifiedBuildId = verified.Id;
                    newerVerifiedImageDigest = verified.ImageDigest;
                }
            }
        }

        // At most one active rebuild per worker is enforced by the store and the
        // partial unique index; a terminal row is deliberately not surfaced.
        var activeRebuild = workerId is null
            ? null
            : ReadRebuilds(connection, transaction, workerId: workerId, activeOnly: true).SingleOrDefault();

        return new EmployeeProfileStatus(
            employeeId,
            bindingId,
            workerId,
            hostId,
            revisionId,
            revisionNumber,
            profileId,
            displayName,
            imageDigest,
            platform,
            NewerRevisionAvailable: newerRevisionId is not null,
            newerRevisionId,
            newerRevisionNumber,
            newerVerifiedBuildId,
            newerVerifiedImageDigest,
            activeRebuild);
    }

    private static string? ReadProfileRevisionId(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string profileId,
        int revisionNumber)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM container_profile_revisions WHERE profile_id = $profile AND revision_number = $number";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$number", revisionNumber);
        return command.ExecuteScalar() as string;
    }
}
