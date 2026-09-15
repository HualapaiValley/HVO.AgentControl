using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class RemoteWorkerControlTests
{
    [Fact]
    public void WorkerControlIsDisabledByDefaultAndRejectsUnsafeConfiguration()
    {
        var options = new WorkerControlOptions();
        Assert.False(options.Enabled);
        Assert.Contains(options.Validate(false), error => error.Contains("ControllerId", StringComparison.Ordinal));
        options.ControllerId = "controller-a"; options.ApprovedImageDigest = "sha256:" + new string('a', 64); options.ConnectorExecutable = "ssh";
        Assert.Contains(options.Validate(false), error => error.Contains("/usr/bin/ssh", StringComparison.Ordinal));
    }

    [Fact]
    public void DeterministicSshCommandPinsHostKeyAndRejectsInjection()
    {
        var host = new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Port = 2222, Username = "docker", KnownHostsPath = "/control/known_hosts", IdentityFilePath = "/control/id_ed25519" };
        var options = new WorkerControlOptions { ControllerId = "controller-a", ApprovedImageDigest = "sha256:" + new string('a', 64) };
        var command = RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.ContainerInspect, ["agentcontrol-worker-a"]);
        Assert.Equal("/usr/bin/ssh", command.Executable);
        Assert.Contains("StrictHostKeyChecking=yes", command.Arguments);
        Assert.Contains("UserKnownHostsFile=/control/known_hosts", command.Arguments);
        Assert.Contains("IdentitiesOnly=yes", command.Arguments);
        Assert.DoesNotContain("StrictHostKeyChecking=no", command.Arguments);
        Assert.Throws<InvalidOperationException>(() => RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.ContainerInspect, ["x;id"]));
    }

    [Fact]
    public void DestructiveOwnershipRequiresEveryExactLabel()
    {
        var identity = new WorkerResourceIdentity("org-a", "controller-a", "host-a", "worker-a", "binding-a", "operation-a");
        RemoteWorkerCommandBuilder.RequireOwnedLabels(identity.Labels, identity);
        var changed = identity.Labels.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal); changed["agentcontrol.worker"] = "other";
        Assert.Throws<InvalidOperationException>(() => RemoteWorkerCommandBuilder.RequireOwnedLabels(changed, identity));
    }

    [Fact]
    public void FreshSchemaIsV4AndCarriesRemoteWorkerTablesWithoutSeededEnrollment()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Path, "control.db");
        using (var store = new OrganizationStore(path)) store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        using var connection = Open(path);
        Assert.Equal(4L, Convert.ToInt64(Scalar(connection, "SELECT version FROM schema_version")));
        foreach (var table in new[] { "execution_hosts", "worker_enrollments", "worker_cursors", "worker_events", "worker_pending_permissions", "worker_tasks", "worker_requests", "provisioning_operations", "resource_records", "worker_recovery_obligations", "remote_terminal_viewers" }) Assert.Equal(1L, Convert.ToInt64(Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'")));
        Assert.Equal(0L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM worker_enrollments")));
    }

    [Fact]
    public void ExactReleasedV3MigratesWithVerifiedCreateOnceBackupAndPreservesPolicy()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Path, "control.db");
        using (var store = new OrganizationStore(path)) store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        using (var connection = Open(path))
        {
            foreach (var table in new[] { "remote_terminal_viewers", "worker_pending_permissions", "worker_events", "worker_recovery_obligations", "resource_records", "provisioning_operations", "worker_cancellations", "worker_requests", "worker_tasks", "worker_cursors", "worker_enrollments", "execution_hosts" }) connection.Execute($"DROP TABLE {table}");
            connection.Execute("UPDATE schema_version SET version=3");
        }
        var policyBefore = Raw(path, "SELECT version || ':' || revision || ':' || summary FROM permission_policies");
        using (var migrated = new OrganizationStore(path)) migrated.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.Equal(policyBefore, Raw(path, "SELECT version || ':' || revision || ':' || summary FROM permission_policies"));
        var backup = Path.Combine(temp.Path, OrganizationStore.SchemaV3BackupFileName); var hash = Path.Combine(temp.Path, OrganizationStore.SchemaV3BackupHashFileName);
        Assert.True(File.Exists(backup)); Assert.Equal(3L, Convert.ToInt64(RawScalar(backup, "SELECT version FROM schema_version")));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant(), File.ReadAllText(hash).Trim());
        var bytes = File.ReadAllBytes(backup); using (var reopened = new OrganizationStore(path)) reopened.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh"); Assert.Equal(bytes, File.ReadAllBytes(backup));
    }

    [Fact]
    public void PersistedWorkerViewerCapabilityControlsRemoteSnapshot()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { ViewerSupported = true, ViewerAvailable = true }, []);
        var overview = fixture.Store.GetOverview();
        var control = new HVO.AgentControl.Runtime.AcpControlHost(Microsoft.Extensions.Options.Options.Create(new HVO.AgentControl.Runtime.ControlOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<HVO.AgentControl.Runtime.AcpControlHost>.Instance);
        typeof(HVO.AgentControl.Runtime.AcpControlHost).GetField("_organization", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(control, fixture.Store);
        var snapshot = Assert.Single(new RemoteWorkerStatusProvider(control).Snapshot(overview).Values);
        Assert.True(snapshot.ViewerSupported);
        Assert.True(snapshot.ViewerAvailable);
        fixture.Store.RecordWorkerConnectionState(enrollment.WorkerId, "disconnected");
        snapshot = Assert.Single(new RemoteWorkerStatusProvider(control).Snapshot(overview).Values);
        Assert.True(snapshot.ViewerSupported);
        Assert.False(snapshot.ViewerAvailable);
    }

    [Fact]
    public void HostProbeFailsClosedForMacSharedOrWrongPlatform()
    {
        using var temp = new TempDirectory(); var known = Path.Combine(temp.Path, "known_hosts"); File.WriteAllText(known, "worker ssh-ed25519 " + Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray()) + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(known, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var host = new ApprovedExecutionHost { Id = "host-a", Hostname = "worker", Username = "docker", KnownHostsPath = known, IdentityFilePath = "/not/read/in-parser" };
        var json = JsonSerializer.Serialize(new { OSType = "linux", Architecture = "amd64", SharedStorage = false, MemTotal = 2L * 1024 * 1024 * 1024, NCPU = 2, VolumeFreeBytes = 2L * 1024 * 1024 * 1024, LimitsSupported = true, HostKeyAlgorithm = "ssh-ed25519", HostKeyFingerprint = "SHA256:abc", ServerVersion = "28.0", ApiVersion = "1.48", Driver = "overlay2", BackingFilesystem = "ext4" });
        Assert.Equal("valid", ExecutionHostRegistry.ParseProbe(json, host, "linux/amd64").CapabilityStatus);
        Assert.Equal("invalid", ExecutionHostRegistry.ParseProbe(json.Replace("linux", "darwin", StringComparison.Ordinal), host, "linux/amd64").CapabilityStatus);
    }

    [Fact]
    public void HostRegistrationStartsUnenrolledAndHidesKnownHostsPath()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Path, "control.db");
        using var store = new OrganizationStore(path); store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        var record = store.RegisterExecutionHost("host-a", "worker.example", 22, "docker", "/controller/private/known_hosts", new ExecutionHostRegistration("host-a", "host-a", "Host A"));
        Assert.False(record.Enrolled); Assert.Equal("configured", record.KnownHostsReferenceStatus);
        Assert.DoesNotContain("/controller/private", JsonSerializer.Serialize(record), StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationRejectsLooseSlugAndDisplayName()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Path, "control.db");
        using var store = new OrganizationStore(path); store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        Assert.Throws<OrganizationValidationException>(() => store.RegisterExecutionHost("host-a", "worker", 22, "docker", "/known", new ExecutionHostRegistration("host-a", "Host A", "Host A")));
        Assert.Throws<OrganizationValidationException>(() => store.RegisterExecutionHost("host-a", "worker", 22, "docker", "/known", new ExecutionHostRegistration("host-a", "host-a", "Host <A>")));
    }

    [Fact]
    public void KnownHostsParserUsesExactLocalEntryAndRejectsHashedWildcardAndMultiple()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Path, "known_hosts"); var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray());
        File.WriteAllText(path, $"[worker.example]:2222 ssh-ed25519 {key}\n"); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var host = new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Port = 2222, Username = "docker", KnownHostsPath = path, IdentityFilePath = "/unused" };
        var parsed = KnownHostsParser.Parse(host, ControllerPrivateFile.EffectiveUid); Assert.Equal("ssh-ed25519", parsed.Algorithm); Assert.StartsWith("SHA256:", parsed.Fingerprint, StringComparison.Ordinal);
        File.WriteAllText(path, $"*.example ssh-ed25519 {key}\n"); Assert.Throws<InvalidOperationException>(() => KnownHostsParser.Parse(host, ControllerPrivateFile.EffectiveUid));
        File.WriteAllText(path, $"[worker.example]:2222 ssh-ed25519 {key}\n[worker.example]:2222 ssh-ed25519 {key}\n"); Assert.Throws<InvalidOperationException>(() => KnownHostsParser.Parse(host, ControllerPrivateFile.EffectiveUid));
    }

    [Fact]
    public void ControllerPrivateFileRejectsSymlinkHardlinkAndWrongMode()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Path, "key"); File.WriteAllBytes(path, new byte[32]); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(32, ControllerPrivateFile.ReadExact(path, ControllerPrivateFile.EffectiveUid, ControllerFileModes.Private0600, 32).Length);
        var symlink = Path.Combine(temp.Path, "symlink"); File.CreateSymbolicLink(symlink, path); Assert.Throws<InvalidOperationException>(() => ControllerPrivateFile.OpenRead(symlink, ControllerPrivateFile.EffectiveUid, ControllerFileModes.Private0600));
        var hardlink = Path.Combine(temp.Path, "hard"); Assert.Equal(0, link(path, hardlink)); Assert.Throws<InvalidOperationException>(() => ControllerPrivateFile.OpenRead(path, ControllerPrivateFile.EffectiveUid, ControllerFileModes.Private0600));
        File.Delete(hardlink); File.SetUnixFileMode(path, UnixFileMode.UserRead); Assert.Throws<InvalidOperationException>(() => ControllerPrivateFile.OpenRead(path, ControllerPrivateFile.EffectiveUid, ControllerFileModes.Private0600));
    }

    [Fact]
    public void RemoteShellTokensAreSingleQuotedAndRejectQuoteOrControl()
    {
        Assert.Equal("'safe-token'", RemoteWorkerCommandBuilder.QuoteShell("safe-token"));
        Assert.Throws<InvalidOperationException>(() => RemoteWorkerCommandBuilder.QuoteShell("bad'quote"));
        Assert.Throws<InvalidOperationException>(() => RemoteWorkerCommandBuilder.QuoteShell("bad\nline"));
    }

    [Fact]
    public void TypedContainerCreatePinsIsolationLimitsVolumesAndDigest()
    {
        var host = new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Username = "docker", KnownHostsPath = "/control/known_hosts", IdentityFilePath = "/control/id" };
        var digest = "sha256:" + new string('a', 64); var options = new WorkerControlOptions { ControllerId = "controller-a", ApprovedImageDigest = digest };
        var identity = new WorkerResourceIdentity("org-a", "controller-a", "host-a", "worker-a", "binding-a", "operation-a");
        var volumes = new[] { new NamedVolumeMount("control-a", "/control"), new NamedVolumeMount("home-a", "/home/worker"), new NamedVolumeMount("workspace-a", "/workspace"), new NamedVolumeMount("session-a", "/session") };
        var command = RemoteWorkerCommandBuilder.BuildContainerCreate(host, options, new ContainerCreateSpec("worker-a", digest, "linux/amd64", identity, volumes, options.MemoryBytes, options.CpuLimit, options.PidsLimit)); var remote = command.Arguments[^1];
        Assert.Contains("--network 'none'", remote, StringComparison.Ordinal); Assert.Contains("--cap-drop 'ALL'", remote, StringComparison.Ordinal); foreach (var capability in new[] { "CHOWN", "SETUID", "SETGID", "KILL" }) Assert.Contains($"--cap-add '{capability}'", remote, StringComparison.Ordinal); Assert.Contains("'WORKER_CONTROL_DIRECTORY=/control'", remote, StringComparison.Ordinal); Assert.Contains("'WORKER_ID=worker-a'", remote, StringComparison.Ordinal); Assert.Contains("'WORKER_CONTROLLER_ID=controller-a'", remote, StringComparison.Ordinal); Assert.Contains("--read-only", remote, StringComparison.Ordinal); Assert.DoesNotContain("--privileged", remote, StringComparison.Ordinal); Assert.Contains("'" + digest + "'", remote, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrk-a")]
    [InlineData("worker.1")]
    [InlineData("worker:one")]
    public void EnrollmentGeneratesFourNamedVolumesAndContainsNoKeyBytes(string workerId)
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrollment(workerId);
        Assert.StartsWith("agentcontrol-control-", enrollment.ControlVolumeName, StringComparison.Ordinal);
        Assert.StartsWith("agentcontrol-home-", enrollment.HomeVolumeName, StringComparison.Ordinal);
        Assert.StartsWith("agentcontrol-workspace-", enrollment.WorkspaceVolumeName, StringComparison.Ordinal);
        Assert.StartsWith("agentcontrol-session-", enrollment.SessionVolumeName, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(new byte[32]), JsonSerializer.Serialize(enrollment), StringComparison.Ordinal);
    }

    [Fact]
    public void EnrollmentRequiresReadyEnabledHost()
    {
        using var fixture = new RemoteStoreFixture(probeHost: false);
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.CreateWorkerEnrollmentForPlan(fixture.BindingId, "host-a", "controller-a", fixture.Digest, "linux/amd64", fixture.KeyPath, fixture.KeyId, "wrk-a"));
    }

    [Fact]
    public void EnrollmentRequiresDeveloperContainerBinding()
    {
        using var fixture = new RemoteStoreFixture(makeDeveloper: false);
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.CreateWorkerEnrollmentForPlan(fixture.BindingId, "host-a", "controller-a", fixture.Digest, "linux/amd64", fixture.KeyPath, fixture.KeyId, "wrk-a"));
    }

    [Fact]
    public void EnrollmentLifecycleUsesConditionalRevisionAndTransition()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrollment();
        enrollment = fixture.Store.UpdateEnrollmentLifecycle(enrollment.WorkerId, enrollment.Revision, "planned", "provisioning");
        Assert.Equal("provisioning", enrollment.LifecycleStatus);
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.UpdateEnrollmentLifecycle(enrollment.WorkerId, 1, "planned", "provisioning"));
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.UpdateEnrollmentLifecycle(enrollment.WorkerId, enrollment.Revision, "provisioning", "stopped"));
    }

    [Fact]
    public void SessionRowAndNativeIdentityRemainDistinctAcrossRequestAndViewer()
    {
        using var fixture = new RemoteStoreFixture(); var request = fixture.CreateEligibleRequest();
        Assert.NotEqual(fixture.SessionId, fixture.NativeSessionId);
        Assert.Equal(fixture.SessionId, request.SessionRecordId);
        Assert.Equal(fixture.NativeSessionId, request.NativeSessionId);
        var viewer = fixture.Store.BeginRemoteTerminalViewer(request.WorkerId, fixture.SessionId, 1);
        Assert.Equal(fixture.SessionId, viewer.SessionRecordId);
        using var connection = Open(fixture.DatabasePath);
        Assert.Equal(fixture.SessionId, Convert.ToString(Scalar(connection, $"SELECT session_id FROM worker_requests WHERE id='{request.Id}'")));
        Assert.Equal(fixture.NativeSessionId, Convert.ToString(Scalar(connection, $"SELECT native_session_id FROM worker_requests WHERE id='{request.Id}'")));
    }

    [Fact]
    public void EmptyWorkerStatusCannotAdvanceControllerCursorFromInformationalAck()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { AcknowledgedWorkerGeneration = 9, AcknowledgedSequence = 99 }, []);
        var cursor = fixture.Store.GetWorkerCursor(enrollment.WorkerId)!;
        Assert.Equal(0, cursor.AcknowledgedWorkerGeneration);
        Assert.Equal(0, cursor.AcknowledgedSequence);
    }

    [Fact]
    public void EventCommitAdvancesControllerCursor()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var payload = "{\"safe\":true}";
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 1, "acp-event", payload, System.Text.Encoding.UTF8.GetByteCount(payload))]);
        var cursor = fixture.Store.GetWorkerCursor(enrollment.WorkerId)!;
        Assert.Equal(1, cursor.AcknowledgedWorkerGeneration); Assert.Equal(1, cursor.AcknowledgedSequence);
    }

    [Fact]
    public void DuplicateIdenticalEventDeduplicates()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled(); var payload = "{}"; var item = new ControllerWorkerEvent(1, 1, "event", payload, 2);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [item]); fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [item]);
        Assert.Single(fixture.Store.ListWorkerEvents(enrollment.WorkerId));
    }

    [Fact]
    public void DuplicateChangedEventConflicts()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 1, "event", "{}", 2)]);
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 1, "other", "{}", 2)]));
    }

    [Fact]
    public void EventByteCountMustMatchSanitizedPayload()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 1, "event", "{}", 99)]));
    }

    [Theory]
    [InlineData("replay-gap", "replay-gap")]
    [InlineData("replay-loss-unreconciled", "replay-loss")]
    [InlineData("journal-failed", "journal-failure")]
    [InlineData("permission-pending", "permission-pending")]
    [InlineData("process-exited", "process-interrupted")]
    public void WorkerHoldsCreateSetRecoveryObligations(string reason, string expectedKind)
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { DispatchHeld = true, HoldReasons = [reason] }, []);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { DispatchHeld = true, HoldReasons = [reason] }, []);
        var obligations = fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true);
        Assert.Single(obligations, x => x.Kind == expectedKind);
    }

    [Fact]
    public void RecoveryRequiresExactMarker()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled(); fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { DispatchHeld = true, HoldReasons = ["replay-gap"] }, []);
        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true));
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.ResolveRecoveryObligation(enrollment.WorkerId, obligation.Kind, "sha256:" + new string('0', 64)));
        fixture.Store.ResolveRecoveryObligation(enrollment.WorkerId, obligation.Kind, obligation.MarkerHash); Assert.Empty(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true));
    }

    [Fact]
    public void ConnectionEpochAndStatusArePersisted()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled(); fixture.Store.RecordWorkerConnectionState(enrollment.WorkerId, "connecting"); fixture.Store.RecordWorkerConnectionState(enrollment.WorkerId, "authenticated", 9);
        var cursor = fixture.Store.GetWorkerCursor(enrollment.WorkerId)!; Assert.Equal("authenticated", cursor.ConnectionState); Assert.Equal(9, cursor.ObservedOwnershipEpoch);
    }

    [Fact]
    public void InvalidConnectionStateIsRejected()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled(); Assert.Throws<OrganizationValidationException>(() => fixture.Store.RecordWorkerConnectionState(enrollment.WorkerId, "online"));
    }

    [Fact]
    public void ProvisioningIntentIsIdempotentForExactHash()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrollment(); var hash = "sha256:" + new string('a', 64);
        var first = fixture.Store.AddProvisioningIntent(enrollment.WorkerId, enrollment.HostId, enrollment.RuntimeBindingId, "start", hash); var second = fixture.Store.AddProvisioningIntent(enrollment.WorkerId, enrollment.HostId, enrollment.RuntimeBindingId, "start", hash);
        Assert.Equal(first.Id, second.Id); Assert.Single(fixture.Store.ListProvisioningOperations(enrollment.WorkerId));
    }

    [Fact]
    public void ProvisioningApplyingExceptionStateCanBecomeUncertainThenApplied()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrollment(); var op = fixture.Store.AddProvisioningIntent(enrollment.WorkerId, enrollment.HostId, enrollment.RuntimeBindingId, "start", "sha256:" + new string('b', 64));
        op = fixture.Store.TransitionProvisioningOperation(op.Id, op.Revision, "Intent", "Applying"); op = fixture.Store.TransitionProvisioningOperation(op.Id, op.Revision, "Applying", "Uncertain", error: "effect-unknown"); op = fixture.Store.TransitionProvisioningOperation(op.Id, op.Revision, "Uncertain", "Applied", "inspected"); Assert.Equal("Applied", op.State);
    }

    [Fact]
    public void ResourceCanBeMarkedForeignAndIsListed()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrollment(); var op = fixture.Store.AddProvisioningIntent(enrollment.WorkerId, enrollment.HostId, enrollment.RuntimeBindingId, "volume-create", "sha256:" + new string('c', 64)); var resource = fixture.Store.AddResourceIntent(op.Id, enrollment.HostId, enrollment.WorkerId, "volume", enrollment.ControlVolumeName, enrollment.ResourceLabelsHash);
        resource = fixture.Store.TransitionResource(resource.Id, resource.Revision, "planned", "foreign"); Assert.Equal("foreign", resource.State);
    }

    [Fact]
    public void StartupReconciliationInterruptsIntentWithoutCreatingWriteUncertainty()
    {
        using var fixture = new RemoteStoreFixture(); var request = fixture.CreateEligibleRequest();
        Assert.Equal(1, fixture.Store.ReconcileControllerStartup());
        Assert.Equal("Interrupted", fixture.Store.GetWorkerRequest(request.Id)!.State);
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");
    }

    [Fact]
    public void StartupReconciliationMarksForwardingRequestUncertainAndCreatesObligation()
    {
        using var fixture = new RemoteStoreFixture(); var request = fixture.CreateEligibleRequest(); request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        Assert.Equal(1, fixture.Store.ReconcileControllerStartup()); Assert.Equal("Uncertain", fixture.Store.GetWorkerRequest(request.Id)!.State); Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");
    }

    [Fact]
    public void RequestIdempotencyReturnsSameRecordAndChangedPayloadRejects()
    {
        using var fixture = new RemoteStoreFixture(); var first = fixture.CreateEligibleRequest(); var same = fixture.BeginRequest("sha256:" + new string('d', 64)); Assert.Equal(first.Id, same.Id);
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.BeginRequest("sha256:" + new string('e', 64)));
    }

    [Fact]
    public void RequestRejectsWrongEmployeeAndSession()
    {
        using var fixture = new RemoteStoreFixture(); fixture.CreateEnrolledAndReady();
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.BeginWorkerRequest(new("emp-wrong", fixture.BindingId, "wrk-a", fixture.SessionId, fixture.NativeSessionId, "idem-wrong", "sha256:" + new string('d', 64), "sha256:" + new string('f', 64), 1, 1, "turn-wrong")));
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.BeginWorkerRequest(new(fixture.EmployeeId, fixture.BindingId, "wrk-a", "ses-wrong", fixture.NativeSessionId, "idem-session", "sha256:" + new string('d', 64), "sha256:" + new string('f', 64), 1, 1, "turn-wrong")));
    }

    [Fact]
    public void OrientationHoldBlocksRequest()
    {
        using var fixture = new RemoteStoreFixture(); fixture.CreateEnrolledAndReady(); using var connection = Open(fixture.DatabasePath); connection.Execute($"INSERT INTO dispatch_holds(id,runtime_binding_id,reason,active,detail,created_at,cleared_at,revision) VALUES('hold-remote','{fixture.BindingId}','manual',1,'test','2026-09-15T00:00:00.0000000+00:00',NULL,1) ON CONFLICT(runtime_binding_id,reason) DO UPDATE SET active=1");
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.BeginRequest("sha256:" + new string('d', 64)));
    }

    [Fact]
    public void ExactRemoteForwardingCanReconcileUncertainRequestToForwardedWithoutClearingRecovery()
    {
        using var fixture = new RemoteStoreFixture(); var request = fixture.CreateEligibleRequest(); request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding"); request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Uncertain", "Forwarded");
        Assert.Equal("Forwarded", request.State);
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");
    }

    [Fact]
    public void UncertainRequestCannotReturnToForwarding()
    {
        using var fixture = new RemoteStoreFixture(); var request = fixture.CreateEligibleRequest(); request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding"); request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain");
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Uncertain", "Forwarding"));
    }

    [Fact]
    public void CancellationIntentIsDurableBeforeForwarded()
    {
        using var fixture = new RemoteStoreFixture(); var request = fixture.CreateEligibleRequest(); request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding"); request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Forwarded"); var cancellation = fixture.Store.BeginWorkerCancellation(request.Id, "sha256:" + new string('9', 64));
        Assert.Equal("Intent", cancellation.State); cancellation = fixture.Store.TransitionWorkerCancellation(cancellation.Id, cancellation.Revision, "Intent", "Forwarded"); Assert.Equal("Forwarded", cancellation.State);
    }

    [Fact]
    public void PendingPermissionProjectionUpsertsExactBoundedOptionsWithoutPayload()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var pending = new ControllerPendingPermission(1, 1, "req-test", "turn-test", "perm-test", "sha256:" + new string('7', 64), ["allow_once", "reject_once"], "pending");
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending }, []);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending }, []);
        var item = Assert.Single(fixture.Store.ListWorkerPendingPermissions(enrollment.WorkerId));
        Assert.Equal("pending", item.State); Assert.Equal(["allow_once", "reject_once"], item.OptionIds); Assert.DoesNotContain("raw", JsonSerializer.Serialize(item), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PendingPermissionAbsentInvalidatesPriorProjectionAndClearsGeneratedHold()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var pending = new ControllerPendingPermission(1, 1, "req-test", "turn-test", "perm-test", "sha256:" + new string('7', 64), ["reject_once"], "pending");
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending, HoldReasons = ["permission-pending"] }, []);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), []);
        Assert.Equal("invalidated", Assert.Single(fixture.Store.ListWorkerPendingPermissions(enrollment.WorkerId)).State);
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "permission-pending");
    }

    [Fact]
    public void PriorEpochPendingStatusIsInvalidatedAndHeldInsteadOfRejectingReconnect()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var prior = new ControllerPendingPermission(1, 1, "req-test", "turn-test", "perm-test", "sha256:" + new string('7', 64), ["reject_once"], "pending");
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { OwnershipEpoch = 2, PendingPermissionHash = prior.PayloadHash, PendingPermission = prior }, []);
        Assert.DoesNotContain(fixture.Store.ListWorkerPendingPermissions(enrollment.WorkerId), x => x.State == "pending");
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "ownership-changed");
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Fact]
    public void PendingPermissionProcessOrEpochChangeInvalidatesPriorItem()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var pending = new ControllerPendingPermission(1, 1, "req-test", "turn-test", "perm-test", "sha256:" + new string('7', 64), ["reject_once"], "pending");
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending }, []);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { ProcessGeneration = 2, OwnershipEpoch = 2 }, []);
        Assert.Equal("invalidated", Assert.Single(fixture.Store.ListWorkerPendingPermissions(enrollment.WorkerId)).State);
    }

    [Fact]
    public void PendingPermissionOptionsAreBoundedAndStableIdsOnly()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var tooMany = Enumerable.Range(0, 17).Select(x => "reject-" + x).ToArray();
        var pending = new ControllerPendingPermission(1, 1, "req-test", "turn-test", "perm-test", "sha256:" + new string('7', 64), tooMany, "pending");
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending }, []));
    }

    [Fact]
    public void PendingPermissionChangedTupleForSameDecisionConflicts()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var pending = new ControllerPendingPermission(1, 1, "req-test", "turn-test", "perm-test", "sha256:" + new string('7', 64), ["reject_once"], "pending");
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending }, []);
        var changed = pending with { RequestId = "req-spoof" };
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = changed.PayloadHash, PendingPermission = changed }, []));
    }

    [Fact]
    public void PendingPermissionDuplicateOptionsAreRejected()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var pending = new ControllerPendingPermission(1, 1, "req-test", "turn-test", "perm-test", "sha256:" + new string('7', 64), ["reject_once", "reject_once"], "pending");
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending }, []));
    }

    [Fact]
    public void PendingPermissionDecisionTransitionRequiresExactRevision()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var pending = new ControllerPendingPermission(1, 1, "req-test", "turn-test", "perm-test", "sha256:" + new string('7', 64), ["reject_once"], "pending");
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending }, []);
        var item = Assert.Single(fixture.Store.ListWorkerPendingPermissions(enrollment.WorkerId));
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.TransitionWorkerPendingPermission(item.WorkerId, item.DecisionId, item.Revision + 1, "pending", "decided"));
        Assert.Equal("decided", fixture.Store.TransitionWorkerPendingPermission(item.WorkerId, item.DecisionId, item.Revision, "pending", "decided").State);
    }

    [Fact]
    public void WorkerGeneratedRecoveryProjectionClearsOnlyAbsentGeneratedCategories()
    {
        using var fixture = new RemoteStoreFixture(); var request = fixture.CreateEligibleRequest(); request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding"); fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain");
        fixture.Store.RecordWorkerStatusAndEvents(request.WorkerId, fixture.Status() with { HoldReasons = ["replay-gap"] }, []);
        fixture.Store.RecordWorkerStatusAndEvents(request.WorkerId, fixture.Status(), []);
        var active = fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true);
        Assert.Contains(active, x => x.Kind == "request-uncertain"); Assert.DoesNotContain(active, x => x.Kind == "replay-gap");
    }

    [Fact]
    public void EventPayloadIsStoredOnlyAsHashAndInputIsBounded()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled(); var raw = "{\"secret\":\"never-store\"}";
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 1, "event", raw, System.Text.Encoding.UTF8.GetByteCount(raw))]);
        using var connection = Open(fixture.DatabasePath); Assert.DoesNotContain("never-store", Raw(fixture.DatabasePath, "SELECT group_concat(payload_hash) FROM worker_events"), StringComparison.Ordinal);
        var huge = new string('x', 64 * 1024 + 1); Assert.Throws<OrganizationValidationException>(() => fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 2, "event", huge, huge.Length)]));
    }

    [Fact]
    public void CursorAdvanceIsLexicographicAcrossGenerations()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(2, 1, "event", "{}", 2)]);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 99, "event", "{}", 2)]);
        var cursor = fixture.Store.GetWorkerCursor(enrollment.WorkerId)!; Assert.Equal(2, cursor.AcknowledgedWorkerGeneration); Assert.Equal(1, cursor.AcknowledgedSequence);
    }

    [Fact]
    public void LocalRequestUncertainObligationSurvivesRepeatedHealthySnapshots()
    {
        using var fixture = new RemoteStoreFixture(); var request = fixture.CreateEligibleRequest(); request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding"); fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain");
        fixture.Store.RecordWorkerStatusAndEvents(request.WorkerId, fixture.Status(), []); fixture.Store.RecordWorkerStatusAndEvents(request.WorkerId, fixture.Status(), []);
        Assert.Single(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");
    }

    [Fact]
    public void ViewerRequestedCanFailButCannotSkipToDetached()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled(); var viewer = fixture.Store.BeginRemoteTerminalViewer(enrollment.WorkerId, fixture.SessionId, 1);
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.TransitionRemoteTerminalViewer(viewer.Id, viewer.Revision, "requested", "detached"));
        Assert.Equal("failed", fixture.Store.TransitionRemoteTerminalViewer(viewer.Id, viewer.Revision, "requested", "failed").State);
    }

    [Fact]
    public void ViewerCountersCanAdvanceWhileConnected()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled(); var viewer = fixture.Store.BeginRemoteTerminalViewer(enrollment.WorkerId, fixture.SessionId, 1);
        viewer = fixture.Store.TransitionRemoteTerminalViewer(viewer.Id, viewer.Revision, "requested", "connected"); viewer = fixture.Store.RecordRemoteTerminalViewerActivity(viewer.Id, 3, 4, 24, 80);
        Assert.Equal("connected", viewer.State); Assert.Equal(3, viewer.InputBytes); Assert.Equal(4, viewer.OutputBytes);
    }

    [Fact]
    public void PermissionRejectPreferenceNeverSelectsAllow()
    {
        var choices = new[] { "allow_once", "reject_always", "allow_always", "reject_once" };
        var selected = choices.Contains("reject_once", StringComparer.Ordinal) ? "reject_once" : choices.Contains("reject_always", StringComparer.Ordinal) ? "reject_always" : null;
        Assert.Equal("reject_once", selected); Assert.DoesNotContain("allow", selected!, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedManagerIsDisabledByDefault()
    {
        Assert.False(new WorkerControlOptions().HostedManagerEnabled);
    }

    [Fact]
    public void RequestPersistsExactTurnAndOwnershipTuple()
    {
        using var fixture = new RemoteStoreFixture(); var request = fixture.CreateEligibleRequest();
        Assert.Equal("turn-test", request.TurnId); Assert.Equal(1, request.OwnershipEpoch); Assert.Equal(1, request.ProcessGeneration);
    }

    [Fact]
    public void RequestRejectsStaleOwnershipTuple()
    {
        using var fixture = new RemoteStoreFixture(); fixture.CreateEnrolledAndReady();
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.BeginWorkerRequest(new(fixture.EmployeeId, fixture.BindingId, "wrk-a", fixture.SessionId, fixture.NativeSessionId, "idem-stale", "sha256:" + new string('d', 64), "sha256:" + new string('f', 64), 2, 1, "turn-stale")));
    }

    [Fact]
    public void RemoteViewerStoreTracksExactTransitionsAndCounts()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var viewer = fixture.Store.BeginRemoteTerminalViewer(enrollment.WorkerId, fixture.SessionId, 1); Assert.Equal("requested", viewer.State);
        viewer = fixture.Store.TransitionRemoteTerminalViewer(viewer.Id, viewer.Revision, "requested", "connected");
        viewer = fixture.Store.TransitionRemoteTerminalViewer(viewer.Id, viewer.Revision, "connected", "detached", 12, 34, 40, 120);
        Assert.Equal(12, viewer.InputBytes); Assert.Equal(34, viewer.OutputBytes); Assert.Equal(40, viewer.Rows); Assert.Equal(120, viewer.Columns);
    }

    [Fact]
    public void RemoteViewerRejectsInvalidDimensionsAndStaleRevision()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled(); var viewer = fixture.Store.BeginRemoteTerminalViewer(enrollment.WorkerId, fixture.SessionId, 1);
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.TransitionRemoteTerminalViewer(viewer.Id, viewer.Revision, "requested", "connected", rows: 0));
        viewer = fixture.Store.TransitionRemoteTerminalViewer(viewer.Id, viewer.Revision, "requested", "connected");
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.TransitionRemoteTerminalViewer(viewer.Id, viewer.Revision - 1, "connected", "detached"));
    }

    [Fact]
    public void SecurePrivateDirectoryAndUnlinkRejectHardlinks()
    {
        if (!OperatingSystem.IsLinux()) return; using var temp = new TempDirectory(); var directory = Path.Combine(temp.Path, "private"); ControllerPrivateFile.EnsurePrivateDirectory(directory, ControllerPrivateFile.EffectiveUid); var path = Path.Combine(directory, "key"); ControllerPrivateFile.PublishExclusive(path, new byte[32], ControllerPrivateFile.EffectiveUid); var hard = Path.Combine(directory, "hard"); Assert.Equal(0, link(path, hard)); Assert.Throws<InvalidOperationException>(() => ControllerPrivateFile.SecureUnlink(path, ControllerPrivateFile.EffectiveUid)); File.Delete(hard);
    }

    [Fact]
    public async Task DockerInspectParsesContainerAndVolumeFixturesAndRejectsTransportFailure()
    {
        var adapter = new RemoteWorkerProvisionerAdapter(new FixtureOperations());
        var host = new ApprovedExecutionHost();
        var container = await adapter.InspectContainerAsync(host, "worker", CancellationToken.None);
        Assert.True(container.Exists); Assert.Equal("running", container.State); Assert.Equal("worker-a", container.Labels["agentcontrol.worker"]);
        var volume = await adapter.InspectVolumeAsync(host, "volume", CancellationToken.None);
        Assert.True(volume.Exists); Assert.Equal("volume-a", volume.Labels["agentcontrol.worker"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.InspectContainerAsync(host, "transport", CancellationToken.None));
        Assert.False((await adapter.InspectContainerAsync(host, "absent", CancellationToken.None)).Exists);
    }

    [Fact]
    public void ConnectorCommandContainsNoBootstrapKey()
    {
        var host = new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Username = "docker", KnownHostsPath = "/control/known_hosts", IdentityFilePath = "/control/id" }; var options = new WorkerControlOptions { ControllerId = "controller-a", ApprovedImageDigest = "sha256:" + new string('a', 64) };
        var command = RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.Connector, ["agentcontrol-worker-a"]); Assert.Null(command.StandardInput); Assert.DoesNotContain("key", command.Arguments[^1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagerReconcilesStartupBeforeConcurrentSyncAndReusesOneCachedSession()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var abandoned = fixture.BeginRequest("sha256:" + new string('d', 64));
        abandoned = fixture.Store.TransitionWorkerRequest(abandoned.Id, abandoned.Revision, "Intent", "Forwarding");
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus());
        var factory = new FakeBridgeSessionFactory(session, () => Assert.Equal("Uncertain", fixture.Store.GetWorkerRequest(abandoned.Id)!.State));
        await using var manager = fixture.CreateManager(factory);

        var first = manager.SynchronizeOnceAsync(enrollment.WorkerId, CancellationToken.None);
        var second = manager.SynchronizeOnceAsync(enrollment.WorkerId, CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal("Uncertain", fixture.Store.GetWorkerRequest(abandoned.Id)!.State);
        Assert.Same(await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None), await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));
    }

    [Fact]
    public async Task HeartbeatRefreshesRemoteStatusAndPersistsChangedProcessGeneration()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(), fixture.BridgeStatus(), fixture.BridgeStatus(2));
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);
        await manager.HeartbeatAsync(enrollment.WorkerId, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(2, fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ObservedProcessGeneration);
        Assert.Equal(2, (await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None))!.Status.ProcessGeneration);
    }

    private sealed class FixtureOperations : IRemoteWorkerOperations
    {
        public Task<RemoteOperationResult> ExecuteAsync(ApprovedExecutionHost host, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken)
        {
            var name = tokens.Single();
            if (name == "transport") return Task.FromResult(new RemoteOperationResult(255, "", "transport"));
            if (name == "absent") return Task.FromResult(new RemoteOperationResult(1, "", "not-found"));
            var json = operation == RemoteDockerOperation.ContainerInspect
                ? "{\"Id\":\"container-id\",\"Config\":{\"Labels\":{\"agentcontrol.worker\":\"worker-a\"}},\"State\":{\"Running\":true,\"Status\":\"running\"}}"
                : "{\"Name\":\"volume-id\",\"Labels\":{\"agentcontrol.worker\":\"volume-a\"}}";
            return Task.FromResult(new RemoteOperationResult(0, json, "none"));
        }
        public Task<RemoteOperationResult> CreateVolumeAsync(ApprovedExecutionHost host, VolumeCreateSpec specification, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteOperationResult> CreateContainerAsync(ApprovedExecutionHost host, ContainerCreateSpec specification, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> StartWorkerPipeAsync(ApprovedExecutionHost host, string containerName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RemoteStoreFixture : IDisposable
    {
        private readonly TempDirectory _temp = new(); public string DatabasePath { get; }
        public OrganizationStore Store { get; }
        public string BindingId { get; }
        public string EmployeeId { get; }
        public string SessionId { get; }
        public string NativeSessionId { get; } = "native-remote";
        public string Digest { get; } = "sha256:" + new string('a', 64); public string KeyPath { get; }
        public string KeyId { get; } = "sha256:" + new string('b', 64);
        public string KnownHostsPath { get; }
        public string IdentityPath { get; }
        public RemoteStoreFixture(bool probeHost = true, bool makeDeveloper = true)
        {
            DatabasePath = Path.Combine(_temp.Path, "control.db"); Store = new OrganizationStore(DatabasePath); Store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            using var c = Open(DatabasePath); BindingId = Raw(DatabasePath, "SELECT id FROM runtime_bindings LIMIT 1"); EmployeeId = Raw(DatabasePath, $"SELECT employee_id FROM runtime_bindings WHERE id='{BindingId}'"); SessionId = "ses-remote"; c.Execute($"UPDATE acp_sessions SET status='closed' WHERE employee_id='{EmployeeId}' AND status='active'; INSERT INTO acp_sessions(id,employee_id,native_session_id,title,status,created_at,updated_at) VALUES('{SessionId}','{EmployeeId}','{NativeSessionId}','Remote','active','2026-09-15T00:00:00.0000000+00:00','2026-09-15T00:00:00.0000000+00:00')"); if (makeDeveloper) c.Execute($"UPDATE runtime_bindings SET placement='DeveloperContainer',container_ref='existing-container',session_ref='{SessionId}' WHERE id='{BindingId}'");
            Store.RegisterExecutionHost("host-a", "worker.example", 22, "docker", "/known", new("host-a", "host-a", "Host A")); if (probeHost) Store.RecordExecutionHostProbe("host-a", 1, new("ssh-ed25519", "SHA256:x", "sha256:" + new string('1', 64), "28", "1.48", "amd64", "overlay2", "ext4", false, 2_000_000_000, 2_000_000_000, 2, true, "linux/amd64", "valid"));
            KeyPath = Path.Combine(_temp.Path, "worker.key"); File.WriteAllBytes(KeyPath, new byte[32]); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            KnownHostsPath = Path.Combine(_temp.Path, "known_hosts"); IdentityPath = Path.Combine(_temp.Path, "id_ed25519");
            File.WriteAllText(KnownHostsPath, "worker.example ssh-ed25519 " + Convert.ToBase64String(new byte[32]) + "\n"); File.WriteAllBytes(IdentityPath, new byte[32]);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(KnownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); File.SetUnixFileMode(IdentityPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        }
        public WorkerEnrollmentRecord CreateEnrollment(string worker = "wrk-a") => Store.CreateWorkerEnrollmentForPlan(BindingId, "host-a", "controller-a", Digest, "linux/amd64", KeyPath, KeyId, worker);
        public WorkerEnrollmentRecord CreateEnrolled() { var e = CreateEnrollment(); e = Store.UpdateEnrollmentLifecycle(e.WorkerId, e.Revision, "planned", "provisioning"); return Store.UpdateEnrollmentLifecycle(e.WorkerId, e.Revision, "provisioning", "enrolled"); }
        public WorkerEnrollmentRecord CreateEnrolledAndReady() { var e = CreateEnrolled(); using (var c = Open(DatabasePath)) { c.Execute($"UPDATE orientation_assignments SET state='Stale' WHERE runtime_binding_id='{BindingId}'; INSERT INTO orientation_assignments(id,employee_id,runtime_binding_id,session_id,policy_id,orientation_version,artifact_file_name,artifact_bytes,state,assigned_at,delivered_at,acknowledged_at,comprehended_at,evidence_hash,evidence_summary,evidence_source,required_runtime_generation,loaded_runtime_generation,last_error,revision) SELECT 'ori-remote','{EmployeeId}','{BindingId}','{SessionId}',id,'remote-v1','orientation.md',1,'Comprehended','2026-09-15T00:00:00.0000000+00:00','2026-09-15T00:00:00.0000000+00:00','2026-09-15T00:00:00.0000000+00:00','2026-09-15T00:00:00.0000000+00:00','sha256:{new string('1', 64)}','ready','owner-submitted',NULL,NULL,NULL,1 FROM permission_policies LIMIT 1"); c.Execute($"UPDATE dispatch_holds SET active=0 WHERE runtime_binding_id='{BindingId}'"); } Store.RecordWorkerStatusAndEvents(e.WorkerId, Status(), []); return Store.GetWorkerEnrollment(e.WorkerId)!; }
        public ControllerWorkerStatus Status() => new(1, 1, "running", null, null, 1, false, [], 0, 0);
        public BridgeWorkerStatus BridgeStatus(long processGeneration = 1) => new(1, processGeneration, "running", "life", 42, null, null, 1, true, false, null, [], 0, 0, 0, 0, null, null, 0, [], true, true);
        public WorkerConnectionManager CreateManager(IWorkerBridgeSessionFactory factory) => new(Control(), factory, Microsoft.Extensions.Options.Options.Create(new WorkerControlOptions
        {
            Enabled = true,
            ControllerId = "controller-a",
            ApprovedImageDigest = Digest,
            ApprovedHosts = [new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Port = 22, Username = "docker", KnownHostsPath = KnownHostsPath, IdentityFilePath = IdentityPath }],
        }), new SystemControllerClock(), new SystemWorkerDelay());
        private HVO.AgentControl.Runtime.AcpControlHost Control()
        {
            var control = new HVO.AgentControl.Runtime.AcpControlHost(Microsoft.Extensions.Options.Options.Create(new HVO.AgentControl.Runtime.ControlOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<HVO.AgentControl.Runtime.AcpControlHost>.Instance);
            typeof(HVO.AgentControl.Runtime.AcpControlHost).GetField("_organization", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(control, Store);
            return control;
        }
        public WorkerRequestRecord BeginRequest(string payload) => Store.BeginWorkerRequest(new(EmployeeId, BindingId, "wrk-a", SessionId, NativeSessionId, "idem-a", payload, "sha256:" + new string('f', 64), 1, 1, "turn-test"));
        public WorkerRequestRecord CreateEligibleRequest() { CreateEnrolledAndReady(); return BeginRequest("sha256:" + new string('d', 64)); }
        public void Dispose() { Store.Dispose(); _temp.Dispose(); }
    }

    private sealed class FakeBridgeSessionFactory(FakeBridgeSession session, Action? beforeConnect = null) : IWorkerBridgeSessionFactory
    {
        private int _connectCount;
        public int ConnectCount => _connectCount;
        public Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken)
        {
            beforeConnect?.Invoke();
            Interlocked.Increment(ref _connectCount);
            return Task.FromResult<IWorkerBridgeSession>(session);
        }
    }

    private sealed class FakeBridgeSession : IWorkerBridgeSession
    {
        private readonly Queue<BridgeWorkerStatus> _statuses;
        public FakeBridgeSession(string controllerId, params BridgeWorkerStatus[] statuses)
        {
            Lease = new WorkerBridgeLease(1, controllerId, Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow);
            _statuses = new Queue<BridgeWorkerStatus>(statuses);
        }
        public WorkerBridgeLease Lease { get; }
        public object Mutation(string operation, IReadOnlyDictionary<string, object?> fields) => new Dictionary<string, object?>(fields) { ["operation"] = operation };
        public Task<WorkerSessionResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken)
        {
            if (operation == "reconcile") throw new WorkerRemoteException("worker-request-rejected");
            object value = operation switch
            {
                "status" => _statuses.Count > 1 ? _statuses.Dequeue() : _statuses.Peek(),
                "replay" => Array.Empty<BridgeWorkerEvent>(),
                _ => new { ok = true },
            };
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions));
            return Task.FromResult(new WorkerSessionResult(operation, document.RootElement.Clone(), mutation));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)] private static extern int link(string oldpath, string newpath);
    private static SqliteConnection Open(string path) { var c = new SqliteConnection($"Data Source={path}"); c.Open(); c.Execute("PRAGMA foreign_keys=ON"); return c; }
    private static object? Scalar(SqliteConnection c, string sql) { using var command = c.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static object? RawScalar(string path, string sql) { using var c = Open(path); return Scalar(c, sql); }
    private static string Raw(string path, string sql) => Convert.ToString(RawScalar(path, sql), System.Globalization.CultureInfo.InvariantCulture)!;
    private sealed class TempDirectory : IDisposable { public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-remote-" + Guid.NewGuid().ToString("N")); public TempDirectory() => Directory.CreateDirectory(Path); public void Dispose() => Directory.Delete(Path, true); }
}
