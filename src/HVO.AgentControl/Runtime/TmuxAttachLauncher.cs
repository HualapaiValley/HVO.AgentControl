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
    private const string PaneOption = "@agentcontrol_attach_pane";
    // A crash between creation and identity persistence leaves ownership of the
    // attach pane unknown. Do not inspect commands (which may contain secrets)
    // or create duplicates; an operator must reconcile the session instead.
    private const string UnknownPaneError = "tmux attach-pane identity is missing or invalid; operator recovery required.";
    private DateTimeOffset _nextProbe;
    private int _retrySeconds = 2;
    private TmuxAttachResult _lastResult = new(false, false, null);
    private TmuxAttachRequest? _lastRequest;
    private string? _paneId;
    private (string PaneId, TmuxAttachRequest Request)? _pendingPane;

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
        cancellationToken.ThrowIfCancellationRequested();
        if (request == _lastRequest && _timeProvider.GetUtcNow() < _nextProbe)
        {
            return _lastResult;
        }
        _lastRequest = request;
        // Never retain a healthy result if this attempt fails or is cancelled.
        _lastResult = new(false, _owned, null);
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
            // Inconclusive health is not loss of ownership. Shutdown must still
            // be able to recheck the owner token before cleaning up.
            return Complete(false, "tmux has-session failed.");
        }

        if (exists.ExitCode == 0)
        {
            _owned = await MatchesOwnerAsync(request.OwnerToken, cancellationToken).ConfigureAwait(false);
            if (!_owned)
            {
                _paneId = null;
                return Complete(false, $"tmux session '{_sessionName}' is not owned by this runtime; refusing to replace it.");
            }

            // Finish a known partial creation before consulting a possibly stale
            // persisted marker. Never create a second pane to retry metadata/UI work.
            if (_pendingPane is { } pending)
            {
                if (pending.Request != request)
                {
                    return Complete(false, "tmux pending attach request changed; operator recovery required.");
                }
                _paneId = pending.PaneId;
                return await FinishPendingPaneAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var identity = await RunAsync(["show-options", "-qv", "-t", "=" + _sessionName, PaneOption], cancellationToken).ConfigureAwait(false);
            if (identity.ExitCode != 0)
            {
                return Complete(false, "tmux attach-pane identity check failed.");
            }
            var prefix = request.OwnerToken + ":";
            var value = identity.StandardOutput.Trim();
            _paneId = value.StartsWith(prefix, StringComparison.Ordinal) && IsPaneId(value[prefix.Length..])
                ? value[prefix.Length..] : null;
            if (_paneId is null)
            {
                return Complete(false, UnknownPaneError);
            }
            var live = await ProbePaneAsync(cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                return Complete(false, "tmux live-pane check failed.");
            }
            if (live.Value)
            {
                // Health checks must not steal the user's selected window/pane.
                return Complete(true, null);
            }
            // Recovery never kills a session, window, or even a retained dead pane:
            // an unknown pane is not ours, and other windows may contain user work.
            if (!await MatchesOwnerAsync(request.OwnerToken, cancellationToken).ConfigureAwait(false))
            {
                _owned = false;
                return Complete(false, "tmux ownership changed before recovery.");
            }
        }
        else
        {
            _owned = false;
            _paneId = null;
            _pendingPane = null;
        }

        // An existing tmux server has its own (possibly credential-bearing) global
        // environment. Clear it again inside the pane, not just in the tmux client.
        var arguments = exists.ExitCode == 0
            ? new List<string> { "new-window", "-d", "-t", "=" + _sessionName + ":" }
            : new List<string> { "new-session", "-d", "-s", _sessionName, "-e", $"{OwnerEnvironmentVariable}={request.OwnerToken}" };
        arguments.AddRange(["-P", "-F", "#{pane_id}", "-c", request.Workspace, "/usr/bin/env", "-i"]);
        arguments.AddRange(_environment.Select(pair => $"{pair.Key}={pair.Value}"));
        arguments.Add("TERM=tmux-256color");
        arguments.AddRange([request.OpenCodeExecutable, "attach", request.NativeUrl, "--dir", request.Workspace, "--session", request.SessionId]);
        var start = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (start.ExitCode != 0 || !IsPaneId(start.StandardOutput.Trim()))
        {
            return Complete(false, "tmux attach-pane creation failed.");
        }

        _paneId = start.StandardOutput.Trim();
        _pendingPane = (_paneId, request);
        _owned = await MatchesOwnerAsync(request.OwnerToken, cancellationToken).ConfigureAwait(false);
        if (!_owned)
        {
            return Complete(false, "tmux ownership check after creation failed.");
        }
        return await FinishPendingPaneAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TmuxAttachResult> FinishPendingPaneAsync(TmuxAttachRequest request, CancellationToken cancellationToken)
    {
        if (await ProbePaneAsync(cancellationToken).ConfigureAwait(false) != true)
        {
            return Complete(false, "tmux pending attach pane is not live; operator recovery required.");
        }
        var saved = await RunAsync(["set-option", "-t", "=" + _sessionName, PaneOption, request.OwnerToken + ":" + _paneId], cancellationToken).ConfigureAwait(false);
        if (saved.ExitCode != 0)
        {
            return Complete(false, "tmux attach-pane identity persistence failed.");
        }
        // The endpoint attaches to the session, so select the replacement TUI window.
        if (!await SelectPaneAsync(cancellationToken).ConfigureAwait(false))
        {
            return Complete(false, "tmux attach window selection failed.");
        }
        if (await ProbePaneAsync(cancellationToken).ConfigureAwait(false) != true)
        {
            return Complete(false, "tmux attach pane is not live.");
        }
        _pendingPane = null;
        return Complete(true, null);
    }

    private async Task<bool> SelectPaneAsync(CancellationToken cancellationToken)
    {
        var window = await RunAsync(["select-window", "-t", _paneId!], cancellationToken).ConfigureAwait(false);
        if (window.ExitCode != 0) return false;
        var pane = await RunAsync(["select-pane", "-t", _paneId!], cancellationToken).ConfigureAwait(false);
        return window.ExitCode == 0 && pane.ExitCode == 0;
    }

    private async Task<bool?> ProbePaneAsync(CancellationToken cancellationToken)
    {
        var panes = await RunAsync(["list-panes", "-s", "-t", "=" + _sessionName, "-F", "#{pane_id} #{pane_dead}"], cancellationToken).ConfigureAwait(false);
        if (panes.ExitCode != 0)
        {
            return null;
        }
        return _paneId is not null && panes.StandardOutput.Split('\n', StringSplitOptions.TrimEntries).Contains(_paneId + " 0", StringComparer.Ordinal);
    }

    private static bool IsPaneId(string value) => value.Length > 1 && value[0] == '%' && value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    private TmuxAttachResult Complete(bool started, string? error)
    {
        _lastResult = new(started, _owned, error);
        _nextProbe = _timeProvider.GetUtcNow().AddSeconds(started ? 5 : _retrySeconds);
        _retrySeconds = started ? 2 : Math.Min(30, _retrySeconds * 2);
        return _lastResult;
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
        _lastRequest = null;
        _lastResult = new(false, false, null);
        // Explicit shutdown retains the owner-matched whole-session policy;
        // recovery deliberately never invokes this operation.
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
