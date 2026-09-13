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
    bool Enabled,
    string OpenCodeExecutable);

/// <summary>Owns only the optional, owner-token-matched tmux attach session.</summary>
public sealed class TmuxAttachLauncher
{
    public const string OwnerEnvironmentVariable = "AGENTCONTROL_OWNER";

    private readonly string _sessionName;
    private readonly string _tmuxExecutable;
    private readonly TimeProvider _timeProvider;
    private bool _owned;
    private Dictionary<string, string> _environment = [];
    private DateTimeOffset _nextStart;
    private int _retrySeconds = 2;

    public TmuxAttachLauncher(string sessionName, string tmuxExecutable = "tmux", TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tmuxExecutable);
        _sessionName = sessionName;
        _tmuxExecutable = tmuxExecutable;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsOwned => _owned;

    public async Task<TmuxAttachResult> EnsureAsync(TmuxAttachRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Enabled)
        {
            return new(false, false, null);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.OpenCodeExecutable);
        _environment = ChildEnvironment.Build(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = request.Home,
            ["XDG_DATA_HOME"] = Path.Combine(request.Home, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(request.Home, "config"),
            ["XDG_STATE_HOME"] = Path.Combine(request.Home, "state"),
            ["XDG_CACHE_HOME"] = Path.Combine(request.Home, "cache"),
            [OwnerEnvironmentVariable] = request.OwnerToken,
            ["OPENCODE_SERVER_USERNAME"] = request.Username,
            ["OPENCODE_SERVER_PASSWORD"] = request.Password,
        });

        // '=' prevents tmux's prefix matching from selecting an unrelated session.
        var exists = await RunAsync(["has-session", "-t", "=" + _sessionName], cancellationToken).ConfigureAwait(false);
        if (exists.ExitCode is not (0 or 1))
        {
            return new(false, false, "tmux has-session failed.");
        }

        if (exists.ExitCode == 0)
        {
            _owned = await MatchesOwnerAsync(request.OwnerToken, cancellationToken).ConfigureAwait(false);
            if (!_owned)
            {
                return new(false, false, $"tmux session '{_sessionName}' is not owned by this runtime; refusing to replace it.");
            }

            var panes = await RunAsync(["list-panes", "-s", "-t", "=" + _sessionName, "-F", "#{pane_dead}"], cancellationToken).ConfigureAwait(false);
            if (panes.ExitCode == 0 && panes.StandardOutput.Split('\n', StringSplitOptions.TrimEntries).Contains("0"))
            {
                _retrySeconds = 2;
                return new(true, true, null);
            }

            // A failed probe is not evidence that it is safe to destroy a session.
            if (panes.ExitCode != 0)
            {
                return new(false, true, "tmux live-pane check failed.");
            }
            if (_timeProvider.GetUtcNow() < _nextStart)
            {
                return new(false, true, null);
            }
            if (!await KillOwnedAsync(request.OwnerToken, cancellationToken).ConfigureAwait(false))
            {
                return new(false, false, "tmux dead-session cleanup failed.");
            }
        }
        else
        {
            _owned = false;
        }

        if (_timeProvider.GetUtcNow() < _nextStart)
        {
            return new(false, false, null);
        }
        _nextStart = _timeProvider.GetUtcNow().AddSeconds(_retrySeconds);
        _retrySeconds = Math.Min(30, _retrySeconds * 2);

        // An existing tmux server has its own (possibly credential-bearing) global
        // environment. Clear it again inside the pane, not just in the tmux client.
        var arguments = new List<string>
        {
            "new-session", "-d", "-s", _sessionName, "-c", request.Workspace,
            "-e", $"{OwnerEnvironmentVariable}={request.OwnerToken}",
            "/usr/bin/env", "-i",
        };
        arguments.AddRange(_environment.Select(pair => $"{pair.Key}={pair.Value}"));
        arguments.Add("TERM=tmux-256color");
        arguments.AddRange([request.OpenCodeExecutable, "attach", request.NativeUrl, "--dir", request.Workspace, "--session", request.SessionId]);
        var start = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (start.ExitCode != 0)
        {
            return new(false, false, "tmux new-session failed.");
        }

        _owned = true;
        // Successful creation alone does not prove that the attach client stayed alive.
        var live = await RunAsync(["list-panes", "-s", "-t", "=" + _sessionName, "-F", "#{pane_dead}"], cancellationToken).ConfigureAwait(false);
        return new(live.ExitCode == 0 && live.StandardOutput.Split('\n', StringSplitOptions.TrimEntries).Contains("0"), true, null);
    }

    private async Task<bool> MatchesOwnerAsync(string ownerToken, CancellationToken cancellationToken)
    {
        var environment = await RunAsync(
            ["show-environment", "-t", "=" + _sessionName, OwnerEnvironmentVariable], cancellationToken).ConfigureAwait(false);
        return environment.ExitCode == 0
            && string.Equals(environment.StandardOutput.Trim(), $"{OwnerEnvironmentVariable}={ownerToken}", StringComparison.Ordinal);
    }

    public async Task<bool> KillOwnedAsync(string ownerToken, CancellationToken cancellationToken)
    {
        if (!_owned || !await MatchesOwnerAsync(ownerToken, cancellationToken).ConfigureAwait(false))
        {
            _owned = false;
            return false;
        }
        var kill = await RunAsync(["kill-session", "-t", "=" + _sessionName], cancellationToken).ConfigureAwait(false);
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
        startInfo.Environment.Clear();
        foreach (var (key, value) in _environment)
        {
            startInfo.Environment[key] = value;
        }
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new(-1, string.Empty);
            }
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
                var stderr = process.StandardError.ReadToEndAsync(bounded.Token);
                await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                return new(process.ExitCode, await stdout.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new(-1, string.Empty);
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Do not echo subprocess output/arguments: they can contain credentials.
            return new(-1, string.Empty);
        }
    }

    public sealed record TmuxAttachResult(bool Started, bool Owned, string? Error);
    private readonly record struct ProcessResult(int ExitCode, string StandardOutput);
}
