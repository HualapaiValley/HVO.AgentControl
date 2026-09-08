using System.Collections.Immutable;
using System.Text.Json;
using HVO.AgentControl.Provisioning;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class LocalDevContainerRunnerTests
{
    [Fact]
    public async Task RequiresExplicitHostAndLedgerAuthority()
    {
        using var fixture = new Fixture();
        Assert.Equal("Unsupported", (await new LocalDevContainerRunner(null, null, fixture.Process).Provision(fixture.Request)).State);
        var unsupported = new LocalDevContainerRunner(fixture.Authority with { Transport = "SSH" }, fixture.Ledger, fixture.Process);
        Assert.Equal("unsupported_provisioner_transport", (await unsupported.Provision(fixture.Request)).Code);
        Assert.Equal(0, fixture.Process.Calls);
    }

    [Fact]
    public async Task FirstCreationVerifiesAndReconstructedCallerReconcilesWithoutRepeatingUp()
    {
        using var fixture = new Fixture();
        var first = await fixture.Runner().Provision(fixture.Request);
        Assert.Equal("VerifiedEnvironment", first.State);
        Assert.Equal("vscode", first.Executed!.RemoteUser);
        Assert.Equal("10.0.400", first.Executed.Tools["dotnet"]);
        Assert.NotNull(first.ResolvedConfiguration);
        var replay = await fixture.Runner().Provision(fixture.Request);
        Assert.Equal("Observed", replay.State); Assert.False(replay.EffectStarted);
        Assert.Equal(first.Observed!.ContainerId, replay.Observed!.ContainerId);
        Assert.Equal(1, fixture.Process.UpCalls);
        Assert.Equal("Failed", (await fixture.Runner().Provision(fixture.Request with { ColdBuild = true })).State);
        Assert.Equal(1, fixture.Process.UpCalls);
    }

    [Theory]
    [InlineData("cancel", "Unknown")]
    [InlineData("malformed", "Unknown")]
    [InlineData("hook-failed", "Failed")]
    [InlineData("truncated", "Unknown")]
    public async Task IncompleteCreationRetainsOwnershipAndDoesNotPromoteOrReplay(string mode, string state)
    {
        using var fixture = new Fixture(); fixture.Process.Mode = mode;
        var result = await fixture.Runner().Provision(fixture.Request);
        Assert.Equal(state, result.State); Assert.True(result.EffectStarted);
        var observed = await fixture.Runner().Reconcile(fixture.Request);
        Assert.Equal("Observed", observed.State); Assert.False(observed.EffectStarted);
        Assert.Equal(1, fixture.Process.UpCalls);
        fixture.Process.Owners = 0;
        var absent = await fixture.Runner().Provision(fixture.Request);
        Assert.Equal("up_attempt_already_recorded_zero_owners_no_retry", absent.Code);
        Assert.False(absent.EffectStarted); Assert.Equal(1, fixture.Process.UpCalls);
    }

    [Theory]
    [InlineData(0, "Unknown", "zero_owners_observed_no_retry_authority")]
    [InlineData(1, "Observed", "owned_container_observed_hooks_not_inferred")]
    [InlineData(2, "Unknown", "multiple_or_malformed_owners")]
    public async Task ReconciliationUsesAuthoritativeExactZeroOneOrMultipleDockerOwners(int count, string state, string code)
    {
        using var fixture = new Fixture(); fixture.Process.Owners = count;
        var result = await fixture.Runner().Reconcile(fixture.Request);
        Assert.Equal(state, result.State); Assert.Equal(code, result.Code);
        Assert.False(result.EffectStarted); Assert.Equal(0, fixture.Process.UpCalls);
    }

    [Theory]
    [InlineData("missing-inspect-fields")]
    [InlineData("malformed-inspect-types")]
    [InlineData("foreign-labels")]
    [InlineData("foreign-mount")]
    [InlineData("extra-capability")]
    [InlineData("extra-security-opt")]
    [InlineData("malformed-id")]
    [InlineData("engine-mismatch")]
    public async Task MalformedOrContradictoryOwnershipNeverExecutesOrEscapes(string mode)
    {
        using var fixture = new Fixture(); fixture.Process.Owners = 1; fixture.Process.Mode = mode;
        var result = await fixture.Runner().Reconcile(fixture.Request);
        Assert.Equal("Unknown", result.State);
        Assert.Equal(0, fixture.Process.UpCalls); Assert.Equal(0, fixture.Process.RemoveCalls); Assert.Equal(0, fixture.Process.ExecCalls);
    }

    [Fact]
    public async Task RejectsChangedSourceConfigurationAndCliBeforeEffects()
    {
        using var fixture = new Fixture();
        fixture.Process.Mode = "source-mismatch";
        Assert.Equal("source_identity_or_clean_checkout_changed", (await fixture.Runner().Provision(fixture.Request)).Code);
        fixture.Process.Mode = "";
        await File.AppendAllTextAsync(fixture.ConfigPath, " ");
        Assert.Equal("configuration_digest_changed", (await fixture.Runner().Provision(fixture.Request)).Code);
        Assert.Equal(0, fixture.Process.UpCalls);
        var wrongCli = new LocalDevContainerRunner(fixture.Authority, fixture.Ledger, fixture.Process) { ReadCliHash = (_, _) => Task.FromResult(new string('0', 64)) };
        Assert.Equal("cli_bundle_identity_mismatch", (await wrongCli.Provision(fixture.Request)).Code);
    }

    [Fact]
    public async Task RejectsSymlinkWorkspaceAndHostExecutable()
    {
        using var fixture = new Fixture();
        var link = Path.Combine(fixture.Root, "link"); Directory.CreateSymbolicLink(link, fixture.Workspace.Directory);
        var workspace = fixture.Workspace with { Directory = link };
        var authority = fixture.Authority with { Workspaces = ImmutableDictionary<string, ApprovedProvisionWorkspace>.Empty.Add(workspace.Id, workspace) };
        var result = await fixture.Runner(authority).Provision(fixture.Request);
        Assert.Equal("symlink_host_path_not_authorized", result.Code); Assert.Equal(0, fixture.Process.UpCalls);
    }

    [Fact]
    public async Task HostHooksAndArbitraryDockerFlagsNeedSeparateAuthority()
    {
        using var fixture = new Fixture();
        var original = File.ReadAllText(fixture.ConfigPath);
        File.WriteAllText(fixture.ConfigPath, original.Replace("\"image\":", "\"initializeCommand\":\"touch /host\",\"image\":", StringComparison.Ordinal));
        var workspace = fixture.Workspace with { ConfigurationSha256 = LocalDevContainerRunner.Hash(File.ReadAllBytes(fixture.ConfigPath)) };
        var authority = fixture.Authority with { Workspaces = ImmutableDictionary<string, ApprovedProvisionWorkspace>.Empty.Add(workspace.Id, workspace) };
        var result = await fixture.Runner(authority).Provision(fixture.Request);
        Assert.Equal("Unsupported", result.State); Assert.Equal("unsupported_configuration_capability_initializeCommand", result.Code);
        Assert.Equal(0, fixture.Process.UpCalls);
    }

    [Fact]
    public async Task EffectiveUserAndToolFailuresCannotBeCalledReady()
    {
        using var fixture = new Fixture(); fixture.Process.Mode = "wrong-user";
        var result = await fixture.Runner().Provision(fixture.Request);
        Assert.Equal("Failed", result.State); Assert.Equal("observed_execution_identity_mismatch", result.Code);
        Assert.NotNull(result.Observed);
    }

    [Theory]
    [InlineData("merged-privileged")]
    [InlineData("merged-capability")]
    [InlineData("merged-security-opt")]
    [InlineData("merged-mount")]
    [InlineData("merged-user")]
    [InlineData("merged-run-args")]
    [InlineData("merged-missing")]
    [InlineData("merged-invalid")]
    public async Task SafeRawConfigurationCannotAuthorizeUnsafeOrMissingEffectiveConfiguration(string mode)
    {
        using var fixture = new Fixture(); fixture.Process.Mode = mode;
        var result = await fixture.Runner().Provision(fixture.Request);
        Assert.Contains(result.State, new[] { "Unsupported", "Failed" });
        Assert.False(result.EffectStarted); Assert.Equal(0, fixture.Process.UpCalls);
        // Correcting read-only resolution evidence still permits the first effect;
        // the rejected resolved configuration did not consume admission.
        fixture.Process.Mode = "";
        Assert.Equal("VerifiedEnvironment", (await fixture.Runner().Provision(fixture.Request)).State);
    }

    [Fact]
    public async Task IgnoredFeatureAndChangedBuildInputsCannotEscapeCommittedSourceValidation()
    {
        using var fixture = new Fixture();
        var raw = File.ReadAllText(fixture.ConfigPath).Replace("\"image\":", "\"features\":{\"./ignored-feature\":{}},\"image\":", StringComparison.Ordinal);
        File.WriteAllText(fixture.ConfigPath, raw);
        File.WriteAllText(Path.Combine(fixture.Workspace.Directory, ".gitignore"), "ignored-feature/\n");
        fixture.Process.Tree = SnapshotTree(fixture.Workspace.Directory);
        var feature = Path.Combine(fixture.Workspace.Directory, "ignored-feature"); Directory.CreateDirectory(feature);
        File.WriteAllText(Path.Combine(feature, "devcontainer-feature.json"), "{\"id\":\"ignored\",\"privileged\":true}");
        var workspace = fixture.Workspace with { ConfigurationSha256 = LocalDevContainerRunner.Hash(File.ReadAllBytes(fixture.ConfigPath)) };
        var authority = fixture.Authority with { Workspaces = fixture.Authority.Workspaces.SetItem(workspace.Id, workspace) };
        var rejected = await fixture.Runner(authority).Provision(fixture.Request);
        Assert.Equal("uncommitted_source_input", rejected.Code); Assert.False(rejected.EffectStarted);
        fixture.Process.Tree = SnapshotTree(fixture.Workspace.Directory);
        File.WriteAllText(Path.Combine(feature, "devcontainer-feature.json"), "{\"id\":\"ignored\",\"privileged\":false}");
        Assert.Equal("committed_source_input_changed", (await fixture.Runner(authority).Provision(fixture.Request)).Code);
        Assert.Equal(0, fixture.Process.UpCalls);
    }

    [Theory]
    [InlineData("cli-missing")]
    [InlineData("cli-changed")]
    [InlineData("source-changed")]
    [InlineData("workspace-missing")]
    public async Task CommittedOwnerCanBeRecoveredAndRemovedAfterExecutionInputsDisappear(string drift)
    {
        using var fixture = new Fixture(); fixture.Process.Mode = "cancel";
        Assert.Equal("Unknown", (await fixture.Runner().Provision(fixture.Request)).State);
        fixture.Process.Mode = "";
        if (drift == "cli-missing") File.Delete(fixture.Authority.CliBundlePath);
        if (drift == "source-changed") await File.AppendAllTextAsync(fixture.ConfigPath, " ");
        if (drift == "workspace-missing") Directory.Delete(fixture.Workspace.Directory, true);
        var runner = drift == "cli-changed"
            ? new LocalDevContainerRunner(fixture.Authority, fixture.Ledger, fixture.Process) { ReadCliHash = (_, _) => Task.FromResult(new string('0', 64)) }
            : fixture.Runner();
        var probes = fixture.Process.ExecCalls;
        var observed = await runner.Reconcile(fixture.Request);
        Assert.Equal("Observed", observed.State); Assert.Equal("owned_container_observed_environment_unverified", observed.Code);
        Assert.Equal(FakeProcess.Container, observed.Observed!.ContainerId);
        Assert.True(fixture.Process.InspectCalls > 0); Assert.Equal(probes, fixture.Process.ExecCalls);
        var removed = await runner.RemoveOwned(fixture.Request, observed.Observed.ContainerId);
        Assert.Equal("Removed", removed.State);
        Assert.Equal("up_attempt_already_recorded_zero_owners_no_retry", (await runner.Provision(fixture.Request)).Code);
        Assert.Equal(1, fixture.Process.UpCalls);
    }

    [Fact]
    public async Task BlockingAdvisoryObserverDoesNotDelayProcessDeadline()
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new ProvisionProcessRequest("/bin/sh", ["-c", "printf started >&2; sleep 30"], fixture.Root,
            ImmutableDictionary<string, string>.Empty, TimeSpan.FromMilliseconds(300), 1024);
        try
        {
            var running = new BoundedProvisionProcess().Run(command, _ => { entered.TrySetResult(); release.Wait(); }, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(7));
            Assert.True(result.Started); Assert.True(result.Interrupted);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData("diagnostics-only", "Failed")]
    [InlineData("malformed-exec", "Unknown")]
    public async Task ExecEvidenceMustComeFromCompleteRawCliJsonEvents(string mode, string expected)
    {
        using var fixture = new Fixture(); fixture.Process.Mode = mode;
        var result = await fixture.Runner().Provision(fixture.Request);
        Assert.Equal(expected, result.State); Assert.NotNull(result.Observed);
    }

    [Fact]
    public async Task RemovalRequiresSingleExactOwnerSeparateEffectAdmissionAndRetainsData()
    {
        using var fixture = new Fixture(); fixture.Process.Owners = 2;
        Assert.Equal("Unknown", (await fixture.Runner().RemoveOwned(fixture.Request, FakeProcess.Container)).State);
        Assert.Equal(0, fixture.Process.RemoveCalls);
        fixture.Process.Owners = 1;
        Assert.Equal("removal_identity_mismatch", (await fixture.Runner().RemoveOwned(fixture.Request, new string('f', 64))).Code);
        Assert.Equal(0, fixture.Process.RemoveCalls);
        var removed = await fixture.Runner().RemoveOwned(fixture.Request, FakeProcess.Container);
        Assert.Equal("Removed", removed.State); Assert.Equal(1, fixture.Process.RemoveCalls);
        Assert.True(Directory.Exists(fixture.Workspace.Directory)); Assert.Contains("workspace:" + fixture.Workspace.Directory, removed.RetainedResources);
        Assert.Equal("AbsentObserved", (await fixture.Runner().RemoveOwned(fixture.Request, FakeProcess.Container)).State);
        Assert.Equal(1, fixture.Process.RemoveCalls);
    }

    [Fact]
    public async Task ProgressAndProcessOutputAreBoundedAndArgumentsStayLiteral()
    {
        using var fixture = new Fixture(); fixture.Process.Mode = "noisy";
        var result = await fixture.Runner().Provision(fixture.Request, progress: _ => throw new InvalidOperationException("advisory subscriber failed"));
        Assert.Equal("VerifiedEnvironment", result.State);
        Assert.True(result.Progress.Length <= 64); Assert.All(result.Progress, p => Assert.True(p.Text.Length <= 1024));
        var marker = Path.Combine(fixture.Root, "should-not-exist");
        var literal = "$(touch " + marker + ") `touch " + marker + "` ;echo unsafe";
        var process = new BoundedProvisionProcess();
        var command = new ProvisionProcessRequest("/usr/bin/printf", ["%s", literal], fixture.Root, ImmutableDictionary<string, string>.Empty, TimeSpan.FromSeconds(3), 1024);
        var output = await process.Run(command, null, CancellationToken.None);
        Assert.Equal(literal, output.StandardOutput); Assert.False(File.Exists(marker));
        var noisy = await process.Run(command with { Arguments = ["%05000d", "0"], OutputLimit = 100 }, null, CancellationToken.None);
        Assert.True(noisy.Truncated); Assert.Equal(100, noisy.StandardOutput.Length); Assert.Equal(0, noisy.ExitCode);
        var interrupted = await process.Run(command with { Executable = "/usr/bin/sleep", Arguments = ["10"], Timeout = TimeSpan.FromMilliseconds(100) }, null, CancellationToken.None);
        Assert.True(interrupted.Started); Assert.True(interrupted.Interrupted);
    }

    private static string SnapshotTree(string directory) => string.Concat(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path =>
    {
        var content = File.ReadAllBytes(path);
        var blob = System.Text.Encoding.UTF8.GetBytes("blob " + content.Length + "\0").Concat(content).ToArray();
        var executable = !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
        return (executable ? "100755" : "100644") + " blob " + Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(blob)) + "\t" + Path.GetRelativePath(directory, path) + "\0";
    }));

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hvo-cli-unit-" + Guid.NewGuid().ToString("N"));
        public ApprovedProvisionWorkspace Workspace { get; }
        public ProvisionerHostAuthority Authority { get; }
        public HostProvisionRequest Request { get; } = new(Guid.NewGuid().ToString("D"), "workspace");
        public FakeProcess Process { get; }
        public IProvisionAttemptLedger Ledger { get; }
        public string ConfigPath => Path.Combine(Workspace.Directory, "devcontainer.json");
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var workspace = Path.Combine(Root, "workspace"); Directory.CreateDirectory(workspace);
            var tools = Path.Combine(Root, "tools"); Directory.CreateDirectory(tools);
            foreach (var file in new[] { "node", "cli.js", "docker", "git", "socket" }) File.WriteAllText(Path.Combine(tools, file), "synthetic fixture");
            var config = Path.Combine(workspace, "devcontainer.json");
            File.WriteAllText(config, "{\"image\":\"fixture@sha256:" + new string('a', 64) + "\",\"remoteUser\":\"vscode\",\"workspaceFolder\":\"/workspaces/project\",\"workspaceMount\":\"source=${localWorkspaceFolder},target=/workspaces/project,type=bind\"}");
            Workspace = new("workspace", workspace, "https://example.invalid/fixture.git", new string('b', 40), "devcontainer.json", LocalDevContainerRunner.Hash(File.ReadAllBytes(config)), "vscode", "/workspaces/project", [new("dotnet", ["dotnet", "--version"], "10.0.400")]);
            Authority = new("fixture", 1, "LocalLinux", Root, tools, Path.Combine(tools, "node"), Path.Combine(tools, "cli.js"), Path.Combine(tools, "docker"), Path.Combine(tools, "git"), Path.Combine(tools, "socket"), "engine", ImmutableDictionary<string, ApprovedProvisionWorkspace>.Empty.Add(Workspace.Id, Workspace));
            Process = new FakeProcess { Tree = SnapshotTree(workspace) };
            Ledger = new ObservingLedger(new FixtureFileLedger(Path.Combine(Root, "ledger")), intent => Process.Intent = intent);
        }
        public LocalDevContainerRunner Runner(ProvisionerHostAuthority? authority = null) => new(authority ?? Authority, Ledger, Process) { ReadCliHash = (_, _) => Task.FromResult(LocalDevContainerRunner.CliSha256) };
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class ObservingLedger(IProvisionAttemptLedger inner, Action<ProvisionIntent> observe) : IProvisionAttemptLedger
    {
        public Task<IProvisionAttempt> Acquire(ProvisionIntent intent, ProvisionAction action, CancellationToken token) { observe(intent); return inner.Acquire(intent, action, token); }
    }
    private sealed class FakeProcess : IProvisionProcessRunner
    {
        public const string Container = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public int Calls, UpCalls, ExecCalls, RemoveCalls, Owners;
        public string Mode = "";
        public string Tree = "";
        public int OwnershipQueries, InspectCalls;
        public ProvisionIntent Intent = default!;
        public Task<ProvisionProcessResult> Run(ProvisionProcessRequest request, Action<string>? progress, CancellationToken token)
        {
            Calls++;
            var args = request.Arguments;
            if (request.Executable.EndsWith("/git", StringComparison.Ordinal))
            {
                if (args[0] == "ls-tree") return Success(Tree);
                return Success(args[0] == "status" ? "" : args[0] == "remote" ? Intent.Workspace.Repository : args[1] == "HEAD" ? Mode == "source-mismatch" ? new string('c', 40) : Intent.Workspace.SourceRevision : Intent.Workspace.Directory);
            }
            if (request.Executable.EndsWith("/docker", StringComparison.Ordinal))
            {
                if (args[0] == "info") return Json(new { ID = Mode == "engine-mismatch" ? "other" : "engine", OSType = "linux" });
                if (args[1] == "ls") { OwnershipQueries++; return Success(Mode == "malformed-id" ? "not-a-container-id" : string.Join('\n', Enumerable.Repeat(Container, Owners))); }
                if (args[1] == "rm") { Assert.Equal(Container, args[^1]); RemoveCalls++; Owners = 0; return Success(Container); }
                InspectCalls++;
                if (Mode == "missing-inspect-fields") return Success("[{}]");
                if (Mode == "malformed-inspect-types") return Success("[{\"Config\":{\"Labels\":1}}]");
                return Json(new[] { new { Id = Container, Image = "sha256:" + new string('b', 64), Config = new { Image = "fixture:resolved", User = "vscode", Labels = Mode == "foreign-labels" ? Intent.Labels.SetItem("hvo.agentcontrol.intent", "other") : Intent.Labels },
                    State = new { Running = true }, HostConfig = new { Privileged = false, CapAdd = Mode == "extra-capability" ? new[] { "SYS_PTRACE" } : [], SecurityOpt = Mode == "extra-security-opt" ? new[] { "seccomp=unconfined" } : [] }, Mounts = new[] { new { Type = "bind", Source = Mode == "foreign-mount" ? "/var/run/docker.sock" : Intent.Workspace.Directory, Destination = Intent.Workspace.ContainerWorkspace, RW = true } } } });
            }
            if (args[1] == "--version") return Success(LocalDevContainerRunner.CliVersion);
            if (args[1] == "read-configuration")
            {
                var merged = new Dictionary<string, object?> { ["remoteUser"] = "vscode", ["workspaceFolder"] = "/workspaces/project", ["workspaceMount"] = "source=" + Intent.Workspace.Directory + ",target=/workspaces/project,type=bind" };
                if (Mode == "merged-privileged") merged["privileged"] = true;
                if (Mode == "merged-capability") merged["capAdd"] = new[] { "SYS_PTRACE" };
                if (Mode == "merged-security-opt") merged["securityOpt"] = new[] { "seccomp=unconfined" };
                if (Mode == "merged-mount") merged["mounts"] = new[] { "source=/var/run/docker.sock,target=/var/run/docker.sock,type=bind" };
                if (Mode == "merged-user") merged["remoteUser"] = "root";
                if (Mode == "merged-run-args") merged["runArgs"] = new[] { "--network=host" };
                if (Mode == "merged-missing") return Json(new { configuration = new { remoteUser = "vscode" } });
                return Json(new { configuration = new { remoteUser = "vscode" }, mergedConfiguration = Mode == "merged-invalid" ? (object)"invalid" : merged });
            }
            if (args[1] == "up")
            {
                Assert.DoesNotContain("--remove-existing-container", args); UpCalls++; Owners = 1;
                if (Mode == "noisy") for (var i = 0; i < 150; i++) progress?.Invoke(new string('p', 4000));
                if (Mode == "cancel") return Task.FromResult(new ProvisionProcessResult(true, null, true, false, "", ""));
                if (Mode == "truncated") return Task.FromResult(new ProvisionProcessResult(true, 0, false, true, "{}", ""));
                if (Mode == "hook-failed") return Task.FromResult(new ProvisionProcessResult(true, 17, false, false, "{\"outcome\":\"error\"}", "hook exited 17"));
                if (Mode == "malformed") return Success("not-json");
                return Json(new { outcome = "success", containerId = Container, remoteUser = "vscode", remoteWorkspaceFolder = "/workspaces/project" });
            }
            Assert.Equal("exec", args[1]); ExecCalls++;
            if (Mode == "diagnostics-only") return Task.FromResult(new ProvisionProcessResult(true, 0, false, false, "", "{\"type\":\"text\",\"text\":\"vscode\"}\n"));
            if (Mode == "malformed-exec") return Task.FromResult(new ProvisionProcessResult(true, 0, false, false, "", "malformed json event"));
            if (args[^1] == "-un") return ExecSuccess(Mode == "wrong-user" ? "root" : "vscode");
            if (args[^1] == "-u") return ExecSuccess("1000");
            if (args[^1] == "pwd") return ExecSuccess("/workspaces/project");
            return ExecSuccess("10.0.400");
        }
        private static Task<ProvisionProcessResult> ExecSuccess(string value) => Task.FromResult(new ProvisionProcessResult(true, 0, false, false, "", JsonSerializer.Serialize(new { type = "raw", text = value }) + "\n"));
        private static Task<ProvisionProcessResult> Success(string value) => Task.FromResult(new ProvisionProcessResult(true, 0, false, false, value, ""));
        private static Task<ProvisionProcessResult> Json<T>(T value) => Success(JsonSerializer.Serialize(value));
    }
}
