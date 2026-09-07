using System.Text;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Renci.SshNet;

namespace HVO.AgentControl.GitHub;

public sealed class GitHubCredentialDelivery(Secrets secrets)
{
    public async Task Deliver(RuntimeRecord runtime, GitHubInstallationToken credential, CancellationToken token)
    {
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
        var directory = (await SshRuntimeTransportFactory.Run(ssh, "printf '%s' \"${GH_CONFIG_DIR:-${XDG_CONFIG_HOME:-$HOME/.config}/gh}\"", token)).TrimEnd('\n');
        ControlStore.ValidatePath(directory);
        var q = BootstrapScript.Quote(directory);
        // A host's existing personal GitHub login is never implicitly replaced.
        await SshRuntimeTransportFactory.Run(ssh, $$"""
            command -v gh >/dev/null || { echo GH_MISSING; exit 1; }
            test -z "${GH_TOKEN:-}${GITHUB_TOKEN:-}" || { echo GITHUB_ENVIRONMENT_OVERRIDE; exit 1; }
            test ! -L {{q}} && umask 077 && mkdir -p {{q}} &&
            test "$(cd {{q}} && pwd -P)" = {{q}} &&
            test ! -L {{q}}/hosts.yml && test ! -L {{q}}/.agentcontrol-owner &&
            { test ! -e {{q}}/hosts.yml || test "$(cat {{q}}/.agentcontrol-owner 2>/dev/null)" = {{BootstrapScript.Quote(runtime.ManagedServerId)}}; } ||
            { echo EXISTING_GITHUB_CONFIGURATION; exit 1; }
            chmod 700 {{q}}
            """, token);
        using var sftp = new SftpClient(connection);
        SshRuntimeTransportFactory.AttachHostKey(sftp, runtime.HostKeySha256);
        await sftp.ConnectAsync(token);
        Write(sftp, directory + "/.agentcontrol-owner", runtime.ManagedServerId);
        // JSON strings are valid YAML scalars. Credentials travel over SFTP, never shell arguments.
        Write(sftp, directory + "/hosts.yml", HostsYaml(credential));
    }

    public static string HostsYaml(GitHubInstallationToken credential)
    {
        var actor = System.Text.Json.JsonSerializer.Serialize(credential.Actor);
        var value = System.Text.Json.JsonSerializer.Serialize(credential.Value);
        // Supply the bot identity and both legacy/current token locations. Otherwise gh's
        // multi-account migration calls /user, which installation tokens cannot satisfy.
        return $"github.com:\n    user: {actor}\n    oauth_token: {value}\n    git_protocol: https\n    users:\n        {actor}:\n            oauth_token: {value}\n";
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
}
