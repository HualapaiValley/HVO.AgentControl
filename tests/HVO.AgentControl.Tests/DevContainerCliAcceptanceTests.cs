using System.Collections.Immutable;
using System.Text.Json;
using HVO.AgentControl.Provisioning;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class DevContainerCliAcceptanceTests
{
    [DevContainerAcceptanceFact]
    public async Task OfficialPinnedCliCreatesColdAndIndependentWarmContainersAndCleansExactOwners()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var repository = Environment.GetEnvironmentVariable("HVO_DEVCONTAINER_REPOSITORY") ?? throw new InvalidOperationException("Set HVO_DEVCONTAINER_REPOSITORY to the checked-out source.");
        var bundle = Path.Combine(repository, "tests/DevContainerCli/node_modules/@devcontainers/cli/dist/spec-node/devContainersSpecCLI.js");
        var root = Path.Combine(Path.GetTempPath(), "hvo-cli-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var state = Path.Combine(root, "host-tools"); Directory.CreateDirectory(state);
        var process = new BoundedProvisionProcess();
        var receipts = new List<HostProvisionResult>();
        var grants = ImmutableDictionary.CreateBuilder<string, ApprovedProvisionWorkspace>();
        var requests = new List<HostProvisionRequest>();
        ProvisionerHostAuthority? authority = null;
        LocalDevContainerRunner? runner = null;
        var removed = new List<string>();
        string[]? coldLayers = null, warmLayers = null;
        try
        {
            foreach (var name in new[] { "cold", "warm", "bad-hook" })
            {
                var directory = Path.Combine(root, name);
                Copy(Path.Combine(repository, "tests/DevContainerCli/fixture"), directory);
                if (name == "bad-hook")
                {
                    var config = Path.Combine(directory, ".devcontainer/devcontainer.json");
                    File.WriteAllText(config, File.ReadAllText(config).Replace("printf 'post-create-observed' > /home/vscode/.hvo-post-create", "exit 17", StringComparison.Ordinal));
                }
                // The only writable bind is this disposable checkout. No owner
                // source, credentials, Docker socket or fleet state is mounted.
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                await Host("/usr/bin/git", ["init", "--quiet"], directory);
                await Host("/usr/bin/git", ["remote", "add", "origin", "https://example.invalid/agentcontrol-disposable-fixture.git"], directory);
                await Host("/usr/bin/git", ["add", "."], directory);
                await Host("/usr/bin/git", ["-c", "user.name=AgentControl fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "Disposable pinned fixture"], directory);
                var revision = (await Host("/usr/bin/git", ["rev-parse", "HEAD"], directory)).StandardOutput.Trim();
                var configHash = LocalDevContainerRunner.Hash(await File.ReadAllBytesAsync(Path.Combine(directory, ".devcontainer/devcontainer.json")));
                grants.Add(name, new(name, directory, "https://example.invalid/agentcontrol-disposable-fixture.git", revision, ".devcontainer/devcontainer.json", configHash,
                    "vscode", "/workspaces/project", [
                        new("dotnet", ["dotnet", "--version"], "10.0.400"),
                        new("git", ["git", "--version"], "git version"),
                        new("bash", ["bash", "--version"], "GNU bash"),
                        new("feature", ["hvo-feature-proof"], "feature-installed"),
                        new("postCreate", ["cat", "/home/vscode/.hvo-post-create"], "post-create-observed"),
                        new("project", ["dotnet", "run", "--project", "Probe.csproj", "--configuration", "Release"], "disposable-project-built")
                    ]));
                requests.Add(new(Guid.NewGuid().ToString("D"), name, ColdBuild: name == "cold"));
            }
            var engine = JsonDocument.Parse((await Host("/usr/bin/docker", ["info", "--format", "{{json .}}"], root)).StandardOutput).RootElement.GetProperty("ID").GetString()!;
            authority = new("acceptance-" + Guid.NewGuid().ToString("N"), 1, "LocalLinux", root, state, Real("/usr/bin/node"), bundle,
                Real("/usr/bin/docker"), Real("/usr/bin/git"), "/var/run/docker.sock", engine, grants.ToImmutable());
            runner = new(authority, new FixtureFileLedger(Path.Combine(root, "attempts")), process);
            var cold = await runner.Provision(requests[0]); receipts.Add(cold);
            Assert.True(cold.State == "VerifiedEnvironment", JsonSerializer.Serialize(cold));
            Assert.Equal("vscode", cold.Executed!.RemoteUser);
            Assert.Equal("feature-installed", cold.Executed.Tools["feature"]);
            Assert.DoesNotContain(cold.Observed!.Mounts, x => x.Source.Contains("docker.sock", StringComparison.Ordinal));
            // Reconstruct runner and its admission store before retrying. An existing
            // owner is inspected; up and lifecycle hooks are not repeated.
            runner = new(authority, new FixtureFileLedger(Path.Combine(root, "attempts")), process);
            var replay = await runner.Provision(requests[0]); receipts.Add(replay);
            Assert.Equal("Observed", replay.State); Assert.False(replay.EffectStarted);
            Assert.Equal(cold.Observed.ContainerId, replay.Observed!.ContainerId);
            var conflict = await runner.Provision(requests[0] with { ColdBuild = false }); receipts.Add(conflict);
            Assert.Equal("operation_intent_conflict", conflict.Code);
            var warm = await runner.Provision(requests[1]); receipts.Add(warm);
            Assert.True(warm.State == "VerifiedEnvironment", JsonSerializer.Serialize(warm));
            Assert.NotEqual(cold.Observed.ContainerId, warm.Observed!.ContainerId);
            Assert.NotEqual(cold.Requested!.Workspace.Directory, warm.Requested!.Workspace.Directory);
            coldLayers = JsonSerializer.Deserialize<string[]>((await Host("/usr/bin/docker", ["image", "inspect", "--format", "{{json .RootFS.Layers}}", cold.Observed.ImageId], root)).StandardOutput)!;
            warmLayers = JsonSerializer.Deserialize<string[]>((await Host("/usr/bin/docker", ["image", "inspect", "--format", "{{json .RootFS.Layers}}", warm.Observed.ImageId], root)).StandardOutput)!;
            Assert.NotEmpty(coldLayers); Assert.Equal(coldLayers, warmLayers);
            var failed = await runner.Provision(requests[2]); receipts.Add(failed);
            Assert.Equal("Failed", failed.State); Assert.Equal("cli_failed_owned_resources_retained", failed.Code);
            Assert.NotNull(failed.Observed);
        }
        finally
        {
            var cleanupErrors = new List<string>();
            var mayDelete = runner is not null;
            if (runner is not null)
                foreach (var request in requests)
                {
                    try
                    {
                        var observed = await runner.Reconcile(request); receipts.Add(observed);
                        if (observed.Observed is null)
                        {
                            if (observed.Code != "zero_owners_observed_no_retry_authority") throw new InvalidOperationException("Ownership remains unresolved: " + observed.Code);
                            continue;
                        }
                        var cleanup = await runner.RemoveOwned(request, observed.Observed.ContainerId); receipts.Add(cleanup);
                        if (cleanup.State != "Removed") throw new InvalidOperationException("Removal remains unresolved: " + cleanup.Code);
                        if (!cleanup.RetainedResources.Contains("workspace:" + grants[request.WorkspaceId].Directory) || !Directory.Exists(grants[request.WorkspaceId].Directory))
                            throw new InvalidOperationException("Cleanup did not retain the owned checkout.");
                        removed.Add(observed.Observed.ContainerId);
                        var absent = await runner.Reconcile(request); receipts.Add(absent);
                        if (absent.Observed is not null || absent.Code != "zero_owners_observed_no_retry_authority") throw new InvalidOperationException("Owned absence was not observed.");
                        var noRetry = await runner.Provision(request); receipts.Add(noRetry);
                        if (noRetry.EffectStarted || noRetry.Code != "up_attempt_already_recorded_zero_owners_no_retry") throw new InvalidOperationException("Consumed up attempt was not preserved.");
                    }
                    catch (Exception error) { mayDelete = false; cleanupErrors.Add(request.OperationId + ": " + error.Message); }
                }
            // An interrupted CLI may still have Docker effects. A momentary zero
            // observation is not enough to delete a potentially mounted checkout.
            if (receipts.Any(x => x.EffectStarted && x.State == "Unknown")) mayDelete = false;
            var report = Environment.GetEnvironmentVariable("HVO_DEVCONTAINER_RECEIPT") ?? Path.Combine(Path.GetTempPath(), "hvo-cli-acceptance-receipt-" + Guid.NewGuid().ToString("N") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                cliVersion = LocalDevContainerRunner.CliVersion,
                cliSha256 = LocalDevContainerRunner.CliSha256,
                receipts,
                removed,
                cleanupErrors,
                coldLayers,
                warmLayers,
                fixtureRoot = root,
                fixtureRootRetained = !mayDelete,
                retained = "Docker images/build cache retained; checkout retention verified before any disposable fixture directory cleanup."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (mayDelete) Directory.Delete(root, true);
            Assert.True(cleanupErrors.Count == 0, string.Join("; ", cleanupErrors));
        }

        async Task<ProvisionProcessResult> Host(string executable, string[] arguments, string directory)
        {
            var env = new Dictionary<string, string> { ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", ["HOME"] = state, ["DOCKER_CONFIG"] = Path.Combine(state, "docker"), ["DOCKER_HOST"] = "unix:///var/run/docker.sock", ["GIT_CONFIG_GLOBAL"] = "/dev/null", ["GIT_CONFIG_NOSYSTEM"] = "1" }.ToImmutableDictionary();
            var result = await process.Run(new(executable, arguments.ToImmutableArray(), directory, env, TimeSpan.FromSeconds(30), 262144), null, CancellationToken.None);
            Assert.True(result.ExitCode == 0 && !result.Truncated && !result.Interrupted, result.StandardError);
            return result;
        }
    }

    private static string Real(string path) => new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) { var target = Path.Combine(destination, Path.GetFileName(file)); File.Copy(file, target); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file)); }
        foreach (var directory in Directory.GetDirectories(source)) Copy(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
    private sealed class DevContainerAcceptanceFactAttribute : FactAttribute
    {
        public DevContainerAcceptanceFactAttribute() { if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("HVO_DEVCONTAINER_CLI_ACCEPTANCE") != "1") Skip = "Requires explicitly enabled disposable host Docker/official CLI acceptance."; }
    }
}

// Acceptance caller, deliberately not a production DB/API implementation. Exclusive
// files and committed effect markers demonstrate the runner's admission contract.
internal sealed class FixtureFileLedger(string directory) : IProvisionAttemptLedger
{
    public Task<IProvisionAttempt> Acquire(ProvisionIntent intent, ProvisionAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory);
        var gate = new FileStream(Path.Combine(directory, "fixture-host-admission.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = Path.Combine(directory, intent.OperationId + ".json");
        try
        {
            var state = File.Exists(path) ? JsonSerializer.Deserialize<FixtureAttemptState>(File.ReadAllText(path))! : new(intent.Digest, []);
            if (state.Digest != intent.Digest) throw new ProvisionIntentConflictException();
            Save(path, state);
            return Task.FromResult<IProvisionAttempt>(new Attempt(gate, path, state, action));
        }
        catch { gate.Dispose(); throw; }
    }
    private static void Save(string path, FixtureAttemptState state)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write)) { JsonSerializer.Serialize(stream, state); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private sealed class Attempt(FileStream gate, string path, FixtureAttemptState state, ProvisionAction action) : IProvisionAttempt
    {
        public Task<bool> TryBeginEffect(string effect, string resourceId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (effect == "up" && action != ProvisionAction.CreateOrObserve || effect == "remove" && action != ProvisionAction.Remove) return Task.FromResult(false);
            var key = effect + ":" + resourceId;
            if (state.Effects.Contains(key)) return Task.FromResult(false);
            state.Effects.Add(key); Save(path, state); return Task.FromResult(true);
        }
        public ValueTask DisposeAsync() { gate.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed record FixtureAttemptState(string Digest, List<string> Effects);
}
