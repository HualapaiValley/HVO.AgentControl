using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.AgentControl.Organization;

/// <summary>
/// The durable, host-owned specification of one worker task (#220 slice A).
/// A task is never an arbitrary prompt: it is a bounded, normalized contract the
/// controller can reason about and the host can verify. Every field is closed and
/// validated so a task carries no free-form shell or test command, no absolute
/// path outside its workspace root, and no unbounded budget.
/// </summary>
/// <remarks>
/// Fields:
/// <list type="bullet">
/// <item><c>Version</c> is always 1 for phase 1.</item>
/// <item><c>Description</c> is a short bounded summary without control characters.</item>
/// <item><c>WorkspaceRoot</c> is an absolute path under <c>/workspace/</c> (never bare).</item>
/// <item><c>AllowedPaths</c> are 1..32 distinct relative paths under the root.</item>
/// <item><c>AllowedTools</c> are 1..16 distinct values from the closed vocabulary.</item>
/// <item><c>ForbiddenActions</c> are 1..32 distinct bounded strings.</item>
/// <item><c>MaximumSeconds</c> is 1..1800 and <c>MaximumTurns</c> is exactly 1.</item>
/// <item><c>TestRecipeId</c> is a closed recipe id or null (never a command).</item>
/// </list>
/// Arrays are normalized by trimming, de-duplicating preserving the first
/// occurrence, and sorting ordinally before canonical JSON is produced.
/// </remarks>
public sealed record WorkerTaskSpec(
    string Description,
    string WorkspaceRoot,
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> ForbiddenActions,
    int MaximumSeconds,
    string? TestRecipeId = null,
    int Version = 1,
    int MaximumTurns = 1);

/// <summary>
/// A model-authored task report. It is evidence of what the model claims it did,
/// never proof of success: only a host verification may move a task to
/// <c>Verified</c>. Every field is bounded and normalized before persistence.
/// </summary>
public sealed record ModelTaskReport(
    [property: System.Text.Json.Serialization.JsonPropertyName("summary")] string Summary,
    [property: System.Text.Json.Serialization.JsonPropertyName("changedPaths")] IReadOnlyList<string> ChangedPaths,
    [property: System.Text.Json.Serialization.JsonPropertyName("tests")] IReadOnlyList<ModelTaskTestReport> Tests,
    [property: System.Text.Json.Serialization.JsonPropertyName("deniedAction")] ModelTaskDeniedAction? DeniedAction,
    [property: System.Text.Json.Serialization.JsonPropertyName("limitations")] IReadOnlyList<string> Limitations);

/// <summary>One model-claimed execution of the host-selected test recipe.</summary>
public sealed record ModelTaskTestReport(
    [property: System.Text.Json.Serialization.JsonPropertyName("recipeId")] string? RecipeId,
    [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("summary")] string Summary);

/// <summary>A model claim that a prohibited action was refused without side effects.</summary>
public sealed record ModelTaskDeniedAction(
    [property: System.Text.Json.Serialization.JsonPropertyName("requested")] string Requested,
    [property: System.Text.Json.Serialization.JsonPropertyName("action")] string Action,
    [property: System.Text.Json.Serialization.JsonPropertyName("result")] string Result,
    [property: System.Text.Json.Serialization.JsonPropertyName("noSideEffect")] bool NoSideEffect);

/// <summary>
/// A host verification outcome for one task. Only <c>Passed</c> carries the
/// manifest and test-summary evidence the database requires; <c>Failed</c> and
/// <c>Uncertain</c> carry a bounded failure detail instead.
/// </summary>
public sealed record HostTaskVerification(
    string State,
    string? ManifestJson,
    string? TestSummaryJson,
    string? DeniedActionJson,
    string? FailureDetail);

/// <summary>Durable host verification evidence for one task.</summary>
public sealed record WorkerTaskVerificationRecord(
    string Id,
    string TaskId,
    string State,
    string? ManifestJson,
    string? ManifestHash,
    string? TestSummaryJson,
    string? TestSummaryHash,
    string? DeniedActionJson,
    string? DeniedActionHash,
    string VerifierVersion,
    string? FailureDetail,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision);

/// <summary>
/// The fixed task state machine. A task is <c>Requested</c> until a request is
/// forwarded, running while a request is in flight, then <c>Completed</c>,
/// <c>Failed</c>, <c>Uncertain</c> or <c>Cancelled</c>. Only a passed host
/// verification moves it to <c>Verified</c>. A model report never does.
/// </summary>
public static class WorkerTaskStates
{
    public const string Requested = "Requested";
    public const string Uncertain = "Uncertain";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Verified = "Verified";

    public static bool IsDefined(string state) =>
        state is Requested or Uncertain or Running or Completed or Failed or Cancelled or Verified;

}

public static class WorkerTaskVerificationStates
{
    public const string Pending = "Pending";
    public const string Passed = "Passed";
    public const string Failed = "Failed";
    public const string Uncertain = "Uncertain";

    public static bool IsDefined(string state) => state is Pending or Passed or Failed or Uncertain;
}

/// <summary>The closed phase-1 tool vocabulary a task specification may allow.</summary>
public static class WorkerTaskTools
{
    public const string Read = "read";
    public const string Edit = "edit";
    public const string Test = "test";

    public static readonly IReadOnlyList<string> Vocabulary = [Read, Edit, Test];

    public static bool IsDefined(string tool) => tool is Read or Edit or Test;
}

/// <summary>The closed phase-1 test-recipe vocabulary. A recipe is an identifier, never a command.</summary>
public static class WorkerTaskTestRecipes
{
    public const string DotnetTestRelease = "dotnet-test-release";

    public static readonly IReadOnlyList<string> Vocabulary = [DotnetTestRelease];

    public static bool IsDefined(string recipe) => recipe == DotnetTestRelease;
}

/// <summary>
/// Schema v13: durable task specification, model report and host verification
/// domain. <c>worker_tasks</c> is rebuilt with the bounded specification and the
/// optional model-report triplet while preserving every existing row and the
/// <c>created_at</c>/<c>updated_at</c>/<c>revision</c> values. The verification
/// table is additive and references the rebuilt task table.
/// </summary>
public sealed partial class OrganizationStore
{
    public const int MaxTaskSpecJsonLength = 32768;
    public const int MaxModelReportJsonLength = 16 * 1024;
    public const int MaxManifestJsonLength = 65536;
    public const int MaxTestSummaryJsonLength = 32768;
    public const int MaxDeniedActionJsonLength = 8192;
    public const int MaxFailureDetailLength = 512;
    public const int MaxTaskDescriptionLength = 2048;
    public const int MaxTaskAllowedPathCount = 32;
    public const int MaxTaskAllowedToolCount = 16;
    public const int MaxTaskForbiddenActionCount = 32;
    public const int MaxTaskForbiddenActionLength = 256;
    public const int MaxTaskMaximumSeconds = 1800;
    public const int MaxTaskMaximumTurns = 1;
    public const string TaskWorkspaceRootPrefix = "/workspace/";

    /// <summary>The bounded legacy compatibility specification used for rows migrated from v12.</summary>
    internal static WorkerTaskSpec LegacyCompatibilitySpec(string descriptionHash) => new(
        Description: "legacy-task:" + descriptionHash,
        WorkspaceRoot: "/workspace/legacy-request",
        AllowedPaths: ["."],
        AllowedTools: [WorkerTaskTools.Read],
        ForbiddenActions: ["unspecified external writes"],
        MaximumSeconds: 900,
        TestRecipeId: null,
        Version: 1,
        MaximumTurns: 1);

    internal const string WorkerTasksSchemaV13Statement =
        """
        CREATE TABLE worker_tasks (
            id TEXT PRIMARY KEY,
            employee_id TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT,
            runtime_binding_id TEXT NOT NULL REFERENCES runtime_bindings(id) ON DELETE RESTRICT,
            worker_id TEXT NOT NULL REFERENCES worker_enrollments(worker_id) ON DELETE RESTRICT,
            description_hash TEXT NOT NULL,
            task_spec_json TEXT NOT NULL CHECK (length(task_spec_json) BETWEEN 2 AND 32768),
            task_spec_hash TEXT NOT NULL CHECK (length(task_spec_hash) = 71 AND substr(task_spec_hash, 1, 7) = 'sha256:'),
            model_report_json TEXT CHECK (model_report_json IS NULL OR length(model_report_json) <= 16384),
            model_report_hash TEXT CHECK (model_report_hash IS NULL OR (length(model_report_hash) = 71 AND substr(model_report_hash, 1, 7) = 'sha256:')),
            model_reported_at TEXT,
            failure_detail TEXT CHECK (failure_detail IS NULL OR length(failure_detail) <= 512),
            state TEXT NOT NULL CHECK (state IN ('Requested', 'Uncertain', 'Running', 'Completed', 'Failed', 'Cancelled', 'Verified')),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            revision INTEGER NOT NULL,
            -- The report triplet is all-or-nothing so a reader can never observe a
            -- hash without the bytes (or a timestamp without the report).
            CHECK (
                (model_report_json IS NULL AND model_report_hash IS NULL AND model_reported_at IS NULL)
                OR (model_report_json IS NOT NULL AND model_report_hash IS NOT NULL AND model_reported_at IS NOT NULL)
            )
        )
        """;

    private static string[] WorkerTaskVerificationSchemaV13Statements =>
    [
        """
        CREATE TABLE worker_task_verifications (
            id TEXT PRIMARY KEY,
            task_id TEXT NOT NULL REFERENCES worker_tasks(id) ON DELETE RESTRICT,
            state TEXT NOT NULL CHECK (state IN ('Pending', 'Passed', 'Failed', 'Uncertain')),
            manifest_json TEXT CHECK (manifest_json IS NULL OR length(manifest_json) <= 65536),
            manifest_hash TEXT CHECK (manifest_hash IS NULL OR (length(manifest_hash) = 71 AND substr(manifest_hash, 1, 7) = 'sha256:')),
            test_summary_json TEXT CHECK (test_summary_json IS NULL OR length(test_summary_json) <= 32768),
            test_summary_hash TEXT CHECK (test_summary_hash IS NULL OR (length(test_summary_hash) = 71 AND substr(test_summary_hash, 1, 7) = 'sha256:')),
            denied_action_json TEXT CHECK (denied_action_json IS NULL OR length(denied_action_json) <= 8192),
            denied_action_hash TEXT CHECK (denied_action_hash IS NULL OR (length(denied_action_hash) = 71 AND substr(denied_action_hash, 1, 7) = 'sha256:')),
            verifier_version TEXT NOT NULL CHECK (length(verifier_version) BETWEEN 1 AND 64),
            failure_detail TEXT CHECK (failure_detail IS NULL OR length(failure_detail) <= 512),
            verified_at TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 1),
            UNIQUE (task_id, verifier_version),
            -- A Passed verification must carry its manifest, its test summary and a
            -- verification timestamp; other states may omit all three.
            CHECK (
                state <> 'Passed'
                OR (manifest_json IS NOT NULL AND manifest_hash IS NOT NULL
                    AND test_summary_json IS NOT NULL AND test_summary_hash IS NOT NULL
                    AND verified_at IS NOT NULL)
            )
        ) WITHOUT ROWID
        """,
        """
        CREATE TRIGGER worker_task_verifications_identity_immutable
            BEFORE UPDATE OF id, task_id, verifier_version, created_at
            ON worker_task_verifications
        BEGIN
            SELECT RAISE(ABORT, 'worker task verification identity is immutable');
        END
        """,
        """
        CREATE TRIGGER worker_task_verifications_no_delete
            BEFORE DELETE ON worker_task_verifications
        BEGIN
            SELECT RAISE(ABORT, 'worker task verifications are never deleted');
        END
        """,
        """
        CREATE TRIGGER worker_task_verifications_no_replace
            BEFORE INSERT ON worker_task_verifications
            WHEN EXISTS (SELECT 1 FROM worker_task_verifications WHERE id = NEW.id)
        BEGIN
            SELECT RAISE(ABORT, 'worker task verifications are never replaced');
        END
        """,
    ];

    /// <summary>
    /// Validates and normalizes a task specification. The returned value has
    /// sorted, de-duplicated arrays and trimmed scalars and is the exact value
    /// that must be serialized and hashed. Anything unbounded or outside the
    /// closed vocabularies is rejected rather than coerced.
    /// </summary>
    public static WorkerTaskSpec NormalizeWorkerTaskSpec(WorkerTaskSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Version != 1) throw new OrganizationValidationException("Only task specification version 1 is supported.");
        if (spec.MaximumTurns != MaxTaskMaximumTurns) throw new OrganizationValidationException("A phase-1 task must run exactly one turn.");

        var description = BoundedText(spec.Description, "task description", 1, MaxTaskDescriptionLength, allowWhitespaceNormalization: true);

        var workspaceRoot = spec.WorkspaceRoot?.Trim() ?? string.Empty;
        if (!workspaceRoot.StartsWith(TaskWorkspaceRootPrefix, StringComparison.Ordinal)
            || workspaceRoot.Length <= TaskWorkspaceRootPrefix.Length
            || workspaceRoot.EndsWith('/')
            || workspaceRoot.Contains("..", StringComparison.Ordinal)
            || workspaceRoot.Contains("//", StringComparison.Ordinal)
            || workspaceRoot.Any(char.IsControl)
            || !IsSafeAbsoluteSegmentPath(workspaceRoot))
        {
            throw new OrganizationValidationException("A task workspace root must be an absolute safe path strictly under /workspace/.");
        }

        if (spec.AllowedPaths is null || spec.AllowedPaths.Count is < 1 or > MaxTaskAllowedPathCount)
            throw new OrganizationValidationException($"A task must allow between 1 and {MaxTaskAllowedPathCount} paths.");
        var allowedPaths = NormalizeRelativePaths(spec.AllowedPaths, "allowed path");

        if (spec.AllowedTools is null || spec.AllowedTools.Count is < 1 or > MaxTaskAllowedToolCount)
            throw new OrganizationValidationException($"A task must allow between 1 and {MaxTaskAllowedToolCount} tools.");
        var allowedTools = spec.AllowedTools
            .Select(tool => tool?.Trim() ?? string.Empty)
            .Select(tool => WorkerTaskTools.IsDefined(tool)
                ? tool
                : throw new OrganizationValidationException("A task tool is not in the closed vocabulary."))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tool => tool, StringComparer.Ordinal)
            .ToArray();

        if (spec.ForbiddenActions is null || spec.ForbiddenActions.Count is < 1 or > MaxTaskForbiddenActionCount)
            throw new OrganizationValidationException($"A task must declare between 1 and {MaxTaskForbiddenActionCount} forbidden actions.");
        var forbiddenActions = spec.ForbiddenActions
            .Select(action => BoundedText(action, "forbidden action", 1, MaxTaskForbiddenActionLength, allowWhitespaceNormalization: true))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(action => action, StringComparer.Ordinal)
            .ToArray();

        if (spec.MaximumSeconds is < 1 or > MaxTaskMaximumSeconds)
            throw new OrganizationValidationException($"A task budget must be between 1 and {MaxTaskMaximumSeconds} seconds.");

        string? testRecipeId = null;
        if (spec.TestRecipeId is not null)
        {
            var recipe = spec.TestRecipeId.Trim();
            testRecipeId = WorkerTaskTestRecipes.IsDefined(recipe)
                ? recipe
                : throw new OrganizationValidationException("A task test recipe is not in the closed vocabulary.");
        }

        return new WorkerTaskSpec(
            description,
            workspaceRoot,
            allowedPaths,
            allowedTools,
            forbiddenActions,
            spec.MaximumSeconds,
            testRecipeId,
            Version: 1,
            MaximumTurns: 1);
    }

    /// <summary>Canonical JSON of a normalized specification, stable across runs.</summary>
    public static string SerializeWorkerTaskSpec(WorkerTaskSpec spec)
    {
        var normalized = NormalizeWorkerTaskSpec(spec);
        var envelope = new CanonicalTaskSpec(
            normalized.Version,
            normalized.Description,
            normalized.WorkspaceRoot,
            normalized.AllowedPaths.ToArray(),
            normalized.AllowedTools.ToArray(),
            normalized.ForbiddenActions.ToArray(),
            normalized.MaximumSeconds,
            normalized.MaximumTurns,
            normalized.TestRecipeId);
        return JsonSerializer.Serialize(envelope, CanonicalJsonOptions);
    }

    /// <summary>Canonical sha256 hash of the serialized normalized specification.</summary>
    public static string HashWorkerTaskSpec(WorkerTaskSpec spec) => HashText(SerializeWorkerTaskSpec(spec));

    public static bool IsWorkerTaskSpecHash(string? value) => IsHash(value ?? string.Empty);

    /// <summary>Returns the canonical JSON and hash for a migrated legacy row derived from its description hash.</summary>
    internal static (string Json, string Hash) LegacyTaskSpecForMigration(string descriptionHash)
    {
        var spec = NormalizeWorkerTaskSpec(LegacyCompatibilitySpec(descriptionHash));
        var json = SerializeWorkerTaskSpec(spec);
        return (json, HashText(json));
    }

    private sealed record CanonicalTaskSpec(
        int Version,
        string Description,
        string WorkspaceRoot,
        IReadOnlyList<string> AllowedPaths,
        IReadOnlyList<string> AllowedTools,
        IReadOnlyList<string> ForbiddenActions,
        int MaximumSeconds,
        int MaximumTurns,
        string? TestRecipeId);

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private static string[] NormalizeRelativePaths(IReadOnlyList<string> paths, string kind)
    {
        var normalized = paths
            .Select(path => path?.Trim().TrimStart('/') ?? string.Empty)
            .Select(path =>
            {
                if (path.Length == 0) throw new OrganizationValidationException($"A task {kind} must not be empty.");
                if (path == ".") return path;
                if (path.Contains("..", StringComparison.Ordinal)
                    || path.Contains("//", StringComparison.Ordinal)
                    || path.StartsWith('/')
                    || path.EndsWith('/')
                    || path.Any(char.IsControl)
                    || !IsSafeAbsoluteSegmentPath(path))
                {
                    throw new OrganizationValidationException($"A task {kind} must be a safe relative path under the workspace root.");
                }

                return path;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length < 1)
            throw new OrganizationValidationException($"A task must declare at least one {kind}.");
        return normalized;
    }

    /// <summary>
    /// A path made only of conservative ASCII segments (<c>[A-Za-z0-9._-]</c>)
    /// separated by single slashes. This deliberately excludes shell
    /// metacharacters, globs, whitespace and non-ASCII so a path can never be
    /// reinterpreted as a command or pattern.
    /// </summary>
    private static bool IsSafeAbsoluteSegmentPath(string path)
    {
        var body = path.StartsWith('/') ? path[1..] : path;
        if (body.Length == 0) return false;
        foreach (var segment in body.Split('/'))
        {
            if (segment.Length == 0) return false;
            // "." and ".." segments are never safe: they can retarget a path
            // outside the declared root even though their characters are benign.
            if (segment is "." or "..") return false;
            foreach (var ch in segment)
            {
                if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')) return false;
            }
        }

        return true;
    }

    private static string BoundedText(string? value, string kind, int minimum, int maximum, bool allowWhitespaceNormalization)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (allowWhitespaceNormalization)
        {
            normalized = string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        if (normalized.Length < minimum) throw new OrganizationValidationException($"A task {kind} is required.");
        if (normalized.Length > maximum) throw new OrganizationValidationException($"A task {kind} must be at most {maximum} characters.");
        if (normalized.Any(char.IsControl)) throw new OrganizationValidationException($"A task {kind} must not contain control characters.");
        return normalized;
    }

    /// <summary>
    /// Synthesizes the bounded legacy compatibility specification from a
    /// description hash. This is the low-level route retained for pre-#220
    /// dispatch callers and migrated v12 rows; it is never a verified #220 task.
    /// </summary>
    internal static (string Json, string Hash) CompatibilityTaskSpecForDescriptionHash(string descriptionHash)
    {
        var spec = LegacyCompatibilitySpec(descriptionHash);
        var json = SerializeWorkerTaskSpec(spec);
        return (json, HashText(json));
    }
}
