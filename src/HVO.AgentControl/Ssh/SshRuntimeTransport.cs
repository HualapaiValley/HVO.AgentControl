using HVO.AgentControl.Telemetry;
using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.OpenCode;
using Renci.SshNet;

namespace HVO.AgentControl.Ssh;

public sealed class SshRuntimeTransportFactory(Secrets secrets) : IRuntimeTransportFactory
{
    public async Task<IRuntimeTransport> Connect(RuntimeRecord runtime, CancellationToken cancellationToken)
    {
        var credential = secrets.Read(runtime.CredentialReference);
        var password = secrets.Read(runtime.ServerPasswordReference);
        if (password.Length < 24) throw new ControlException("OpenCode server password must contain at least 24 characters.");
        using var key = runtime.Authentication == "privateKey" ?
            new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(credential)),
                runtime.PassphraseReference is { Length: > 0 } phrase ? secrets.Read(phrase) : null) : null;
        AuthenticationMethod auth = key is not null ? new PrivateKeyAuthenticationMethod(runtime.Username, key)
            : new PasswordAuthenticationMethod(runtime.Username, credential);
        var info = new Renci.SshNet.ConnectionInfo(runtime.Host, runtime.Port, runtime.Username, auth) { Timeout = TimeSpan.FromSeconds(15) };
        var hostKey = info.HostKeyAlgorithms[runtime.HostKeyAlgorithm];
        info.HostKeyAlgorithms.Clear(); info.HostKeyAlgorithms.Add(runtime.HostKeyAlgorithm, hostKey);
        var ssh = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(10) };
        AttachHostKey(ssh, runtime.HostKeySha256);
        ForwardedPortLocal? forward = null;
        OpenCodeClient? client = null;
        try
        {
            await ssh.ConnectAsync(cancellationToken);
            await PrepareStateDirectory(ssh, runtime, cancellationToken);
            var ownership = runtime.StateDirectory + "/owner";
            await Run(ssh, $"if test -e {BootstrapScript.Quote(ownership)}; then test ! -L {BootstrapScript.Quote(ownership)} && test \"$(cat {BootstrapScript.Quote(ownership)})\" = {BootstrapScript.Quote(runtime.ManagedServerId + ":" + runtime.ApiPort)} || {{ echo OWNERSHIP_CONFLICT; exit 33; }}; fi", cancellationToken);
            using (var sftp = new SftpClient(info))
            {
                AttachHostKey(sftp, runtime.HostKeySha256);
                await sftp.ConnectAsync(cancellationToken);
                Upload(sftp, runtime.StateDirectory + "/ensure.sh", BootstrapScript.Create(runtime));
                Upload(sftp, runtime.StateDirectory + "/launch.sh", BootstrapScript.Launcher(runtime));
                Upload(sftp, runtime.StateDirectory + "/server.env", BootstrapScript.ServerEnvironment(runtime, password));
            }
            var result = await Run(ssh, "/bin/sh " + BootstrapScript.Quote(runtime.StateDirectory + "/ensure.sh"), cancellationToken, 240);
            forward = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)runtime.ApiPort);
            ssh.AddForwardedPort(forward); forward.Start();
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{forward.BoundPort}"), Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
            client = new OpenCodeClient(http);
            var health = await client.Get("/global/health", cancellationToken);
            if (!health.GetProperty("healthy").GetBoolean() || health.GetProperty("version").GetString() != BootstrapScript.Version)
                throw new ControlException("The owned OpenCode API is unhealthy or has an incompatible version.");
            var nativeProcess = NativeProcessProbe.Parse(runtime.ManagedServerId,
                await Run(ssh, NativeProcessProbe.ReadScript(runtime), cancellationToken, 5), ControlStore.Now);
            await Run(ssh, "printf '0' > " + BootstrapScript.Quote(runtime.StateDirectory + "/restarts"), cancellationToken);
            var installed = (await Run(ssh, "cat " + BootstrapScript.Quote(runtime.StateDirectory + "/executable"), cancellationToken)).Trim();
            ControlStore.ValidatePath(installed);
            return new SshRuntimeTransport(ssh, forward, client, runtime, result.Trim(), installed, nativeProcess);
        }
        catch { client?.Dispose(); forward?.Dispose(); ssh.Dispose(); throw; }
    }

    internal static void AttachHostKey(BaseClient client, string expected) => client.HostKeyReceived += (_, args) =>
    {
        var actual = "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=');
        args.CanTrust = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
    };

    internal static async Task PrepareStateDirectory(SshClient ssh, RuntimeRecord runtime, CancellationToken token)
    {
        // Reject symbolic-link ancestors before uploading protected configuration.
        await Run(ssh, $"test ! -L {BootstrapScript.Quote(runtime.StateDirectory)} && umask 077 && mkdir -p {BootstrapScript.Quote(runtime.StateDirectory)}", token);
        async Task<string> Canonical(string path)
        {
            var result = await Run(ssh, "cd " + BootstrapScript.Quote(path) + " && pwd -P", token);
            return result.EndsWith('\n') ? result[..^1] : result;
        }
        var canonicalState = await Canonical(runtime.StateDirectory);
        if (canonicalState != runtime.StateDirectory) throw new ControlException("Use a physically canonical bootstrap directory (pwd -P); symbolic-link ancestors are not allowed.");
        foreach (var root in ControlStore.Roots(runtime))
            if (ControlStore.IsWithin(canonicalState, await Canonical(root))) throw new ControlException("Bootstrap state must be outside canonical workspace roots.");
        await Run(ssh, "chmod 700 " + BootstrapScript.Quote(runtime.StateDirectory), token);
    }

    private static void Upload(SftpClient sftp, string path, string content)
    {
        if (sftp.Exists(path) && sftp.GetAttributes(path).IsSymbolicLink) throw new ControlException("Bootstrap file is a symbolic link; inspect ownership.");
        var temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(content));
        try
        {
            sftp.UploadFile(bytes, temporary);
            sftp.ChangePermissions(temporary, 600); // SSH.NET accepts octal digits as a decimal integer.
            sftp.RenameFile(temporary, path, isPosix: true);
        }
        finally { if (sftp.Exists(temporary)) sftp.DeleteFile(temporary); }
    }

    internal static async Task<string> Run(SshClient ssh, string text, CancellationToken token, int seconds = 30)
    {
        using var command = ssh.CreateCommand(RemoteEnvironment.Command(text)); command.CommandTimeout = TimeSpan.FromSeconds(seconds);
        await command.ExecuteAsync(token);
        if (command.ExitStatus != 0)
        {
            // Never forward arbitrary shell stderr (which may include environment or credentials).
            var code = command.Result.Split('\n').FirstOrDefault(x => x.Length < 100 && x.All(c => char.IsAsciiLetterUpper(c) || c is '_' or ':' || char.IsAsciiLetterLower(c)));
            throw new ControlException($"Remote operation failed (exit {command.ExitStatus}; {code ?? "inspect runtime prerequisites and owned server log"}).");
        }
        if (command.Result.Length > 16000) throw new ControlException("Remote probe exceeded its output limit.");
        return command.Result;
    }
}

internal sealed class SshRuntimeTransport(SshClient ssh, ForwardedPortLocal forward, OpenCodeClient api,
    RuntimeRecord runtime, string platform, string installedExecutable, NativeProcessObservation nativeProcess) : IRuntimeTransport
{
    public OpenCodeClient Api { get; } = api;
    public bool Connected => ssh.IsConnected && forward.IsStarted;
    public string Platform { get; } = platform;
    public string InstalledExecutable { get; } = installedExecutable;
    public NativeProcessObservation NativeProcess { get; } = nativeProcess;

    public async Task<RuntimeProcessIdentity?> ProbeProcessIdentity(CancellationToken cancellationToken)
    {
        var observed = NativeProcessProbe.Parse(runtime.ManagedServerId,
            await SshRuntimeTransportFactory.Run(ssh, NativeProcessProbe.LiveScript(runtime), cancellationToken, 5), ControlStore.Now);
        return observed.State == NativeProcessObservationState.Observed
            ? new(RuntimeConnections.Ssh, runtime.ManagedServerId, observed.Incarnation, observed.ProcessId, observed.ObservedAt)
            : null;
    }

    public async Task<RuntimeTelemetrySample?> SampleTelemetry(string identity, CancellationToken cancellationToken) =>
        TelemetryProbe.Parse(await SshRuntimeTransportFactory.Run(ssh, TelemetryProbe.Script, cancellationToken, 5), identity);

    public async Task<CapabilitySnapshot> ProbeCapabilities(string directory, CancellationToken cancellationToken)
    {
        ControlStore.ValidatePath(directory);
        try { return CapabilityProbe.Parse(await SshRuntimeTransportFactory.Run(ssh, CapabilityProbe.Script(directory), cancellationToken, 10), directory); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return CapabilityProbe.Parse("probe\tunavailable", directory); }
    }

    public async Task<WorkspaceIdentity> Workspace(RuntimeRecord profile, CreateWorkerInput input, CancellationToken cancellationToken)
    {
        async Task<string> Canonical(string path)
        {
            ControlStore.ValidatePath(path);
            var value = await SshRuntimeTransportFactory.Run(ssh, "cd " + BootstrapScript.Quote(path) + " && pwd -P", cancellationToken);
            return value.EndsWith('\n') ? value[..^1] : value;
        }
        var roots = new List<string>();
        foreach (var root in ControlStore.Roots(profile)) roots.Add(await Canonical(root));
        var directory = input.Directory;
        if (input.Repository is { Length: > 0 } repository)
        {
            var source = await Canonical(repository);
            var parent = await Canonical(directory[..directory.LastIndexOf('/')]);
            if (!roots.Any(x => ControlStore.IsWithin(source, x)) || !roots.Any(x => ControlStore.IsWithin(parent, x)))
                throw new ControlException("Worktree source and destination must be inside allowed roots.");
            var branch = input.Branch ?? "";
            var baseRef = input.BaseRef ?? "HEAD";
            if (branch.Length is < 1 or > 120 || branch.StartsWith('-') || baseRef.StartsWith('-') || baseRef.Length > 120 || branch.Any(char.IsControl) || baseRef.Any(char.IsControl))
                throw new ControlException("Specify a safe new branch name and base ref.", 400);
            await SshRuntimeTransportFactory.Run(ssh, $$"""
                cd {{BootstrapScript.Quote(source)}} &&
                test -z "$(git status --porcelain)" &&
                test ! -e {{BootstrapScript.Quote(directory)}} &&
                git check-ref-format --branch {{BootstrapScript.Quote(branch)}} >/dev/null &&
                git rev-parse --verify {{BootstrapScript.Quote(baseRef + "^{commit}")}} >/dev/null &&
                git worktree add -b {{BootstrapScript.Quote(branch)}} -- {{BootstrapScript.Quote(directory)}} {{BootstrapScript.Quote(baseRef)}} >/dev/null
                """, cancellationToken);
        }
        directory = await Canonical(directory);
        if (!roots.Any(x => ControlStore.IsWithin(directory, x))) throw new ControlException("Canonical workspace is outside allowed roots.");
        var branchName = await SshRuntimeTransportFactory.Run(ssh,
            "cd " + BootstrapScript.Quote(directory) + " && test -r . && test -w . && (git symbolic-ref --short HEAD 2>/dev/null || true)", cancellationToken);
        return new WorkspaceIdentity(directory, branchName.TrimEnd('\n'));
    }

    public async Task<PreparedCheckoutVerification> VerifyPreparedCheckout(RuntimeRecord profile, VerifyPreparedCheckoutInput input,
        CancellationToken cancellationToken)
    {
        static PreparedCheckoutVerification Rejected(VerifyPreparedCheckoutInput request, string code, string detail,
            string? canonical = null) => new(PreparedCheckoutStatus.Rejected, code, detail, request.Directory,
                canonical, null, null, null, null, ControlStore.Now);

        async Task<string?> Canonical(string path)
        {
            try
            {
                var value = await SshRuntimeTransportFactory.Run(ssh,
                    "cd " + BootstrapScript.Quote(path) + " 2>/dev/null && pwd -P", cancellationToken);
                value = value.TrimEnd('\r', '\n');
                return value.Length is > 0 and <= 2000 && !value.Any(char.IsControl) ? value : null;
            }
            catch (ControlException) { return null; }
        }

        var directory = await Canonical(input.Directory);
        if (directory is null) return Rejected(input, "directory_unavailable", "The prepared checkout directory is unavailable or could not be canonicalized.");
        if (directory != input.Directory) return Rejected(input, "noncanonical_directory", "Use the exact physical checkout directory returned by pwd -P.", directory);
        var roots = new List<string>();
        foreach (var root in ControlStore.Roots(profile))
            if (await Canonical(root) is { } canonicalRoot) roots.Add(canonicalRoot);
        if (!roots.Any(x => ControlStore.IsWithin(directory, x)))
            return Rejected(input, "outside_allowed_root", "The canonical checkout directory is outside the runtime allowed roots.", directory);

        var code = (await SshRuntimeTransportFactory.Run(ssh, $$"""
            cd {{BootstrapScript.Quote(directory)}} || exit 1
            # Do not let repository attributes or fsmonitor execute configuration-controlled programs.
            git_read() {
              GIT_OPTIONAL_LOCKS=0 GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null \
                git -c core.fsmonitor=false -c core.attributesfile=/dev/null "$@"
            }
            snapshot() {
              top=$(git_read rev-parse --show-toplevel 2>/dev/null) || return 1
              origin=$(git_read remote get-url origin 2>/dev/null) || return 1
              branch=$(git_read symbolic-ref --quiet --short HEAD 2>/dev/null) || return 1
              head=$(git_read rev-parse HEAD 2>/dev/null) || return 1
            }
            snapshot || { printf 'snapshot_unavailable'; exit 0; }
            test "$top" = {{BootstrapScript.Quote(directory)}} || { printf 'not_repository_root'; exit 0; }
            test "$origin" = {{BootstrapScript.Quote(input.Repository)}} || { printf 'origin_mismatch'; exit 0; }
            test "$branch" = {{BootstrapScript.Quote(input.Branch)}} || { printf 'branch_mismatch'; exit 0; }
            test "$head" = {{BootstrapScript.Quote(input.Head)}} || { printf 'head_mismatch'; exit 0; }
            pre_top=$top; pre_origin=$origin; pre_branch=$branch; pre_head=$head
            # Lowercase entries are assume-unchanged; S is skip-worktree. Either hides content from status.
            git_read ls-files -v 2>/dev/null | grep -Eq '^[a-zS] ' && { printf 'index_flags'; exit 0; }
            status=$(git_read status --porcelain=v1 --untracked-files=all --ignore-submodules=none 2>/dev/null) || { printf 'status_unavailable'; exit 0; }
            test -z "$status" || { printf 'dirty_worktree'; exit 0; }
            snapshot || { printf 'snapshot_unavailable'; exit 0; }
            test "$top" = "$pre_top" && test "$origin" = "$pre_origin" && test "$branch" = "$pre_branch" && test "$head" = "$pre_head" || { printf 'snapshot_changed'; exit 0; }
            test "$top" = {{BootstrapScript.Quote(directory)}} || { printf 'not_repository_root'; exit 0; }
            test "$origin" = {{BootstrapScript.Quote(input.Repository)}} || { printf 'origin_mismatch'; exit 0; }
            test "$branch" = {{BootstrapScript.Quote(input.Branch)}} || { printf 'branch_mismatch'; exit 0; }
            test "$head" = {{BootstrapScript.Quote(input.Head)}} || { printf 'head_mismatch'; exit 0; }
            printf 'verified'
            """, cancellationToken)).TrimEnd('\r', '\n');
        return code switch
        {
            "verified" => new(PreparedCheckoutStatus.Verified, "verified", "Prepared checkout matches the exact requested repository, branch, HEAD and clean state.",
                input.Directory, directory, input.Repository, input.Branch, input.Head, true, ControlStore.Now),
            "not_repository_root" => Rejected(input, code, "The prepared directory is not the exact Git worktree root.", directory),
            "origin_mismatch" => Rejected(input, code, "The checkout origin does not match the expected repository.", directory),
            "branch_mismatch" => Rejected(input, code, "The checkout branch does not match the expected branch.", directory),
            "head_mismatch" => Rejected(input, code, "The checkout HEAD does not match the expected commit.", directory),
            "index_flags" => Rejected(input, code, "The checkout index has assume-unchanged or skip-worktree entries that can hide changes.", directory),
            "snapshot_changed" => Rejected(input, code, "Checkout identity changed while clean state was being verified.", directory),
            "dirty_worktree" => Rejected(input, code, "The checkout has tracked, staged, or untracked changes.", directory),
            "status_unavailable" or "snapshot_unavailable" => Rejected(input, code, "The checkout identity or clean state could not be verified.", directory),
            _ => Rejected(input, "invalid_probe_result", "The checkout verification probe returned an invalid bounded result.", directory)
        };
    }

    public async Task StopOwnedServer(CancellationToken cancellationToken)
    {
        await SshRuntimeTransportFactory.Run(ssh, $$"""
            test "$(cat {{BootstrapScript.Quote(runtime.StateDirectory + "/owner")}})" = {{BootstrapScript.Quote(runtime.ManagedServerId + ":" + runtime.ApiPort)}} &&
            test "$(tmux -L {{BootstrapScript.Quote(runtime.TmuxName)}} show-option -v -t managed @hvo-owner)" = {{BootstrapScript.Quote(runtime.ManagedServerId)}} &&
            tmux -L {{BootstrapScript.Quote(runtime.TmuxName)}} kill-session -t managed
            """, cancellationToken);
    }

    public async Task StopOwnedServer(OwnedNativeProcess expected, CancellationToken cancellationToken)
    {
        if (expected.ManagedServerId != runtime.ManagedServerId || expected.ProcessId < 1 || !NativeProcessProbe.ValidMarker(expected.Incarnation))
            throw new ControlException("Stop requires a valid owned native-process identity.");
        await SshRuntimeTransportFactory.Run(ssh, $$"""
            test "$(cat {{BootstrapScript.Quote(runtime.StateDirectory + "/owner")}})" = {{BootstrapScript.Quote(runtime.ManagedServerId + ":" + runtime.ApiPort)}} &&
            test "$(tmux -L {{BootstrapScript.Quote(runtime.TmuxName)}} show-option -v -t managed @hvo-owner)" = {{BootstrapScript.Quote(runtime.ManagedServerId)}} &&
            pid=$(tmux -L {{BootstrapScript.Quote(runtime.TmuxName)}} display-message -p -t managed '#{pane_pid}') &&
            test "$pid" = {{BootstrapScript.Quote(expected.ProcessId.ToString())}} &&
            test -r "/proc/$pid/stat" && test -r /proc/sys/kernel/random/boot_id &&
            stat=$(cat "/proc/$pid/stat") && rest=${stat##*) } && index=1 && start='' &&
            for field in $rest; do [ "$index" = 20 ] && start=$field; index=$((index+1)); done &&
            boot=$(cat /proc/sys/kernel/random/boot_id) &&
            test "$boot:$start" = {{BootstrapScript.Quote(expected.Incarnation)}} &&
            tmux -L {{BootstrapScript.Quote(runtime.TmuxName)}} kill-session -t managed
            """, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Api.Dispose(); forward.Dispose(); ssh.Dispose();
        return ValueTask.CompletedTask;
    }
}
