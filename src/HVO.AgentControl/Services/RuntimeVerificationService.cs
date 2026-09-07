using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Microsoft.AspNetCore.DataProtection;
using Renci.SshNet;
using Renci.SshNet.Common;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace HVO.AgentControl.Services;

public sealed class RuntimeVerificationService(Secrets secrets, ControlStore store, IDataProtectionProvider protection)
{
    private readonly IDataProtector tickets = protection.CreateProtector("HVO.AgentControl.RuntimeVerification.v1");
    private readonly SemaphoreSlim connections = new(4);
    private static readonly string[] Algorithms = ["ssh-ed25519", "ecdsa-sha2-nistp256", "rsa-sha2-512"];
    private sealed record Ticket(string Kind, string Binding, long Expires, string Fingerprint = "", string Algorithm = "");

    public async Task<RuntimeVerification> Verify(RuntimeVerifyInput input, CancellationToken cancellationToken)
    {
        var profile = Json.Read<RuntimeRecord>(Json.Write(input.Profile));
        ValidateEndpoint(profile);
        _ = StartupOptions.Arguments(profile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await connections.WaitAsync(timeout.Token);
        try { return await VerifyCore(input, profile, timeout.Token); }
        finally { connections.Release(); }
    }

    public Task<CommandRecord> SaveAndConnect(VerifiedRuntimeInput input)
    {
        _ = Open(input.VerificationToken, "verified", Hash(Json.Write(input.Profile)));
        return store.SaveRuntimeAndConnect(input.Profile, input.Id);
    }

    public Task<CommandRecord> SaveForSetup(VerifiedRuntimeInput input)
    {
        _ = Open(input.VerificationToken, "ssh-profile", Hash(Json.Write(input.Profile)));
        return store.SaveRuntimeForSetup(input.Profile, input.Id);
    }

    private async Task<RuntimeVerification> VerifyCore(RuntimeVerifyInput input, RuntimeRecord profile, CancellationToken token)
    {
        var existing = await store.Read(async db => await db.Runtimes.FindAsync(profile.Id));
        if (existing is not null)
        {
            if (existing.DesiredConnected || existing.Transport == "Connected") throw new ControlException("Disconnect the runtime before verifying profile changes.");
            if (existing.Revision != profile.Revision) throw new ControlException("Runtime changed; reopen its profile before verifying.");
        }
        if (!string.IsNullOrEmpty(input.TrustToken))
        {
            var trusted = Open(input.TrustToken, "trust", Endpoint(profile));
            profile.HostKeySha256 = trusted.Fingerprint; profile.HostKeyAlgorithm = trusted.Algorithm;
        }
        if (existing is not null && existing.Host == profile.Host && existing.Port == profile.Port &&
            (existing.HostKeySha256 != profile.HostKeySha256 || existing.HostKeyAlgorithm != profile.HostKeyAlgorithm))
            throw new ControlException("The saved host key cannot be replaced through Verify. Inspect a changed key independently before deliberately updating its trusted fingerprint.");
        if (string.IsNullOrEmpty(profile.HostKeySha256))
        {
            // Stop during key exchange. No password, private key, or SSH authentication request is sent.
            var info = new ConnectionInfo(profile.Host, profile.Port, profile.Username, new NoneAuthenticationMethod(profile.Username)) { Timeout = TimeSpan.FromSeconds(10) };
            foreach (var algorithm in info.HostKeyAlgorithms.Keys.Except(Algorithms).ToArray()) info.HostKeyAlgorithms.Remove(algorithm);
            using var probe = new SshClient(info);
            string? fingerprint = null, selected = null;
            probe.HostKeyReceived += (_, args) =>
            {
                fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=');
                selected = args.HostKeyName; args.CanTrust = false;
            };
            try { await probe.ConnectAsync(token); }
            catch (SshConnectionException) when (fingerprint is not null) { }
            if (fingerprint is null || selected is null) throw new ControlException("SSH did not present a supported host key.");
            var trust = Seal(new("trust", Endpoint(profile), ControlStore.Now + 600000, fingerprint, selected));
            profile.HostKeySha256 = fingerprint; profile.HostKeyAlgorithm = selected;
            return new("TrustRequired", profile, "This host is new. Confirm that you trust the displayed key before credentials are sent. Compare it through another channel when available.", [], TrustToken: trust);
        }
        if (!Algorithms.Contains(profile.HostKeyAlgorithm) || profile.HostKeySha256.Length != 50 || !profile.HostKeySha256.StartsWith("SHA256:", StringComparison.Ordinal))
            throw new ControlException("Invalid trusted SSH key. Clear the fingerprint to discover a new host, or supply a verified fingerprint and algorithm.", 400);
        var credential = profile.Authentication == "password" ? input.Password : input.PrivateKey;
        if (string.IsNullOrEmpty(credential) && !string.IsNullOrEmpty(profile.CredentialReference)) credential = secrets.Read(profile.CredentialReference);
        if (string.IsNullOrEmpty(credential)) return new("CredentialsRequired", profile,
            profile.Authentication == "password" ? "Enter the SSH password, then Verify again." : "Paste the SSH private key or choose a mounted credential reference, then Verify again.", []);
        if (credential.Length > 64000 || credential.Contains('\0')) throw new ControlException("SSH credential is too large or contains NUL.", 400);
        var phrase = input.Passphrase;
        if (string.IsNullOrEmpty(phrase) && !string.IsNullOrEmpty(profile.PassphraseReference)) phrase = secrets.Read(profile.PassphraseReference);
        using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(credential));
        PrivateKeyFile? key = null;
        try
        {
            try { if (profile.Authentication == "privateKey") key = new PrivateKeyFile(keyStream, phrase); }
            catch (Exception ex) when (ex is SshException or InvalidOperationException or CryptographicException)
            { throw new ControlException("Private key could not be opened. Check its format and enter its passphrase if encrypted.", 400); }
            AuthenticationMethod auth = key is null ? new PasswordAuthenticationMethod(profile.Username, credential) : new PrivateKeyAuthenticationMethod(profile.Username, key);
            var info = new ConnectionInfo(profile.Host, profile.Port, profile.Username, auth) { Timeout = TimeSpan.FromSeconds(10) };
            var algorithm = info.HostKeyAlgorithms[profile.HostKeyAlgorithm]; info.HostKeyAlgorithms.Clear(); info.HostKeyAlgorithms.Add(profile.HostKeyAlgorithm, algorithm);
            using var ssh = new SshClient(info);
            SshRuntimeTransportFactory.AttachHostKey(ssh, profile.HostKeySha256);
            try { await ssh.ConnectAsync(token); }
            catch (SshAuthenticationException) { return new("CredentialsRequired", profile, "SSH authentication failed. Check the username and credential, or change the authentication method.", []); }
            catch (SshConnectionException) { throw new ControlException("SSH connection failed or the host key changed. No new trust was accepted; verify the saved fingerprint."); }
            var checks = new List<VerificationCheck> { new("SSH authentication", true, "Authenticated with the pinned host key.") };
            async Task<string> Run(string script) => await SshRuntimeTransportFactory.Run(ssh, script, token);
            using (var sftp = new SftpClient(info))
            {
                SshRuntimeTransportFactory.AttachHostKey(sftp, profile.HostKeySha256);
                try { await sftp.ConnectAsync(token); checks.Add(new("SFTP", true, "Protected bootstrap files can be transferred.")); }
                catch (SshException) { checks.Add(new("SFTP", false, "Enable the SSH SFTP subsystem for bootstrap transfers.")); }
            }
            var platform = (await Run("uname -sm")).Trim(); profile.Platform = platform;
            checks.Add(new("Platform", platform is "Linux x86_64" or "Linux aarch64" or "Linux arm64" or "Darwin arm64" or "Darwin x86_64", platform));
            foreach (var utility in new[] { "tmux", "lsof", "curl", "git" })
            {
                var location = (await Run("command -v " + utility + " || true")).Trim();
                checks.Add(new(utility, location.Length > 0, location.Length > 0 ? "Available at " + location + "." :
                    utility + " was not found in the agent command PATH. Install it or add its location to the SSH environment, then Verify again. No sudo is attempted."));
            }
            var remoteHome = await Run("cd \"$HOME\" && pwd -P"); remoteHome = remoteHome.TrimEnd('\n');
            ControlStore.ValidatePath(remoteHome);
            if (string.IsNullOrWhiteSpace(profile.AllowedRoots))
            {
                profile.AllowedRoots = remoteHome.TrimEnd('/') + "/workspaces";
                await Run("umask 077 && mkdir -p " + BootstrapScript.Quote(profile.AllowedRoots));
            }
            if (string.IsNullOrWhiteSpace(profile.StateDirectory)) profile.StateDirectory = remoteHome.TrimEnd('/') + "/.local/state/hvo-agentcontrol/" + profile.Id;
            ControlStore.ValidatePath(profile.StateDirectory);
            var roots = ControlStore.Roots(profile);
            if (roots.Length is < 1 or > 16) throw new ControlException("Choose 1–16 workspace roots.", 400);
            foreach (var root in roots)
            {
                ControlStore.ValidatePath(root);
                if (root == "/" || ControlStore.IsWithin(profile.StateDirectory, root)) throw new ControlException("Bootstrap state must be outside workspace roots, and / cannot be a workspace root.", 400);
                var usable = await Run("if test -d " + BootstrapScript.Quote(root) + " && test -r " + BootstrapScript.Quote(root) + " && test -w " + BootstrapScript.Quote(root) + "; then printf yes; else printf no; fi");
                checks.Add(new("Workspace: " + root, usable == "yes", usable == "yes" ? "Readable and writable." : "Create this directory or choose an existing writable root."));
            }
            if (checks.All(x => x.Passed))
            {
                try
                {
                    await SshRuntimeTransportFactory.PrepareStateDirectory(ssh, profile, token);
                    checks.Add(new("Bootstrap directory", true, "Canonical, protected and outside workspace roots."));
                }
                catch (ControlException ex) { checks.Add(new("Bootstrap directory", false, ex.Message)); }
                var expectedOwner = existing?.ManagedServerId ?? profile.ManagedServerId;
                var owner = BootstrapScript.Quote(expectedOwner + ":" + profile.ApiPort);
                var stateOwner = BootstrapScript.Quote(profile.StateDirectory + "/owner");
                var socket = BootstrapScript.Quote("hvo-" + expectedOwner);
                var portState = await Run($$"""
                    listeners=$(lsof -nP -t -iTCP:{{profile.ApiPort}} -sTCP:LISTEN || true)
                    if [ -z "$listeners" ]; then printf free
                    elif [ -f {{stateOwner}} ] && [ "$(cat {{stateOwner}})" = {{owner}} ] &&
                      [ "$(tmux -L {{socket}} show-option -v -t managed @hvo-owner 2>/dev/null)" = {{BootstrapScript.Quote(expectedOwner)}} ] &&
                      [ "$(tmux -L {{socket}} display-message -p -t managed '#{pane_pid}' 2>/dev/null)" = "$listeners" ]; then printf owned
                    else printf conflict; fi
                    """);
                checks.Add(new("OpenCode port", portState is "free" or "owned", portState == "free" ? "Port is available." : portState == "owned" ? "The existing owned server will be reused." : "Port is occupied by another process. Choose another API port."));
                if (portState == "owned")
                {
                    var previousOptions = await Run("cat " + BootstrapScript.Quote(profile.StateDirectory + "/startup-options") + " 2>/dev/null || printf " + BootstrapScript.Quote(StartupOptions.Fingerprint(new RuntimeRecord())));
                    checks.Add(new("Running startup options", previousOptions == StartupOptions.Fingerprint(profile),
                        previousOptions == StartupOptions.Fingerprint(profile) ? "Existing process uses the selected options." : "Stop the owned server before applying changed startup options."));
                }
            }
            if (!string.IsNullOrEmpty(profile.Executable)) ControlStore.ValidatePath(profile.Executable);
            profile.InstalledExecutable = "";
            var binary = await Run($$"""
                binary={{BootstrapScript.Quote(profile.Executable)}}
                if [ -z "$binary" ]; then
                  for candidate in {{BootstrapScript.Quote(profile.StateDirectory + "/bin/opencode")}} "$HOME/.opencode/bin/opencode" "$HOME/.local/bin/opencode" /opt/homebrew/bin/opencode /usr/local/bin/opencode; do
                    if [ -x "$candidate" ]; then binary="$candidate"; break; fi
                  done
                  if [ -z "$binary" ]; then binary=$(command -v opencode || true); fi
                fi
                if [ -n "$binary" ] && [ -x "$binary" ]; then printf '%s' "$binary"; fi
                """);
            if (binary.Length > 0)
            {
                profile.InstalledExecutable = binary;
                var version = (await Run(BootstrapScript.Quote(binary) + " --version")).Trim();
                checks.Add(new("OpenCode", version == BootstrapScript.Version, "Found " + version + " at " + binary + "; required " + BootstrapScript.Version + "."));
                if (version == BootstrapScript.Version && StartupOptions.Arguments(profile).Length > 0)
                {
                    var help = await Run(BootstrapScript.Quote(binary) + " serve --help");
                    var supported = (!profile.PureMode || help.Contains("--pure", StringComparison.Ordinal)) &&
                        (!profile.PrintLogs || help.Contains("--print-logs", StringComparison.Ordinal)) &&
                        (profile.LogLevel.Length == 0 || help.Contains("--log-level", StringComparison.Ordinal));
                    checks.Add(new("Startup options", supported, supported ? "Selected options are advertised by this server." : "The executable does not advertise the selected serve options."));
                }
            }
            else
            {
                checks.Add(new("OpenCode", profile.InstallIfMissing && profile.Executable.Length == 0,
                    profile.InstallIfMissing && profile.Executable.Length == 0 ? "Not installed yet. Version " + BootstrapScript.Version + " is planned for connection at " + profile.StateDirectory + "/bin/opencode. Save for setup does not install it." : "Executable missing; enable installation or correct its absolute path.", Planned: profile.InstallIfMissing && profile.Executable.Length == 0));
                var tools = platform.StartsWith("Darwin", StringComparison.Ordinal) ? new[] { "unzip", "shasum" } : new[] { "tar", "sha256sum" };
                foreach (var utility in tools)
                {
                    var available = await Run("if command -v " + utility + " >/dev/null 2>&1; then printf yes; else printf no; fi");
                    checks.Add(new(utility, available == "yes", available == "yes" ? "Installer prerequisite available." : "Install " + utility + " before connecting."));
                }
            }

            foreach (var utility in new[] { "gh", "az", "aws", "gcloud", "terraform", "kubectl" })
            {
                var location = (await Run("command -v " + utility + " || true")).Trim();
                checks.Add(new(utility, location.Length > 0, location.Length > 0 ? "Available at " + location + "." :
                    "Optional tool not found. Install through the admin terminal if a task needs it.", Required: false));
            }
            // No plaintext credential enters the runtime, durable command payload, ticket or audit.
            if (!string.IsNullOrEmpty(profile.Authentication == "password" ? input.Password : input.PrivateKey)) profile.CredentialReference = secrets.StoreEncrypted(credential);
            if (!string.IsNullOrEmpty(input.Passphrase)) profile.PassphraseReference = secrets.StoreEncrypted(input.Passphrase);
            if (string.IsNullOrEmpty(profile.ServerPasswordReference)) profile.ServerPasswordReference = secrets.StoreEncrypted(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            else if (secrets.Read(profile.ServerPasswordReference).Length < 24) throw new ControlException("OpenCode server password must contain at least 24 characters.", 400);
            var failed = checks.Where(x => x.Required && !x.Passed).Select(x => x.Name).ToArray();
            profile.Health = failed.Length > 0 ? "SetupRequired" : "Unknown";
            profile.Diagnostic = failed.Length > 0
                ? "SSH verified. Setup required: " + string.Join(", ", failed) + ". Open the admin terminal to resolve these checks, then Verify again."
                : "SSH and prerequisites verified. Saved without starting OpenCode.";
            var saveToken = Seal(new("ssh-profile", Hash(Json.Write(profile)), ControlStore.Now + 600000));
            if (failed.Length > 0) return new("RequirementsFailed", profile,
                "SSH authentication succeeded. Save for setup to use the admin terminal, then resolve the failed checks and Verify again.", checks, SaveToken: saveToken);
            var verification = Seal(new("verified", Hash(Json.Write(profile)), ControlStore.Now + 600000));
            return new("Verified", profile, "SSH and prerequisites verified. Save and connect to check OpenCode health and continue to worker setup.", checks, VerificationToken: verification, SaveToken: saveToken);
        }
        finally { key?.Dispose(); }
    }

    private static void ValidateEndpoint(RuntimeRecord value)
    {
        if (!Guid.TryParseExact(value.Id, "N", out _) || string.IsNullOrWhiteSpace(value.Host) || value.Host.Length > 253 || value.Host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            string.IsNullOrWhiteSpace(value.Username) || value.Username.Length > 100 || value.Username.Any(char.IsControl) || value.Port is < 1 or > 65535 ||
            value.ApiPort is < 1024 or > 65535 || value.Capacity is < 1 or > 16 || value.Authentication is not ("password" or "privateKey"))
            throw new ControlException("Enter a valid SSH host, port, username and authentication method.", 400);
        if (string.IsNullOrWhiteSpace(value.Name)) value.Name = value.Host;
    }
    private static string Endpoint(RuntimeRecord profile) => Hash(Json.Write(new { profile.Id, profile.Host, profile.Port, profile.Username }));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string Seal(Ticket ticket) => tickets.Protect(Json.Write(ticket));
    private Ticket Open(string value, string kind, string binding)
    {
        try
        {
            var ticket = Json.Read<Ticket>(tickets.Unprotect(value));
            if (ticket.Kind != kind || ticket.Binding != binding || ticket.Expires < ControlStore.Now) throw new CryptographicException();
            return ticket;
        }
        catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException)
        { throw new ControlException("Verification expired or the profile changed. Verify this profile again.", 400); }
    }
}
