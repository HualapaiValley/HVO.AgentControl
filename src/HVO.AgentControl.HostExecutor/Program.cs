using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Provisioning;

if (!OperatingSystem.IsLinux()) return Fail("The host executor requires Linux.");
if (args.Length != 2 || args[0] is not ("authorize" or "run"))
    return Fail("Usage: HVO.AgentControl.HostExecutor <authorize|run> <protected-manifest.json>");

try
{
    var manifestPath = ProtectedFile(args[1], "authority manifest");
    var manifest = JsonSerializer.Deserialize<HostExecutorManifest>(await File.ReadAllTextAsync(manifestPath), ExecutorJson.Options)
        ?? throw new InvalidOperationException("Authority manifest is empty.");
    manifest.Validate();
    var credential = (await File.ReadAllTextAsync(ProtectedFile(manifest.CredentialFile, "credential file"))).Trim();
    if (!credential.StartsWith(manifest.EnrollmentId + ".", StringComparison.Ordinal) || credential.Length > 200)
        throw new InvalidOperationException("Credential identity does not match the manifest enrollment.");
    var authorityDigest = AuthorityDigest(manifest.Authority);
    if (!string.Equals(authorityDigest, manifest.AuthorityDigest, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Protected authority manifest digest changed.");

    using var http = new HttpClient { BaseAddress = new Uri(manifest.ControllerUri), Timeout = TimeSpan.FromSeconds(45) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
    var api = new ExecutorApi(http);
    await api.Post<ActivateHostExecutorInput, HostExecutorEnrollment>("api/v1/host-executor/activate",
        new(manifest.EnrollmentId, manifest.Authority.HostId, manifest.AuthorityGeneration,
            authorityDigest, manifest.BootId, manifest.IncarnationId));
    var request = new HostProvisionRequest(manifest.OperationId, manifest.WorkspaceId, manifest.ColdBuild);
    var process = new BoundedProvisionProcess();
    var intentBuilder = new LocalDevContainerRunner(manifest.Authority, null, process);
    var intent = intentBuilder.CreateIntent(request);
    await api.Post<ApproveHostProvisionAuthorityInput, ProvisionOperationView>(
        $"api/v1/host-executor/provisioning/{manifest.OperationId}/authority", new(intent));
    if (args[0] == "authorize") return 0;
    if (string.IsNullOrWhiteSpace(manifest.ReservationId))
        throw new InvalidOperationException("Run mode requires a physical resource reservation ID.");

    var assignment = await api.Post<ClaimHostProvisioningInput, HostProvisioningAssignment>(
        $"api/v1/host-executor/provisioning/{manifest.OperationId}/claim", new(manifest.ReservationId));
    if (!ProvisionIntentIdentity.Same(intent, assignment.Intent))
        throw new InvalidOperationException("Controller assignment differs from the protected host intent.");
    var observation = await LinuxObservation.Collect(manifest, assignment, intent, process);
    var observed = await api.Post<SubmitHostResourceObservationInput, HostResourceObservation>(
        "api/v1/host-executor/observations", observation);
    var ledger = new ControllerProvisionAttemptLedger(api, assignment, observed.Id);
    var runner = new LocalDevContainerRunner(manifest.Authority, ledger, process);
    var result = await runner.Provision(request);
    foreach (var progress in result.Progress)
    {
        try
        {
            await api.Post<HostProvisionProgressInput, ProvisionOperationView>(
                $"api/v1/host-executor/provisioning/{manifest.OperationId}/progress",
                new(ReportId(manifest.OperationId, assignment.ClaimGeneration, "progress", progress.Sequence),
                    assignment.ClaimGeneration, progress.Sequence, progress.Stage));
        }
        catch (HttpRequestException) { /* Result delivery still carries the terminal boundary. */ }
    }
    var resultSequence = Math.Max(1, result.Progress.Select(x => x.Sequence).DefaultIfEmpty().Max() + 1);
    var reportedState = ledger.PermitUncertain ? "Unknown" : result.State;
    var reportedCode = ledger.PermitUncertain ? "effect_permit_response_lost_reconciliation_required" : result.Code;
    await api.Post<HostProvisionResultInput, ProvisionOperationView>(
        $"api/v1/host-executor/provisioning/{manifest.OperationId}/result",
        new(ReportId(manifest.OperationId, assignment.ClaimGeneration, "result", resultSequence),
            assignment.ClaimGeneration, resultSequence, result.OperationId, intent.Digest, reportedState,
            reportedCode, result.Observed, result.Executed, result.RetainedResources));
    return result.State == "VerifiedEnvironment" ? 0 : 2;
}
catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or JsonException)
{
    return Fail(error.Message);
}

static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

static string ProtectedFile(string path, string name)
{
    if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException($"The {name} path must be absolute.");
    var file = new FileInfo(Path.GetFullPath(path));
    if (!file.Exists || file.LinkTarget is not null) throw new InvalidOperationException($"The {name} is missing or linked.");
    var mode = File.GetUnixFileMode(file.FullName);
    if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherRead)) != 0)
        throw new InvalidOperationException($"The {name} permissions are too broad.");
    return file.FullName;
}

static string AuthorityDigest(ProvisionerHostAuthority authority) => Convert.ToHexString(SHA256.HashData(
    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(authority, ExecutorJson.Options))));

static string ReportId(string operationId, long claim, string kind, long sequence)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId}:{claim}:{kind}:{sequence}"));
    return new Guid(bytes.AsSpan(0, 16)).ToString("N");
}

internal sealed record HostExecutorManifest(int SchemaVersion, string ControllerUri, string CredentialFile,
    string EnrollmentId, int AuthorityGeneration, string AuthorityDigest, string EndpointId,
    string PhysicalHostId, string EngineId, string BuilderId, string BootId, string IncarnationId,
    long ObservationSequence, string WorkspaceId, string OperationId, string? ReservationId, bool ColdBuild,
    string StatPath, string DfPath, ProvisionerHostAuthority Authority)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || !Uri.TryCreate(ControllerUri, UriKind.Absolute, out var controller) || controller.Scheme != Uri.UriSchemeHttps ||
            !Guid.TryParse(EnrollmentId, out var enrollment) || enrollment == Guid.Empty || AuthorityGeneration < 1 ||
            !Guid.TryParse(OperationId, out var operation) || operation == Guid.Empty || !Guid.TryParse(WorkspaceId, out var workspace) || workspace == Guid.Empty ||
            ObservationSequence < 1 || Authority.Transport != "LocalLinux" || Authority.HostId.Length == 0 || Authority.Revision != AuthorityGeneration ||
            Authority.Workspaces.Count != 1 || !Authority.Workspaces.ContainsKey(WorkspaceId) ||
            new[] { AuthorityDigest, EndpointId, PhysicalHostId, EngineId, BuilderId, BootId, IncarnationId }.Any(x =>
                x.Length != 64 || x.Any(c => !Uri.IsHexDigit(c))) ||
            new[] { StatPath, DfPath }.Any(x => !Path.IsPathFullyQualified(x)))
            throw new InvalidOperationException("Invalid protected host executor manifest.");
    }
}

internal sealed class ExecutorApi(HttpClient http)
{
    public async Task<TResponse> Post<TRequest, TResponse>(string path, TRequest request)
    {
        using var response = await http.PostAsJsonAsync(path, request, ExecutorJson.Options);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Controller rejected {path} with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<TResponse>(ExecutorJson.Options)
            ?? throw new HttpRequestException("Controller returned an empty response.");
    }
}

internal sealed class ControllerProvisionAttemptLedger(ExecutorApi api, HostProvisioningAssignment assignment,
    string observationId) : IProvisionAttemptLedger
{
    public bool PermitUncertain { get; private set; }

    public Task<IProvisionAttempt> Acquire(ProvisionIntent intent, ProvisionAction action, CancellationToken token)
    {
        if (action != ProvisionAction.CreateOrObserve || !ProvisionIntentIdentity.Same(intent, assignment.Intent))
            throw new ProvisionAdmissionException("executor_assignment_intent_mismatch");
        return Task.FromResult<IProvisionAttempt>(new Attempt(this, api, assignment, observationId));
    }

    private sealed class Attempt(ControllerProvisionAttemptLedger owner, ExecutorApi api,
        HostProvisioningAssignment assignment, string observationId) : IProvisionAttempt
    {
        public Task<bool> HasEffect(string effect, string resourceId, CancellationToken token) =>
            Task.FromResult(assignment.Operation.Effects.Any(x => x.Effect == effect && x.ResourceId == resourceId));

        public async Task<bool> TryBeginEffect(string effect, string resourceId, CancellationToken token)
        {
            if (effect != "up" || resourceId != assignment.Intent.Workspace.Id)
                throw new ProvisionAdmissionException("invalid_effect_identity");
            try
            {
                var decision = await api.Post<BeginHostProvisionEffectInput, HostProvisionEffectDecision>(
                    $"api/v1/host-executor/provisioning/{assignment.Operation.Id}/begin-effect",
                    new(assignment.ClaimGeneration, assignment.CapacityReservationId, assignment.CapacityRevision,
                        assignment.ReservationGrantGeneration, assignment.Intent.Digest, assignment.Intent.Workspace.Id, observationId));
                return decision.AuthorizedNow;
            }
            catch (HttpRequestException error) when (error.StatusCode is null)
            {
                owner.PermitUncertain = true;
                throw new IOException("Provision effect permit response was lost; reconciliation is required.", error);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

[SupportedOSPlatform("linux")]
internal static class LinuxObservation
{
    public static async Task<SubmitHostResourceObservationInput> Collect(HostExecutorManifest manifest,
        HostProvisioningAssignment assignment, ProvisionIntent intent, IProvisionProcessRunner process)
    {
        var from = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var workspace = new DriveInfo(Path.GetPathRoot(intent.Workspace.Directory)!);
        var workspaceDevice = await Command(process, manifest.StatPath, ["--format=%d", intent.Workspace.Directory], manifest.Authority.ToolStateDirectory);
        var workspaceInodes = long.Parse(await Command(process, manifest.DfPath, ["--output=iavail", intent.Workspace.Directory], manifest.Authority.ToolStateDirectory), CultureInfo.InvariantCulture);
        var dockerInfo = await Command(process, manifest.Authority.DockerPath, ["info", "--format", "{{json .}}"], manifest.Authority.ToolStateDirectory,
            new Dictionary<string, string> { ["DOCKER_HOST"] = "unix://" + manifest.Authority.DockerSocket });
        using var info = JsonDocument.Parse(dockerInfo);
        if (info.RootElement.GetProperty("ID").GetString() != manifest.Authority.DockerEngineId)
            throw new InvalidOperationException("Docker engine identity changed.");
        var dockerRoot = info.RootElement.GetProperty("DockerRootDir").GetString() ?? throw new InvalidOperationException("Docker root is unavailable.");
        var dockerDrive = new DriveInfo(Path.GetPathRoot(dockerRoot)!);
        var dockerDevice = await Command(process, manifest.StatPath, ["--format=%d", dockerRoot], manifest.Authority.ToolStateDirectory);
        var dockerInodes = long.Parse(await Command(process, manifest.DfPath, ["--output=iavail", dockerRoot], manifest.Authority.ToolStateDirectory), CultureInfo.InvariantCulture);
        var memory = EffectiveMemory();
        var cpu = EffectiveCpuMillis();
        var ownership = await Command(process, manifest.Authority.DockerPath,
            ["container", "ls", "--all", "--filter", "label=hvo.agentcontrol.operation=" + intent.OperationId, "--format", "{{.ID}}"],
            manifest.Authority.ToolStateDirectory, new Dictionary<string, string> { ["DOCKER_HOST"] = "unix://" + manifest.Authority.DockerSocket });
        var state = string.IsNullOrWhiteSpace(ownership) ? "Absent" : "Present";
        var to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new(Guid.NewGuid().ToString("N"), manifest.EnrollmentId, manifest.Authority.HostId, manifest.EndpointId,
            manifest.PhysicalHostId, manifest.EngineId, manifest.BuilderId, manifest.AuthorityGeneration, manifest.BootId,
            manifest.IncarnationId, NextSequence(manifest), 1, HostObservationState.Complete, from, to,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), cpu, cpu, memory.Limit, memory.Available,
            null, null, null, null, null, manifest.WorkspaceId, Digest(Path.GetFullPath(intent.Workspace.Directory).TrimEnd(Path.DirectorySeparatorChar)),
            Digest("device:" + workspaceDevice), workspace.AvailableFreeSpace, workspaceInodes, Digest("device:" + dockerDevice),
            dockerDrive.AvailableFreeSpace, dockerInodes, true, 1, state, intent.Digest);
    }

    private static async Task<string> Command(IProvisionProcessRunner process, string executable, ImmutableArray<string> arguments,
        string workingDirectory, Dictionary<string, string>? additional = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["LANG"] = "C.UTF-8",
            ["HOME"] = workingDirectory
        };
        if (additional is not null) foreach (var pair in additional) environment[pair.Key] = pair.Value;
        var result = await process.Run(new(executable, arguments, workingDirectory,
            environment.ToImmutableDictionary(StringComparer.Ordinal), TimeSpan.FromSeconds(30), 262144), null, default);
        if (!result.Started || result.ExitCode != 0 || result.Interrupted || result.Truncated)
            throw new IOException("Required Linux host observation command failed.");
        return string.Join('\n', result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).SkipWhile(x => x == "IUse%"));
    }

    private static long EffectiveCpuMillis()
    {
        var effective = checked((long)Environment.ProcessorCount * 1000);
        if (!File.Exists("/sys/fs/cgroup/cpu.max")) return effective;
        var parts = File.ReadAllText("/sys/fs/cgroup/cpu.max").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && long.TryParse(parts[0], out var quota) && long.TryParse(parts[1], out var period) && quota > 0 && period > 0)
            effective = Math.Min(effective, checked(quota * 1000 / period));
        return effective;
    }

    private static (long Limit, long Available) EffectiveMemory()
    {
        var info = File.ReadAllLines("/proc/meminfo").Select(x => x.Split(':', 2)).ToDictionary(x => x[0], x => x[1].Trim());
        var total = ParseKb(info["MemTotal"]);
        var available = ParseKb(info["MemAvailable"]);
        if (File.Exists("/sys/fs/cgroup/memory.max"))
        {
            var text = File.ReadAllText("/sys/fs/cgroup/memory.max").Trim();
            if (long.TryParse(text, out var limit) && limit > 0)
            {
                total = Math.Min(total, limit);
                if (File.Exists("/sys/fs/cgroup/memory.current") && long.TryParse(File.ReadAllText("/sys/fs/cgroup/memory.current").Trim(), out var current))
                    available = Math.Min(available, Math.Max(0, total - current));
            }
        }
        return (total, Math.Min(total, available));
    }

    private static long NextSequence(HostExecutorManifest manifest)
    {
        var path = Path.Combine(manifest.Authority.ToolStateDirectory, "host-observation-sequence");
        var prior = File.Exists(path) && long.TryParse(File.ReadAllText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var saved) ? saved : 0;
        var next = Math.Max(manifest.ObservationSequence, checked(prior + 1));
        var temporary = path + ".new";
        File.WriteAllText(temporary, next.ToString(CultureInfo.InvariantCulture));
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
        return next;
    }

    private static long ParseKb(string value) => checked(long.Parse(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture) * 1024);
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal static class ExecutorJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
}
