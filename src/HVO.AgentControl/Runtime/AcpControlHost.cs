using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// V2 background control host. It owns a single OpenCode ACP process on
/// loopback, records organization/session identity durably, rejects unexpected
/// permission requests and keeps an optional tmux attach client alive as a
/// separate process. It is disabled unless <see cref="ControlOptions.Enabled"/>
/// is set.
/// </summary>
public sealed class AcpControlHost : BackgroundService
{
    private static readonly TimeSpan CancelDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound for a single model change, including ACP/HTTP.</summary>
    private static readonly TimeSpan ModelWriteDeadline = TimeSpan.FromSeconds(10);

    private readonly ControlOptions _options;
    private readonly ILogger<AcpControlHost> _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _hostLifetime = new();
    private readonly SanitizedLogBuffer _stderrBuffer;

    /// <summary>
    /// Serializes model writes with the authoritative model poll so a stale
    /// read cannot overwrite a newer selection.
    /// </summary>
    private readonly SemaphoreSlim _modelUpdateLock = new(1, 1);

    private ControlState _state = ControlState.Disabled;
    private string? _error;
    private DateTimeOffset? _startedAt;
    private string? _sessionId;
    private string? _sessionState;
    private string _model = "unknown";
    private IReadOnlyList<ControlModel>? _models;
    private bool _terminalReady;
    private string? _terminalError;
    private bool _isNewSession;
    private string _password = string.Empty;
    private string? _ownerToken;

    private Process? _process;
    private AcpRpcSession? _session;
    private OpenCodeNativeClient? _native;
    private TmuxAttachLauncher? _terminal;
    private Task? _bootstrapTask;
    private Task? _statusPollerTask;
    private Task? _notificationTask;
    private Task? _stderrTask;

    public AcpControlHost(IOptions<ControlOptions> options, ILogger<AcpControlHost> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stderrBuffer = new SanitizedLogBuffer(secret: null);
    }

    /// <summary>Absolute data directory used by the runtime.</summary>
    public string DataDirectory => Path.GetFullPath(_options.DataDirectory);

    /// <summary>Loopback URL of the native OpenCode HTTP server exposed by ACP.</summary>
    public string NativeUrl => new UriBuilder("http", _options.Hostname, _options.NativePort).Uri.GetLeftPart(UriPartial.Authority);

    /// <summary>tmux session name the optional attach client runs under.</summary>
    public string TmuxSessionName => _options.TmuxSessionName;

    /// <summary>Returns an immutable status snapshot; never performs I/O.</summary>
    public ControlStatus GetStatus()
    {
        lock (_gate)
        {
            return new ControlStatus
            {
                State = _state.ToWireValue(),
                OrganizationName = _options.OrganizationName,
                SessionId = _sessionId,
                Model = _model,
                Models = _models ?? [],
                Error = _error,
                StartedAt = _startedAt,
                TerminalReady = _terminalReady,
                SessionState = _sessionState,
            };
        }
    }

    /// <summary>
    /// Sends <c>session/cancel</c> for the established session. Returns true only
    /// when the notification was written to a live child; false when there is no
    /// controllable session or the child is gone. The write is bounded by a 5 second deadline
    /// linked to <paramref name="cancellationToken"/>. Cancellation does not roll
    /// back external effects; the caller maps false to an unavailable response.
    /// </summary>
    /// <remarks>
    /// This permits both <see cref="ControlState.Ready"/> and
    /// <see cref="ControlState.Degraded"/> with an owned session and live child. A degraded
    /// runtime — for example one whose bootstrap turn failed against a transient
    /// provider error — still owns a live transport, and that is precisely when
    /// an operator needs to interrupt an orphaned turn. Refusing here would make
    /// the degraded state unrecoverable. A protocol fault is a different case:
    /// it rejects new control requests as soon as faulted is published, even
    /// while asynchronous teardown is still reaping the child.
    /// </remarks>
    public async Task<bool> CancelAsync(CancellationToken cancellationToken)
    {
        if (!GetState().AllowsControl())
        {
            return false;
        }

        var session = _session;
        var sessionId = _sessionId;
        if (session is null || string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        try
        {
            if (_process is null || _process.HasExited)
            {
                return false;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(CancelDeadline);
        try
        {
            await session.NotifyAsync(
                "session/cancel",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["sessionId"] = sessionId },
                bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is AcpProtocolException or OperationCanceledException or IOException)
        {
            _logger.LogDebug("Could not send ACP session/cancel.");
            return false;
        }
    }

    /// <summary>
    /// Changes the model for the established session so it applies to
    /// subsequent provider turns. The write prefers ACP
    /// <c>session/set_config_option</c> (configId <c>model</c>, value
    /// <c>provider/model</c>) and falls back to the native
    /// <c>POST /api/session/{id}/model</c> endpoint when the agent does not
    /// expose that config option. The operation is bounded to 10 seconds and
    /// serialized against the authoritative model poll.
    /// </summary>
    /// <param name="model">A <c>provider/model</c> reference previously advertised by <see cref="GetStatus"/>.</param>
    /// <returns>
    /// True only when a write path accepted the change. False when there is no
    /// established session, the caller cancels, the deadline expires, or every
    /// write path fails — failures are never reported as success.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The reference is not a syntactically valid <c>provider/model</c>, or it
    /// is not in the advertised connected-model catalog while that catalog is
    /// available.
    /// </exception>
    public async Task<bool> SetModelAsync(string model, CancellationToken cancellationToken)
    {
        var reference = ParseModelReference(model);

        if (_session is null || string.IsNullOrEmpty(_sessionId) || !IsProcessAlive(_process))
        {
            return false;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ModelWriteDeadline);
        var token = bounded.Token;

        try
        {
            await _modelUpdateLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Model change to {Model} timed out waiting for the model lock.", reference.Reference);
            return false;
        }

        try
        {
            // The session can only be observed under the lock; re-read it so a
            // shutdown that raced the lock cannot be written to.
            var session = _session;
            var sessionId = _sessionId;
            if (session is null || string.IsNullOrEmpty(sessionId) || !IsProcessAlive(_process))
            {
                return false;
            }

            var catalog = GetModelsSnapshot();
            if (catalog is not null
                && !catalog.Any(candidate => string.Equals(candidate.Id, reference.Reference, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Model '{reference.Reference}' is not an advertised connected model.",
                    nameof(model));
            }

            var written = await TryWriteModelAsync(session, sessionId, reference, token).ConfigureAwait(false);
            if (!written)
            {
                _logger.LogWarning("Model change to {Model} was rejected by every write path.", reference.Reference);
                return false;
            }

            // Do not advertise a requested value as observed state when readback fails.
            string? confirmed = null;
            var native = _native;
            if (native is not null)
            {
                var snapshot = await native.GetSessionModelAsync(sessionId, token).ConfigureAwait(false);
                if (snapshot.Available)
                {
                    confirmed = snapshot.Model?.Reference;
                }
            }

            if (confirmed is null)
            {
                return false;
            }
            SetModel(confirmed);
            return string.Equals(confirmed, reference.Reference, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Model change to {Model} timed out.", reference.Reference);
            return false;
        }
        finally
        {
            _modelUpdateLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            SetStatus(ControlState.Disabled, null);
            _logger.LogInformation("AgentControl runtime is disabled (Control:Enabled=false); no OpenCode process launched.");
            return;
        }

        var validationErrors = _options.Validate();
        if (validationErrors.Count > 0)
        {
            var message = string.Join(" ", validationErrors);
            SetStatus(ControlState.Faulted, message);
            _logger.LogError("AgentControl runtime configuration is invalid: {Errors}", message);
            return;
        }

        SetStarted();

        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var message = DescribeFault(exception);
            _logger.LogError("AgentControl runtime faulted; see status error for detail.");
            SetStatus(ControlState.Faulted, message);
        }
        finally
        {
            SetTerminalReady(false);
            await ShutdownAsync().ConfigureAwait(false);
            if (GetState() is ControlState.Ready or ControlState.Starting or ControlState.Degraded)
            {
                SetStatus(ControlState.Stopped, GetError());
            }
        }
    }

    public override void Dispose()
    {
        try
        {
            _hostLifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _hostLifetime.Dispose();
        _modelUpdateLock.Dispose();
        base.Dispose();
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _hostLifetime.Token);
        var cancellationToken = lifetime.Token;

        PrepareDirectories();
        var state = LoadOrCreateState();
        _ownerToken = state.TmuxOwnerToken;
        PersistState(state);

        await ConnectAsync(state, cancellationToken).ConfigureAwait(false);
        await StartTerminalAsync(cancellationToken).ConfigureAwait(false);

        StartStatusPolling(cancellationToken);
        StartNotificationDrain(cancellationToken);

        SetStatus(ControlState.Ready, null);
        StartBootstrap(cancellationToken);

        await MonitorAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PrepareDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(WorkspacePath);
        Directory.CreateDirectory(HomePath);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                HomePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private PersistedRuntimeState LoadOrCreateState()
    {
        var state = RuntimeStateStore.Load(RuntimeStatePath) ?? RuntimeStateStore.CreateNew(_options.OrganizationName);
        state.OrganizationName = _options.OrganizationName;
        state.TmuxOwnerToken ??= RuntimeStateStore.NewOwnerToken();
        return state;
    }

    private void PersistState(PersistedRuntimeState state)
    {
        RuntimeStateStore.Save(RuntimeStatePath, state);
    }

    private async Task ConnectAsync(PersistedRuntimeState state, CancellationToken cancellationToken)
    {
        _password = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        _stderrBuffer.SetSecret(_password);

        var instructionsPath = Path.Combine(HomePath, AgentControlOpenCodeConfig.InstructionsFileName);
        await File.WriteAllTextAsync(
            instructionsPath,
            AgentControlOpenCodeConfig.BuildInstructions(_options.OrganizationName),
            cancellationToken).ConfigureAwait(false);

        var configContent = AgentControlOpenCodeConfig.Build(_options.Model, instructionsPath);
        var startInfo = BuildProcessStartInfo(configContent);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new AcpProtocolException("Failed to start the OpenCode ACP process.");
        }

        _process = process;
        _logger.LogInformation("Started OpenCode ACP process (pid {Pid}) for model {Model}.", process.Id, _options.Model);

        var session = new AcpRpcSession(process.StandardOutput.BaseStream, WriteToProcessAsync)
        {
            IncomingRequestHandler = HandleIncomingRequestAsync,
        };
        session.Start();
        _session = session;

        _stderrTask = Task.Run(() => DrainStderrAsync(process, cancellationToken), CancellationToken.None);
        _native = new OpenCodeNativeClient(NativeUrl, "opencode", _password, workspaceDirectory: WorkspacePath);

        await HandshakeAsync(state, session, cancellationToken).ConfigureAwait(false);
    }

    private ProcessStartInfo BuildProcessStartInfo(string configContent)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.OpenCodeExecutable,
            WorkingDirectory = WorkspacePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("acp");
        startInfo.ArgumentList.Add("--hostname");
        startInfo.ArgumentList.Add(_options.Hostname);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(_options.NativePort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--cwd");
        startInfo.ArgumentList.Add(WorkspacePath);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = HomePath,
            ["XDG_DATA_HOME"] = Path.Combine(HomePath, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(HomePath, "config"),
            ["XDG_STATE_HOME"] = Path.Combine(HomePath, "state"),
            ["XDG_CACHE_HOME"] = Path.Combine(HomePath, "cache"),
            ["OPENCODE_CONFIG_CONTENT"] = configContent,
            ["OPENCODE_SERVER_USERNAME"] = "opencode",
            ["OPENCODE_SERVER_PASSWORD"] = _password,
        };

        var environment = ChildEnvironment.Build(overrides);
        startInfo.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    private async Task HandshakeAsync(PersistedRuntimeState state, AcpRpcSession session, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.StartupTimeoutSeconds);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(timeout);
        var token = startup.Token;

        var initialize = await session.RequestAsync(
            "initialize",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["fs"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["readTextFile"] = false,
                        ["writeTextFile"] = false,
                    },
                    ["terminal"] = false,
                },
                ["clientInfo"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = "HVO.AgentControl",
                    ["version"] = ApplicationVersion.Current,
                },
            },
            timeout,
            token).ConfigureAwait(false);

        if (initialize.ValueKind != JsonValueKind.Object
            || !initialize.TryGetProperty("protocolVersion", out var protocolVersion)
            || protocolVersion.ValueKind != JsonValueKind.Number
            || !protocolVersion.TryGetInt32(out var version)
            || version != 1)
        {
            throw new AcpProtocolException("ACP initialize response must include numeric protocolVersion 1.");
        }

        string sessionId;
        bool isNew;
        if (!string.IsNullOrWhiteSpace(state.SessionId))
        {
            try
            {
                await session.RequestAsync(
                    "session/load",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["sessionId"] = state.SessionId,
                        ["cwd"] = WorkspacePath,
                        ["mcpServers"] = Array.Empty<object>(),
                    },
                    timeout,
                    token).ConfigureAwait(false);
            }
            catch (AcpRemoteException exception)
            {
                throw new AcpProtocolException(
                    $"Recorded session '{state.SessionId}' could not be loaded; refusing to create a new session silently.",
                    exception);
            }

            sessionId = state.SessionId!;
            isNew = false;
        }
        else
        {
            var created = await session.RequestAsync(
                "session/new",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cwd"] = WorkspacePath,
                    ["mcpServers"] = Array.Empty<object>(),
                },
                timeout,
                token).ConfigureAwait(false);

            sessionId = created.TryGetProperty("sessionId", out var sessionElement)
                ? sessionElement.GetString() ?? string.Empty
                : string.Empty;

            if (sessionId.Length == 0)
            {
                throw new AcpProtocolException("ACP session/new did not return a sessionId.");
            }

            isNew = true;
        }

        try
        {
            await session.RequestAsync(
                "session/set_mode",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId,
                    ["modeId"] = AgentControlOpenCodeConfig.RoleName,
                },
                timeout,
                token).ConfigureAwait(false);
        }
        catch (AcpRemoteException exception)
        {
            throw new AcpProtocolException(
                $"Could not activate the '{AgentControlOpenCodeConfig.RoleName}' control role: {exception.Message}",
                exception);
        }

        _sessionId = sessionId;
        _isNewSession = isNew;

        var title = $"{_options.OrganizationName} AgentControl";
        var titled = await _native!.TrySetSessionTitleAsync(sessionId, title, token).ConfigureAwait(false);

        state.SessionId = sessionId;
        if (titled)
        {
            state.SessionTitle = title;
        }

        // Persist identity before the bootstrap prompt so a crash cannot re-create
        // the session or lose the organization/session mapping.
        PersistState(state);
        SetSession(sessionId);

        _logger.LogInformation(
            "OpenCode ACP {Mode} session {SessionId} established.",
            isNew ? "created" : "loaded",
            sessionId);
    }

    private async Task StartTerminalAsync(CancellationToken cancellationToken)
    {
        _terminal ??= new TmuxAttachLauncher(_options.TmuxSessionName);

        var request = new TmuxAttachRequest(
            NativeUrl,
            WorkspacePath,
            HomePath,
            _sessionId ?? string.Empty,
            "opencode",
            _password,
            _ownerToken ?? string.Empty,
            _options.EnableTerminal,
            _options.OpenCodeExecutable);

        var result = await _terminal.EnsureAsync(request, cancellationToken).ConfigureAwait(false);
        SetTerminalReady(result.Started && result.Owned);

        if (result.Error is not null && !string.Equals(_terminalError, result.Error, StringComparison.Ordinal))
        {
            _logger.LogWarning("tmux attach client not ready: {Error}", result.Error);
        }
        _terminalError = result.Error;
    }

    private void StartStatusPolling(CancellationToken cancellationToken)
    {
        _statusPollerTask = Task.Run(
            async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? value = null;
                    try
                    {
                        var sessionId = _sessionId;
                        var native = _native;
                        if (native is not null && !string.IsNullOrEmpty(sessionId))
                        {
                            value = await native.GetSessionStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        _logger.LogDebug("Native session status poll failed.");
                    }

                    // A failed or sparse poll is reported as unknown, never as a
                    // stale previous state.
                    SetSessionState(value);

                    try
                    {
                        await RefreshModelsAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        _logger.LogDebug("Native model poll failed.");
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_options.SessionStatePollSeconds), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Reads the authoritative session model and, when not yet known, the
    /// connected-provider catalog. Runs under <see cref="_modelUpdateLock"/> so
    /// it cannot interleave with <see cref="SetModelAsync"/>. A read that is
    /// unavailable leaves the last observed model intact; a successful read
    /// that reports no model sets it to unknown.
    /// </summary>
    private async Task RefreshModelsAsync(CancellationToken cancellationToken)
    {
        var native = _native;
        var sessionId = _sessionId;
        if (native is null || string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        await _modelUpdateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await native.GetSessionModelAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Available)
            {
                SetModel(snapshot.Model?.Reference);
            }

            if (GetModelsSnapshot() is null)
            {
                var catalog = await native.GetConnectedModelCatalogAsync(cancellationToken).ConfigureAwait(false);
                if (catalog is not null)
                {
                    SetModels(catalog);
                }
            }
        }
        finally
        {
            _modelUpdateLock.Release();
        }
    }

    private void StartNotificationDrain(CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _notificationTask = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var parameters in session.Notifications.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        LogSessionUpdate(parameters);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                    _logger.LogDebug("ACP notification drain ended unexpectedly.");
                }
            },
            CancellationToken.None);
    }

    private void LogSessionUpdate(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("update", out var update)
            || update.ValueKind != JsonValueKind.Object
            || !update.TryGetProperty("sessionUpdate", out var kindElement))
        {
            return;
        }

        var kind = kindElement.GetString();
        switch (kind)
        {
            case "tool_call":
            case "tool_call_update":
                // Tool titles can echo user/model content, so only the kind and
                // status (never the title or arguments) are logged.
                var status = update.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
                _logger.LogDebug("ACP tool update {Kind} status={Status}", kind, status);
                break;
            case "agent_thought_chunk":
            case "agent_message_chunk":
            case "user_message_chunk":
                // Reasoning/message text is intentionally never logged or retained.
                break;
            case "config_option_update":
                // The model poll reads the native session and is authoritative;
                // the event is not applied to state so a delayed notification
                // cannot overwrite a newer selection.
                _logger.LogDebug("ACP config option update received; native poll remains authoritative.");
                break;
            default:
                _logger.LogDebug("ACP update {Kind} received.", kind);
                break;
        }
    }

    private void StartBootstrap(CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null || string.IsNullOrEmpty(_sessionId))
        {
            return;
        }

        if (!_isNewSession)
        {
            _logger.LogInformation("Loaded session {SessionId}; bootstrap prompt skipped.", _sessionId);
            return;
        }

        var sessionId = _sessionId;
        var prompt = $"Bootstrap check for the {_options.OrganizationName} AgentControl runtime. " +
                     "Reply with one brief readiness line. Do not call tools and do not perform code work.";

        _bootstrapTask = Task.Run(
            async () =>
            {
                try
                {
                    SetSessionState("busy");
                    var result = await session.RequestAsync(
                        "session/prompt",
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["sessionId"] = sessionId,
                            ["prompt"] = new object[]
                            {
                                new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["type"] = "text",
                                    ["text"] = prompt,
                                },
                            },
                        },
                        TimeSpan.FromSeconds(_options.PromptTimeoutSeconds),
                        cancellationToken).ConfigureAwait(false);

                    var stopReason = result.TryGetProperty("stopReason", out var stopElement) ? stopElement.GetString() : null;
                    SetSessionState("idle");
                    if (!string.Equals(stopReason, "end_turn", StringComparison.Ordinal))
                    {
                        // A turn that did not end normally must not leave the host
                        // reporting ready with a stale busy session.
                        _logger.LogWarning("Bootstrap prompt stopped with reason {StopReason}.", stopReason ?? "unknown");
                        SetStatus(
                            ControlState.Degraded,
                            $"Bootstrap prompt stop reason was '{stopReason ?? "unknown"}'.");
                    }
                    else
                    {
                        _logger.LogInformation("Bootstrap prompt completed.");
                    }
                }
                catch (OperationCanceledException)
                {
                    SetSessionState(null);
                }
                catch (AcpRemoteException exception)
                {
                    SetSessionState(null);
                    _logger.LogError("Bootstrap prompt failed with ACP error code {Code}.", exception.Code);
                    if (exception.Code is -32601 or -32602)
                    {
                        Fault($"Bootstrap prompt reported a protocol/schema fault (code {exception.Code}).");
                    }
                    else
                    {
                        SetStatus(ControlState.Degraded, $"Bootstrap prompt failed (code {exception.Code}).");
                    }
                }
                catch (TimeoutException)
                {
                    // The deadline abandons the local correlation only; the agent
                    // may still be mid-turn. Reconcile the effect we know about
                    // instead of leaving an orphaned turn running against the
                    // owned session. This is best effort and never upgrades or
                    // downgrades the honest Degraded outcome below.
                    SetSessionState(null);
                    _logger.LogError(
                        "Bootstrap prompt timed out after {Seconds}s; requesting cancellation of the abandoned turn.",
                        _options.PromptTimeoutSeconds);
                    await TryCancelAbandonedTurnAsync().ConfigureAwait(false);
                    SetStatus(
                        ControlState.Degraded,
                        $"Bootstrap prompt timed out after {_options.PromptTimeoutSeconds}s; the turn may still have run.");
                }
                catch (Exception exception)
                {
                    SetSessionState(null);
                    _logger.LogError("Bootstrap prompt failed; see status error for detail.");
                    SetStatus(ControlState.Degraded, $"Bootstrap prompt failed: {_stderrBuffer.Redact(exception.Message)}");
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Best-effort <c>session/cancel</c> for a turn this host stopped waiting on.
    /// Bounded by <see cref="CancelDeadline"/> and independent of the host
    /// lifetime token, because the reconcile runs exactly when the prompt's own
    /// deadline has already expired. The result is intentionally not promoted
    /// into status: a receipt is not proof the turn stopped.
    /// </summary>
    private async Task TryCancelAbandonedTurnAsync()
    {
        try
        {
            if (!await CancelAsync(CancellationToken.None).ConfigureAwait(false))
            {
                _logger.LogDebug("Abandoned bootstrap turn could not be cancelled; the session may be gone.");
            }
        }
        catch (Exception exception) when (exception is AcpProtocolException or OperationCanceledException or IOException)
        {
            _logger.LogDebug("Cancellation of the abandoned bootstrap turn failed.");
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new AcpProtocolException("OpenCode process was not started.");
        var session = _session ?? throw new AcpProtocolException("ACP session was not started.");

        var processExit = process.WaitForExitAsync(cancellationToken);
        var sessionCompletion = session.Completion;
        var hostLifetime = Task.Delay(Timeout.Infinite, cancellationToken);

        Task completed;
        while (true)
        {
            var terminalTick = Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            completed = await Task.WhenAny(processExit, sessionCompletion, hostLifetime, terminalTick).ConfigureAwait(false);
            if (completed != terminalTick || cancellationToken.IsCancellationRequested)
            {
                break;
            }
            if (_options.EnableTerminal)
            {
                // Terminal recovery is independent of the native ACP process/session.
                await StartTerminalAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (completed == processExit)
        {
            throw new AcpProtocolException(
                $"OpenCode ACP process exited unexpectedly with code {process.ExitCode}. {_stderrBuffer.SnapshotText()}");
        }

        await sessionCompletion.ConfigureAwait(false);
        throw new AcpProtocolException("ACP transport closed unexpectedly.");
    }

    private Task<AcpResponse> HandleIncomingRequestAsync(AcpEnvelope request, CancellationToken cancellationToken)
    {
        if (string.Equals(request.Method, "session/request_permission", StringComparison.Ordinal))
        {
            _logger.LogInformation("Rejected inbound ACP permission request.");
            return Task.FromResult(AcpResponse.Ok(PermissionPolicy.BuildRejection(request.Params)));
        }

        _logger.LogDebug("Unhandled ACP request {Method}; responding method-not-found.", request.Method);
        return Task.FromResult(AcpResponse.Error(-32601, $"Method not supported: {request.Method}"));
    }

    private async ValueTask WriteToProcessAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            throw new AcpSessionClosedException("OpenCode ACP process is not available for writing.");
        }

        await process.StandardInput.BaseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        var reader = process.StandardError;
        var chunk = new char[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var text = new string(chunk, 0, read);
                _stderrBuffer.Append(text);
                _logger.LogDebug("opencode stderr: {Line}", _stderrBuffer.Redact(text.TrimEnd()));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            _stderrBuffer.Flush();
        }
    }

    private async Task ShutdownAsync()
    {
        var grace = TimeSpan.FromSeconds(Math.Max(0, _options.ShutdownGraceSeconds));
        using var shutdown = new CancellationTokenSource(grace > TimeSpan.Zero ? grace : TimeSpan.FromMilliseconds(1));
        var shutdownToken = shutdown.Token;

        SetTerminalReady(false);
        await CancelAsync(shutdownToken).ConfigureAwait(false);

        if (_bootstrapTask is not null)
        {
            try
            {
                await _bootstrapTask.WaitAsync(shutdownToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or AcpProtocolException)
            {
            }
        }

        _session?.RequestStop();

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(shutdownToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
            }
        }

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        if (_terminal is not null && _ownerToken is not null)
        {
            try
            {
                await _terminal.KillOwnedAsync(_ownerToken, shutdownToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
            {
            }
        }

        // Observe (but do not wait indefinitely for) the background drains. The
        // bounded waits keep shutdown deterministic without masking a stuck pipe.
        await AwaitQuietlyAsync(_notificationTask, grace).ConfigureAwait(false);
        await AwaitQuietlyAsync(_statusPollerTask, grace).ConfigureAwait(false);
        await AwaitQuietlyAsync(_stderrTask, grace).ConfigureAwait(false);
        _notificationTask = null;
        _statusPollerTask = null;
        _stderrTask = null;

        _process?.Dispose();
        _process = null;
        _native?.Dispose();
        _native = null;
    }

    private static async Task AwaitQuietlyAsync(Task? task, TimeSpan timeout)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or AcpProtocolException)
        {
        }
    }

    private void SetStarted()
    {
        lock (_gate)
        {
            _startedAt = DateTimeOffset.UtcNow;
            _state = ControlState.Starting;
            _error = null;
        }
    }

    private void SetStatus(ControlState state, string? error)
    {
        lock (_gate)
        {
            _state = state;
            _error = error is null ? null : _stderrBuffer.Redact(error);
            if (state is ControlState.Disabled or ControlState.Faulted or ControlState.Stopped)
            {
                _terminalReady = false;
            }
        }
    }

    private void SetSession(string sessionId)
    {
        lock (_gate)
        {
            _sessionId = sessionId;
        }
    }

    private void SetTerminalReady(bool ready)
    {
        lock (_gate)
        {
            _terminalReady = ready;
        }
    }

    private void SetSessionState(string? sessionState)
    {
        lock (_gate)
        {
            _sessionState = sessionState;
        }
    }

    private void SetModel(string? model)
    {
        lock (_gate)
        {
            _model = string.IsNullOrWhiteSpace(model) ? "unknown" : model;
        }
    }

    private void SetModels(IReadOnlyList<ControlModel> models)
    {
        lock (_gate)
        {
            _models = models;
        }
    }

    private IReadOnlyList<ControlModel>? GetModelsSnapshot()
    {
        lock (_gate)
        {
            return _models;
        }
    }

    /// <summary>
    /// Updates ACP's selection and then the persisted native session selection.
    /// These are separate stores in OpenCode 1.18.30; neither controls the attached
    /// TUI picker. The public endpoint is gated until TUI synchronization exists.
    /// </summary>
    private async Task<bool> TryWriteModelAsync(
        AcpRpcSession session,
        string sessionId,
        NativeModelReference reference,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.RequestAsync(
                "session/set_config_option",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId,
                    ["configId"] = "model",
                    ["value"] = reference.Reference,
                },
                ModelWriteDeadline,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcpRemoteException exception) when (exception.Code is -32601 or -32602)
        {
            _logger.LogDebug("ACP session/set_config_option unavailable (code {Code}); using native HTTP.", exception.Code);
        }
        catch (AcpRemoteException exception)
        {
            _logger.LogWarning("ACP model change rejected with code {Code}.", exception.Code);
            return false;
        }
        catch (Exception exception) when (exception is AcpProtocolException or IOException or TimeoutException)
        {
            _logger.LogWarning("ACP model change transport failed; not retrying on the native API.");
            return false;
        }

        var native = _native;
        return native is not null
            && await native.TrySetSessionModelAsync(sessionId, reference, cancellationToken).ConfigureAwait(false);
    }

    private static NativeModelReference ParseModelReference(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model reference is required.", nameof(model));
        }

        var trimmed = model.Trim();
        var separator = trimmed.IndexOf('/');
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            throw new ArgumentException("A model reference must be in 'provider/model' form.", nameof(model));
        }

        var provider = trimmed[..separator];
        var modelId = trimmed[(separator + 1)..];
        if (provider.Length == 0 || modelId.Length == 0)
        {
            throw new ArgumentException("A model reference must be in 'provider/model' form.", nameof(model));
        }

        return new NativeModelReference(provider, modelId, Variant: null);
    }

    private static bool IsProcessAlive(Process? process)
    {
        if (process is null)
        {
            return false;
        }

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void Fault(string message)
    {
        SetStatus(ControlState.Faulted, message);
        try
        {
            _hostLifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private ControlState GetState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    private string? GetError()
    {
        lock (_gate)
        {
            return _error;
        }
    }

    private string DescribeFault(Exception exception)
    {
        var message = _stderrBuffer.Redact(exception.Message);
        var stderr = _stderrBuffer.SnapshotText();
        return stderr.Length == 0 ? message : $"{message} | stderr: {stderr}";
    }

    private string WorkspacePath => Path.Combine(DataDirectory, "workspace");

    private string HomePath => Path.Combine(DataDirectory, "home");

    private string RuntimeStatePath => Path.Combine(DataDirectory, "runtime.json");
}
