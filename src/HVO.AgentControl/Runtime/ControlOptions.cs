using System.Net;
using HVO.AgentControl.Terminal;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// Configuration for the V2 background OpenCode ACP control host.
/// Disabled by default so tests and local host development do not require the
/// OpenCode binary; container deployments opt in through <c>Control:Enabled</c>.
/// </summary>
public sealed class ControlOptions
{
    public const string SectionName = "Control";
    public const int MinimumCliProxySecretLength = 16;

    /// <summary>Whether the background control runtime should start. Defaults to disabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Root directory for the agent-owned workspace and private OpenCode home.
    /// In the isolated container this tree belongs to the agent UID; the
    /// controller neither writes to it nor stores its own state there.
    /// </summary>
    public string DataDirectory { get; set; } = "/data";

    /// <summary>
    /// Controller-private state directory (mode <c>0700</c>, controller UID).
    /// When empty the runtime state stays beside <see cref="DataDirectory"/>,
    /// which is the host-development and single-identity layout.
    /// </summary>
    public string PrivateDataDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Host-owned, agent-readable orientation directory. The controller writes
    /// the instruction file here (mode <c>0644</c> in a <c>0755</c> directory) so
    /// the agent can read but never replace it. When empty the instructions stay
    /// in the agent home, which is the single-identity layout.
    /// </summary>
    public string InstructionsDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path of the privileged launcher that starts agent-identity
    /// children (<c>agentcontrol-launch</c>). Empty means direct, same-identity
    /// launching: host development, unit tests and the disabled CI runtime.
    /// </summary>
    public string AgentLauncher { get; set; } = string.Empty;

    /// <summary>Human readable organization name used in instructions and the session title.</summary>
    public string OrganizationName { get; set; } = "AgentControl Development";

    /// <summary>
    /// Owner authorization reference recorded in the adoption audit for the one
    /// approved initial Operations/IT employee. It is never a model assertion.
    /// </summary>
    /// <remarks>
    /// Source: the owner explicitly authorized bounded work on issue #211
    /// (initial organization persistence and the single seed employee) and the
    /// current implementation decision is to record the exact string
    /// <c>owner-approved:issue-211</c>. Changing it requires a new explicit
    /// owner decision, not a quiet edit. The value is written verbatim into the
    /// <c>adoption_audit.authorization_reference</c> column and surfaced by the
    /// organization overview.
    /// </remarks>
    public string AdoptionAuthorizationReference { get; set; } = "owner-approved:issue-211";

    /// <summary>Executable used to launch the OpenCode ACP server.</summary>
    public string OpenCodeExecutable { get; set; } = "opencode";

    /// <summary>Loopback port for the OpenCode native HTTP server exposed by the ACP process.</summary>
    public int NativePort { get; set; } = 4096;

    /// <summary>Model identifier supplied in the generated OpenCode configuration.</summary>
    public string Model { get; set; } = "opencode/big-pickle";

    /// <summary>Requested OpenCode variant, separate from observed provider/model metadata.</summary>
    public string ModelVariant { get; set; } = string.Empty;

    /// <summary>Fixed OpenAI-compatible CLIProxy endpoint; required only for cliproxy/* lanes.</summary>
    public string CliProxyEndpoint { get; set; } = string.Empty;

    /// <summary>Absolute file containing the Phase 1 shared inference key; never persisted in generated config.</summary>
    public string CliProxySecretFile { get; set; } = string.Empty;

    /// <summary>Loopback hostname for the native HTTP server.</summary>
    public string Hostname { get; set; } = "127.0.0.1";

    /// <summary>tmux session name used for the optional attach client.</summary>
    public string TmuxSessionName { get; set; } = "agentcontrol";

    /// <summary>When false the tmux attach client is not started (used by tests/host development).</summary>
    public bool EnableTerminal { get; set; } = true;

    /// <summary>Bounded grace period used while cancelling and terminating owned processes.</summary>
    public int ShutdownGraceSeconds { get; set; } = 10;

    /// <summary>Timeout for the ACP initialize handshake and session establishment.</summary>
    public int StartupTimeoutSeconds { get; set; } = 60;

    /// <summary>Timeout applied to the one-time bootstrap prompt for a new session.</summary>
    public int PromptTimeoutSeconds { get; set; } = 300;

    /// <summary>Interval for best-effort native session-status polling.</summary>
    public int SessionStatePollSeconds { get; set; } = 5;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            errors.Add($"{nameof(DataDirectory)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(OrganizationName))
        {
            errors.Add($"{nameof(OrganizationName)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(OpenCodeExecutable))
        {
            errors.Add($"{nameof(OpenCodeExecutable)} must not be empty.");
        }

        // The optional isolation paths are all absolute: a relative value would
        // resolve against the controller's working directory and silently place
        // private state, orientation or the privileged launcher somewhere else.
        if (!IsAbsoluteOrEmpty(PrivateDataDirectory))
        {
            errors.Add($"{nameof(PrivateDataDirectory)} must be an absolute path when configured.");
        }

        if (!IsAbsoluteOrEmpty(InstructionsDirectory))
        {
            errors.Add($"{nameof(InstructionsDirectory)} must be an absolute path when configured.");
        }

        if (!IsAbsoluteOrEmpty(AgentLauncher))
        {
            errors.Add($"{nameof(AgentLauncher)} must be an absolute path when configured.");
        }

        if (string.IsNullOrWhiteSpace(AdoptionAuthorizationReference))
        {
            errors.Add($"{nameof(AdoptionAuthorizationReference)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            errors.Add($"{nameof(Model)} must not be empty.");
        }
        else if (CliProxyRuntimeConfiguration.IsCliProxyModel(Model))
        {
            // Provider selection, endpoint, variant and secret bytes are checked
            // after the organization store opens. That ordering lets a failed
            // required-provider start durably record unavailable/revoked before
            // faulting, while still occurring before any OpenCode child starts.
        }
        else if (!string.IsNullOrWhiteSpace(ModelVariant))
        {
            errors.Add($"{nameof(ModelVariant)} is supported only for curated CLIProxy policy lanes.");
        }

        if (!IsLoopbackHostname(Hostname))
        {
            errors.Add($"{nameof(Hostname)} must be a loopback address (127.0.0.0/8 or ::1) or localhost.");
        }

        if (NativePort is < 1 or > 65535)
        {
            errors.Add($"{nameof(NativePort)} must be between 1 and 65535.");
        }

        // Share the terminal target rule so an accepted session name cannot be
        // rejected (or silently reinterpreted) by tmux later.
        if (!TerminalProtocol.IsValidSessionName(TmuxSessionName))
        {
            errors.Add($"{nameof(TmuxSessionName)} must be 1-{TerminalProtocol.MaxSessionNameLength} characters using only letters, digits, '_' or '-'.");
        }

        if (ShutdownGraceSeconds < 1)
        {
            errors.Add($"{nameof(ShutdownGraceSeconds)} must be at least one second.");
        }

        if (StartupTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(StartupTimeoutSeconds)} must be positive.");
        }

        if (PromptTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(PromptTimeoutSeconds)} must be positive.");
        }

        if (SessionStatePollSeconds < 1)
        {
            errors.Add($"{nameof(SessionStatePollSeconds)} must be positive.");
        }

        return errors;
    }

    /// <summary>Controller-private state root; falls back to <see cref="DataDirectory"/>.</summary>
    public string ResolvePrivateDataDirectory() =>
        string.IsNullOrWhiteSpace(PrivateDataDirectory) ? DataDirectory : PrivateDataDirectory;

    /// <summary>
    /// Absolute path of the authoritative SQLite store. It is always
    /// <c>control.db</c> inside the controller-private directory: the path is
    /// fixed, not configurable, so the rollback tooling, the container layout
    /// preparation and the host can never disagree about where the authoritative
    /// store lives and no deployment can silently select a different one.
    /// </summary>
    public string ResolveDatabasePath() =>
        Path.Combine(ResolvePrivateDataDirectory(), HVO.AgentControl.Organization.OrganizationStore.DatabaseFileName);

    private static bool IsAbsoluteOrEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value);

    /// <summary>
    /// Accepts only numeric loopback addresses plus the exact host name
    /// <c>localhost</c>. The native HTTP server must never be reachable off the
    /// container; anything that resolves or routes elsewhere fails closed.
    /// </summary>
    private static bool IsLoopbackHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return false;
        }

        if (IPAddress.TryParse(hostname, out var address))
        {
            return IPAddress.IsLoopback(address);
        }

        return string.Equals(hostname, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
