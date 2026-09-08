using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Renci.SshNet;

namespace HVO.AgentControl.GitHub;

public sealed class GitHubCredentialDelivery(Secrets secrets)
{
    internal Task<GitHubProcessEnvironmentEvidence> Deliver(RuntimeRecord runtime, GitHubInstallationToken credential, CancellationToken token) =>
        WithSsh(runtime, token, async (ssh, connection) =>
        {
            var directory = BootstrapScript.ManagedGitHubConfigDirectory(runtime);
            var q = BootstrapScript.Quote(directory);
            var state = BootstrapScript.Quote(runtime.StateDirectory);
            // A host's existing personal GitHub login is never implicitly replaced.
            await SshRuntimeTransportFactory.Run(ssh, $$"""
                command -v gh >/dev/null || { echo GH_MISSING; exit 1; }
                test -z "${GH_TOKEN:-}${GITHUB_TOKEN:-}" || { echo GITHUB_ENVIRONMENT_OVERRIDE; exit 1; }
                test ! -L {{state}} && test "$(cd {{state}} && pwd -P)" = {{state}} &&
                umask 077 && mkdir -p {{q}} &&
                test ! -L {{q}} && test "$(cd {{q}} && pwd -P)" = {{q}} &&
                test ! -L {{q}}/hosts.yml && test ! -L {{q}}/.agentcontrol-owner &&
                ( { test ! -e {{q}}/hosts.yml && test ! -e {{q}}/.agentcontrol-owner; } ||
                  { test -e {{q}}/hosts.yml && test "$(cat {{q}}/.agentcontrol-owner 2>/dev/null)" = {{BootstrapScript.Quote(runtime.ManagedServerId)}}; } ) ||
                { echo EXISTING_GITHUB_CONFIGURATION; exit 1; }
                chmod 700 {{q}}
                """, token);
            using var sftp = new SftpClient(connection);
            SshRuntimeTransportFactory.AttachHostKey(sftp, runtime.HostKeySha256);
            await sftp.ConnectAsync(token);
            var hostsPath = directory + "/hosts.yml";
            var ownerPath = directory + "/.agentcontrol-owner";
            var hostsExist = sftp.Exists(hostsPath);
            var owner = sftp.Exists(ownerPath) ? Read(sftp, ownerPath) : null;
            if (!IsManagedConfigurationReplacementAllowed(hostsExist, owner, runtime.ManagedServerId))
                throw new ControlException("Existing GitHub configuration ownership is not verified; refusing replacement.");
            if (hostsExist)
            {
                var hosts = Read(sftp, hostsPath);
                if (!IsExclusivelyManagedHosts(hosts, credential.Actor))
                    throw new ControlException("Existing GitHub configuration contains accounts or hosts outside AgentControl; refusing replacement.");
            }
            Write(sftp, directory + "/.agentcontrol-owner", runtime.ManagedServerId);
            // JSON strings are valid YAML scalars. Credentials travel over SFTP, never shell arguments.
            var managedHosts = HostsYaml(credential);
            Write(sftp, directory + "/hosts.yml", managedHosts);
            return await Probe(ssh, runtime, GitHubProcessEnvironment.CredentialFingerprint(managedHosts), token);
        });

    internal Task<GitHubProcessEnvironmentEvidence> VerifyEnvironment(RuntimeRecord runtime,
        string credentialConfigurationFingerprint, CancellationToken token) =>
        WithSsh(runtime, token, (ssh, _) => Probe(ssh, runtime, credentialConfigurationFingerprint, token));

    private async Task<T> WithSsh<T>(RuntimeRecord runtime, CancellationToken token,
        Func<SshClient, Renci.SshNet.ConnectionInfo, Task<T>> action)
    {
        if (runtime.ConnectionKind != RuntimeConnections.Ssh) throw new ControlException("Control services do not use SSH credential delivery.");
        using var privateKey = runtime.Authentication == "privateKey" ? new PrivateKeyFile(
            new MemoryStream(Encoding.UTF8.GetBytes(secrets.Read(runtime.CredentialReference))),
            runtime.PassphraseReference is { Length: > 0 } phrase ? secrets.Read(phrase) : null) : null;
        AuthenticationMethod authentication = privateKey is null
            ? new PasswordAuthenticationMethod(runtime.Username, secrets.Read(runtime.CredentialReference))
            : new PrivateKeyAuthenticationMethod(runtime.Username, privateKey);
        var connection = new Renci.SshNet.ConnectionInfo(runtime.Host, runtime.Port, runtime.Username, authentication) { Timeout = TimeSpan.FromSeconds(15) };
        var algorithm = connection.HostKeyAlgorithms[runtime.HostKeyAlgorithm]; connection.HostKeyAlgorithms.Clear(); connection.HostKeyAlgorithms.Add(runtime.HostKeyAlgorithm, algorithm);
        using var ssh = new SshClient(connection);
        SshRuntimeTransportFactory.AttachHostKey(ssh, runtime.HostKeySha256);
        await ssh.ConnectAsync(token);
        return await action(ssh, connection);
    }

    private static async Task<GitHubProcessEnvironmentEvidence> Probe(SshClient ssh, RuntimeRecord runtime,
        string credentialConfigurationFingerprint, CancellationToken token)
    {
        var observedAt = ControlStore.Now;
        try
        {
            return GitHubProcessEnvironment.Parse(runtime,
                await SshRuntimeTransportFactory.Run(ssh,
                    GitHubProcessEnvironment.Script(runtime, credentialConfigurationFingerprint), token, 5), observedAt,
                credentialConfigurationFingerprint);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Delivery may already be complete. Preserve that receipt while refusing
            // to claim effective access from missing process evidence.
            return new(GitHubProcessEnvironment.Unavailable, "Unknown", null, "", observedAt,
                GitHubProcessEnvironment.CurrentPolicyVersion, GitHubProcessEnvironment.Fingerprint(runtime),
                credentialConfigurationFingerprint);
        }
    }

    public static string HostsYaml(GitHubInstallationToken credential)
    {
        var actor = System.Text.Json.JsonSerializer.Serialize(credential.Actor);
        var value = System.Text.Json.JsonSerializer.Serialize(credential.Value);
        // Supply the bot identity and both legacy/current token locations. Otherwise gh's
        // multi-account migration calls /user, which installation tokens cannot satisfy.
        return $"github.com:\n    user: {actor}\n    oauth_token: {value}\n    git_protocol: https\n    users:\n        {actor}:\n            oauth_token: {value}\n";
    }

    public static bool IsExclusivelyManagedHosts(string hosts, string actor)
    {
        var actorYaml = JsonSerializer.Serialize(actor);
        var lines = hosts.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (hosts.Contains('\r') || lines.Length != 8 || lines[7].Length != 0 ||
            lines[0] != "github.com:" ||
            lines[1] != "    user: " + actorYaml ||
            lines[3] != "    git_protocol: https" ||
            lines[4] != "    users:" ||
            lines[5] != "        " + actorYaml + ":") return false;

        const string primaryPrefix = "    oauth_token: ";
        const string userPrefix = "            oauth_token: ";
        if (!lines[2].StartsWith(primaryPrefix, StringComparison.Ordinal) ||
            !lines[6].StartsWith(userPrefix, StringComparison.Ordinal)) return false;
        var primary = lines[2][primaryPrefix.Length..];
        var user = lines[6][userPrefix.Length..];
        return JsonScalarIsCanonical(primary) && primary == user;
    }

    public static bool IsManagedConfigurationReplacementAllowed(bool hostsExist, string? owner, string managedServerId) =>
        hostsExist ? owner == managedServerId : owner is null;

    private static bool JsonScalarIsCanonical(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.String &&
                JsonSerializer.Serialize(document.RootElement.GetString()) == value;
        }
        catch (JsonException) { return false; }
    }

    private static void Write(SftpClient sftp, string path, string text)
    {
        var temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            sftp.UploadFile(stream, temporary); sftp.ChangePermissions(temporary, 600);
            sftp.RenameFile(temporary, path, isPosix: true);
        }
        finally { if (sftp.Exists(temporary)) sftp.DeleteFile(temporary); }
    }

    private static string Read(SftpClient sftp, string path)
    {
        using var stream = new MemoryStream();
        sftp.DownloadFile(path, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
