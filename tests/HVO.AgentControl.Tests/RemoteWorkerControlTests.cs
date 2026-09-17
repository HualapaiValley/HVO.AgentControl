using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Worker;
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
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.ContainerInspect, ["x;id"]));
    }

    [Fact]
    public void DestructiveOwnershipRequiresEveryExactLabel()
    {
        var identity = new WorkerResourceIdentity("org-a", "controller-a", "host-a", "worker-a", "binding-a", "operation-a");
        var actual = identity.Labels.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        actual["org.opencontainers.image.revision"] = "inherited-image-label";
        RemoteWorkerCommandBuilder.RequireOwnedLabels(actual, identity);

        var changed = new Dictionary<string, string>(actual, StringComparer.Ordinal) { ["agentcontrol.worker"] = "other" };
        Assert.Throws<ForeignResourceException>(() => RemoteWorkerCommandBuilder.RequireOwnedLabels(changed, identity));

        var unexpected = new Dictionary<string, string>(actual, StringComparer.Ordinal) { ["agentcontrol.unexpected"] = "present" };
        Assert.Throws<ForeignResourceException>(() => RemoteWorkerCommandBuilder.RequireOwnedLabels(unexpected, identity));

        var missing = new Dictionary<string, string>(actual, StringComparer.Ordinal);
        missing.Remove("agentcontrol.operation");
        Assert.Throws<ForeignResourceException>(() => RemoteWorkerCommandBuilder.RequireOwnedLabels(missing, identity));
    }

    [Fact]
    public void FreshSchemaIsV6AndCarriesRemoteWorkerTablesWithoutSeededEnrollment()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Path, "control.db");
        using (var store = new OrganizationStore(path)) store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        using var connection = Open(path);
        Assert.Equal(6L, Convert.ToInt64(Scalar(connection, "SELECT version FROM schema_version")));
        foreach (var table in new[] { "execution_hosts", "worker_enrollments", "worker_cursors", "worker_events", "worker_pending_permissions", "worker_tasks", "worker_requests", "provisioning_operations", "resource_records", "worker_recovery_obligations", "worker_recovery_audit", "remote_terminal_viewers", "worker_event_retention" }) Assert.Equal(1L, Convert.ToInt64(Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'")));
        Assert.Equal(0L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM worker_enrollments")));
    }

    [Fact]
    public void ExactReleasedV3MigratesWithVerifiedCreateOnceBackupAndPreservesPolicy()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Path, "control.db");
        using (var store = new OrganizationStore(path)) store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
        using (var connection = Open(path))
        {
            foreach (var table in new[] { "worker_event_retention", "remote_terminal_viewers", "worker_recovery_audit", "worker_pending_permissions", "worker_events", "worker_recovery_obligations", "resource_records", "provisioning_operations", "worker_cancellations", "worker_requests", "worker_tasks", "worker_cursors", "worker_enrollments", "execution_hosts" }) connection.Execute($"DROP TABLE {table}");
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

    /// <summary>
    /// The probe parser is exercised against the real captured output of Docker
    /// Engine 29 rather than a hand-written shape, so a field this controller
    /// invented would fail here instead of passing on a synthetic fixture.
    /// </summary>
    [Fact]
    public void HostProbeParsesRealDockerOutputAndDerivesPlatformFromVersionNotUname()
    {
        using var fixture = new ProbeFixture();
        var probe = HostProbeParser.Parse(fixture.Payload(), fixture.Host, "linux/amd64", ControllerPrivateFile.EffectiveUid);

        Assert.Equal("valid", probe.CapabilityStatus);
        Assert.Equal("29.8.0", probe.DockerVersion);
        Assert.Equal("1.56", probe.DockerApiVersion);
        // docker info reports the uname machine ("x86_64"); the image platform must
        // come from docker version ("amd64"), or the digest would never match.
        Assert.Equal("amd64", probe.Architecture);
        Assert.Equal("linux/amd64", probe.ImagePlatform);
        Assert.Equal("overlayfs", probe.StorageDriver);
        Assert.False(probe.SharedStorage);
        Assert.True(probe.LimitsSupported);
        Assert.Equal(810_025_492_480L, probe.FreeBytes);
        Assert.Equal(12, probe.CpuCount);
        // The containerd snapshotter reports no backing filesystem; an absent
        // optional value is recorded, not treated as a capability failure.
        Assert.Equal("unknown", probe.BackingFilesystem);
    }

    [Fact]
    public void HostProbeFailsClosedForANonLinuxOrMismatchedPlatform()
    {
        using var fixture = new ProbeFixture();
        var darwin = fixture.VersionJson.Replace("\"Os\":\"linux\"", "\"Os\":\"darwin\"", StringComparison.Ordinal);
        Assert.Equal("invalid", HostProbeParser.Parse(fixture.Payload(versionJson: darwin), fixture.Host, "linux/amd64", ControllerPrivateFile.EffectiveUid).CapabilityStatus);
        // The host is healthy but is not the platform this controller's approved
        // image digest was built for.
        Assert.Equal("invalid", HostProbeParser.Parse(fixture.Payload(), fixture.Host, "linux/arm64", ControllerPrivateFile.EffectiveUid).CapabilityStatus);
    }

    [Theory]
    [InlineData("\"MemoryLimit\":true", "\"MemoryLimit\":false")]                        // no memory limit
    [InlineData("\"CpuCfsQuota\":true", "\"CpuCfsQuota\":false")]                        // no cpu quota
    [InlineData("\"PidsLimit\":true", "\"PidsLimit\":false")]                            // no pids limit
    [InlineData("\"Volume\":[\"local\"]", "\"Volume\":[\"local\",\"nfs-cluster\"]")]     // shared volume plugin
    [InlineData("\"Driver\":\"overlayfs\"", "\"Driver\":\"some-cluster-fs\"")]           // unknown storage driver
    [InlineData("\"MemTotal\":50443812864", "\"MemTotal\":1024")]                        // too little memory
    public void HostProbeFailsClosedForEachMissingMandatoryCapability(string original, string replacement)
    {
        using var fixture = new ProbeFixture();
        var payload = fixture.Payload(infoJson: fixture.InfoJson.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Equal("invalid", HostProbeParser.Parse(payload, fixture.Host, "linux/amd64", ControllerPrivateFile.EffectiveUid).CapabilityStatus);
    }

    [Fact]
    public void HostProbeTreatsMissingMandatoryFieldAsInvalidAndUnparsableOutputAsUnavailable()
    {
        using var fixture = new ProbeFixture();
        // Present but unprovable capability is a truthful host observation.
        var withoutPlugins = fixture.InfoJson.Replace("\"Volume\":[\"local\"]", "\"Volume\":null", StringComparison.Ordinal);
        Assert.Equal("invalid", HostProbeParser.Parse(fixture.Payload(infoJson: withoutPlugins), fixture.Host, "linux/amd64", ControllerPrivateFile.EffectiveUid).CapabilityStatus);
        // A version document without the mandatory fields is parsed but proves
        // nothing, so it is invalid rather than an error.
        var withoutVersionFields = HostProbeParser.Parse(fixture.Payload(versionJson: "{}"), fixture.Host, "linux/amd64", ControllerPrivateFile.EffectiveUid);
        Assert.Equal("invalid", withoutVersionFields.CapabilityStatus);
        Assert.Equal("unknown", withoutVersionFields.DockerApiVersion);
        // Output the controller cannot parse at all means it learned nothing.
        Assert.Throws<RemoteWorkerUnavailableException>(() => HostProbeParser.Parse(fixture.Payload(infoJson: "not-json"), fixture.Host, "linux/amd64", ControllerPrivateFile.EffectiveUid));
        Assert.Throws<RemoteWorkerUnavailableException>(() => HostProbeParser.Parse(fixture.Payload(versionJson: "[1,2]"), fixture.Host, "linux/amd64", ControllerPrivateFile.EffectiveUid));
    }

    [Fact]
    public void HostProbeReadsRealDockerRootDirectoryAndRejectsAnUnsafeOne()
    {
        using var fixture = new ProbeFixture();
        Assert.Equal("/var/lib/docker", HostProbeParser.ReadDockerRootDirectory(fixture.InfoJson));
        var unsafeRoot = fixture.InfoJson.Replace("\"DockerRootDir\":\"/var/lib/docker\"", "\"DockerRootDir\":\"/var/lib/docker; rm -rf /\"", StringComparison.Ordinal);
        Assert.Throws<RemoteWorkerUnavailableException>(() => HostProbeParser.ReadDockerRootDirectory(unsafeRoot));
        var relativeRoot = fixture.InfoJson.Replace("\"DockerRootDir\":\"/var/lib/docker\"", "\"DockerRootDir\":\"var/lib/docker\"", StringComparison.Ordinal);
        Assert.Throws<RemoteWorkerUnavailableException>(() => HostProbeParser.ReadDockerRootDirectory(relativeRoot));
    }

    /// <summary>The exact two-line output of <c>df -B1 --output=avail</c>.</summary>
    [Fact]
    public void StorageFreeParsesTheExactDfOutputAndRejectsAnythingElse()
    {
        Assert.Equal(810_025_492_480L, HostProbeParser.ParseAvailableBytes("     Avail\n810025492480\n"));
        Assert.Throws<RemoteWorkerUnavailableException>(() => HostProbeParser.ParseAvailableBytes("810025492480\n"));
        Assert.Throws<RemoteWorkerUnavailableException>(() => HostProbeParser.ParseAvailableBytes("Avail\n-1\n"));
        Assert.Throws<RemoteWorkerUnavailableException>(() => HostProbeParser.ParseAvailableBytes("Avail\n12\n34\n"));
        Assert.Throws<RemoteWorkerUnavailableException>(() => HostProbeParser.ParseAvailableBytes(string.Empty));
    }

    [Fact]
    public void ProbeCommandsAreTheFixedTypedDockerAndDfInvocations()
    {
        var host = new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Username = "docker", KnownHostsPath = "/control/known_hosts", IdentityFilePath = "/control/id" };
        var options = new WorkerControlOptions { ControllerId = "controller-a", ApprovedImageDigest = "sha256:" + new string('a', 64) };
        Assert.Equal("docker system info --format '{{json .}}'", RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.Probe, []).Arguments[^1]);
        Assert.Equal("docker version --format '{{json .Server}}'", RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.VersionProbe, []).Arguments[^1]);
        Assert.Equal("df -B1 --output=avail -- '/var/lib/docker'", RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.StorageFree, ["/var/lib/docker"]).Arguments[^1]);
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.StorageFree, ["/var/lib/docker; id"]));
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.Build(host, options, RemoteDockerOperation.StorageFree, ["/var/lib/../etc"]));
    }

    /// <summary>
    /// The exact stderr Docker Engine 29 prints for a missing resource, captured
    /// from the real CLI, must classify as absent for its own kind only.
    /// </summary>
    [Theory]
    [InlineData(RemoteDockerOperation.VolumeInspect, "Error response from daemon: get nosuchvolume-xyz: no such volume", "not-found")]
    [InlineData(RemoteDockerOperation.ContainerInspect, "Error response from daemon: No such container: nosuchcontainer-xyz", "not-found")]
    [InlineData(RemoteDockerOperation.ImageInspect, "Error response from daemon: No such image: sha256:0000", "not-found")]
    [InlineData(RemoteDockerOperation.VolumeInspect, "Error response from daemon: No such container: other", "remote-command-failed")]
    [InlineData(RemoteDockerOperation.ContainerInspect, "Error response from daemon: no such volume", "remote-command-failed")]
    [InlineData(RemoteDockerOperation.ContainerRemove, "Error response from daemon: No such container: x", "remote-command-failed")]
    public void AbsenceIsConcludedOnlyFromTheExactDockerMessageForThatKind(RemoteDockerOperation operation, string stderr, string expected)
    {
        Assert.Equal(expected, ProcessRemoteWorkerOperations.ClassifyError(operation, 1, stderr));
        // Case differs between Docker messages ("no such volume" vs "No such container"),
        // so matching is case-insensitive but still kind-specific.
        Assert.Equal(expected, ProcessRemoteWorkerOperations.ClassifyError(operation, 1, stderr.ToUpperInvariant()));
        // A non-1 exit code never proves absence, whatever the message says.
        Assert.Equal("remote-command-failed", ProcessRemoteWorkerOperations.ClassifyError(operation, 125, stderr));
        Assert.Equal("transport", ProcessRemoteWorkerOperations.ClassifyError(operation, 255, stderr));
    }

    private sealed class ProbeFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        public ProbeFixture()
        {
            var known = Path.Combine(_temp.Path, "known_hosts");
            File.WriteAllText(known, "worker ssh-ed25519 " + Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray()) + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(known, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Host = new ApprovedExecutionHost { Id = "host-a", Hostname = "worker", Username = "docker", KnownHostsPath = known, IdentityFilePath = "/not/read/in-parser" };
            var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            InfoJson = File.ReadAllText(Path.Combine(fixtures, "docker-info-29.json"));
            VersionJson = File.ReadAllText(Path.Combine(fixtures, "docker-version-29.json"));
        }
        public ApprovedExecutionHost Host { get; }
        public string InfoJson { get; }
        public string VersionJson { get; }
        public HostProbePayload Payload(string? infoJson = null, string? versionJson = null, long freeBytes = 810_025_492_480L) =>
            new(infoJson ?? InfoJson, versionJson ?? VersionJson, "/var/lib/docker", freeBytes);
        public void Dispose() => _temp.Dispose();
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
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.QuoteShell("bad'quote"));
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.QuoteShell("bad\nline"));
    }

    [Fact]
    public void TypedContainerCreatePinsIsolationLimitsVolumesAndDigest()
    {
        var host = new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Username = "docker", KnownHostsPath = "/control/known_hosts", IdentityFilePath = "/control/id" };
        var digest = "sha256:" + new string('a', 64); var options = new WorkerControlOptions { ControllerId = "controller-a", ApprovedImageDigest = digest };
        var identity = new WorkerResourceIdentity("org-a", "controller-a", "host-a", "worker-a", "binding-a", "operation-a");
        var volumes = new[] { new NamedVolumeMount("control-a", "/control"), new NamedVolumeMount("home-a", "/home/worker"), new NamedVolumeMount("workspace-a", "/workspace"), new NamedVolumeMount("session-a", "/session") };
        var command = RemoteWorkerCommandBuilder.BuildContainerCreate(host, options, new ContainerCreateSpec("worker-a", digest, "linux/amd64", identity, volumes, options.MemoryBytes, options.CpuLimit, options.PidsLimit)); var remote = command.Arguments[^1];
        Assert.Contains("--cap-drop 'ALL'", remote, StringComparison.Ordinal); foreach (var capability in new[] { "CHOWN", "SETUID", "SETGID", "KILL" }) Assert.Contains($"--cap-add '{capability}'", remote, StringComparison.Ordinal); Assert.Contains("'WORKER_CONTROL_DIRECTORY=/control'", remote, StringComparison.Ordinal); Assert.Contains("'WORKER_ID=worker-a'", remote, StringComparison.Ordinal); Assert.Contains("'WORKER_CONTROLLER_ID=controller-a'", remote, StringComparison.Ordinal); Assert.Contains("--read-only", remote, StringComparison.Ordinal); Assert.DoesNotContain("--privileged", remote, StringComparison.Ordinal); Assert.Contains("'" + digest + "'", remote, StringComparison.Ordinal);
        // Contract: outbound egress for the approved provider is permitted, but the
        // worker publishes no ingress. The network is exactly one positional pair and
        // is never host or an explicit publish/expose.
        var tokens = remote.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, tokens.Count(token => token == "--network"));
        var networkAt = Array.IndexOf(tokens, "--network");
        Assert.True(networkAt >= 0 && networkAt + 1 < tokens.Length, "the create command must carry a network value.");
        Assert.Equal("'bridge'", tokens[networkAt + 1]);
        Assert.NotEqual("'host'", tokens[networkAt + 1]);
        Assert.DoesNotContain("--publish", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("--expose", remote, StringComparison.Ordinal);
        Assert.DoesNotContain(" -p ", remote, StringComparison.Ordinal);
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

    /// <summary>
    /// The worker's own uncertain session creation is a genuine, terminal
    /// worker-side condition and must remain a worker-source obligation. The
    /// identically-named controller reason that accompanies a marker-bearing
    /// controller obligation must never be re-projected as a worker twin.
    /// </summary>
    [Fact]
    public void WorkerSessionCreateUncertainProjectsTerminalObligationWithoutControllerTwin()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { DispatchHeld = true, HoldReasons = ["session-create-uncertain"] }, []);
        var workerObligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true));
        Assert.Equal("worker", workerObligation.Source);
        Assert.Equal("session-reconciliation", workerObligation.Kind);

        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { DispatchHeld = true, HoldReasons = ["session-reconciliation"] }, []);
        var retained = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true));
        Assert.Equal(workerObligation.Id, retained.Id);
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
        Assert.Equal(1, fixture.Store.ReconcileControllerStartup()); Assert.Equal("Uncertain", fixture.Store.GetWorkerRequest(request.Id)!.State);
        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain" && x.MarkerJson == WorkerConnectionManager.RequestMarker(request.Id));
        Assert.False(fixture.Store.AcknowledgeControllerRecovery(request.WorkerId, obligation.Id, obligation.Revision, "sha256:" + new string('a', 64), "acknowledged-after-external-reconciliation").Active);
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
    public void InterruptingUncertainRequestRetainsRecoveryObligation()
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain");

        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Uncertain", "Interrupted");

        Assert.Equal("Interrupted", request.State);
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");
    }

    [Fact]
    public void AcknowledgingInterruptedRequestReconcilesExternalEffectAndSuppressesRecreation()
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Uncertain", "Interrupted");
        var marker = WorkerConnectionManager.RequestMarker(request.Id);
        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain" && x.MarkerJson == marker);

        var acknowledged = fixture.Store.AcknowledgeControllerRecovery(request.WorkerId, obligation.Id, obligation.Revision, "sha256:" + new string('a', 64), "acknowledged-after-external-reconciliation");

        Assert.False(acknowledged.Active);
        // External-effects reconciliation never advances the request or its task.
        Assert.Equal("Interrupted", fixture.Store.GetWorkerRequest(request.Id)!.State);
        Assert.Equal("Uncertain", fixture.Store.GetWorkerTask(request.TaskId)!.State);
        Assert.Equal("request-uncertain", Assert.Single(fixture.Store.ListWorkerRecoveryAudit(request.WorkerId), x => x.ObligationId == obligation.Id).Kind);

        // The immutable audit suppresses re-creating the exact acknowledged obligation.
        fixture.Store.RecordControllerRecovery(request.WorkerId, "request-uncertain", request.Id, marker);
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");

        // A marker that does not hash the named request is refused even when the
        // request is Interrupted.
        fixture.Store.RecordControllerRecovery(request.WorkerId, "request-uncertain", "other-marker", marker);
        var mismatched = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.AcknowledgeControllerRecovery(request.WorkerId, mismatched.Id, mismatched.Revision, "sha256:" + new string('a', 64), "acknowledged-after-external-reconciliation"));
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

    /// <summary>
    /// The statx binding must match the kernel structure exactly. A wrong size or
    /// a swapped rdev/dev pair silently compares the wrong device and would defeat
    /// the inode re-check that protects every controller-private read.
    /// </summary>
    [Fact]
    public void StatxLayoutMatchesTheKernelStructureAndReportsTheRealDevice()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Equal(256, System.Runtime.InteropServices.Marshal.SizeOf<ControllerPrivateFile.Statx>());

        // /dev/null is a character device: its rdev is non-zero while its dev is the
        // devtmpfs it lives on. Reading the pair the other way round inverts both.
        var deviceNode = ControllerPrivateFile.StatForTests("/dev/null");
        Assert.Equal(1u, deviceNode.RDeviceMajor);
        Assert.Equal(3u, deviceNode.RDeviceMinor);
        Assert.True(deviceNode.DeviceMajor != 1 || deviceNode.DeviceMinor != 3, "dev and rdev were read from the same offsets.");
        Assert.Equal(0x2000, deviceNode.Mode & 0xF000);

        // A regular file has no rdev at all, which is exactly why a swapped pair
        // would compare two constant zeroes and always "match".
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "regular");
        File.WriteAllBytes(path, new byte[8]);
        var regular = ControllerPrivateFile.StatForTests(path);
        Assert.Equal(0u, regular.RDeviceMajor);
        Assert.Equal(0u, regular.RDeviceMinor);
        Assert.True(regular.DeviceMajor != 0 || regular.DeviceMinor != 0, "the regular file reported no filesystem device.");
        Assert.Equal(8ul, regular.Size);
        Assert.Equal(1u, regular.Links);
        Assert.True(regular.Inode > 0);
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
        await Assert.ThrowsAsync<RemoteWorkerUnavailableException>(() => adapter.InspectContainerAsync(host, "transport", CancellationToken.None));
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
    public async Task ForwardedDispatchReconcilesToCompletionAndCancellationUsesCachedLease()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus()) { ReconcileRequestState = "forwarded" };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var dispatched = await manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "async-dispatch", "hello"), CancellationToken.None);
        Assert.Equal("Forwarded", dispatched.State);
        var lease = await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None);

        var cancellation = await manager.CancelAsync(new(dispatched.Id), CancellationToken.None);
        Assert.Equal("Forwarded", cancellation.State);
        Assert.Same(lease, await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));

        session.ReconcileRequestState = "completed";
        await manager.SynchronizeOnceAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal("Completed", fixture.Store.GetWorkerRequest(dispatched.Id)!.State);
        Assert.Contains(session.Invocations, x => x.Operation == "reconcile");
    }

    [Fact]
    public async Task ReconnectAtNewLeaseEpochReconcilesRequestStoredUnderPriorEpoch()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var epoch1 = new FakeBridgeSession(enrollment.ControllerId, 1, fixture.BridgeStatus()) { ReconcileRequestState = "forwarded" };
        WorkerRequestRecord dispatched;
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(epoch1)))
            dispatched = await manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "epoch-reconnect", "hello"), CancellationToken.None);

        Assert.Equal("Forwarded", dispatched.State);
        Assert.Equal(1, dispatched.OwnershipEpoch);

        var epoch2Status = fixture.BridgeStatus() with { OwnershipEpoch = 2 };
        var epoch2 = new FakeBridgeSession(enrollment.ControllerId, 2, epoch2Status)
        {
            ReconcileRequestState = "completed",
            ReconcileOwnershipEpoch = 1,
            ReconcileTurnId = dispatched.TurnId,
        };
        await using var reconnected = fixture.CreateManager(new FakeBridgeSessionFactory(epoch2));
        await reconnected.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal("Completed", fixture.Store.GetWorkerRequest(dispatched.Id)!.State);
        Assert.Contains(epoch2.Invocations, x => x.Operation == "reconcile" && x.Payload.Contains(dispatched.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "request-uncertain");

        var next = await reconnected.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "epoch2-dispatch", "next"), CancellationToken.None);
        Assert.Equal("Forwarded", next.State);
        Assert.Equal(2, next.OwnershipEpoch);
    }

    [Fact]
    public async Task HeartbeatProcessGenerationChangeMakesForwardedRequestUncertainUntilExactCompletion()
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Uncertain", "Forwarded");
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");
        var current = fixture.BridgeStatus();
        var restarted = fixture.BridgeStatus(processGeneration: 2);
        var session = new FakeBridgeSession("controller-a", current, current, restarted) { ReconcileRequestState = "forwarded" };
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session)))
        {
            await manager.ConnectAndSynchronizeAsync(request.WorkerId, CancellationToken.None);
            await manager.HeartbeatAsync(request.WorkerId, TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        Assert.Equal("Uncertain", fixture.Store.GetWorkerRequest(request.Id)!.State);
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain" && x.MarkerJson == WorkerConnectionManager.RequestMarker(request.Id));

        var completed = new FakeBridgeSession("controller-a", fixture.BridgeStatus()) { ReconcileRequestState = "completed" };
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(completed)))
            await manager.ConnectAndSynchronizeAsync(request.WorkerId, CancellationToken.None);

        Assert.Equal("Completed", fixture.Store.GetWorkerRequest(request.Id)!.State);
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");
    }

    [Fact]
    public async Task UnknownForwardedRequestBecomesUncertainThenExactCompletionClearsRecovery()
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Forwarded");
        var unknown = new FakeBridgeSession("controller-a", fixture.BridgeStatus());
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(unknown)))
            await manager.ConnectAndSynchronizeAsync(request.WorkerId, CancellationToken.None);

        Assert.Equal("Uncertain", fixture.Store.GetWorkerRequest(request.Id)!.State);
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain" && x.MarkerJson == WorkerConnectionManager.RequestMarker(request.Id));

        var completed = new FakeBridgeSession("controller-a", fixture.BridgeStatus()) { ReconcileRequestState = "completed" };
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(completed)))
            await manager.ConnectAndSynchronizeAsync(request.WorkerId, CancellationToken.None);

        Assert.Equal("Completed", fixture.Store.GetWorkerRequest(request.Id)!.State);
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");
    }

    [Fact]
    public async Task UncertainForwardedCompletionClearsRecoveryAndAllowsDispatch()
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain");
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");

        // Reconcile the uncertain request back to an exact forwarded receipt first;
        // that durable re-confirmation must retain the obligation.
        var reForwarded = new FakeBridgeSession("controller-a", fixture.BridgeStatus()) { ReconcileRequestState = "forwarded" };
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(reForwarded)))
            await manager.ConnectAndSynchronizeAsync(request.WorkerId, CancellationToken.None);

        Assert.Equal("Forwarded", fixture.Store.GetWorkerRequest(request.Id)!.State);
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");

        // The later exact completion clears the obligation regardless of coming from
        // Forwarded rather than Uncertain, and releases the worker to accept work.
        var completed = new FakeBridgeSession("controller-a", fixture.BridgeStatus()) { ReconcileRequestState = "completed" };
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(completed)))
        {
            await manager.ConnectAndSynchronizeAsync(request.WorkerId, CancellationToken.None);

            Assert.Equal("Completed", fixture.Store.GetWorkerRequest(request.Id)!.State);
            Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");

            var dispatched = await manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, request.WorkerId, fixture.SessionId, fixture.NativeSessionId, "after-uncertain-completion", "hello"), CancellationToken.None);
            Assert.Equal("Forwarded", dispatched.State);
        }
    }

    [Fact]
    public async Task AcknowledgedUncertainRequestStaysAdjudicatedAcrossReconnectAndAllowsDispatch()
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain");
        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");
        fixture.Store.AcknowledgeControllerRecovery(request.WorkerId, obligation.Id, obligation.Revision, "sha256:" + new string('a', 64), "acknowledged-after-external-reconciliation");

        var session = new FakeBridgeSession("controller-a", fixture.BridgeStatus()) { ReconcileRequestState = "unknown" };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));
        await manager.ConnectAndSynchronizeAsync(request.WorkerId, CancellationToken.None);

        Assert.Equal("Uncertain", fixture.Store.GetWorkerRequest(request.Id)!.State);
        Assert.DoesNotContain(session.Invocations, x => x.Operation == "reconcile" && x.Payload.Contains(request.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");

        var dispatched = await manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, request.WorkerId, fixture.SessionId, fixture.NativeSessionId, "after-owner-ack", "next"), CancellationToken.None);
        Assert.Equal("Forwarded", dispatched.State);
    }

    [Fact]
    public async Task MalformedRequestReconciliationPersistsRecoveryDropsSessionAndHoldsWorker()
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Forwarded");
        var session = new FakeBridgeSession("controller-a", fixture.BridgeStatus()) { ReconcileRequestState = "forwarded" };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));
        await manager.ConnectAndSynchronizeAsync(request.WorkerId, CancellationToken.None);
        session.ReconcileOwnershipEpoch = 2;

        await Assert.ThrowsAsync<WorkerReconciliationInvalidException>(() => manager.SynchronizeOnceAsync(request.WorkerId, CancellationToken.None));

        Assert.Equal("Uncertain", fixture.Store.GetWorkerRequest(request.Id)!.State);
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain" && x.MarkerJson == WorkerConnectionManager.RequestMarker(request.Id));
        Assert.Equal("held", fixture.Store.GetWorkerCursor(request.WorkerId)!.ConnectionState);
        Assert.Null(await manager.GetCachedLeaseAsync(request.WorkerId, CancellationToken.None));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task MalformedSubmitCorrelationPersistsRecoveryDropsSessionAndHoldsWorker()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus()) { SubmitOwnershipEpoch = 99 };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await Assert.ThrowsAsync<WorkerReconciliationInvalidException>(() => manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "malformed-submit", "hello"), CancellationToken.None));

        var request = Assert.Single(fixture.Store.ListWorkerRequests(), x => x.IdempotencyKey == "malformed-submit");
        Assert.Equal("Uncertain", request.State);
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "request-uncertain" && x.MarkerJson == WorkerConnectionManager.RequestMarker(request.Id));
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
        Assert.Null(await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.True(session.Disposed);

        // The durable obligation, not only the dropped lease, blocks the next
        // dispatch from reaching the worker again.
        var submits = session.Invocations.Count(x => x.Operation == "submit");
        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "blocked-submit", "hello"), CancellationToken.None));
        Assert.Equal(submits, session.Invocations.Count(x => x.Operation == "submit"));
    }

    [Fact]
    public async Task MalformedSubmitPayloadPersistsRecoveryDropsSessionAndHoldsWorker()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus()) { MalformedSubmitResult = true };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        // A wrongly-typed stored-request payload is not a transport loss: an exact
        // durable uncertain obligation is recorded, the owner session dropped, and
        // the worker held before the sanitized 502 leaves the API.
        var exception = await Assert.ThrowsAsync<WorkerReconciliationInvalidException>(() => manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "malformed-submit-json", "hello"), CancellationToken.None));
        Assert.True(Program.IsRemoteWorkerFailure(exception));

        var request = Assert.Single(fixture.Store.ListWorkerRequests(), x => x.IdempotencyKey == "malformed-submit-json");
        Assert.Equal("Uncertain", request.State);
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "request-uncertain" && x.MarkerJson == WorkerConnectionManager.RequestMarker(request.Id));
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
        Assert.Null(await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.True(session.Disposed);

        // The durable obligation, not only the dropped lease, blocks the next
        // dispatch from reaching the worker again.
        var submits = session.Invocations.Count(x => x.Operation == "submit");
        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "blocked-submit-json", "hello"), CancellationToken.None));
        Assert.Equal(submits, session.Invocations.Count(x => x.Operation == "submit"));
    }

    [Fact]
    public async Task MalformedStatusDuringRefreshDropsSessionAndHoldsWorker()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus()) { ReconcileRequestState = "forwarded" };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));
        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);
        Assert.NotNull(await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));

        session.MalformedStatusResult = true;

        await Assert.ThrowsAsync<WorkerReconciliationInvalidException>(() => manager.SynchronizeOnceAsync(enrollment.WorkerId, CancellationToken.None));

        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
        Assert.Null(await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task MalformedStatusDuringConnectHoldsWorkerAndDisposesSession()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus()) { MalformedStatusResult = true };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var exception = await Assert.ThrowsAsync<WorkerReconciliationInvalidException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.True(Program.IsRemoteWorkerFailure(exception));

        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
        Assert.Null(await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task CleanStatusRejectionPropagatesWithoutDroppingAuthenticatedSession()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus()) { ReconcileRequestState = "forwarded" };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));
        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);
        var lease = await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None);
        Assert.NotNull(lease);

        session.RejectStatus = true;

        await Assert.ThrowsAsync<WorkerRemoteException>(() => manager.SynchronizeOnceAsync(enrollment.WorkerId, CancellationToken.None));

        // A clean authenticated status rejection must not be mistaken for a lost
        // session: the effect is known to have been refused, so the lease, cursor,
        // and worker connection state all survive the propagation.
        Assert.Same(lease, await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.False(session.Disposed);
        Assert.Equal("authenticated", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Theory]
    [InlineData("permission-pending")]
    [InlineData("replay-gap-unreconciled")]
    [InlineData("manual-hold")]
    public async Task CancellationForwardsThroughManagerDuringDispatchAndRecoveryHolds(string holdReason)
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Forwarded");
        var pending = holdReason == "permission-pending"
            ? new BridgePendingPermission(1, 1, request.Id, request.TurnId, "perm-cancel", "sha256:" + new string('7', 64), ["reject_once"], "pending", null)
            : null;
        var held = fixture.BridgeStatus() with { ActiveRequestId = request.Id, PendingPermission = pending, DispatchHeld = true, HoldReason = holdReason, HoldReasons = [holdReason] };
        var session = new FakeBridgeSession("controller-a", held);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var cancellation = await manager.CancelAsync(new(request.Id), CancellationToken.None);

        Assert.Equal("Forwarded", cancellation.State);
        Assert.Single(session.Invocations, x => x.Operation == "cancel");
    }

    [Fact]
    public async Task CancellationRejectsOwnershipOrProcessMismatch()
    {
        using var processFixture = new RemoteStoreFixture();
        var processRequest = processFixture.CreateEligibleRequest();
        processRequest = processFixture.Store.TransitionWorkerRequest(processRequest.Id, processRequest.Revision, "Intent", "Forwarding");
        processRequest = processFixture.Store.TransitionWorkerRequest(processRequest.Id, processRequest.Revision, "Forwarding", "Forwarded");
        var changedProcess = new FakeBridgeSession("controller-a", processFixture.BridgeStatus(processGeneration: 2));
        await using (var manager = processFixture.CreateManager(new FakeBridgeSessionFactory(changedProcess)))
            await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => manager.CancelAsync(new(processRequest.Id), CancellationToken.None));

        using var ownerFixture = new RemoteStoreFixture();
        var ownerRequest = ownerFixture.CreateEligibleRequest();
        ownerRequest = ownerFixture.Store.TransitionWorkerRequest(ownerRequest.Id, ownerRequest.Revision, "Intent", "Forwarding");
        ownerRequest = ownerFixture.Store.TransitionWorkerRequest(ownerRequest.Id, ownerRequest.Revision, "Forwarding", "Forwarded");
        var changedOwner = new FakeBridgeSession("controller-a", ownerFixture.BridgeStatus() with { OwnershipEpoch = 2 });
        await using (var manager = ownerFixture.CreateManager(new FakeBridgeSessionFactory(changedOwner)))
            await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => manager.CancelAsync(new(ownerRequest.Id), CancellationToken.None));
    }

    [Fact]
    public async Task ManagerSynchronizesExitedWorkerWithoutSessionReadinessAndKeepsRecoveryAvailable()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var exited = fixture.BridgeStatus() with { ProcessState = "exited", AcpInitialized = false, SessionId = null, DispatchHeld = true, HoldReason = "process-exited", HoldReasons = ["process-exited"] };
        var session = new FakeBridgeSession(enrollment.ControllerId, exited);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal("exited", lease.Status.ProcessState);
        Assert.DoesNotContain(session.Invocations, x => x.Operation is "load-session" or "new-session");
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Fact]
    public async Task ManagerPreservesExistingManualHoldWithoutLoadingRecordedSession()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var held = fixture.BridgeStatus() with { SessionId = null, DispatchHeld = true, HoldReason = "manual-hold", HoldReasons = ["manual-hold"] };
        var session = new FakeBridgeSession(enrollment.ControllerId, held);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.True(lease.Status.DispatchHeld);
        Assert.Equal("manual-hold", lease.Status.HoldReason);
        Assert.DoesNotContain(session.Invocations, x => x.Operation == "load-session");
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Fact]
    public async Task ManagerLoadsExactNativeSessionAndReturnsHeldLeaseForSessionReconciliation()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var unloaded = fixture.BridgeStatus() with { SessionId = null };
        var loading = new FakeBridgeSession(enrollment.ControllerId, unloaded);
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(loading)))
        {
            var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);
            Assert.Equal(fixture.NativeSessionId, loading.LoadedSessionId);
            Assert.Equal(fixture.NativeSessionId, lease.Status.SessionId);
            Assert.Equal("status", loading.Invocations[0].Operation);
            Assert.Equal("load-session", loading.Invocations[1].Operation);
            Assert.Equal("status", loading.Invocations[2].Operation);
        }

        var mismatch = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus() with { SessionId = "native-other" });
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(mismatch)))
        {
            var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);
            Assert.True(lease.Status.DispatchHeld);
            Assert.Equal("session-reconciliation", lease.Status.HoldReason);
            Assert.DoesNotContain(mismatch.Invocations, x => x.Operation == "load-session");
        }
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
        // The controller-synthesized session-reconciliation hold must project exactly
        // one marker-bearing controller obligation. A worker-source twin would survive
        // acknowledgement and keep the worker held forever.
        var mismatchRecovery = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true));
        Assert.Equal("controller", mismatchRecovery.Source);
        Assert.Equal("session-reconciliation", mismatchRecovery.Kind);
        Assert.Contains("\"expectedSessionId\":\"native-remote\"", mismatchRecovery.MarkerJson, StringComparison.Ordinal);
        Assert.Contains("\"observedSessionId\":\"native-other\"", mismatchRecovery.MarkerJson, StringComparison.Ordinal);
        fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, mismatchRecovery.Id, mismatchRecovery.Revision, "sha256:" + new string('a', 64), "acknowledged-after-external-reconciliation");

        // With the controller obligation acknowledged and the worker reporting the
        // exact authoritative session, the hold must clear, leave zero obligations,
        // and let dispatch reach the worker.
        var healthy = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus());
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(healthy)))
        {
            var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);
            Assert.False(lease.Status.DispatchHeld);
            Assert.Empty(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true));
            var dispatched = await manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "after-reconciliation", "hello"), CancellationToken.None);
            Assert.Equal("Forwarded", dispatched.State);
            Assert.Single(healthy.Invocations, x => x.Operation == "submit");
            healthy.ReconcileRequestState = "completed";
            await manager.SynchronizeOnceAsync(enrollment.WorkerId, CancellationToken.None);
        }

        var rejected = new FakeBridgeSession(enrollment.ControllerId, unloaded) { RejectLoadSession = true };
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(rejected)))
        {
            var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);
            Assert.True(lease.Status.DispatchHeld);
            Assert.Equal("session-reconciliation", lease.Status.HoldReason);
            await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "held-session-reconciliation", "blocked"), CancellationToken.None));
        }
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
        var rejectedRecovery = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true));
        Assert.Equal("controller", rejectedRecovery.Source);
        Assert.Equal("session-reconciliation", rejectedRecovery.Kind);
        Assert.Contains("\"observation\":\"load-rejected\"", rejectedRecovery.MarkerJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UncertainSessionLoadPersistsReconciliationBeforeTheConnectionIsLost()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus() with { SessionId = null }) { FailLoadSessionUncertain = true };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await Assert.ThrowsAsync<WorkerWriteUncertainException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));

        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Source == "controller" && x.Kind == "session-reconciliation");
        Assert.Contains("\"observation\":\"load-uncertain\"", obligation.MarkerJson, StringComparison.Ordinal);
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
        Assert.Null(await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));
    }

    /// <summary>
    /// A caller cancellation at the recorded-session load is a clean abort, not an
    /// uncertain reconciliation. It must leave no obligation, report the
    /// connection as disconnected rather than held, and let a later healthy
    /// connect succeed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCanceledSessionLoadLeavesNoObligationAndDisconnectsWithoutHolding(bool bridgeReported)
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        using var canceled = new CancellationTokenSource();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus() with { SessionId = null });
        if (bridgeReported) session.FailLoadSessionAsCallerCanceled = true;
        else session.OnInvoke = operation => { if (operation == "load-session") canceled.Cancel(); };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, canceled.Token));

        // No load effect was recorded, no session-reconciliation obligation exists,
        // and the connection is disconnected rather than held.
        Assert.DoesNotContain(session.Invocations, x => x.Operation == "load-session");
        Assert.Empty(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, activeOnly: true));
        Assert.Equal("disconnected", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
        Assert.Null(await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));

        // A later healthy connect must not be blocked by the canceled attempt.
        session.FailLoadSessionAsCallerCanceled = false;
        var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);
        Assert.Equal(fixture.NativeSessionId, lease.Status.SessionId);
        Assert.False(lease.Status.DispatchHeld);
        Assert.Empty(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, activeOnly: true));
    }

    /// <summary>
    /// The bootstrap key is encoded exactly the way the worker entry point parses
    /// it, and the encoded buffer never survives the call.
    /// </summary>
    [Fact]
    public void ControllerBootstrapEncodingIsExactBase64WithOneNewlineAndZeroesItsBuffer()
    {
        var key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var encoded = WorkerBootstrapEncoding.Encode(key);
        Assert.Equal(45, encoded.Length);
        Assert.Equal((byte)'\n', encoded[^1]);
        Assert.Equal(Convert.ToBase64String(key), System.Text.Encoding.ASCII.GetString(encoded, 0, 44));
        Assert.Single(encoded, b => b == (byte)'\n');
        Assert.Throws<WorkerControlConfigurationException>(() => WorkerBootstrapEncoding.Encode(new byte[31]));
    }

    /// <summary>
    /// Contract test: the exact controller-produced bytes are fed to the worker's
    /// own bootstrap, so an encoding change on either side fails here rather than
    /// at enrollment time on a real host.
    /// </summary>
    [Fact]
    public void ControllerEncodedKeyIsAcceptedByTheWorkerBootstrapUnchanged()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        var control = Path.Combine(temp.Path, "control");
        // The worker requires a bridge-private 0700 control directory; the production
        // helper creates and verifies exactly that.
        ControllerPrivateFile.EnsurePrivateDirectory(control, ControllerPrivateFile.EffectiveUid);

        var key = Enumerable.Range(0, 32).Select(x => (byte)(x * 7 % 251)).ToArray();
        var encoded = WorkerBootstrapEncoding.Encode(key);
        var transmitted = System.Text.Encoding.UTF8.GetString(encoded);

        var keyId = HVO.AgentControl.Worker.WorkerKeyBootstrap.Bootstrap(control, transmitted);
        Assert.Equal(HVO.AgentControl.Worker.WorkerProtocol.KeyId(key), keyId);
        // An exact repeat is a verified no-op, which is what makes bootstrap
        // reconciliation safe to retry.
        Assert.Equal(keyId, HVO.AgentControl.Worker.WorkerKeyBootstrap.Bootstrap(control, transmitted));
        Assert.Equal(key, HVO.AgentControl.Worker.WorkerKeyBootstrap.ReadKey(control));
    }

    /// <summary>
    /// The bootstrap runs as its own ephemeral container against the control
    /// volume only, before any long-lived container exists.
    /// </summary>
    [Fact]
    public void BootstrapIsAnEphemeralRunAgainstTheControlVolumeOnly()
    {
        var host = new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Username = "docker", KnownHostsPath = "/control/known_hosts", IdentityFilePath = "/control/id" };
        var digest = "sha256:" + new string('a', 64);
        var options = new WorkerControlOptions { ControllerId = "controller-a", ApprovedImageDigest = digest };
        var identity = new WorkerResourceIdentity("org-a", "controller-a", "host-a", "worker-a", "binding-a", "operation-a");
        var remote = RemoteWorkerCommandBuilder.BuildBootstrap(host, options, new BootstrapSpec("agentcontrol-control-a", digest, "linux/amd64", identity), [1, 2, 3]).Arguments[^1];

        Assert.StartsWith("docker run --rm -i", remote, StringComparison.Ordinal);
        Assert.Contains("--user '1101:1101'", remote, StringComparison.Ordinal);
        Assert.Contains("--network 'none'", remote, StringComparison.Ordinal);
        Assert.Contains("--read-only", remote, StringComparison.Ordinal);
        Assert.Contains("--cap-drop 'ALL'", remote, StringComparison.Ordinal);
        Assert.Contains("--security-opt 'no-new-privileges'", remote, StringComparison.Ordinal);
        Assert.Contains("'type=volume,src=agentcontrol-control-a,dst=/control'", remote, StringComparison.Ordinal);
        Assert.Contains("'--worker-bootstrap-key'", remote, StringComparison.Ordinal);
        Assert.Contains("'" + digest + "'", remote, StringComparison.Ordinal);
        // Only the control volume is mounted, and no long-lived container is touched.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(remote, "--mount"));
        Assert.DoesNotContain("container exec", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/worker", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("--cap-add", remote, StringComparison.Ordinal);
        // The key material itself only ever travels on standard input; the single
        // occurrence of "key" in the command line is the fixed mode flag.
        Assert.Contains("'--worker-bootstrap-key'", remote, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(remote, "key", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Throws<WorkerControlConfigurationException>(() => RemoteWorkerCommandBuilder.BuildBootstrap(host, options, new BootstrapSpec("agentcontrol-control-a", "sha256:" + new string('b', 64), "linux/amd64", identity), null));
    }

    [Fact]
    public async Task ProvisioningCreatesFreshWorkerSessionAndRecordsAuthoritativeBindingBeforeEnrollment()
    {
        using var fixture = new RemoteStoreFixture(makeDeveloper: true, seedSession: false);
        var remote = new RecordingProvisioner();
        var verification = new FakeBridgeSession("controller-a", fixture.BridgeStatus() with { SessionId = null }) { NewSessionId = "native-fresh" };
        var coordinator = fixture.CreateCoordinator(remote, new FakeBridgeSessionFactory(verification));
        var enrollment = await coordinator.PlanAsync(fixture.BindingId, "host-a", CancellationToken.None);

        var enrolled = await coordinator.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None);

        var employee = fixture.Store.GetOverview().Employees.Single(x => x.RuntimeBindingId == fixture.BindingId);
        Assert.Equal("enrolled", enrolled.LifecycleStatus);
        Assert.Equal("native-fresh", employee.NativeSessionId);
        Assert.NotNull(employee.SessionRecordId);
        Assert.Contains(verification.Invocations, x => x.Operation == "new-session");
        Assert.DoesNotContain(verification.Invocations, x => x.Operation == "load-session");
    }

    /// <summary>
    /// Provisioning applies the control volume and the key bootstrap before the
    /// long-lived container is created or started.
    /// </summary>
    [Fact]
    public async Task ProvisioningBootstrapsTheKeyBeforeTheLongLivedContainerExists()
    {
        using var fixture = new RemoteStoreFixture();
        var remote = new RecordingProvisioner();
        var coordinator = fixture.CreateCoordinator(remote);
        var enrollment = await coordinator.PlanAsync(fixture.BindingId, "host-a", CancellationToken.None);
        await coordinator.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None);

        var order = remote.Effects;
        var bootstrap = order.IndexOf("bootstrap:" + enrollment.ControlVolumeName);
        var controlVolume = order.IndexOf("volume:" + enrollment.ControlVolumeName);
        var containerCreate = order.IndexOf("container:" + enrollment.ContainerName);
        var start = order.IndexOf("start:" + enrollment.ContainerName);

        Assert.True(controlVolume >= 0 && bootstrap > controlVolume, string.Join(",", order));
        Assert.True(containerCreate > bootstrap, string.Join(",", order));
        Assert.True(start > containerCreate, string.Join(",", order));
        Assert.Equal(4, order.Count(x => x.StartsWith("volume:", StringComparison.Ordinal)));
    }

    /// <summary>A repeated bootstrap with the identical key is a verified no-op.</summary>
    [Fact]
    public async Task UncertainBootstrapReconcilesByRepeatingTheIdenticalKeyWithoutOverwriting()
    {
        using var fixture = new RemoteStoreFixture();
        var remote = new RecordingProvisioner { FailBootstrapOnce = true };
        var coordinator = fixture.CreateCoordinator(remote);
        var enrollment = await coordinator.PlanAsync(fixture.BindingId, "host-a", CancellationToken.None);

        await Assert.ThrowsAsync<RemoteWorkerUnavailableException>(() => coordinator.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.Equal("Uncertain", fixture.Store.ListProvisioningOperations(enrollment.WorkerId).Single(x => x.Kind == "bootstrap").State);

        await coordinator.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None);
        var bootstrap = fixture.Store.ListProvisioningOperations(enrollment.WorkerId).Single(x => x.Kind == "bootstrap");
        Assert.Equal("Applied", bootstrap.State);
        Assert.Equal(2, remote.Effects.Count(x => x.StartsWith("bootstrap:", StringComparison.Ordinal)));
        Assert.All(remote.BootstrapKeyIds, id => Assert.Equal(remote.BootstrapKeyIds[0], id));
    }

    /// <summary>
    /// When reconciliation itself cannot reach the host, the step is held rather
    /// than left pending, so repeated application cannot loop forever.
    /// </summary>
    [Fact]
    public async Task UnreachableReconciliationHoldsTheStepInsteadOfLoopingForever()
    {
        using var fixture = new RemoteStoreFixture();
        var remote = new RecordingProvisioner { FailBootstrapOnce = true, FailBootstrapAlways = true };
        var coordinator = fixture.CreateCoordinator(remote);
        var enrollment = await coordinator.PlanAsync(fixture.BindingId, "host-a", CancellationToken.None);

        await Assert.ThrowsAsync<RemoteWorkerUnavailableException>(() => coordinator.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteWorkerUnavailableException>(() => coordinator.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.Equal("Held", fixture.Store.ListProvisioningOperations(enrollment.WorkerId).Single(x => x.Kind == "bootstrap").State);

        // A held plan is reported as recovery-required, never retried into a loop.
        var failure = await Assert.ThrowsAsync<WorkerRecoveryRequiredException>(() => coordinator.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.Equal("reconciliation-unavailable", failure.Kind);
    }

    /// <summary>
    /// Cleanup must not delete the enrolled key or claim absence when the host is
    /// simply unreachable.
    /// </summary>
    [Fact]
    public async Task CleanupHoldsResourcesUncertainAndKeepsTheKeyWhenTheTransportIsAbsent()
    {
        using var fixture = new RemoteStoreFixture();
        var remote = new RecordingProvisioner();
        var coordinator = fixture.CreateCoordinator(remote);
        var enrollment = await coordinator.PlanAsync(fixture.BindingId, "host-a", CancellationToken.None);
        await coordinator.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None);

        remote.FailInspect = true;
        await Assert.ThrowsAsync<RemoteWorkerUnavailableException>(() => coordinator.CleanupAsync(enrollment.WorkerId, CancellationToken.None));

        Assert.Contains(fixture.Store.ListWorkerResources(enrollment.WorkerId), x => x.State == "uncertain");
        Assert.True(File.Exists(enrollment.KeyFilePath), "An unreachable host must never cause the enrolled key to be deleted.");
        Assert.DoesNotContain(remote.Effects, x => x.StartsWith("remove", StringComparison.Ordinal));
    }

    /// <summary>
    /// Cleanup uses the organization persisted on the enrollment, so the labels it
    /// requires cannot drift with the controller's current organization.
    /// </summary>
    [Fact]
    public async Task CleanupMatchesLabelsFromThePersistedEnrollmentOrganization()
    {
        using var fixture = new RemoteStoreFixture();
        var remote = new RecordingProvisioner();
        var coordinator = fixture.CreateCoordinator(remote);
        var enrollment = await coordinator.PlanAsync(fixture.BindingId, "host-a", CancellationToken.None);
        await coordinator.ApplyAllAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal(fixture.Store.GetOverview().Id, enrollment.OrganizationId);
        // A resource labelled for a different organization is foreign, so cleanup
        // refuses it instead of destroying someone else's resource.
        remote.OwnerOverride = "other-organization/controller-a";
        await Assert.ThrowsAsync<WorkerRecoveryRequiredException>(() => coordinator.CleanupAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.Contains(fixture.Store.ListWorkerResources(enrollment.WorkerId), x => x.State == "foreign");
        Assert.DoesNotContain(remote.Effects, x => x.StartsWith("remove", StringComparison.Ordinal));
        Assert.True(File.Exists(enrollment.KeyFilePath));
    }

    /// <summary>
    /// Re-observing an unchanged pending permission refreshes the observation but
    /// must not bump the revision an operator is holding as a decision token.
    /// </summary>
    [Fact]
    public void UnchangedPendingPermissionProjectionDoesNotInvalidateTheOperatorDecisionToken()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var pending = new ControllerPendingPermission(1, 1, "req-test", "turn-test", "perm-test", "sha256:" + new string('7', 64), ["reject_once"], "pending");
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending }, []);
        var first = Assert.Single(fixture.Store.ListWorkerPendingPermissions(enrollment.WorkerId));

        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending }, []);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending }, []);
        var refreshed = Assert.Single(fixture.Store.ListWorkerPendingPermissions(enrollment.WorkerId));

        Assert.Equal(first.Revision, refreshed.Revision);
        Assert.True(refreshed.ObservedAt >= first.ObservedAt);
        // The revision read before the refreshes still decides the permission.
        Assert.Equal("decided", fixture.Store.TransitionWorkerPendingPermission(first.WorkerId, first.DecisionId, first.Revision, "pending", "decided").State);
    }

    /// <summary>
    /// A permission whose decision delivery was uncertain must stay uncertain even
    /// if the worker keeps reporting it pending: the decision may have applied.
    /// </summary>
    [Fact]
    public void UncertainPermissionStaysUncertainOnReconnectWithoutRejectingTheSnapshot()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var pending = new ControllerPendingPermission(1, 1, "req-test", "turn-test", "perm-test", "sha256:" + new string('7', 64), ["reject_once"], "pending");
        var status = fixture.Status() with { PendingPermissionHash = pending.PayloadHash, PendingPermission = pending };
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, status, []);
        var item = Assert.Single(fixture.Store.ListWorkerPendingPermissions(enrollment.WorkerId));
        fixture.Store.TransitionWorkerPendingPermission(item.WorkerId, item.DecisionId, item.Revision, "pending", "uncertain");

        // Reconnect: the worker still reports the identical pending tuple.
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, status, []);

        var after = Assert.Single(fixture.Store.ListWorkerPendingPermissions(enrollment.WorkerId));
        Assert.Equal("uncertain", after.State);
        // Rejecting again is refused, because the earlier decision may have applied.
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.TransitionWorkerPendingPermission(after.WorkerId, after.DecisionId, after.Revision, "pending", "decided"));
    }

    /// <summary>
    /// Acknowledged events are pruned to the configured bounds, and what was
    /// dropped stays visible as diagnostics.
    /// </summary>
    [Fact]
    public void AcknowledgedEventsArePrunedToTheBoundWithARetainedDiagnosticsRow()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        var payload = "{\"safe\":true}";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        var events = Enumerable.Range(1, OrganizationStore.ControllerEventLimit + 50)
            .Select(index => new ControllerWorkerEvent(1, index, "acp-event", payload, bytes))
            .ToArray();

        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), events);

        var retained = fixture.Store.ListWorkerEvents(enrollment.WorkerId);
        Assert.True(retained.Count <= OrganizationStore.ControllerEventLimit, $"retained {retained.Count}");
        // The cursor is committed in the same transaction as the prune, so nothing
        // is ever dropped that the controller has not durably accepted.
        var cursor = fixture.Store.GetWorkerCursor(enrollment.WorkerId)!;
        Assert.Equal(events[^1].Sequence, cursor.AcknowledgedSequence);
        Assert.Equal(events[^1].Sequence, retained[^1].Sequence);

        var diagnostics = Assert.Single(fixture.Store.ListWorkerEventRetention(enrollment.WorkerId));
        Assert.Equal(50, diagnostics.DroppedCount);
        Assert.Equal(50L * bytes, diagnostics.DroppedBytes);
        Assert.Equal(retained[0].Sequence, diagnostics.FirstRetainedSequence);
    }

    [Fact]
    public void UnacknowledgedEventsAreNeverPruned()
    {
        using var fixture = new RemoteStoreFixture(); var enrollment = fixture.CreateEnrolled();
        // One event committed with a cursor that stays behind it: nothing may drop.
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 1, "acp-event", "{}", 2)]);
        Assert.Single(fixture.Store.ListWorkerEvents(enrollment.WorkerId));
        Assert.Empty(fixture.Store.ListWorkerEventRetention(enrollment.WorkerId));
    }

    /// <summary>
    /// A duplicate employee for one runtime binding makes worker ownership
    /// ambiguous, so the snapshot fails closed instead of picking one.
    /// </summary>
    [Fact]
    public void AmbiguousEmployeeBindingFailsTheStatusSnapshotClosed()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var overview = fixture.Store.GetOverview();
        var employee = overview.Employees.Single(x => x.RuntimeBindingId == enrollment.RuntimeBindingId);
        var duplicated = overview with { Employees = [.. overview.Employees, employee with { Id = employee.Id + "-duplicate" }] };

        var provider = new RemoteWorkerStatusProvider(fixture.ControlHost());
        Assert.Single(provider.Snapshot(overview));
        Assert.Throws<OrganizationStoreCorruptException>(() => provider.Snapshot(duplicated));
    }

    [Fact]
    public async Task RejectPermissionRequiresTheExactTrackedRequestSessionClaim()
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Forwarded");
        var pending = new BridgePendingPermission(1, 1, request.Id, request.TurnId, "perm-exact", "sha256:" + new string('7', 64), ["reject_once"], "pending", null);
        var session = new FakeBridgeSession("controller-a", fixture.BridgeStatus() with { ActiveRequestId = request.Id, PendingPermission = pending, DispatchHeld = true, HoldReason = "permission-pending", HoldReasons = ["permission-pending"] });
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));
        await manager.ConnectAndSynchronizeAsync(request.WorkerId, CancellationToken.None);
        var authoritative = fixture.Store.GetWorkerPendingPermission(request.WorkerId, pending.DecisionId)!;
        using (var c = Open(fixture.DatabasePath)) c.Execute($"UPDATE runtime_bindings SET session_ref=NULL WHERE id='{fixture.BindingId}'");

        await Assert.ThrowsAsync<OrganizationConcurrencyException>(() => manager.RejectPermissionAsync(new(request.WorkerId, authoritative.DecisionId, authoritative.Revision), CancellationToken.None));

        Assert.DoesNotContain(session.Invocations, x => x.Operation == "permission");
    }

    [Fact]
    public async Task RejectPermissionDoesNotReconcileForwardedRequestDuringDecision()
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Forwarded");
        var pending = new BridgePendingPermission(1, 1, request.Id, request.TurnId, "perm-reject", "sha256:" + new string('7', 64), ["reject_once"], "pending", null);
        var status = fixture.BridgeStatus() with { ActiveRequestId = request.Id, PendingPermission = pending, DispatchHeld = true, HoldReason = "permission-pending", HoldReasons = ["permission-pending"] };
        var session = new FakeBridgeSession("controller-a", status) { ReconcileRequestState = "forwarded" };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));
        await manager.ConnectAndSynchronizeAsync(request.WorkerId, CancellationToken.None);
        var authoritative = fixture.Store.GetWorkerPendingPermission(request.WorkerId, pending.DecisionId)!;
        var reconcileCount = session.Invocations.Count(x => x.Operation == "reconcile");
        session.ReconcileRequestState = "unknown";

        await manager.RejectPermissionAsync(new(request.WorkerId, authoritative.DecisionId, authoritative.Revision), CancellationToken.None);

        Assert.Equal(reconcileCount, session.Invocations.Count(x => x.Operation == "reconcile"));
        Assert.Equal("decided", fixture.Store.GetWorkerPendingPermission(request.WorkerId, pending.DecisionId)!.State);
        Assert.Equal("Forwarded", fixture.Store.GetWorkerRequest(request.Id)!.State);
        Assert.Single(session.Invocations, x => x.Operation == "permission");
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
        public Task<RemoteOperationResult> BootstrapAsync(ApprovedExecutionHost host, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HostProbePayload> ProbeHostAsync(ApprovedExecutionHost host, CancellationToken cancellationToken) => throw new NotSupportedException();
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
        public RemoteStoreFixture(bool probeHost = true, bool makeDeveloper = true, bool seedSession = true)
        {
            DatabasePath = Path.Combine(_temp.Path, "control.db"); Store = new OrganizationStore(DatabasePath); Store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");
            using var c = Open(DatabasePath); BindingId = Raw(DatabasePath, "SELECT id FROM runtime_bindings LIMIT 1"); EmployeeId = Raw(DatabasePath, $"SELECT employee_id FROM runtime_bindings WHERE id='{BindingId}'"); SessionId = "ses-remote"; if (seedSession) c.Execute($"UPDATE acp_sessions SET status='closed' WHERE employee_id='{EmployeeId}' AND status='active'; INSERT INTO acp_sessions(id,employee_id,native_session_id,title,status,created_at,updated_at) VALUES('{SessionId}','{EmployeeId}','{NativeSessionId}','Remote','active','2026-09-15T00:00:00.0000000+00:00','2026-09-15T00:00:00.0000000+00:00')"); else c.Execute($"UPDATE acp_sessions SET status='closed' WHERE employee_id='{EmployeeId}' AND status='active'"); if (makeDeveloper) c.Execute($"UPDATE runtime_bindings SET placement='DeveloperContainer',container_ref='existing-container',session_ref={(seedSession ? $"'{SessionId}'" : "NULL")} WHERE id='{BindingId}'");
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
        public BridgeWorkerStatus BridgeStatus(long processGeneration = 1, long workerGeneration = 1, long lastSequence = 0) => new(workerGeneration, processGeneration, "running", "life", 42, null, null, 1, true, false, null, [], 0, lastSequence, 0, 0, null, null, 0, [], true, true, true, NativeSessionId);
        public WorkerConnectionManager CreateManager(IWorkerBridgeSessionFactory factory) => new(Control(), factory, Microsoft.Extensions.Options.Options.Create(new WorkerControlOptions
        {
            Enabled = true,
            ControllerId = "controller-a",
            ApprovedImageDigest = Digest,
            ApprovedHosts = [new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Port = 22, Username = "docker", KnownHostsPath = KnownHostsPath, IdentityFilePath = IdentityPath }],
        }), new SystemControllerClock(), new SystemWorkerDelay());
        private HVO.AgentControl.Runtime.AcpControlHost Control()
        {
            var options = new HVO.AgentControl.Runtime.ControlOptions { DataDirectory = _temp.Path, PrivateDataDirectory = Path.Combine(_temp.Path, "private") };
            var control = new HVO.AgentControl.Runtime.AcpControlHost(Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Logging.Abstractions.NullLogger<HVO.AgentControl.Runtime.AcpControlHost>.Instance);
            typeof(HVO.AgentControl.Runtime.AcpControlHost).GetField("_organization", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(control, Store);
            return control;
        }
        public WorkerRequestRecord BeginRequest(string payload) => Store.BeginWorkerRequest(new(EmployeeId, BindingId, "wrk-a", SessionId, NativeSessionId, "idem-a", payload, "sha256:" + new string('f', 64), 1, 1, "turn-test"));
        public WorkerRequestRecord CreateEligibleRequest() { CreateEnrolledAndReady(); return BeginRequest("sha256:" + new string('d', 64)); }

        public HVO.AgentControl.Runtime.AcpControlHost ControlHost() => Control();

        public WorkerControlOptions Options() => new()
        {
            Enabled = true,
            ControllerId = "controller-a",
            ApprovedImageDigest = Digest,
            ApprovedHosts = [new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Port = 22, Username = "docker", KnownHostsPath = KnownHostsPath, IdentityFilePath = IdentityPath }],
            ExpectedControllerUid = ControllerPrivateFile.EffectiveUid,
        };

        public RemoteWorkerProvisioningCoordinator CreateCoordinator(IRemoteWorkerProvisioner remote, IWorkerBridgeSessionFactory? verification = null) =>
            new(Control(), remote, Microsoft.Extensions.Options.Options.Create(Options()), verification ?? new StubVerificationFactory(this));

        private sealed class StubVerificationFactory(RemoteStoreFixture fixture) : IWorkerBridgeSessionFactory
        {
            // Stands in for the post-provisioning verification probe only; it makes
            // no durable claim about the worker.
            public Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken) =>
                Task.FromResult<IWorkerBridgeSession>(new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus()));
        }

        public void Dispose() { Store.Dispose(); _temp.Dispose(); }
    }

    /// <summary>
    /// Records the exact provisioning effects in the order they were applied, so a
    /// test can assert ordering rather than only the end state.
    /// </summary>
    private sealed class RecordingProvisioner : IRemoteWorkerProvisioner
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _labels = new(StringComparer.Ordinal);
        private int _bootstrapAttempts;

        public List<string> Effects { get; } = [];
        public List<string> BootstrapKeyIds { get; } = [];
        public bool FailBootstrapOnce { get; set; }
        public bool FailBootstrapAlways { get; set; }
        public bool FailInspect { get; set; }
        public string? OwnerOverride { get; set; }

        public Task<HostProbePayload> ProbeAsync(ApprovedExecutionHost host, CancellationToken token) => throw new NotSupportedException();

        public Task<string> CreateVolumeAsync(ApprovedExecutionHost host, VolumeCreateSpec spec, CancellationToken token)
        {
            Effects.Add("volume:" + spec.Name);
            _labels[spec.Name] = spec.Identity.Labels;
            return Task.FromResult("volume-ref-" + spec.Name);
        }

        public Task<string> CreateContainerAsync(ApprovedExecutionHost host, ContainerCreateSpec spec, CancellationToken token)
        {
            Effects.Add("container:" + spec.Name);
            _labels[spec.Name] = spec.Identity.Labels;
            return Task.FromResult("container-ref-" + spec.Name);
        }

        public Task BootstrapAsync(ApprovedExecutionHost host, BootstrapSpec spec, byte[] key, CancellationToken token)
        {
            try
            {
                Effects.Add("bootstrap:" + spec.ControlVolumeName);
                BootstrapKeyIds.Add(HVO.AgentControl.Worker.WorkerProtocol.KeyId(key));
                if (FailBootstrapAlways || (FailBootstrapOnce && Interlocked.Increment(ref _bootstrapAttempts) == 1)) throw new RemoteWorkerUnavailableException("injected bootstrap failure", transport: true);
                return Task.CompletedTask;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
        }

        public Task StartAsync(ApprovedExecutionHost host, string container, CancellationToken token) { Effects.Add("start:" + container); return Task.CompletedTask; }
        public Task StopAsync(ApprovedExecutionHost host, string container, CancellationToken token) { Effects.Add("stop:" + container); return Task.CompletedTask; }
        public Task RemoveContainerAsync(ApprovedExecutionHost host, string container, CancellationToken token) { Effects.Add("remove-container:" + container); _labels.Remove(container); return Task.CompletedTask; }
        public Task RemoveVolumeAsync(ApprovedExecutionHost host, string volume, CancellationToken token) { Effects.Add("remove-volume:" + volume); _labels.Remove(volume); return Task.CompletedTask; }

        public Task<RemoteResourceInspection> InspectVolumeAsync(ApprovedExecutionHost host, string name, CancellationToken token) => Inspect(name, "present");
        public Task<RemoteResourceInspection> InspectContainerAsync(ApprovedExecutionHost host, string name, CancellationToken token) => Inspect(name, "running");

        private Task<RemoteResourceInspection> Inspect(string name, string state)
        {
            if (FailInspect) throw new RemoteWorkerUnavailableException("injected inspect failure", transport: true);
            if (!_labels.TryGetValue(name, out var labels)) return Task.FromResult(new RemoteResourceInspection(false, null, new Dictionary<string, string>(), "absent"));
            var effective = new Dictionary<string, string>(labels, StringComparer.Ordinal)
            {
                ["org.opencontainers.image.revision"] = "inherited-image-label",
            };
            if (OwnerOverride is not null) effective["agentcontrol.owner"] = OwnerOverride;
            return Task.FromResult(new RemoteResourceInspection(true, "ref-" + name, effective, state));
        }
    }

    /// <summary>
    /// The acknowledgment must describe the batch that was actually replayed, not
    /// the controller's global cursor, or the worker would prune events in a
    /// generation the controller never received.
    /// </summary>
    [Fact]
    public async Task ReplayAcknowledgesTheExactBatchGenerationAndSequenceNotTheGlobalCursor()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        // The controller's global cursor is already at generation 2, sequence 7,
        // while the worker now reports generation 1 (its journal was rebuilt from an
        // older control volume). The cursor therefore cannot advance for anything the
        // generation-1 replay delivers.
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { WorkerGeneration = 2 }, [new(2, 7, "acp-event", "{}", 2)]);
        Assert.Equal((2L, 7L), (fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.AcknowledgedWorkerGeneration, fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.AcknowledgedSequence));

        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 4));
        session.ReplayBatches[(1, 0)] = [new(1, 3, "acp-event", "{}", 2), new(1, 4, "acp-event", "{}", 2)];
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        // The acknowledgment describes the batch that was actually delivered.
        var acknowledgment = Assert.Single(session.Acknowledgments);
        Assert.Equal((1L, 4L), acknowledgment);
        // Acknowledging the global cursor here would have told the worker the
        // controller holds generation 2 through sequence 7 on a generation-1 batch.
        Assert.DoesNotContain(session.Acknowledgments, x => x.Generation == 2);
    }

    [Fact]
    public async Task ReplayPagesAreCommittedAndAcknowledgedOnePageAtATime()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 3));
        session.ReplayPages[(1, 0)] = new BridgeReplayPage([new(1, 1, "acp-event", "{}", 2), new(1, 2, "acp-event", "{}", 2)], true, 2);
        session.ReplayPages[(1, 2)] = new BridgeReplayPage([new(1, 3, "acp-event", "{}", 2)], false, 3);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal([(1L, 2L), (1L, 3L)], session.Acknowledgments);
        Assert.Contains(session.Invocations, x => x.Operation == "replay" && x.Payload.Contains("\"afterSequence\":2", StringComparison.Ordinal));
        Assert.Equal((1L, 3L), (fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.AcknowledgedWorkerGeneration, fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.AcknowledgedSequence));
    }

    [Fact]
    public async Task ReplayAllowsManyByteBoundPagesIndependentOfTheInitialSequenceSnapshot()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 160));
        var payload = JsonSerializer.Serialize(new { value = new string('x', 60 * 1024) });
        for (var pageIndex = 0; pageIndex < 40; pageIndex++)
        {
            var firstSequence = pageIndex * 4L + 1;
            var events = Enumerable.Range(0, 4).Select(offset => new BridgeWorkerEvent(1, firstSequence + offset, "acp-event", payload, payload.Length)).ToArray();
            session.ReplayPages[(1, firstSequence - 1)] = new BridgeReplayPage(events, pageIndex < 39, firstSequence + 3);
        }
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal(40, session.Acknowledgments.Count);
        Assert.Equal(160, fixture.Store.ListWorkerEvents(enrollment.WorkerId).Count);
    }

    [Fact]
    public async Task ReplayAllowsTenThousandSingleEventPagesAndOneFinalPage()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: WorkerConnectionManager.MaxReplayEvents));
        session.ReplayPageFactory = (generation, after) => after < WorkerConnectionManager.MaxReplayEvents
            ? new BridgeReplayPage([new(generation, after + 1, "acp-event", "{}", 2)], true, after + 1)
            : new BridgeReplayPage([], false, after);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal(WorkerConnectionManager.MaxReplayEvents, session.Acknowledgments.Count);
        Assert.Equal(WorkerConnectionManager.MaxReplayEvents, session.Invocations.Count(x => x.Operation == "replay"));
    }

    [Fact]
    public async Task MaliciousReplayCannotRequestPastTheIndependentPageBound()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 1, "acp-event", "{}", 2)]);
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(workerGeneration: 2));
        session.ReplayPageFactory = (generation, after) => new BridgeReplayPage([new(generation, after + 1, "acp-event", "{}", 2)], true, after + 1);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await Assert.ThrowsAsync<WorkerProtocolException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));

        Assert.Equal(WorkerConnectionManager.MaxReplayPages, session.Invocations.Count(x => x.Operation == "replay"));
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "replay-gap" && x.Source == "controller");
    }

    [Fact]
    public async Task ReplayPageThatCannotProgressIsRejectedAndCreatesARecoveryObligation()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 1));
        session.ReplayPages[(1, 0)] = new BridgeReplayPage([], true, 0);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await Assert.ThrowsAsync<WorkerProtocolException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));

        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "replay-gap" && x.Source == "controller" && x.MarkerJson is null);
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Fact]
    public async Task NullReplayEventsAndItemsAreRejectedAsWorkerProtocolFailures()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var nullEvents = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 1));
        nullEvents.ReplayPages[(1, 0)] = new BridgeReplayPage(null, false, 0);
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(nullEvents)))
            await Assert.ThrowsAsync<WorkerProtocolException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));

        var nullItem = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 1));
        nullItem.ReplayPages[(1, 0)] = new BridgeReplayPage([null], false, 0);
        await using var second = fixture.CreateManager(new FakeBridgeSessionFactory(nullItem));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => second.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));
    }

    [Fact]
    public async Task CurrentGenerationCursorPastSnapshotTargetCreatesControllerReplayGapAndHolds()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 2, "acp-event", "{}", 2)]);
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 1));
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.True(lease.Status.DispatchHeld);
        Assert.Contains("replay-gap", lease.Status.HoldReasons);
        Assert.DoesNotContain(session.Invocations, item => item.Operation == "replay");
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), item => item.Source == "controller" && item.Kind == "replay-gap" && item.MarkerJson is null);
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Fact]
    public async Task ConflictingDuplicateReplayMetadataCreatesControllerReplayGapAndThrowsProtocolFailure()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 1, "acp-event", "{}", 2)]);
        using (var connection = Open(fixture.DatabasePath))
            connection.Execute($"UPDATE worker_cursors SET acknowledged_sequence=0 WHERE worker_id='{enrollment.WorkerId}'");
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 1));
        session.ReplayPages[(1, 0)] = new BridgeReplayPage([new(1, 1, "different", "{}", 2)], false, 1);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));

        Assert.IsType<OrganizationConcurrencyException>(failure.InnerException);
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), item => item.Source == "controller" && item.Kind == "replay-gap" && item.MarkerJson is null);
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Fact]
    public async Task ReplayEndingBeforeSnapshotPersistsExactAuthoritativeWorkerMarkersAndHolds()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var initial = fixture.BridgeStatus(lastSequence: 2);
        var gap = new BridgeReplayGap("gap:terminal-page", "loss", 1, 0, 3, 2, 1, 2);
        var loss = new BridgeReplayLoss(1, 2, 1, 2);
        var authoritative = initial with
        {
            DispatchHeld = true,
            HoldReason = "replay-loss-unreconciled",
            HoldReasons = ["replay-loss-unreconciled", "replay-gap-unreconciled"],
            ReplayLoss = loss,
            ReplayGapCount = 1,
            ReplayGaps = [gap],
        };
        var session = new FakeBridgeSession(enrollment.ControllerId, initial, authoritative);
        session.ReplayPages[(1, 0)] = new BridgeReplayPage([], false, 0);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.True(lease.Status.DispatchHeld);
        Assert.Equal(authoritative.ReplayLoss, lease.Status.ReplayLoss);
        Assert.Equal(authoritative.ReplayGaps, lease.Status.ReplayGaps);
        Assert.Equal(authoritative.HoldReasons, lease.Status.HoldReasons);
        var active = fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true);
        Assert.Contains(active, item => item.Source == "worker" && item.Kind == "replay-gap" && item.MarkerJson == JsonSerializer.Serialize(gap, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions));
        Assert.Contains(active, item => item.Source == "worker" && item.Kind == "replay-loss" && item.MarkerJson == JsonSerializer.Serialize(loss, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions));
        Assert.DoesNotContain(active, item => item.Source == "controller" && item.Kind == "replay-gap");
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Fact]
    public async Task ReplayEndingBeforeSnapshotWithUnavailableStatusCreatesControllerMarkerAndThrows()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 2))
        {
            FailStatusAfterReplay = true,
        };
        session.ReplayPages[(1, 0)] = new BridgeReplayPage([], false, 0);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));

        Assert.IsType<WorkerReadUncertainException>(failure.InnerException);
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), item => item.Source == "controller" && item.Kind == "replay-gap" && item.MarkerJson is null);
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Fact]
    public async Task ReplayEndingBeforeSnapshotWithExactStatusStoreConflictCreatesControllerMarkerAndThrows()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var hash = "sha256:" + new string('c', 64);
        var existing = new ControllerPendingPermission(1, 1, "request-a", "turn", "decision", hash, ["allow"], "pending");
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { PendingPermissionHash = hash, PendingPermission = existing }, []);
        var initial = fixture.BridgeStatus(lastSequence: 2);
        var gap = new BridgeReplayGap("gap:conflicting-projection", "loss", 1, 0, 3, 2, 1, 2);
        var authoritative = initial with
        {
            DispatchHeld = true,
            HoldReason = "replay-gap-unreconciled",
            HoldReasons = ["replay-gap-unreconciled"],
            ReplayGapCount = 1,
            ReplayGaps = [gap],
            PendingPermission = new BridgePendingPermission(1, 1, "request-b", "turn", "decision", hash, ["allow"], "pending", null),
        };
        var session = new FakeBridgeSession(enrollment.ControllerId, initial, authoritative);
        session.ReplayPages[(1, 0)] = new BridgeReplayPage([], false, 0);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));

        Assert.IsType<OrganizationConcurrencyException>(failure.InnerException);
        var active = fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true);
        Assert.Contains(active, item => item.Source == "controller" && item.Kind == "replay-gap" && item.MarkerJson is null);
        Assert.DoesNotContain(active, item => item.Source == "worker" && item.Kind == "replay-gap");
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Fact]
    public async Task ZeroRetentionWorkerReplayLossIsProjectedExactlyWithoutMarkerlessReplacement()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        using var workerTemp = new TempDirectory();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(workerTemp.Path, (UnixFileMode)0x1C0);
        var workerOptions = new WorkerOptions(workerTemp.Path, "worker-test", "controller-test", System.IO.Path.Combine(workerTemp.Path, "bridge.sock"), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), EventLimit: 0);
        using var workerStore = new WorkerStore(workerOptions);
        workerStore.BeginProcessStart();
        workerStore.CompleteProcessStart("lifecycle", 123);
        Assert.Throws<WorkerReplayLossException>(() => workerStore.AppendEvent("acp-event", "{}"));
        var workerStatus = workerStore.Status();
        var loss = Assert.IsType<ReplayLoss>(workerStatus.ReplayLoss);
        var authoritative = fixture.BridgeStatus(lastSequence: workerStatus.LastSequence) with
        {
            DispatchHeld = workerStatus.DispatchHeld,
            HoldReason = workerStatus.HoldReason,
            HoldReasons = workerStatus.HoldReasons,
            FirstRetainedSequence = workerStatus.FirstRetainedSequence,
            ReplayLoss = new(loss.WorkerGeneration, loss.MarkerSequence, loss.DroppedCount, loss.DroppedBytes),
        };
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: workerStatus.LastSequence), authoritative)
        {
            ReplayPageFactory = (generation, after) =>
            {
                Assert.Throws<WorkerProtocolException>(() => workerStore.Replay(generation, after));
                throw new WorkerRemoteException("worker-request-rejected");
            },
        };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.True(lease.Status.DispatchHeld);
        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), item => item.Source == "worker" && item.Kind == "replay-loss" && item.MarkerJson is not null);
        Assert.Equal(JsonSerializer.Serialize(authoritative.ReplayLoss, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions), obligation.MarkerJson);
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), item => item.Source == "controller" && item.Kind == "replay-gap");
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    [Fact]
    public async Task ReplayPageCrossingCurrentGenerationSnapshotCommitsAndAcksWithoutRecoveryThenNextSyncGetsSuffix()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 1));
        session.ReplayPages[(1, 0)] = new BridgeReplayPage([new(1, 1, "acp-event", "{}", 2), new(1, 2, "acp-event", "{}", 2)], true, 2);
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session)))
        {
            var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

            Assert.Equal([(1L, 2L)], session.Acknowledgments);
            Assert.False(lease.Status.DispatchHeld);
            Assert.DoesNotContain(session.Invocations, x => x.Operation == "replay" && x.Payload.Contains("\"afterSequence\":2", StringComparison.Ordinal));
            Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), item => item.Kind == "replay-gap");
        }

        var next = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 3));
        next.ReplayPages[(1, 2)] = new BridgeReplayPage([new(1, 3, "acp-event", "{}", 2)], false, 3);
        await using var second = fixture.CreateManager(new FakeBridgeSessionFactory(next));
        await second.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal([(1L, 3L)], next.Acknowledgments);
        Assert.Contains(next.Invocations, x => x.Operation == "replay" && x.Payload.Contains("\"afterSequence\":2", StringComparison.Ordinal));
        Assert.Equal([(1L, 1L), (1L, 2L), (1L, 3L)], fixture.Store.ListWorkerEvents(enrollment.WorkerId).Select(x => (x.WorkerGeneration, x.Sequence)).ToArray());
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), item => item.Kind == "replay-gap");
    }

    [Fact]
    public async Task ReplayAppendDuringPagingIsAcceptedFromAWorkerStore()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        using var workerTemp = new TempDirectory();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(workerTemp.Path, (UnixFileMode)0x1C0);
        var workerOptions = new WorkerOptions(workerTemp.Path, "worker-test", "controller-test", System.IO.Path.Combine(workerTemp.Path, "bridge.sock"), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), EventLimit: 10);
        using var workerStore = new WorkerStore(workerOptions);
        workerStore.BeginProcessStart();
        workerStore.CompleteProcessStart("lifecycle", 123);
        workerStore.AppendEvent("acp-event", "{}");
        var initial = workerStore.Status();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: initial.LastSequence))
        {
            ReplayPageFactory = (generation, after) =>
            {
                var page = workerStore.Replay(generation, after);
                if (after == 0) workerStore.AppendEvent("acp-event", "{}");
                return new BridgeReplayPage(page.Events.Select(x => new BridgeWorkerEvent(x.WorkerGeneration, x.Sequence, x.Kind, x.PayloadJson, x.ByteCount)).ToArray(), page.HasMore || after == 0, page.NextAfterSequence);
            },
        };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal([(1L, 1L)], session.Acknowledgments);
        Assert.Single(fixture.Store.ListWorkerEvents(enrollment.WorkerId));
    }

    [Fact]
    public async Task ReplayStopsAtTheCurrentGenerationSnapshotWhileTheWorkerContinuouslyAppends()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        const long target = WorkerConnectionManager.MaxReplayEvents;
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: target));
        session.ReplayPageFactory = (generation, after) =>
        {
            var events = Enumerable.Range(1, 256).Select(offset => new BridgeWorkerEvent(generation, after + offset, "acp-event", "{}", 2)).ToArray();
            return new BridgeReplayPage(events, true, after + events.Length);
        };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal(40, session.Acknowledgments.Count);
        Assert.Equal(10_240, session.Acknowledgments[^1].Sequence);
        Assert.Equal(40, session.Invocations.Count(x => x.Operation == "replay"));
        Assert.DoesNotContain(session.Invocations, x => x.Operation == "replay" && x.Payload.Contains("\"afterSequence\":10240", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1, "{}", "acp-event")]
    [InlineData(65_537, "{}", "acp-event")]
    [InlineData(1, "é", "acp-event")]
    [InlineData(2, "{}", "")]
    public async Task InvalidRawReplayEventMetadataIsRejectedBeforeSanitization(int byteCount, string payload, string kind)
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 1));
        session.ReplayPages[(1, 0)] = new BridgeReplayPage([new(1, 1, kind, payload, byteCount)], false, 1);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await Assert.ThrowsAsync<WorkerProtocolException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));

        Assert.Empty(fixture.Store.ListWorkerEvents(enrollment.WorkerId));
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "replay-gap" && x.Source == "controller");
    }

    [Fact]
    public async Task OversizedRawReplayPayloadAndKindAreRejectedBeforeSanitization()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var payload = JsonSerializer.Serialize(new { value = new string('x', 64 * 1024) });
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 1));
        session.ReplayPages[(1, 0)] = new BridgeReplayPage([new(1, 1, new string('k', 65), payload, Encoding.UTF8.GetByteCount(payload))], false, 1);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await Assert.ThrowsAsync<WorkerProtocolException>(() => manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None));

        Assert.Empty(fixture.Store.ListWorkerEvents(enrollment.WorkerId));
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "replay-gap" && x.Source == "controller");
    }

    [Fact]
    public async Task ReplayValidatesRawBytesThenStoresSanitizedMetadata()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        const string payload = "{ \"value\": 1 }";
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 1));
        session.ReplayPages[(1, 0)] = new BridgeReplayPage([new(1, 1, "acp event!", payload, Encoding.UTF8.GetByteCount(payload))], false, 1);
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        var stored = Assert.Single(fixture.Store.ListWorkerEvents(enrollment.WorkerId));
        Assert.Equal("acpevent", stored.Kind);
        Assert.Equal(11, stored.PayloadBytes);
    }

    /// <summary>
    /// A failed acknowledgment is a real data-loss risk, so it becomes a durable
    /// obligation whose hold survives a later healthy status and is discharged only
    /// when the worker accepts the exact marker.
    /// </summary>
    [Fact]
    public async Task FailedAcknowledgmentHoldsUntilTheExactMarkerIsAcceptedOnReconnect()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();

        var failing = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 2)) { FailAcknowledgment = true };
        failing.ReplayBatches[(1, 0)] = [new(1, 1, "acp-event", "{}", 2), new(1, 2, "acp-event", "{}", 2)];
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(failing)))
        {
            await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);
        }

        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "replay-ack-uncertain");
        Assert.Equal(WorkerConnectionManager.AckMarker(1, 2), obligation.MarkerJson);
        // The hold survives the healthy final status of the same connect.
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);

        // Reconnect: the exact marker is re-sent before replay and then discharged.
        var recovering = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus());
        await using var second = fixture.CreateManager(new FakeBridgeSessionFactory(recovering));
        await second.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Contains(recovering.Acknowledgments, x => x.Generation == 1 && x.Sequence == 2);
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "replay-ack-uncertain");
    }

    [Fact]
    public async Task FailedPageAcknowledgmentBlocksNewerGenerationUntilReconnectFinishesTheSuffix()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), [new(1, 1, "acp-event", "{}", 2)]);
        var failing = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(workerGeneration: 2, lastSequence: 1))
        {
            FailAcknowledgmentAt = (1, 2),
        };
        failing.ReplayPages[(1, 1)] = new BridgeReplayPage([new(1, 2, "acp-event", "{}", 2)], true, 2);
        failing.ReplayPages[(1, 2)] = new BridgeReplayPage([new(1, 3, "acp-event", "{}", 2)], false, 3);
        failing.ReplayPages[(2, 0)] = new BridgeReplayPage([new(2, 1, "acp-event", "{}", 2)], false, 1);
        await using (var manager = fixture.CreateManager(new FakeBridgeSessionFactory(failing)))
            await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.DoesNotContain(failing.Invocations, x => x.Operation == "replay" && x.Payload.Contains("\"workerGeneration\":2", StringComparison.Ordinal));
        Assert.Equal([(1L, 1L), (1L, 2L)], fixture.Store.ListWorkerEvents(enrollment.WorkerId).Select(x => (x.WorkerGeneration, x.Sequence)).ToArray());
        Assert.Equal((1L, 2L), (fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.AcknowledgedWorkerGeneration, fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.AcknowledgedSequence));
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);

        var recovering = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(workerGeneration: 2, lastSequence: 1));
        recovering.ReplayPages[(1, 2)] = new BridgeReplayPage([new(1, 3, "acp-event", "{}", 2)], false, 3);
        recovering.ReplayPages[(2, 0)] = new BridgeReplayPage([new(2, 1, "acp-event", "{}", 2)], false, 1);
        await using var second = fixture.CreateManager(new FakeBridgeSessionFactory(recovering));
        await second.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.Equal([(1L, 2L), (1L, 3L), (2L, 1L)], recovering.Acknowledgments);
        var replayInvocations = recovering.Invocations.Where(x => x.Operation == "replay").ToArray();
        Assert.Contains("\"workerGeneration\":1", replayInvocations[0].Payload, StringComparison.Ordinal);
        Assert.Contains("\"afterSequence\":2", replayInvocations[0].Payload, StringComparison.Ordinal);
        Assert.Contains("\"workerGeneration\":2", replayInvocations[1].Payload, StringComparison.Ordinal);
        Assert.Equal([(1L, 1L), (1L, 2L), (1L, 3L), (2L, 1L)], fixture.Store.ListWorkerEvents(enrollment.WorkerId).Select(x => (x.WorkerGeneration, x.Sequence)).ToArray());
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "replay-ack-uncertain");
    }

    [Fact]
    public async Task FailedPendingAcknowledgmentKeepsDispatchHeldAndSkipsAllReplay()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        fixture.Store.RecordControllerRecovery(enrollment.WorkerId, "replay-ack-uncertain", "1:2", WorkerConnectionManager.AckMarker(1, 2));
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(workerGeneration: 2, lastSequence: 1)) { FailAcknowledgment = true };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        Assert.True(lease.Status.DispatchHeld);
        Assert.Contains("replay-ack-uncertain", lease.Status.HoldReasons);
        Assert.DoesNotContain(session.Invocations, x => x.Operation == "replay");
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    /// <summary>
    /// Caller cancellation before any byte is written leaves no remote effect, so
    /// it must not fault the owner session other callers share.
    /// </summary>
    [Fact]
    public async Task CallerCancellationBeforeTheWriteDoesNotFaultTheSharedOwnerSession()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus());
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));
        var lease = await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.HeartbeatAsync(enrollment.WorkerId, TimeSpan.FromSeconds(10), canceled.Token));

        // The cached lease is still the same live session, and the store was not
        // told the connection was lost.
        Assert.Same(lease, await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.NotEqual("disconnected", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
    }

    /// <summary>
    /// Once a mutation write has been attempted the outcome is unknown, so the
    /// session is faulted and the request is recorded uncertain rather than retried.
    /// </summary>
    [Fact]
    public async Task UncertainWriteAfterTheAttemptFaultsTheSessionAndRecordsUncertainty()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus()) { FailSubmitAsWriteUncertain = true };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var request = await manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "idem-uncertain", "hello"), CancellationToken.None);

        Assert.Equal("Uncertain", request.State);
        Assert.Null(await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "request-uncertain");
    }

    [Fact]
    public async Task RemoteUncertainCategoryFaultsTheSessionAndRecordsRequestUncertainty()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus()) { FailSubmitAsRemoteUncertain = true };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        var request = await manager.DispatchAsync(new(fixture.EmployeeId, fixture.BindingId, enrollment.WorkerId, fixture.SessionId, fixture.NativeSessionId, "idem-remote-uncertain", "hello"), CancellationToken.None);

        Assert.Equal("Uncertain", request.State);
        Assert.Equal("worker-operation-uncertain", request.OutcomeCategory);
        Assert.Null(await manager.GetCachedLeaseAsync(enrollment.WorkerId, CancellationToken.None));
    }

    [Fact]
    public async Task ReplayRejectionReadsAuthoritativeGapAndLossMarkersForExactRecovery()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var initial = fixture.BridgeStatus(lastSequence: 1);
        var gap = new BridgeReplayGap("gap:rejected-replay", "future-cursor", 1, 9, 2, 8, 1, 7);
        var loss = new BridgeReplayLoss(1, 7, 3, 128);
        var held = initial with
        {
            DispatchHeld = true,
            HoldReason = "replay-gap-unreconciled",
            HoldReasons = ["replay-gap-unreconciled", "replay-loss-unreconciled"],
            ReplayLoss = loss,
            ReplayGapCount = 1,
            ReplayGaps = [gap],
        };
        var session = new FakeBridgeSession(enrollment.ControllerId, initial, held)
        {
            RejectReplay = true,
            ClearGapsAfterReconcile = true,
            ClearLossAfterReconcile = true,
        };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        var active = fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true);
        var gapObligation = Assert.Single(active, item => item.Source == "worker" && item.Kind == "replay-gap" && item.MarkerJson is not null);
        var lossObligation = Assert.Single(active, item => item.Source == "worker" && item.Kind == "replay-loss" && item.MarkerJson is not null);
        Assert.DoesNotContain(active, item => item.Source == "controller" && item.Kind == "replay-gap");
        Assert.Contains("gap:rejected-replay", gapObligation.MarkerJson, StringComparison.Ordinal);
        Assert.Contains("\"markerSequence\":7", lossObligation.MarkerJson, StringComparison.Ordinal);

        await manager.RecoverAsync(enrollment.WorkerId, gapObligation.Id, gapObligation.Revision, CancellationToken.None);
        lossObligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), item => item.Kind == "replay-loss" && item.MarkerJson is not null);
        await manager.RecoverAsync(enrollment.WorkerId, lossObligation.Id, lossObligation.Revision, CancellationToken.None);

        var gapRecovery = Assert.Single(session.Invocations, item => item.Operation == "reconcile-replay-gap");
        Assert.Contains("gap:rejected-replay", gapRecovery.Payload, StringComparison.Ordinal);
        var lossRecovery = Assert.Single(session.Invocations, item => item.Operation == "reconcile-replay-loss");
        Assert.Contains("\"workerGeneration\":1", lossRecovery.Payload, StringComparison.Ordinal);
        Assert.Contains("\"markerSequence\":7", lossRecovery.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), item => item.Kind is "replay-gap" or "replay-loss");
    }

    [Fact]
    public async Task ReplayRejectionWithoutReadableStatusRequiresAuditedOwnerDisposition()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var session = new FakeBridgeSession(enrollment.ControllerId, fixture.BridgeStatus(lastSequence: 1))
        {
            RejectReplay = true,
            FailStatusAfterRejectedReplay = true,
        };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None);

        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), item => item.Source == "controller" && item.Kind == "replay-gap");
        Assert.Null(obligation.MarkerJson);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status(), []);
        obligation = fixture.Store.GetWorkerRecoveryObligation(obligation.Id)!;
        Assert.True(obligation.Active); // Healthy status never auto-clears controller evidence.
        await Assert.ThrowsAsync<WorkerRecoveryRequiredException>(() => manager.RecoverAsync(enrollment.WorkerId, obligation.Id, obligation.Revision, CancellationToken.None));
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, obligation.Id, obligation.Revision, "sha256:bad", "acknowledged-after-external-reconciliation"));
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, obligation.Id, obligation.Revision, "sha256:" + new string('a', 64), "wrong"));

        var resolved = await manager.AcknowledgeRecoveryAsync(enrollment.WorkerId, obligation.Id, obligation.Revision, "sha256:" + new string('a', 64), "acknowledged-after-external-reconciliation", CancellationToken.None);

        Assert.False(resolved.Active);
        var audit = Assert.Single(fixture.Store.ListWorkerRecoveryAudit(enrollment.WorkerId));
        Assert.Equal(obligation.Id, audit.ObligationId);
        Assert.Equal(obligation.MarkerHash, audit.MarkerHash);
        Assert.Equal("sha256:" + new string('a', 64), audit.EvidenceHash);
        Assert.Equal("acknowledged-after-external-reconciliation", audit.Disposition);
        var acknowledgedCursor = fixture.Store.GetWorkerCursor(enrollment.WorkerId)!;
        Assert.Equal("disconnected", acknowledgedCursor.ConnectionState);
        Assert.Null(acknowledgedCursor.HoldSummary);
        Assert.False(acknowledgedCursor.ViewerAvailable);
        fixture.Store.RecordWorkerConnectionState(enrollment.WorkerId, "authenticated", acknowledgedCursor.ObservedOwnershipEpoch);
        Assert.Equal("authenticated", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState);
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, obligation.Id, obligation.Revision, "sha256:" + new string('b', 64), "acknowledged-after-external-reconciliation"));
    }

    [Fact]
    public void ControllerRecoveryAcknowledgementRejectsWrongSourceMarkerKindAndRevision()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        const string evidence = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var uncertainRequest = fixture.BeginRequest("sha256:" + new string('d', 64));
        uncertainRequest = fixture.Store.TransitionWorkerRequest(uncertainRequest.Id, uncertainRequest.Revision, "Intent", "Forwarding");
        fixture.Store.TransitionWorkerRequest(uncertainRequest.Id, uncertainRequest.Revision, "Forwarding", "Uncertain");

        var gap = new BridgeReplayGap("gap:test", "status", 1, 5, 1, 4, null, null);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { DispatchHeld = true, HoldReasons = ["replay-gap-unreconciled"], ReplayGapMarkers = [JsonSerializer.Serialize(gap, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions)] }, []);
        var worker = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Source == "worker" && x.MarkerJson is not null);
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, worker.Id, worker.Revision, evidence, "acknowledged-after-external-reconciliation"));

        fixture.Store.RecordControllerRecovery(enrollment.WorkerId, "replay-ack-uncertain", "ack");
        var wrongKind = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Source == "controller" && x.Kind == "replay-ack-uncertain");
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, wrongKind.Id, wrongKind.Revision, evidence, "acknowledged-after-external-reconciliation"));

        fixture.Store.RecordControllerRecovery(enrollment.WorkerId, "ownership-changed", "owner", "{}");
        var markerBearing = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Source == "controller" && x.Kind == "ownership-changed");
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, markerBearing.Id, markerBearing.Revision, evidence, "acknowledged-after-external-reconciliation"));

        fixture.Store.RecordControllerRecovery(enrollment.WorkerId, "session-reconciliation", "session", "{\"kind\":\"session-reconciliation\",\"expectedSessionId\":\"native-remote\",\"observedSessionId\":\"native-other\"}");
        var session = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Source == "controller" && x.Kind == "session-reconciliation");
        Assert.False(fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, session.Id, session.Revision, evidence, "acknowledged-after-external-reconciliation").Active);
        Assert.Equal("session-reconciliation", Assert.Single(fixture.Store.ListWorkerRecoveryAudit(enrollment.WorkerId), x => x.ObligationId == session.Id).Kind);

        var requestUncertain = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Source == "controller" && x.Kind == "request-uncertain");
        Assert.False(fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, requestUncertain.Id, requestUncertain.Revision, evidence, "acknowledged-after-external-reconciliation").Active);
        Assert.Equal("request-uncertain", Assert.Single(fixture.Store.ListWorkerRecoveryAudit(enrollment.WorkerId), x => x.ObligationId == requestUncertain.Id).Kind);

        fixture.Store.RecordControllerRecovery(enrollment.WorkerId, "request-uncertain", "request-invalid-marker", "{\"kind\":\"request-uncertain\",\"requestId\":\"different-request\"}");
        var invalidRequestMarker = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Source == "controller" && x.Kind == "request-uncertain");
        Assert.Throws<OrganizationValidationException>(() => fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, invalidRequestMarker.Id, invalidRequestMarker.Revision, evidence, "acknowledged-after-external-reconciliation"));

        fixture.Store.RecordControllerRecovery(enrollment.WorkerId, "replay-gap", "controller-gap");
        var markerless = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Source == "controller" && x.Kind == "replay-gap");
        Assert.Throws<OrganizationConcurrencyException>(() => fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, markerless.Id, markerless.Revision + 1, evidence, "acknowledged-after-external-reconciliation"));

        fixture.Store.RecordControllerRecovery(enrollment.WorkerId, "replay-gap", "other-gap");
        var first = fixture.Store.AcknowledgeControllerRecovery(enrollment.WorkerId, markerless.Id, markerless.Revision, evidence, "acknowledged-after-external-reconciliation");
        Assert.False(first.Active);
        Assert.Equal("held", fixture.Store.GetWorkerCursor(enrollment.WorkerId)!.ConnectionState); // another obligation still holds dispatch
    }

    /// <summary>
    /// Owner-triggered recovery must invoke the exact worker reconciliation and
    /// clear the controller obligation only after the worker accepted it.
    /// </summary>
    [Fact]
    public async Task OwnerRecoveryInvokesTheExactWorkerReconciliationBeforeClearing()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var gap = new BridgeReplayGap("gap:test", "status", 1, 5, 1, 4, null, null);
        var gapMarker = JsonSerializer.Serialize(gap, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { DispatchHeld = true, HoldReasons = ["replay-gap-unreconciled"], ReplayGapMarkers = [gapMarker] }, []);
        // The hold reason and the gap marker both project a replay-gap obligation;
        // only the marker-bearing one can address the exact worker-side gap.
        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "replay-gap" && x.MarkerJson is not null);

        // The worker still reports the gap while the owner acts on it, and stops
        // reporting it once it accepts the reconciliation.
        var held = fixture.BridgeStatus() with { DispatchHeld = true, HoldReasons = ["replay-gap-unreconciled"], ReplayGaps = [gap], ReplayGapCount = 1 };
        var session = new FakeBridgeSession(enrollment.ControllerId, held) { ClearGapsAfterReconcile = true };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await manager.RecoverAsync(enrollment.WorkerId, obligation.Id, obligation.Revision, CancellationToken.None);

        // The worker was actually told to reconcile the exact gap, before anything
        // was cleared on the controller side.
        var invoked = Assert.Single(session.Invocations, x => x.Operation == "reconcile-replay-gap");
        Assert.Contains("gap:test", invoked.Payload, StringComparison.Ordinal);
        Assert.True(session.Invocations.FindIndex(x => x.Operation == "reconcile-replay-gap") >= 0);
        Assert.DoesNotContain(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Id == obligation.Id);
    }

    [Fact]
    public async Task OwnerRecoveryRefusesWhenTheWorkerRejectsAndLeavesTheObligationOpen()
    {
        using var fixture = new RemoteStoreFixture();
        var enrollment = fixture.CreateEnrolledAndReady();
        var gap = new BridgeReplayGap("gap:test", "status", 1, 5, 1, 4, null, null);
        var gapMarker = JsonSerializer.Serialize(gap, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions);
        fixture.Store.RecordWorkerStatusAndEvents(enrollment.WorkerId, fixture.Status() with { DispatchHeld = true, HoldReasons = ["replay-gap-unreconciled"], ReplayGapMarkers = [gapMarker] }, []);
        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Kind == "replay-gap" && x.MarkerJson is not null);

        var held = fixture.BridgeStatus() with { DispatchHeld = true, HoldReasons = ["replay-gap-unreconciled"], ReplayGaps = [gap], ReplayGapCount = 1 };
        var session = new FakeBridgeSession(enrollment.ControllerId, held) { RejectRecovery = true };
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await Assert.ThrowsAsync<WorkerRemoteException>(() => manager.RecoverAsync(enrollment.WorkerId, obligation.Id, obligation.Revision, CancellationToken.None));
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(enrollment.WorkerId, true), x => x.Id == obligation.Id);
    }

    /// <summary>
    /// An uncertain request has no remote reconciliation that can establish its
    /// real outcome, so recovery refuses it rather than clearing the obligation.
    /// </summary>
    [Fact]
    public async Task OwnerRecoveryRefusesKindsWithNoRemoteReconciliation()
    {
        using var fixture = new RemoteStoreFixture();
        var request = fixture.CreateEligibleRequest();
        request = fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Intent", "Forwarding");
        fixture.Store.TransitionWorkerRequest(request.Id, request.Revision, "Forwarding", "Uncertain");
        var obligation = Assert.Single(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Kind == "request-uncertain");

        var session = new FakeBridgeSession("controller-a", fixture.BridgeStatus());
        await using var manager = fixture.CreateManager(new FakeBridgeSessionFactory(session));

        await Assert.ThrowsAsync<WorkerRecoveryRequiredException>(() => manager.RecoverAsync(request.WorkerId, obligation.Id, obligation.Revision, CancellationToken.None));
        Assert.Contains(fixture.Store.ListWorkerRecoveryObligations(request.WorkerId, true), x => x.Id == obligation.Id);
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
            : this(controllerId, 1, statuses)
        {
        }

        public FakeBridgeSession(string controllerId, long leaseEpoch, params BridgeWorkerStatus[] statuses)
        {
            Lease = new WorkerBridgeLease(leaseEpoch, controllerId, Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow);
            _statuses = new Queue<BridgeWorkerStatus>(statuses);
        }

        public WorkerBridgeLease Lease { get; }

        /// <summary>Replay results keyed by the exact (generation, afterSequence) request.</summary>
        public Dictionary<(long Generation, long After), BridgeWorkerEvent[]> ReplayBatches { get; } = [];
        public Dictionary<(long Generation, long After), BridgeReplayPage> ReplayPages { get; } = [];
        public Func<long, long, BridgeReplayPage>? ReplayPageFactory { get; set; }
        public List<(long Generation, long Sequence)> Acknowledgments { get; } = [];
        public List<(string Operation, string Payload)> Invocations { get; } = [];
        public bool FailAcknowledgment { get; set; }
        public (long Generation, long Sequence)? FailAcknowledgmentAt { get; set; }
        public bool FailSubmitAsWriteUncertain { get; set; }
        public bool FailSubmitAsRemoteUncertain { get; set; }
        /// <summary>Returns a JSON string where a stored-request object is required, so deserialization throws.</summary>
        public bool MalformedSubmitResult { get; set; }
        public string SubmitState { get; set; } = "forwarded";
        public long? SubmitOwnershipEpoch { get; set; }
        public string ReconcileRequestState { get; set; } = "unknown";
        public long? ReconcileOwnershipEpoch { get; set; }
        public string? ReconcileTurnId { get; set; }
        public bool RejectReplay { get; set; }
        public bool FailStatusAfterRejectedReplay { get; set; }
        public bool FailStatusAfterReplay { get; set; }
        public bool RejectStatus { get; set; }
        /// <summary>Returns a JSON string where a status object is required, so deserialization throws.</summary>
        public bool MalformedStatusResult { get; set; }
        public bool RejectRecovery { get; set; }
        public bool RejectLoadSession { get; set; }
        public bool FailLoadSessionUncertain { get; set; }
        public bool FailLoadSessionAsCallerCanceled { get; set; }
        public bool Disposed { get; private set; }
        /// <summary>Runs before the cancellation check so a test can cancel mid-connect.</summary>
        public Action<string>? OnInvoke { get; set; }
        public string? NewSessionId { get; set; }
        public string? LoadedSessionId => _loadedSessionId;
        private bool _replayRejected;
        private bool _replayInvoked;

        /// <summary>Models a worker that stops reporting its gaps once it accepts the reconciliation.</summary>
        public bool ClearGapsAfterReconcile { get; set; }
        public bool ClearLossAfterReconcile { get; set; }
        private bool _gapReconciled;
        private bool _lossReconciled;
        private string? _loadedSessionId;
        private BridgeWorkerStatus? _observedStatus;

        public object Mutation(string operation, IReadOnlyDictionary<string, object?> fields) => new Dictionary<string, object?>(fields) { ["operation"] = operation };

        /// <summary>
        /// Mirrors the worker's stored-request answer to a submitted prompt so a
        /// test can assert that dispatch reached the worker and correlated exactly,
        /// rather than only that the local recovery gate rejected it.
        /// </summary>
        private object SubmitResult(JsonElement root)
        {
            var status = _observedStatus ?? _statuses.Peek();
            return new
            {
                requestId = root.GetProperty("requestId").GetString(),
                payloadHash = "sha256:" + new string('0', 64),
                state = SubmitState,
                outcomeJson = (string?)null,
                processGeneration = status.ProcessGeneration,
                ownershipEpoch = SubmitOwnershipEpoch ?? status.OwnershipEpoch,
                turnId = root.GetProperty("turnId").GetString(),
                sessionId = status.SessionId,
            };
        }

        public Task<WorkerSessionResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken)
        {
            OnInvoke?.Invoke(operation);
            cancellationToken.ThrowIfCancellationRequested();
            // A prewrite caller cancellation leaves no invocation effect, exactly as
            // the real bridge reports it.
            if (operation == "load-session" && FailLoadSessionAsCallerCanceled) throw new WorkerCallerCanceledException("injected caller cancellation");
            var payload = JsonSerializer.Serialize(request, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions);
            Invocations.Add((operation, payload));
            using var requestDocument = JsonDocument.Parse(payload);
            var root = requestDocument.RootElement;

            if (operation == "reconcile" && ReconcileRequestState == "unknown") throw new WorkerRemoteException("worker-request-rejected");
            if (operation == "replay") _replayInvoked = true;
            if (operation == "replay" && RejectReplay)
            {
                _replayRejected = true;
                throw new WorkerRemoteException("worker-request-rejected");
            }
            if (operation == "status" && ((FailStatusAfterRejectedReplay && _replayRejected) || (FailStatusAfterReplay && _replayInvoked)))
            {
                FailStatusAfterRejectedReplay = false;
                FailStatusAfterReplay = false;
                throw new WorkerReadUncertainException("injected unavailable status");
            }
            if (operation == "status" && RejectStatus) throw new WorkerRemoteException("worker-request-rejected");
            if (operation == "load-session")
            {
                if (FailLoadSessionUncertain) throw new WorkerWriteUncertainException("injected uncertain session load");
                if (RejectLoadSession) throw new WorkerRemoteException("worker-request-rejected");
                _loadedSessionId = root.GetProperty("sessionId").GetString();
            }
            if (operation == "new-session") _loadedSessionId = NewSessionId ?? "native-created";
            if (operation == "submit" && FailSubmitAsWriteUncertain) throw new WorkerWriteUncertainException("injected uncertain submit");
            if (operation == "submit" && FailSubmitAsRemoteUncertain) throw new WorkerRemoteException("worker-operation-uncertain");
            if (operation.StartsWith("reconcile-", StringComparison.Ordinal))
            {
                if (RejectRecovery) throw new WorkerRemoteException("worker-operation-failed");
                if (operation == "reconcile-replay-gap") _gapReconciled = true;
                if (operation == "reconcile-replay-loss") _lossReconciled = true;
            }
            if (operation == "ack-events")
            {
                var generation = root.GetProperty("workerGeneration").GetInt64();
                var sequence = root.GetProperty("sequence").GetInt64();
                if (FailAcknowledgment || FailAcknowledgmentAt == (generation, sequence)) throw new WorkerWriteUncertainException("injected uncertain acknowledgment");
                Acknowledgments.Add((generation, sequence));
            }

            object value = operation switch
            {
                "status" => MalformedStatusResult ? (object)"not-a-status" : Status(),
                "replay" => ReplayResult(root),
                "new-session" => _loadedSessionId!,
                "submit" => MalformedSubmitResult ? (object)"not-a-request" : SubmitResult(root),
                "reconcile" => ReconcileResult(root),
                _ => new { ok = true },
            };
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, HVO.AgentControl.Worker.WorkerProtocol.JsonOptions));
            return Task.FromResult(new WorkerSessionResult(operation, document.RootElement.Clone(), mutation));
        }

        private object ReconcileResult(JsonElement root)
        {
            var status = _observedStatus ?? _statuses.Peek();
            return new
            {
                requestId = root.GetProperty("requestId").GetString(),
                payloadHash = "sha256:" + new string('0', 64),
                state = ReconcileRequestState,
                outcomeJson = ReconcileRequestState is "completed" or "failed" ? "{}" : null,
                processGeneration = status.ProcessGeneration,
                ownershipEpoch = ReconcileOwnershipEpoch ?? status.OwnershipEpoch,
                turnId = ReconcileTurnId ?? FindRequestTurn(root.GetProperty("requestId").GetString()!),
                sessionId = status.SessionId,
            };
        }

        private string FindRequestTurn(string requestId)
        {
            var submit = Invocations.LastOrDefault(x => x.Operation == "submit" && x.Payload.Contains(requestId, StringComparison.Ordinal));
            if (submit == default) return "turn-test";
            using var document = JsonDocument.Parse(submit.Payload);
            return document.RootElement.GetProperty("turnId").GetString()!;
        }

        private BridgeReplayPage ReplayResult(JsonElement root)
        {
            var key = (root.GetProperty("workerGeneration").GetInt64(), root.GetProperty("afterSequence").GetInt64());
            if (ReplayPageFactory is not null) return ReplayPageFactory(key.Item1, key.Item2);
            if (ReplayPages.TryGetValue(key, out var page)) return page;
            return ReplayBatches.TryGetValue(key, out var batch)
                ? new BridgeReplayPage(batch, false, batch.LastOrDefault()?.Sequence ?? key.Item2)
                : new BridgeReplayPage([], false, key.Item2);
        }

        private BridgeWorkerStatus Status()
        {
            var status = _statuses.Count > 1 ? _statuses.Dequeue() : _statuses.Peek();
            var replayGaps = ClearGapsAfterReconcile && _gapReconciled ? Array.Empty<BridgeReplayGap>() : status.ReplayGaps;
            var replayLoss = ClearLossAfterReconcile && _lossReconciled ? null : status.ReplayLoss;
            var holdReasons = status.HoldReasons
                .Where(reason => !(ClearGapsAfterReconcile && _gapReconciled && reason.Contains("replay-gap", StringComparison.Ordinal)))
                .Where(reason => !(ClearLossAfterReconcile && _lossReconciled && reason.Contains("replay-loss", StringComparison.Ordinal)))
                .ToArray();
            var observed = status with
            {
                DispatchHeld = holdReasons.Length > 0,
                HoldReason = holdReasons.FirstOrDefault(),
                HoldReasons = holdReasons,
                ReplayLoss = replayLoss,
                ReplayGaps = replayGaps,
                ReplayGapCount = replayGaps.Count,
                SessionId = status.SessionId ?? _loadedSessionId,
            };
            _observedStatus = observed;
            return observed;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)] private static extern int link(string oldpath, string newpath);
    private static SqliteConnection Open(string path) { var c = new SqliteConnection($"Data Source={path}"); c.Open(); c.Execute("PRAGMA foreign_keys=ON"); return c; }
    private static object? Scalar(SqliteConnection c, string sql) { using var command = c.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static object? RawScalar(string path, string sql) { using var c = Open(path); return Scalar(c, sql); }
    private static string Raw(string path, string sql) => Convert.ToString(RawScalar(path, sql), System.Globalization.CultureInfo.InvariantCulture)!;
    private sealed class TempDirectory : IDisposable { public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-remote-" + Guid.NewGuid().ToString("N")); public TempDirectory() => Directory.CreateDirectory(Path); public void Dispose() => Directory.Delete(Path, true); }
}
