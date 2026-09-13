using System.Diagnostics;

namespace HVO.AgentControl.Runtime;

/// <summary>Inputs required to launch the optional detached attach client.</summary>
public sealed record TmuxAttachRequest(
    string NativeUrl,
    string Workspace,
    string Home,
    string SessionId,
    string Username,
    string Password,
    string OwnerToken,
    bool Enabled);

/// <summary>
/// Owns the optional tmux-hosted <c>opencode attach</c> client. Only a session
/// created by this launcher is ever killed; a pre-existing session with the same
/// name is left untouched and reported as a conflict.
/// </summary>
public sealed class TmuxAttachLauncher
{
    public const string OwnerEnvironmentVariable = "AGENTCONTROL_OWNER";

    private readonly string _sessionName;
    private readonly string _tmuxExecutable;
    private bool _owned;

    public TmuxAttachLauncher(string sessionName, string tmuxExecutable = "tmux")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tmuxExecutable);
        _sessionName = sessionName;
        _tmuxExecutable = tmuxExecutable;
    }

    public bool IsOwned => _owned;

    public async Task<TmuxAttachResult> EnsureAsync(TmuxAttachRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Enabled)
        {
            return new TmuxAttachResult(false, false, null);
        }

        var exists = await RunAsync(["has-session", "-t", _sessionName], cancellationToken).ConfigureAwait(false);
        if (exists.ExitCode != 0 && exists.ExitCode != 1)
        {
            return new TmuxAttachResult(false, false, $"tmux has-session failed: {Describe(exists)}");
        }

        if (exists.ExitCode == 0)
        {
            var environment = await RunAsync(
                ["show-environment", "-t", _sessionName, OwnerEnvironmentVariable],
                cancellationToken).ConfigureAwait(false);

            var expected = $"{OwnerEnvironmentVariable}={request.OwnerToken}";
            if (environment.ExitCode == 0
                && string.Equals(environment.StandardOutput.Trim(), expected, StringComparison.Ordinal))
            {
                _owned = true;
                return new TmuxAttachResult(true, true, null);
            }

            return new TmuxAttachResult(
                false,
                false,
                $"tmux session '{_sessionName}' already exists and is not owned by this runtime; refusing to adopt or replace it.");
        }

        var start = await RunAsync(
            [
                "new-session",
                "-d",
                "-s", _sessionName,
                "-c", request.Workspace,
                "-e", $"HOME={request.Home}",
                "-e", $"{OwnerEnvironmentVariable}={request.OwnerToken}",
                "-e", $"OPENCODE_SERVER_USERNAME={request.Username}",
                "-e", $"OPENCODE_SERVER_PASSWORD={request.Password}",
                "opencode", "attach", request.NativeUrl, "--dir", request.Workspace, "--session", request.SessionId,
            ],
            cancellationToken).ConfigureAwait(false);

        if (start.ExitCode != 0)
        {
            return new TmuxAttachResult(false, false, $"tmux new-session failed: {Describe(start)}");
        }

        _owned = true;
        return new TmuxAttachResult(true, true, null);
    }

    public async Task<bool> KillOwnedAsync(string ownerToken, CancellationToken cancellationToken)
    {
        if (!_owned)
        {
            return false;
        }

        var environment = await RunAsync(
            ["show-environment", "-t", _sessionName, OwnerEnvironmentVariable],
            cancellationToken).ConfigureAwait(false);
        var expected = $"{OwnerEnvironmentVariable}={ownerToken}";
        if (environment.ExitCode != 0
            || !string.Equals(environment.StandardOutput.Trim(), expected, StringComparison.Ordinal))
        {
            _owned = false;
            return false;
        }

        var kill = await RunAsync(["kill-session", "-t", _sessionName], cancellationToken).ConfigureAwait(false);
        _owned = false;
        return kill.ExitCode == 0;
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _tmuxExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ProcessResult(-1, string.Empty, "process did not start");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
    }

    private static string Describe(ProcessResult result)
    {
        var error = result.StandardError.Trim();
        return error.Length > 0 ? error : $"exit code {result.ExitCode}";
    }

    public sealed record TmuxAttachResult(bool Started, bool Owned, string? Error);

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
