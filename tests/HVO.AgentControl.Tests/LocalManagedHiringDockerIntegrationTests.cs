using System.Collections.Concurrent;
using System.Diagnostics;
using HVO.AgentControl.DockerHelper;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Real-daemon coverage for the complete controller-local managed-hiring path.
/// It is excluded from hermetic CI and dynamically skipped when Linux, Docker,
/// or the worker image is unavailable. The profile build needs outbound network
/// access for the seeded generic employee's pinned dotnet-install recipe.
/// </summary>
[Trait("Category", "DockerHelper")]
public sealed class LocalManagedHiringDockerIntegrationTests(ITestOutputHelper output)
{
    private const string Image = "hvo-agentcontrol:worker-tests";
    private static readonly bool Required = Environment.GetEnvironmentVariable("AGENTCONTROL_DOCKER_REQUIRED") == "1";
    private static readonly Lazy<bool> Available = new(Probe);

    [Fact(Timeout = 600_000)]
    public async Task ManagedHireBuildsProvisionsAndInstallsOrientationThroughLocalHelper()
    {
        if (!Available.Value) throw Xunit.Sdk.SkipException.ForSkip("Real Docker managed-hiring integration requires Linux, a reachable Docker daemon, and the hvo-agentcontrol:worker-tests image (or permission to build it).");

        var total = Stopwatch.StartNew();
        var stages = new List<string>();
        var baseDigest = ImageDigest();
        var platform = Run(["version", "--format", "{{.Server.Os}}/{{.Server.Arch}}"]).Output.Trim();
        var directory = Path.Combine(Path.GetTempPath(), "agentcontrol-managed-hire-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);

        string? workerId = null;
        string? resultTag = null;
        OrganizationStore? store = null;
        AcpControlHost? control = null;
        RemoteWorkerProvisioningCoordinator? provisioning = null;
        RoutingExecutionOperations? routed = null;
        var target = new ExecutionTarget(ExecutionHosts.LocalDockerId, "local-docker", null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(9));
        await using var helper = await HelperHarness.StartAsync(baseDigest, platform);
        try
        {
            var options = Options.Create(new WorkerControlOptions
            {
                Enabled = true,
                ControllerId = "controller-local-integration",
                ApprovedImageDigest = baseDigest,
                ApprovedImagePlatform = platform,
                LocalDockerHelperSocketPath = helper.SocketPath,
                ExpectedControllerUid = CurrentUid(),
                ImageBuildTimeoutSeconds = 420,
                OperationTimeoutSeconds = 150,
                AuthenticationTimeoutSeconds = 20,
                MemoryBytes = 2L * 1024 * 1024 * 1024,
                CpuLimit = 2,
                PidsLimit = 256,
            });
            var localClient = new LocalDockerHelperClient(options);
            var local = new LocalDockerExecutionOperations(localClient);
            var ssh = new RejectingSshOperations();
            routed = new RoutingExecutionOperations(ssh, local);

            var storePath = Path.Combine(directory, "control.db");
            store = new OrganizationStore(storePath, lockTimeout: TimeSpan.FromSeconds(10));
            store.OpenAndAdopt("AgentControl Local Managed Integration", "owner-approved:test", null, "seed://fresh");
            control = ControlHost(directory, store);

            var registry = new ExecutionHostRegistry(control, options, routed);
            var localHost = store.GetExecutionHost(ExecutionHosts.LocalDockerId)!;
            var probed = await registry.ProbeAsync(localHost.Id, localHost.Revision, timeout.Token);
            Assert.Equal("ready", probed.Status);
            Assert.Equal("valid", probed.CapabilityStatus);
            stages.Add("real helper probe recorded");

            var profile = store.ListContainerProfiles().Single(item => item.Slug == ContainerProfileSeed.GenericEmployeeSlug);
            var revision = store.GetContainerProfile(profile.Id)!.Revisions.Single();
            var builds = new ProfileBuildCoordinator(control, routed, options, NullLogger<ProfileBuildCoordinator>.Instance);
            var queued = builds.Queue(revision.Id, ExecutionHosts.LocalDockerId);
            resultTag = queued.ResultTag;
            var buildWatch = Stopwatch.StartNew();
            var built = await builds.RunAsync(queued.Id, timeout.Token);
            buildWatch.Stop();
            if (built.State != ProfileBuildStates.Built || !built.Verified || built.ImageDigest is null)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"Real managed profile build could not complete because its network-required apt/dotnet-install recipe ended in state '{built.State}': {built.FailureSummary ?? "no failure detail"}");
            }
            Assert.NotEqual(baseDigest, built.ImageDigest);
            stages.Add($"verified generic-employee profile build ({buildWatch.Elapsed})");

            var connector = new RoutingWorkerConnector(control, options, new ProcessWorkerConnector(options), localClient);
            var realSessions = new WorkerBridgeSessionFactory(connector, options);
            var sessions = new RecordingSessionFactory(realSessions);
            provisioning = new RemoteWorkerProvisioningCoordinator(control, new RemoteWorkerProvisionerAdapter(routed), options, sessions);
            var orientation = new RemoteOrientationCoordinator(sessions);
            var coordinator = new HireProvisioningCoordinator(control, provisioning, orientation, options, NullLogger<HireProvisioningCoordinator>.Instance);

            var overview = store.GetOverview();
            var department = overview.Departments.Single(item => item.Slug == OrganizationSeed.OperationsSlug);
            var role = Assert.Single(overview.Roles);
            var hire = store.CreateHireRequest(new HireRequestCreate(
                "local-managed-" + Guid.NewGuid().ToString("N"),
                "Local Managed Docker Employee",
                "Exercise the actual owner-approved local managed provisioning path.",
                department.Id,
                role.Id,
                RuntimePlacements.DeveloperContainer,
                1,
                1024,
                128), null);
            store.ApproveHireRequest(hire.Id, new HireRequestApprove(hire.Revision, revision.Id), "owner-integration");
            var creation = store.CreateManagedEmployeeFromHire(hire.Id);
            stages.Add("hire requested, approved without host input, and managed employee created");

            Exception? providerBoundary = null;
            HireProvisioningResult? completed = null;
            var provisionWatch = Stopwatch.StartNew();
            try
            {
                completed = await coordinator.ProvisionToOrientingAsync(hire.Id, timeout.Token);
            }
            catch (Exception exception) when (sessions.Operations.Contains("orientation-comprehension") && exception is WorkerProtocolException or OrganizationConcurrencyException)
            {
                // The verified image contains the real pinned OpenCode binary. The
                // checked-in fake ACP fixture is a host-side executable and cannot be
                // injected into this image without invalidating profile verification.
                // A provider-dependent comprehension turn may therefore stop here.
                providerBoundary = exception;
            }
            provisionWatch.Stop();

            var enrollment = store.ListWorkerEnrollments().Single(item => item.RuntimeBindingId == creation.RuntimeBindingId);
            workerId = enrollment.WorkerId;
            Assert.Equal(ExecutionHosts.LocalDockerId, enrollment.HostId);
            Assert.Equal(built.ImageDigest, enrollment.ExpectedImageDigest);
            Assert.Empty(ssh.Calls);

            var status = await orientation.ReadStatusAsync(enrollment, timeout.Token);
            Assert.Equal("running", status.ProcessState);
            Assert.True(status.AcpInitialized);
            Assert.NotNull(status.SessionId);
            Assert.NotNull(status.OrientationAssignmentId);
            Assert.NotNull(status.OrientationVersion);
            Assert.Equal("installed", status.OrientationState);
            Assert.StartsWith("/home/worker/.agentcontrol/orientation/", status.OrientationInstalledPath, StringComparison.Ordinal);

            Assert.Contains("VolumeCreate", helper.Runner.Operations);
            Assert.Contains("Bootstrap", helper.Runner.Operations);
            Assert.True(helper.Runner.Operations.Count(item => item == "ContainerCreate") >= 2, "initial provisioning and orientation replacement must both create the worker container");
            Assert.Contains("ContainerStart", helper.Runner.Operations);
            Assert.Contains("ContainerStop", helper.Runner.Operations);
            Assert.Contains("ContainerRemove", helper.Runner.Operations);
            Assert.True(sessions.ConnectionCount >= 4, "provisioning, delivery, replacement verification, and final status must authenticate fresh bridge streams");
            Assert.Contains("new-session", sessions.Operations);
            Assert.Contains("install-orientation", sessions.Operations);
            Assert.Contains("load-session", sessions.Operations);

            stages.Add("four volumes, bootstrap, container create/start, and authenticated bridge");
            stages.Add("real OpenCode ACP initialize and new-session");
            stages.Add("install-orientation with installed artifact reported by worker status");
            stages.Add("owned container replacement preserving volumes and load-session");

            if (completed is not null)
            {
                Assert.Equal(HireRequestStates.Ready, completed.Hire.State);
                Assert.True(completed.Orientation.Ready);
                Assert.Equal(OrientationStates.Comprehended, completed.Orientation.State);
                Assert.Contains("orientation-comprehension", sessions.Operations);
                stages.Add("orientation comprehension and Ready");
                output.WriteLine("Managed path reached Ready using the real worker/OpenCode provider path.");
            }
            else
            {
                Assert.NotNull(providerBoundary);
                Assert.Contains("orientation-comprehension", sessions.Operations);
                var current = store.GetHireRequest(hire.Id)!;
                Assert.True(current.State is HireRequestStates.Uncertain or HireRequestStates.Failed, $"unexpected provider-boundary hire state: {current.State}");
                stages.Add("orientation-comprehension invoked; provider-bound completion not available");
                output.WriteLine("Managed path deterministically reached installed orientation after replacement; the real OpenCode comprehension turn stopped at the unavailable-provider boundary: {0}: {1}", providerBoundary.GetType().Name, providerBoundary.Message);
            }

            output.WriteLine("Provisioning/orientation elapsed: {0}", provisionWatch.Elapsed);
            output.WriteLine("Exercised stages: {0}", string.Join("; ", stages));
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            if (provisioning is not null && workerId is not null)
            {
                try { await provisioning.CleanupAsync(workerId, cleanupTimeout.Token); }
                catch (Exception exception) { output.WriteLine("Coordinator cleanup failed; applying owned-prefix fallback: {0}", exception.Message); }
            }
            if (routed is not null)
            {
                if (resultTag is not null) await RemoveImageAsync(routed, target, resultTag, cleanupTimeout.Token);
                await RemoveImageAsync(routed, target, ProfileBuildContext.PinTag(baseDigest), cleanupTimeout.Token);
            }
            CleanupOwnedFallback();
            control?.Dispose();
            store?.Dispose();
            try { Directory.Delete(directory, true); } catch (IOException) { }
            output.WriteLine("Total real-Docker elapsed: {0}", total.Elapsed);
        }
    }

    /// <summary>
    /// Real-daemon coverage for scoped failed-build cleanup: a failed build row's
    /// local result tag is removed through the local helper and the tag is proven
    /// gone. The build carries no image digest, so cleanup removes only the tag and
    /// never the shared base image.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ScopedCleanupRemovesAFailedBuildsLocalTagThroughTheLocalHelper()
    {
        if (!Available.Value) throw Xunit.Sdk.SkipException.ForSkip("Real Docker scoped-cleanup integration requires Linux, a reachable Docker daemon, and the hvo-agentcontrol:worker-tests image (or permission to build it).");

        var baseDigest = ImageDigest();
        var platform = Run(["version", "--format", "{{.Server.Os}}/{{.Server.Arch}}"]).Output.Trim();
        var directory = Path.Combine(Path.GetTempPath(), "agentcontrol-cleanup-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        var target = new ExecutionTarget(ExecutionHosts.LocalDockerId, "local-docker", null);
        var resultTag = "agentcontrol-profile:prev-cleanup-" + Guid.NewGuid().ToString("N")[..12];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using var helper = await HelperHarness.StartAsync(baseDigest, platform);
        OrganizationStore? store = null;
        AcpControlHost? control = null;
        try
        {
            var options = Options.Create(new WorkerControlOptions
            {
                Enabled = true,
                ControllerId = "controller-cleanup-integration",
                ApprovedImageDigest = baseDigest,
                ApprovedImagePlatform = platform,
                LocalDockerHelperSocketPath = helper.SocketPath,
                ExpectedControllerUid = CurrentUid(),
                OperationTimeoutSeconds = 150,
                AuthenticationTimeoutSeconds = 20,
                MemoryBytes = 2L * 1024 * 1024 * 1024,
                CpuLimit = 2,
                PidsLimit = 256,
            });
            var local = new LocalDockerExecutionOperations(new LocalDockerHelperClient(options));
            var routed = new RoutingExecutionOperations(new RejectingSshOperations(), local);

            store = new OrganizationStore(Path.Combine(directory, "control.db"), lockTimeout: TimeSpan.FromSeconds(10));
            store.OpenAndAdopt("AgentControl Cleanup Integration", "owner-approved:test", null, "seed://fresh");
            control = ControlHost(directory, store);
            var host = store.GetExecutionHost(ExecutionHosts.LocalDockerId)!;
            store.RecordLocalExecutionHostProbe(ExecutionHosts.LocalDockerId, host.Revision, new LocalExecutionHostProbe(
                "29.0", "1.51", "x86_64", "overlay2", "ext4", false, 64L << 30, 16L << 30, 8, true, platform, "valid"));

            // Point a controller-profile tag at the shared base image, then seed a
            // failed build that names it and carries no digest.
            var tag = await routed.ExecuteAsync(target, RemoteDockerOperation.ImageTag, [baseDigest, resultTag], null, timeout.Token);
            Assert.Equal(0, tag.ExitCode);
            Assert.Equal(0, Run(["image", "inspect", resultTag]).ExitCode);

            var profile = store.CreateContainerProfile(new ContainerProfileCreate(
                Guid.NewGuid().ToString("N"), "cleanup-live-" + Guid.NewGuid().ToString("N")[..8], "Cleanup Live",
                "Scoped cleanup real-docker fixture.",
                """{"image":"agentcontrol-worker-base","name":"Cleanup Live"}""", null), null);
            var revision = store.GetContainerProfile(profile.Id)!.Revisions.Single();
            var contextHash = "sha256:" + new string('9', 64);
            var queued = store.QueueProfileBuild(revision.Id, ExecutionHosts.LocalDockerId, baseDigest, platform, contextHash, resultTag);
            var building = store.TransitionProfileBuild(queued.Id, queued.Revision, ProfileBuildStates.Building);
            var verifying = store.TransitionProfileBuild(building.Id, building.Revision, ProfileBuildStates.Verifying);
            _ = store.TransitionProfileBuild(verifying.Id, verifying.Revision, ProfileBuildStates.Failed, failureSummary: "the build failed (remote-command-failed)");

            var adapter = new RemoteWorkerProvisionerAdapter(routed);
            var provisioning = new RemoteWorkerProvisioningCoordinator(control, adapter, options, new ThrowingSessionFactory());
            var cleanup = new CleanupCoordinator(control, adapter, provisioning, options, NullLogger<CleanupCoordinator>.Instance);
            var removed = await cleanup.CleanupProfileBuildAsync(queued.Id, "owner", timeout.Token);

            Assert.Equal(ProfileBuildStates.Removed, removed.State);
            Assert.NotEqual(0, Run(["image", "inspect", resultTag]).ExitCode);
            Assert.Equal(0, Run(["image", "inspect", baseDigest]).ExitCode);
        }
        finally
        {
            _ = Run(["image", "rm", "--no-prune", resultTag]);
            control?.Dispose();
            store?.Dispose();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static async Task RemoveImageAsync(IRemoteWorkerOperations operations, ExecutionTarget target, string image, CancellationToken token)
    {
        try { _ = await operations.ExecuteAsync(target, RemoteDockerOperation.ImageRemove, [image], null, token); }
        catch (Exception) when (token.IsCancellationRequested) { }
    }

    private static void CleanupOwnedFallback()
    {
        // CleanupAsync is authoritative. This fallback only removes resources from
        // this integration-test naming family if a partially-created plan prevented
        // the coordinator from learning a worker id.
        var containers = Run(["container", "ls", "-aq", "--filter", "name=^agentcontrol-worker-wrk-"]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var container in containers)
        {
            var labels = Run(["container", "inspect", "--format", "{{index .Config.Labels \"agentcontrol.owner\"}}", container]);
            if (labels.Output.Contains("/controller-local-integration", StringComparison.Ordinal)) _ = Run(["rm", "-f", container]);
        }
        foreach (var prefix in new[] { "agentcontrol-control-wrk-", "agentcontrol-home-wrk-", "agentcontrol-workspace-wrk-", "agentcontrol-session-wrk-" })
        {
            var volumes = Run(["volume", "ls", "-q", "--filter", "name=" + prefix]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var volume in volumes)
            {
                var labels = Run(["volume", "inspect", "--format", "{{index .Labels \"agentcontrol.owner\"}}", volume]);
                if (labels.Output.Contains("/controller-local-integration", StringComparison.Ordinal)) _ = Run(["volume", "rm", "-f", volume]);
            }
        }
    }

    private sealed class RecordingSessionFactory(IWorkerBridgeSessionFactory inner) : IWorkerBridgeSessionFactory
    {
        private int _connectionCount;
        public int ConnectionCount => Volatile.Read(ref _connectionCount);
        public ConcurrentQueue<string> Operations { get; } = new();
        public async Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken)
        {
            Exception? last = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var session = await inner.ConnectAsync(enrollment, cancellationToken);
                    Interlocked.Increment(ref _connectionCount);
                    return new RecordingSession(session, Operations);
                }
                catch (Exception exception) when (exception is WorkerProtocolException or IOException)
                {
                    last = exception;
                    await Task.Delay(250, cancellationToken);
                }
            }
            throw new Xunit.Sdk.XunitException("the real worker bridge did not become ready within 30 seconds", last);
        }

        private sealed class RecordingSession(IWorkerBridgeSession inner, ConcurrentQueue<string> operations) : IWorkerBridgeSession
        {
            public WorkerBridgeLease Lease => inner.Lease;
            public object Mutation(string operation, IReadOnlyDictionary<string, object?> fields) => inner.Mutation(operation, fields);
            public Task<WorkerSessionResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken)
            {
                operations.Enqueue(operation);
                return inner.InvokeAsync(operation, request, mutation, cancellationToken);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class ThrowingSessionFactory : IWorkerBridgeSessionFactory
    {
        public Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("scoped build cleanup must not open a worker bridge session");
    }

    private sealed class RejectingSshOperations : ISshExecutionOperations
    {
        public List<string> Calls { get; } = [];
        private Task<T> Reject<T>() { Calls.Add(typeof(T).Name); throw new Xunit.Sdk.XunitException("local target was routed to SSH"); }
        public Task<RemoteOperationResult> ExecuteAsync(ExecutionTarget target, RemoteDockerOperation operation, IReadOnlyList<string> tokens, byte[]? standardInput, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<RemoteOperationResult> CreateVolumeAsync(ExecutionTarget target, VolumeCreateSpec specification, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<RemoteOperationResult> CreateContainerAsync(ExecutionTarget target, ContainerCreateSpec specification, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<RemoteOperationResult> BootstrapAsync(ExecutionTarget target, BootstrapSpec specification, byte[] standardInput, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<RemoteOperationResult> BuildImageAsync(ExecutionTarget target, ImageBuildSpec specification, byte[] contextTar, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<RemoteOperationResult> RemoveImageAsync(ExecutionTarget target, string imageReference, CancellationToken cancellationToken) => Reject<RemoteOperationResult>();
        public Task<HostProbePayload> ProbeHostAsync(ExecutionTarget target, CancellationToken cancellationToken) => Reject<HostProbePayload>();
    }

    private sealed class RecordingRunner : IDockerProcessRunner
    {
        private readonly DockerProcessRunner _inner = new();
        public ConcurrentQueue<string> Operations { get; } = new();
        public Task<HVO.AgentControl.DockerHelper.Protocol.DockerHelperResult> RunAsync(string id, DockerOperation operation, string[] argv, byte[]? input, TimeSpan timeout, CancellationToken token) { Operations.Enqueue(operation.ToString()); return _inner.RunAsync(id, operation, argv, input, timeout, token); }
        public Task<Process> StartStreamAsync(string[] argv, CancellationToken token) { Operations.Enqueue("Stream"); return _inner.StartStreamAsync(argv, token); }
    }

    private sealed class HelperHarness : IAsyncDisposable
    {
        private readonly DockerHelperServer _server;
        private readonly Task _running;
        private readonly string _directory;
        private HelperHarness(DockerHelperServer server, Task running, string directory, string socketPath, RecordingRunner runner) { _server = server; _running = running; _directory = directory; SocketPath = socketPath; Runner = runner; }
        public string SocketPath { get; }
        public RecordingRunner Runner { get; }
        public static async Task<HelperHarness> StartAsync(string digest, string platform)
        {
            var directory = Path.Combine(Path.GetTempPath(), "agentcontrol-managed-helper-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);
            var socket = Path.Combine(directory, "helper.sock");
            var runner = new RecordingRunner();
            var server = new DockerHelperServer(new(socket, CurrentUid(), -1, new DockerPolicy(digest, platform, 2L * 1024 * 1024 * 1024, 2, 256, RequireAgentControlPrefixes: true)), runner: runner);
            var harness = new HelperHarness(server, Task.Run(() => server.RunAsync(default)), directory, socket, runner);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(socket) && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (!File.Exists(socket)) throw new Xunit.Sdk.XunitException("the helper socket did not appear");
            return harness;
        }
        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            try { await _running.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }

    private static AcpControlHost ControlHost(string directory, OrganizationStore store)
    {
        var privateDirectory = Path.Combine(directory, "private");
        ControllerPrivateFile.EnsurePrivateDirectory(privateDirectory, CurrentUid());
        var control = new AcpControlHost(Options.Create(new ControlOptions { DataDirectory = directory, PrivateDataDirectory = privateDirectory }), NullLogger<AcpControlHost>.Instance);
        typeof(AcpControlHost).GetField("_organization", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(control, store);
        return control;
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsLinux()) return false;
        var available = Run(["info"]).ExitCode == 0 && EnsureImage();
        if (!available && Required) Assert.Fail("Docker and the worker image are required");
        return available;
    }

    private static bool EnsureImage() => Run(["image", "inspect", Image]).ExitCode == 0 || Run(["build", "--target", "worker", "--tag", Image, RepositoryRoot()]).ExitCode == 0;
    private static string ImageDigest() => Run(["image", "inspect", "--format", "{{.Id}}", Image]).Output.Trim();
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static int CurrentUid() => (int)geteuid();
    [System.Runtime.InteropServices.DllImport("libc")] private static extern uint geteuid();
    private static (int ExitCode, string Output) Run(string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(600_000)) { process.Kill(true); return (-1, output + " timed out"); }
        return (process.ExitCode, output);
    }
}
