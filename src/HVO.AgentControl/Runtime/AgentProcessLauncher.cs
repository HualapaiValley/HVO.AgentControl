using System.Diagnostics;
using System.Globalization;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// Routes every agent-facing child process through the narrow privileged
/// launcher (<c>src/launcher/agentcontrol-launch.c</c>) so it runs under the
/// separate agent UID instead of the controller's.
/// </summary>
/// <remarks>
/// <para>
/// The launcher is configured by <see cref="ControlOptions.AgentLauncher"/>. When
/// that option is empty - host development, unit tests and the disabled CI
/// runtime - <see cref="IsEnabled"/> is false and callers keep the historical
/// same-identity behavior. Container deployments set the path, and Compose runs
/// the controller as UID 1001 where the same-identity path would simply fail.
/// </para>
/// <para>
/// Only the fixed operation tokens exposed by the launcher are reachable from
/// here. There is deliberately no method that forwards an arbitrary executable
/// path, UID or environment block: the launcher would reject it, and this type
/// must not imply otherwise.
/// </para>
/// <para>
/// <b>What this is not.</b> <see cref="WrapTmux"/> forwards the caller's tmux
/// arguments, and <c>new-session</c>/<c>new-window</c> take a child command
/// vector - so a caller that reaches this seam can run a program of its choosing
/// inside a tmux pane (OpenCode's own TUI is exactly that). The guarantee is
/// confinement, not an execution allow-list: whatever runs, runs as the agent
/// UID, with no capabilities, <c>no_new_privs</c> set and an allow-listed
/// environment, so it can reach neither the controller's private state nor the
/// owner secret nor the privileged launcher itself.
/// </para>
/// </remarks>
public sealed class AgentProcessLauncher
{
    /// <summary>Launcher installed by the container image.</summary>
    public const string ContainerPath = "/usr/local/bin/agentcontrol-launch";

    /// <summary>
    /// Working directory used when starting the launcher itself.
    /// </summary>
    /// <remarks>
    /// The agent's home and workspace are mode <c>0700</c> and owned by the agent
    /// UID, so the controller cannot enter them: <see cref="Process.Start()"/>
    /// would fail with "Permission denied" while still applying the parent's
    /// working directory. The launcher performs the real <c>chdir</c> after it has
    /// dropped to the agent identity, which is also the only point at which the
    /// directory's ownership is evaluated against the identity that will use it.
    /// </remarks>
    private const string NeutralWorkingDirectory = "/";

    private readonly string? _launcherPath;

    public AgentProcessLauncher(string? launcherPath)
    {
        _launcherPath = string.IsNullOrWhiteSpace(launcherPath) ? null : launcherPath;
    }

    /// <summary>A launcher that keeps direct, same-identity launches.</summary>
    public static AgentProcessLauncher Direct { get; } = new(null);

    /// <summary>True when child processes are launched under the agent identity.</summary>
    public bool IsEnabled => _launcherPath is not null;

    /// <summary>Configured launcher path, or null when direct launching is in effect.</summary>
    public string? LauncherPath => _launcherPath;

    /// <summary>
    /// Rewrites <paramref name="startInfo"/> to run <c>opencode acp</c> under the
    /// agent identity. The launcher rebuilds the argument vector from the
    /// validated hostname, port and working directory; the caller's executable
    /// name is never forwarded, because the launcher owns that mapping.
    /// </summary>
    public ProcessStartInfo WrapAcp(ProcessStartInfo startInfo, string hostname, int port, string workspace)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        if (_launcherPath is null)
        {
            return startInfo;
        }

        startInfo.FileName = _launcherPath;
        startInfo.WorkingDirectory = NeutralWorkingDirectory;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("acp");
        startInfo.ArgumentList.Add(hostname);
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(workspace);
        return startInfo;
    }

    /// <summary>
    /// Rewrites <paramref name="startInfo"/> to run a tmux subcommand under the
    /// agent identity, preserving the caller's argument order.
    /// </summary>
    /// <remarks>
    /// The arguments are forwarded verbatim and the launcher only allow-lists the
    /// subcommand, so a pane command vector here executes an arbitrary program -
    /// as the agent UID, never as the controller or root. See the type remarks.
    /// </remarks>
    public ProcessStartInfo WrapTmux(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (_launcherPath is null)
        {
            return startInfo;
        }

        var arguments = startInfo.ArgumentList.ToArray();
        startInfo.FileName = _launcherPath;
        startInfo.WorkingDirectory = NeutralWorkingDirectory;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("tmux");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>
    /// Rewrites <paramref name="startInfo"/> to run the PTY bridge under the
    /// agent identity. The interpreter and the bridge script are fixed inside the
    /// launcher, so only the home directory and tmux session survive.
    /// </summary>
    public ProcessStartInfo WrapPty(ProcessStartInfo startInfo, string homeDirectory, string sessionName)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);

        if (_launcherPath is null)
        {
            return startInfo;
        }

        startInfo.FileName = _launcherPath;
        startInfo.WorkingDirectory = NeutralWorkingDirectory;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("pty");
        startInfo.ArgumentList.Add(homeDirectory);
        startInfo.ArgumentList.Add(sessionName);
        return startInfo;
    }

    /// <summary>
    /// Terminates an owned child. A controller running as UID 1001 cannot signal
    /// its UID 1000 children directly, so the launcher performs the verified
    /// signal; without a launcher this is the ordinary in-process kill.
    /// </summary>
    /// <remarks>
    /// The PID is read from a live <see cref="Process"/> the caller still owns, so
    /// it cannot have been recycled: the child is not reaped until the caller
    /// waits on it. The launcher additionally verifies parentage and the target's
    /// UID before signalling.
    /// </remarks>
    /// <returns>True when a termination request was issued.</returns>
    public bool TryTerminate(Process process, bool force)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            if (process.HasExited)
            {
                return false;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (_launcherPath is null)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
            {
                return false;
            }
        }

        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return SendSignal(pid, force ? "KILL" : "TERM");
    }

    private bool SendSignal(int pid, string signal)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _launcherPath!,
            WorkingDirectory = NeutralWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("signal");
        startInfo.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(signal);

        try
        {
            using var helper = Process.Start(startInfo);
            if (helper is null)
            {
                return false;
            }

            if (!helper.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                helper.Kill(entireProcessTree: true);
                return false;
            }

            return helper.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or IOException)
        {
            return false;
        }
    }
}
