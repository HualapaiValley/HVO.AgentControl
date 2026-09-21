using HVO.AgentControl.Organization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Schema v13: the durable task specification, model report and host verification
/// domain. Model evidence never becomes task success; only a passed host
/// verification moves a completed task to Verified.
/// </summary>
public sealed class WorkerTaskV13Tests
{
    private const string ValidDigest = "sha256:" + "a" + "000000000000000000000000000000000000000000000000000000000000000";

    private static WorkerTaskSpec Spec(
        string description = "Bounded change",
        string workspaceRoot = "/workspace/project",
        IReadOnlyList<string>? allowedPaths = null,
        IReadOnlyList<string>? allowedTools = null,
        IReadOnlyList<string>? forbiddenActions = null,
        int maximumSeconds = 300,
        string? testRecipeId = null,
        int maximumTurns = 1) => new(
            description,
            workspaceRoot,
            allowedPaths ?? ["src", "tests"],
            allowedTools ?? [WorkerTaskTools.Read, WorkerTaskTools.Edit, WorkerTaskTools.Test],
            forbiddenActions ?? ["network egress", "secret access"],
            maximumSeconds,
            testRecipeId,
            Version: 1,
            MaximumTurns: maximumTurns);

    // ---------------------------------------------------------------- spec ----

    [Fact]
    public void TaskSpecRejectsTraversalBareWorkspaceControlsAndUnboundedBudgets()
    {
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(workspaceRoot: "/workspace")));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(workspaceRoot: "/workspace/")));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(workspaceRoot: "/workspace/a/../b")));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(workspaceRoot: "/workspace/a b")));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(workspaceRoot: "/etc/passwd")));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(description: "bad\u0007control")));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(maximumSeconds: 0)));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(maximumSeconds: 1801)));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(maximumTurns: 2)));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(allowedPaths: ["../outside"])));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(allowedPaths: [])));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(allowedTools: ["shell"])));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(allowedTools: [])));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(testRecipeId: "dotnet test")));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(testRecipeId: "arbitrary-command")));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(allowedPaths: Enumerable.Range(0, 33).Select(i => "p" + i).ToArray())));
        Assert.Throws<OrganizationValidationException>(() => OrganizationStore.NormalizeWorkerTaskSpec(Spec(description: new string('x', 2049))));
    }

    [Fact]
    public void TaskSpecNormalizesSortsDeduplicatesAndProducesCanonicalJsonAndHash()
    {
        var normalized = OrganizationStore.NormalizeWorkerTaskSpec(Spec(
            allowedPaths: ["tests", "src", "src"],
            allowedTools: [WorkerTaskTools.Test, WorkerTaskTools.Read, WorkerTaskTools.Read],
            forbiddenActions: ["z", "a", "a"],
            testRecipeId: WorkerTaskTestRecipes.DotnetTestRelease));
        Assert.Equal(["src", "tests"], normalized.AllowedPaths);
        Assert.Equal([WorkerTaskTools.Read, WorkerTaskTools.Test], normalized.AllowedTools);
        Assert.Equal(["a", "z"], normalized.ForbiddenActions);
        Assert.Equal(WorkerTaskTestRecipes.DotnetTestRelease, normalized.TestRecipeId);

        // The legacy compatibility path uses the bare workspace special path.
        var legacy = OrganizationStore.NormalizeWorkerTaskSpec(Spec(workspaceRoot: "/workspace/legacy-request", allowedPaths: ["."]));
        Assert.Equal(["."], legacy.AllowedPaths);

        var json = OrganizationStore.SerializeWorkerTaskSpec(normalized);
        var hash = OrganizationStore.HashWorkerTaskSpec(normalized);
        Assert.StartsWith("sha256:", hash, StringComparison.Ordinal);
        Assert.Equal(71, hash.Length);
        // Canonical output is byte-stable across equivalent inputs.
        var reordered = OrganizationStore.HashWorkerTaskSpec(Spec(
            allowedPaths: ["src", "tests"],
            allowedTools: [WorkerTaskTools.Read, WorkerTaskTools.Test],
            forbiddenActions: ["a", "z"],
            testRecipeId: WorkerTaskTestRecipes.DotnetTestRelease));
        Assert.Equal(hash, reordered);
        Assert.Equal(json, OrganizationStore.SerializeWorkerTaskSpec(Spec(
            allowedPaths: ["src", "tests"],
            allowedTools: [WorkerTaskTools.Read, WorkerTaskTools.Test],
            forbiddenActions: ["a", "z"],
            testRecipeId: WorkerTaskTestRecipes.DotnetTestRelease)));
    }

    // ------------------------------------------------------- atomic persist ----

    [Fact]
    public void BeginWorkerRequestPersistsSpecAndRequestAtomically()
    {
        using var fixture = new RemoteWorkerControlTests.RemoteStoreFixture();
        fixture.CreateEnrolledAndReady();
        var spec = Spec();
        var request = fixture.Store.BeginWorkerRequest(new(
            fixture.EmployeeId, fixture.BindingId, "wrk-a", fixture.SessionId, fixture.NativeSessionId,
            "idem-spec", "sha256:" + new string('d', 64), "sha256:" + new string('f', 64), 1, 1, "turn-spec"), spec);

        var task = fixture.Store.GetWorkerTask(request.TaskId)!;
        Assert.Equal("Requested", task.State);
        Assert.Equal(OrganizationStore.SerializeWorkerTaskSpec(spec), task.TaskSpecJson);
        Assert.Equal(OrganizationStore.HashWorkerTaskSpec(spec), task.TaskSpecHash);
        Assert.Null(task.ModelReportHash);
        Assert.NotEqual(default, task.CreatedAt);
        Assert.NotEqual(default, task.UpdatedAt);
        Assert.Equal(request.TaskId, fixture.Store.ListWorkerRequests().Single(x => x.Id == request.Id).TaskId);
    }

    [Fact]
    public void InvalidSpecFailsBeforeAnyTaskOrRequestIsPersisted()
    {
        using var fixture = new RemoteWorkerControlTests.RemoteStoreFixture();
        fixture.CreateEnrolledAndReady();
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.BeginWorkerRequest(new(
            fixture.EmployeeId, fixture.BindingId, "wrk-a", fixture.SessionId, fixture.NativeSessionId,
            "idem-bad", "sha256:" + new string('d', 64), "sha256:" + new string('f', 64), 1, 1, "turn-bad"),
            Spec(allowedTools: ["shell"])));
        Assert.Empty(fixture.Store.ListWorkerTasks());
        Assert.Empty(fixture.Store.ListWorkerRequests());
    }

    [Fact]
    public void IdempotentReplayCannotSubstituteADifferentTaskSpec()
    {
        using var fixture = new RemoteWorkerControlTests.RemoteStoreFixture();
        fixture.CreateEnrolledAndReady();
        var begin = new BeginWorkerRequest(fixture.EmployeeId, fixture.BindingId, "wrk-a", fixture.SessionId, fixture.NativeSessionId, "idem-spec", "sha256:" + new string('d', 64), "sha256:" + new string('f', 64), 1, 1, "turn-spec");
        var first = fixture.Store.BeginWorkerRequest(begin, Spec());
        var same = fixture.Store.BeginWorkerRequest(begin, Spec());
        Assert.Equal(first.Id, same.Id);
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.BeginWorkerRequest(begin, Spec(maximumSeconds: 301)));
    }

    // ------------------------------------------------------- model report ----

    [Fact]
    public void ModelReportIsBoundedCompletedOnlyAndSetOnce()
    {
        using var fixture = new RemoteWorkerControlTests.RemoteStoreFixture();
        var task = CompleteTask(fixture);
        var report = new ModelTaskReport("Implemented the change", ["src/a.cs", "src/a.cs", "src/b.cs"], [new ModelTaskTestReport(WorkerTaskTestRecipes.DotnetTestRelease, "passed", "dotnet test passed")], new ModelTaskDeniedAction("network write", "network egress", "denied", true), ["none"]);

        Assert.Throws<OrganizationValidationException>(() => fixture.Store.RecordWorkerTaskModelReport(task.Id, task.Revision, new ModelTaskReport(new string('s', 2049), [], [], null, [])));
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.RecordWorkerTaskModelReport(task.Id, task.Revision, new ModelTaskReport("ok", ["../escape"], [], null, [])));

        var recorded = fixture.Store.RecordWorkerTaskModelReport(task.Id, task.Revision, report);
        Assert.NotNull(recorded.ModelReportHash);
        Assert.NotNull(recorded.ModelReportedAt);
        Assert.Equal("Completed", recorded.State);

        // An identical replay is idempotent; a changed report is a conflict.
        var replay = fixture.Store.RecordWorkerTaskModelReport(task.Id, recorded.Revision, report);
        Assert.Equal(recorded.ModelReportHash, replay.ModelReportHash);
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.RecordWorkerTaskModelReport(task.Id, recorded.Revision, report with { Summary = "different" }));
    }

    [Fact]
    public void ModelReportIsRejectedUnlessTheTaskIsCompleted()
    {
        using var fixture = new RemoteWorkerControlTests.RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        var task = fixture.Store.GetWorkerTask(request.TaskId)!;
        Assert.Equal("Requested", task.State);
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.RecordWorkerTaskModelReport(task.Id, task.Revision, new ModelTaskReport("ok", [], [], null, [])));
    }

    // --------------------------------------------------------- verification ----

    [Fact]
    public void HostVerificationRequiresAReportAndOnlyPassedReachesVerified()
    {
        using var fixture = new RemoteWorkerControlTests.RemoteStoreFixture();
        var task = CompleteTask(fixture);

        Assert.Throws<OrganizationValidationException>(() => fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "workspace-task-verify-v1"));

        task = fixture.Store.RecordWorkerTaskModelReport(task.Id, task.Revision, new ModelTaskReport("done", ["src/a.cs"], [new ModelTaskTestReport(WorkerTaskTestRecipes.DotnetTestRelease, "passed", "dotnet test passed")], null, []));
        var verification = fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "workspace-task-verify-v1");
        Assert.Equal("Pending", verification.State);
        Assert.Equal(task.Id, verification.TaskId);

        // A model report plus a pending verification is still not Verified.
        Assert.Equal("Completed", fixture.Store.GetWorkerTask(task.Id)!.State);

        var completed = fixture.Store.CompleteWorkerTaskVerification(verification.Id, verification.Revision, new HostTaskVerification(
            WorkerTaskVerificationStates.Passed, """{"files":1}""", """{"passed":1}""", null, null));
        Assert.Equal("Passed", completed.State);
        Assert.NotNull(completed.VerifiedAt);
        Assert.NotNull(completed.ManifestHash);
        Assert.NotNull(completed.TestSummaryHash);
        Assert.Equal("Verified", fixture.Store.GetWorkerTask(task.Id)!.State);
    }

    [Fact]
    public void FailedVerificationLeavesTheTaskCompletedAndRecordsTheFailure()
    {
        using var fixture = new RemoteWorkerControlTests.RemoteStoreFixture();
        var task = CompleteTask(fixture);
        task = fixture.Store.RecordWorkerTaskModelReport(task.Id, task.Revision, new ModelTaskReport("done", [], [], null, []));
        var verification = fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "workspace-task-verify-v1");

        Assert.Throws<OrganizationValidationException>(() => fixture.Store.CompleteWorkerTaskVerification(verification.Id, verification.Revision, new HostTaskVerification(WorkerTaskVerificationStates.Passed, null, null, null, null)));

        var failed = fixture.Store.CompleteWorkerTaskVerification(verification.Id, verification.Revision, new HostTaskVerification(
            WorkerTaskVerificationStates.Failed, null, null, null, "manifest mismatch"));
        Assert.Equal("Failed", failed.State);
        Assert.Null(failed.VerifiedAt);
        Assert.Equal("manifest mismatch", failed.FailureDetail);
        Assert.Equal("Completed", fixture.Store.GetWorkerTask(task.Id)!.State);
    }

    [Fact]
    public void StartupReconciliationMakesPendingVerificationUncertainClearsOnlyMatchingHoldAndAllowsRetry()
    {
        using var fixture = new RemoteWorkerControlTests.RemoteStoreFixture();
        var task = CompleteTask(fixture);
        task = fixture.Store.RecordWorkerTaskModelReport(task.Id, task.Revision, new ModelTaskReport("done", [], [], null, []));
        var pending = fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "workspace-task-verify-v1");
        using (var connection = Raw(fixture.DatabasePath))
        {
            Execute(connection, $"INSERT INTO dispatch_holds(id,runtime_binding_id,reason,active,detail,created_at,cleared_at,revision) VALUES('hold-manual-restart','{fixture.BindingId}','manual',1,'operator','2026-09-20T00:00:00.0000000+00:00',NULL,1)");
        }

        Assert.Equal(1, fixture.Store.ReconcileControllerStartup());

        var recovered = fixture.Store.GetWorkerTaskVerification(task.Id)!;
        Assert.Equal(pending.Id, recovered.Id);
        Assert.Equal(WorkerTaskVerificationStates.Uncertain, recovered.State);
        Assert.Equal("controller-restart-during-verification", recovered.FailureDetail);
        Assert.Equal(WorkerTaskStates.Completed, fixture.Store.GetWorkerTask(task.Id)!.State);
        Assert.Equal(0L, RawScalar(fixture.DatabasePath, "SELECT COUNT(*) FROM dispatch_holds WHERE reason='task-verification' AND active=1"));
        Assert.Equal(1L, RawScalar(fixture.DatabasePath, "SELECT COUNT(*) FROM dispatch_holds WHERE reason='manual' AND active=1 AND detail='operator'"));
        Assert.Equal(0, fixture.Store.ReconcileControllerStartup());

        using (var connection = Raw(fixture.DatabasePath))
            Execute(connection, "UPDATE dispatch_holds SET active=0,cleared_at='2026-09-20T00:01:00.0000000+00:00',revision=revision+1 WHERE reason='manual'");
        var retry = fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "workspace-task-verify-v1");
        Assert.Equal("workspace-task-verify-v1.2", retry.VerifierVersion);
        fixture.Store.CompleteWorkerTaskVerification(retry.Id, retry.Revision, new HostTaskVerification(
            WorkerTaskVerificationStates.Failed, null, null, null, "retry-failed"));
        var dispatched = fixture.Store.BeginWorkerRequest(new(
            fixture.EmployeeId, fixture.BindingId, "wrk-a", fixture.SessionId, fixture.NativeSessionId,
            "idem-after-verification-restart", "sha256:" + new string('7', 64), "sha256:" + new string('8', 64), 1, 1, "turn-after-verification-restart"), Spec());
        Assert.Equal("Intent", dispatched.State);
    }

    [Fact]
    public void StartupReconciliationLeavesCompletedVerificationAndTaskUntouched()
    {
        using var fixture = new RemoteWorkerControlTests.RemoteStoreFixture();
        var task = CompleteTask(fixture);
        task = fixture.Store.RecordWorkerTaskModelReport(task.Id, task.Revision, new ModelTaskReport("done", [], [], null, []));
        var pending = fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "workspace-task-verify-v1");
        var failed = fixture.Store.CompleteWorkerTaskVerification(pending.Id, pending.Revision, new HostTaskVerification(
            WorkerTaskVerificationStates.Failed, null, null, null, "test-failed"));

        Assert.Equal(0, fixture.Store.ReconcileControllerStartup());
        Assert.Equal(failed, fixture.Store.GetWorkerTaskVerification(task.Id));
        Assert.Equal(WorkerTaskStates.Completed, fixture.Store.GetWorkerTask(task.Id)!.State);
    }

    [Fact]
    public void HostVerificationRetriesPreserveImmutableAttemptsAndReadLatest()
    {
        using var fixture = new RemoteWorkerControlTests.RemoteStoreFixture();
        var task = CompleteTask(fixture);
        task = fixture.Store.RecordWorkerTaskModelReport(task.Id, task.Revision, new ModelTaskReport("done", [], [], null, []));
        var verification = fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "workspace-task-verify-v1");
        var replay = fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "workspace-task-verify-v1");
        Assert.Equal(verification.Id, replay.Id);
        Assert.Equal("workspace-task-verify-v1.1", verification.VerifierVersion);
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "other-verifier"));
        fixture.Store.CompleteWorkerTaskVerification(verification.Id, verification.Revision, new HostTaskVerification(
            WorkerTaskVerificationStates.Failed, null, null, null, "first failed"));

        var retry = fixture.Store.BeginWorkerTaskVerification(task.Id, task.Revision, "workspace-task-verify-v1");
        Assert.NotEqual(verification.Id, retry.Id);
        Assert.Equal("workspace-task-verify-v1.2", retry.VerifierVersion);
        Assert.Equal(retry.Id, fixture.Store.GetWorkerTaskVerification(task.Id)!.Id);

        using var connection = Raw(fixture.DatabasePath);
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM worker_task_verifications";
            Assert.Equal(2L, Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }
        Assert.Throws<SqliteException>(() => Execute(connection, $"UPDATE worker_task_verifications SET task_id='other' WHERE id='{verification.Id}'"));
        Assert.Throws<SqliteException>(() => Execute(connection, $"DELETE FROM worker_task_verifications WHERE id='{verification.Id}'"));
        Assert.Throws<SqliteException>(() => Execute(connection, $"INSERT INTO worker_task_verifications (id, task_id, state, verifier_version, created_at, updated_at, revision) VALUES ('{verification.Id}', '{task.Id}', 'Pending', 'workspace-task-verify-v1.3', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00', 1)"));
    }

    // -------------------------------------------------------- migration v12 ----

    [Fact]
    public void FaultAfterV12BackupBeforeV13MigrationRetainsRecoveryEvidenceAndRestartsCleanly()
    {
        using var root = new TempDirectory();
        var path = System.IO.Path.Combine(root.Path, "control.db");
        using (var store = new OrganizationStore(path))
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        DowngradeToCanonicalV12(path);
        ExecuteRaw(path, "INSERT INTO dispatch_holds(id,runtime_binding_id,reason,active,detail,created_at,cleared_at,revision) SELECT 'hold-v12-preserved',id,'manual',1,'v12 detail','2026-09-20T00:00:00.0000000+00:00',NULL,7 FROM runtime_bindings LIMIT 1;");
        Assert.DoesNotContain("task-verification", Raw(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='dispatch_holds'"), StringComparison.Ordinal);

        using (var faulted = new OrganizationStore(path))
        {
            faulted.BeforeMigrationCommit = () => throw new InvalidOperationException("simulated v12 migration interruption");
            Assert.Throws<InvalidOperationException>(() =>
                faulted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        }

        var backup = System.IO.Path.Combine(root.Path, OrganizationStore.SchemaV12BackupFileName);
        var hash = System.IO.Path.Combine(root.Path, OrganizationStore.SchemaV12BackupHashFileName);
        var retained = File.ReadAllBytes(backup);
        var retainedHash = File.ReadAllBytes(hash);
        Assert.Equal(12L, RawScalar(path, "SELECT version FROM schema_version"));
        Assert.DoesNotContain("task-verification", Raw(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='dispatch_holds'"), StringComparison.Ordinal);
        Assert.Equal(1L, RawScalar(path, "SELECT COUNT(*) FROM dispatch_holds WHERE id='hold-v12-preserved' AND revision=7"));
        Assert.False(File.Exists(backup + "-wal"));
        Assert.False(File.Exists(backup + "-shm"));
        Assert.DoesNotContain("task-verification", RawReadOnly(backup, "SELECT sql FROM sqlite_master WHERE type='table' AND name='dispatch_holds'"), StringComparison.Ordinal);
        Assert.Equal(1L, RawScalarReadOnly(backup, "SELECT COUNT(*) FROM dispatch_holds WHERE id='hold-v12-preserved' AND revision=7"));
        SqliteConnection.ClearAllPools();
        if (File.Exists(backup + "-wal")) File.Delete(backup + "-wal");
        if (File.Exists(backup + "-shm")) File.Delete(backup + "-shm");

        using (var restarted = new OrganizationStore(path))
            restarted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.Equal(13L, RawScalar(path, "SELECT version FROM schema_version"));
        Assert.Contains("task-verification", Raw(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='dispatch_holds'"), StringComparison.Ordinal);
        Assert.Equal(1L, RawScalar(path, "SELECT COUNT(*) FROM dispatch_holds WHERE id='hold-v12-preserved' AND reason='manual' AND active=1 AND detail='v12 detail' AND revision=7"));
        Assert.Equal(retained, File.ReadAllBytes(backup));
        Assert.Equal(retainedHash, File.ReadAllBytes(hash));
    }

    [Fact]
    public void ExactSchemaV12MigratesToV13WithVerifiedBackupAndPreservesLegacyTaskAndRequest()
    {
        using var root = new TempDirectory();
        var path = System.IO.Path.Combine(root.Path, "control.db");
        string bindingId;
        using (var store = new OrganizationStore(path))
        {
            var identity = store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            bindingId = identity.RuntimeBindingId;
        }

        DowngradeToCanonicalV12(path);
        Assert.Equal(12L, RawScalar(path, "SELECT version FROM schema_version"));
        Assert.Equal(0L, RawScalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'worker_task_verifications'"));
        SeedV12LegacyTaskAndRequest(path, bindingId);
        var descriptionHash = Raw(path, "SELECT description_hash FROM worker_tasks WHERE id='tsk-legacy'");

        using (var migrated = new OrganizationStore(path))
        {
            migrated.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

            var task = migrated.GetWorkerTask("tsk-legacy")!;
            Assert.Equal("Completed", task.State);
            Assert.Equal(3, task.Revision);
            // The migrated legacy row carries a deterministic, non-empty spec
            // derived from its description hash rather than a raw prompt.
            var (expectedJson, expectedHash) = OrganizationStore.LegacyTaskSpecForMigration(descriptionHash);
            Assert.Equal(expectedJson, task.TaskSpecJson);
            Assert.Equal(expectedHash, task.TaskSpecHash);
            Assert.True(task.TaskSpecJson.Length >= 2);
            Assert.Null(task.ModelReportHash);

            var request = migrated.GetWorkerRequest("req-legacy")!;
            Assert.Equal("tsk-legacy", request.TaskId);
            Assert.NotEqual(default, request.CreatedAt);
            Assert.Equal("Completed", request.State);

            // A new task can be verified on the rebuilt table.
            var fresh = migrated.GetWorkerTask(task.Id)!;
            fresh = migrated.RecordWorkerTaskModelReport(fresh.Id, fresh.Revision, new ModelTaskReport("migrated", [], [], null, []));
            var verification = migrated.BeginWorkerTaskVerification(fresh.Id, fresh.Revision, "workspace-task-verify-v1");
            migrated.CompleteWorkerTaskVerification(verification.Id, verification.Revision, new HostTaskVerification(
                WorkerTaskVerificationStates.Passed, """{"ok":true}""", """{"passed":1}""", null, null));
            Assert.Equal("Verified", migrated.GetWorkerTask(task.Id)!.State);
        }

        Assert.Equal(13L, RawScalar(path, "SELECT version FROM schema_version"));
        Assert.Equal(1L, RawScalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='worker_task_verifications'"));
        Assert.Equal(1L, RawScalar(path, "SELECT COUNT(*) FROM pragma_table_info('worker_tasks') WHERE name='task_spec_json'"));
        Assert.Equal(0L, RawScalar(path, "SELECT COUNT(*) FROM pragma_foreign_key_check"));

        var backup = System.IO.Path.Combine(root.Path, OrganizationStore.SchemaV12BackupFileName);
        var hash = System.IO.Path.Combine(root.Path, OrganizationStore.SchemaV12BackupHashFileName);
        Assert.True(File.Exists(backup));
        Assert.False(File.Exists(backup + "-wal"));
        Assert.False(File.Exists(backup + "-shm"));
        Assert.Equal(12L, RawScalar(backup, "SELECT version FROM schema_version"));
        Assert.Equal(0L, RawScalar(backup, "SELECT COUNT(*) FROM pragma_table_info('worker_tasks') WHERE name='task_spec_json'"));
        Assert.DoesNotContain("task-verification", RawReadOnly(backup, "SELECT sql FROM sqlite_master WHERE type='table' AND name='dispatch_holds'"), StringComparison.Ordinal);
        Assert.Equal(1L, RawScalar(backup, "SELECT COUNT(*) FROM worker_tasks WHERE id='tsk-legacy'"));
        Assert.Equal(1L, RawScalar(backup, "SELECT COUNT(*) FROM worker_requests WHERE id='req-legacy'"));
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant(),
            File.ReadAllText(hash).Trim());

        var retained = File.ReadAllBytes(backup);
        using (var restarted = new OrganizationStore(path))
        {
            restarted.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        Assert.Equal(retained, File.ReadAllBytes(backup));
    }

    [Theory]
    [InlineData("ALTER TABLE worker_tasks ADD COLUMN unknown_v12_value TEXT;")]
    [InlineData("PRAGMA foreign_keys=OFF; CREATE TABLE dispatch_holds_changed (id TEXT PRIMARY KEY,runtime_binding_id TEXT NOT NULL REFERENCES runtime_bindings(id) ON DELETE RESTRICT,reason TEXT NOT NULL CHECK (reason IN ('orientation-unacknowledged','stale','failed','policy-update','orientation-reload-required','manual','task-verification')),active INTEGER NOT NULL CHECK (active IN (0,1)),detail TEXT,created_at TEXT NOT NULL,cleared_at TEXT,revision INTEGER NOT NULL,UNIQUE(runtime_binding_id,reason)); INSERT INTO dispatch_holds_changed SELECT * FROM dispatch_holds; DROP TABLE dispatch_holds; ALTER TABLE dispatch_holds_changed RENAME TO dispatch_holds; PRAGMA foreign_keys=ON;")]
    public void UnknownSchemaV12ShapeFailsBeforeBackupOrMigration(string mutation)
    {
        using var root = new TempDirectory();
        var path = System.IO.Path.Combine(root.Path, "control.db");
        using (var store = new OrganizationStore(path))
        {
            store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        }

        DowngradeToCanonicalV12(path);
        ExecuteRaw(path, mutation);
        using var reopened = new OrganizationStore(path);
        Assert.Throws<OrganizationStoreCorruptException>(() => reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"));
        Assert.Equal(12L, RawScalar(path, "SELECT version FROM schema_version"));
        Assert.False(File.Exists(System.IO.Path.Combine(root.Path, OrganizationStore.SchemaV12BackupFileName)));
    }

    /// <summary>
    /// v13→v12 downgrade used by the migration tests: removes the verification
    /// table and triggers and rebuilds the old worker_tasks shape while preserving
    /// every task and request row.
    /// </summary>
    private static void DowngradeToCanonicalV12(string path)
    {
        ExecuteRaw(
            path,
            """
            PRAGMA foreign_keys = OFF;
            DROP TRIGGER worker_task_verifications_identity_immutable;
            DROP TRIGGER worker_task_verifications_no_delete;
            DROP TRIGGER worker_task_verifications_no_replace;
            DROP TABLE worker_task_verifications;
            CREATE TABLE dispatch_holds_v12 (
                id TEXT PRIMARY KEY,
                runtime_binding_id TEXT NOT NULL REFERENCES runtime_bindings(id) ON DELETE RESTRICT,
                reason TEXT NOT NULL CHECK (reason IN ('orientation-unacknowledged', 'stale', 'failed', 'policy-update', 'orientation-reload-required', 'manual')),
                active INTEGER NOT NULL CHECK (active IN (0, 1)),
                detail TEXT,
                created_at TEXT NOT NULL,
                cleared_at TEXT,
                revision INTEGER NOT NULL,
                UNIQUE (runtime_binding_id, reason)
            );
            INSERT INTO dispatch_holds_v12 (id, runtime_binding_id, reason, active, detail, created_at, cleared_at, revision)
                SELECT id, runtime_binding_id, reason, active, detail, created_at, cleared_at, revision FROM dispatch_holds
                WHERE reason <> 'task-verification';
            DROP TABLE dispatch_holds;
            ALTER TABLE dispatch_holds_v12 RENAME TO dispatch_holds;
            CREATE TABLE worker_tasks_v12 (id TEXT PRIMARY KEY, employee_id TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT, runtime_binding_id TEXT NOT NULL REFERENCES runtime_bindings(id) ON DELETE RESTRICT, worker_id TEXT NOT NULL REFERENCES worker_enrollments(worker_id) ON DELETE RESTRICT, description_hash TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('Requested','Uncertain','Running','Completed','Failed','Cancelled','Verified')), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, revision INTEGER NOT NULL);
            INSERT INTO worker_tasks_v12 (id, employee_id, runtime_binding_id, worker_id, description_hash, state, created_at, updated_at, revision)
                SELECT id, employee_id, runtime_binding_id, worker_id, description_hash, state, created_at, updated_at, revision FROM worker_tasks;
            DROP TABLE worker_tasks;
            ALTER TABLE worker_tasks_v12 RENAME TO worker_tasks;
            UPDATE schema_version SET version = 12;
            PRAGMA foreign_keys = ON;
            """);
    }

    private static void SeedV12LegacyTaskAndRequest(string path, string bindingId)
    {
        var now = "2026-09-16T00:00:00.0000000+00:00";
        var descriptionHash = "sha256:" + new string('5', 64);
        ExecuteRaw(
            path,
            $"""
            INSERT INTO acp_sessions (id, employee_id, native_session_id, title, status, created_at, updated_at)
                SELECT 'ses-legacy', id, 'native-legacy', 'Legacy', 'active', '{now}', '{now}' FROM employees LIMIT 1;
            INSERT INTO worker_enrollments (
                worker_id, runtime_binding_id, host_id, organization_id,
                container_name, control_volume_name, home_volume_name, workspace_volume_name, session_volume_name,
                resource_labels_hash, expected_image_digest, expected_platform, controller_id, key_file_path, key_id,
                bridge_socket_path, lifecycle_status, worker_generation, process_generation, ownership_epoch, enabled,
                created_at, updated_at, revision)
            VALUES ('wrk-legacy', '{bindingId}', '{ExecutionHosts.LocalDockerId}', (SELECT id FROM organizations LIMIT 1),
                'container-wrk-legacy', 'control-wrk-legacy', 'home-wrk-legacy', 'workspace-wrk-legacy', 'session-wrk-legacy',
                'sha256:{new string('a', 64)}', '{ValidDigest}', 'linux/amd64', 'controller-v12', '/control/key',
                'sha256:{new string('c', 64)}', '/control/bridge.sock', 'enrolled', 1, 1, 1, 1, '{now}', '{now}', 1);
            INSERT INTO worker_tasks (id, employee_id, runtime_binding_id, worker_id, description_hash, state, created_at, updated_at, revision)
                SELECT 'tsk-legacy', b.employee_id, b.id, 'wrk-legacy', '{descriptionHash}', 'Completed', '{now}', '{now}', 3
                FROM runtime_bindings b WHERE b.id = '{bindingId}';
            INSERT INTO worker_requests (id, task_id, session_id, native_session_id, employee_id, runtime_binding_id, worker_id, payload_hash, state, ownership_epoch, process_generation, turn_id, outcome_hash, outcome_category, outcome_bytes, idempotency_key, created_at, forwarded_at, completed_at, updated_at, revision)
                SELECT 'req-legacy', 'tsk-legacy', 'ses-legacy', 'native-legacy', b.employee_id, b.id, 'wrk-legacy',
                    'sha256:{new string('d', 64)}', 'Completed', 1, 1, 'turn-legacy', NULL, NULL, NULL, 'idem-legacy',
                    '{now}', '{now}', '{now}', '{now}', 2
                FROM runtime_bindings b WHERE b.id = '{bindingId}';
            """);
    }

    private static WorkerTaskRecord CompleteTask(RemoteWorkerControlTests.RemoteStoreFixture fixture)
    {
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Forwarded");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarded", "Completed");
        return fixture.Store.GetWorkerTask(request.TaskId)!;
    }

    private static SqliteConnection Raw(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteRaw(string path, string sql)
    {
        using var connection = Raw(path);
        Execute(connection, sql);
    }

    private static object? RawScalar(string path, string sql)
    {
        using var connection = Raw(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string Raw(string path, string sql) =>
        Convert.ToString(RawScalar(path, sql), System.Globalization.CultureInfo.InvariantCulture)!;

    private static object? RawScalarReadOnly(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string RawReadOnly(string path, string sql) =>
        Convert.ToString(RawScalarReadOnly(path, sql), System.Globalization.CultureInfo.InvariantCulture)!;

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-v13-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, true); } catch (IOException) { }
        }
    }
}
