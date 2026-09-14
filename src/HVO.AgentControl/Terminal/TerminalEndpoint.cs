using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Runtime;

namespace HVO.AgentControl.Terminal;

/// <summary>
/// Owner-facing websocket endpoint that attaches a single browser viewer to the
/// runtime's <c>agentcontrol</c> tmux session through the Python PTY bridge.
/// <para>
/// The parent host owns authentication and the <c>TerminalReady</c> gate. This
/// type only validates the websocket upgrade/origin, enforces one viewer at a
/// time, and pumps frames. A browser disconnect tears down the bridge and its
/// own <c>tmux attach</c> client; the tmux server, TUI and ACP runtime are left
/// running.
/// </para>
/// </summary>
public static class TerminalEndpoint
{
    private const string BridgeDirectory = "Terminal";
    private const string BridgeFileName = "pty_bridge.py";
    private const string PythonExecutable = "python3";
    private const string TerminalType = "xterm-256color";
    private const string ColorTerm = "truecolor";
    private const string DefaultLocale = "C.UTF-8";

    private const int BridgeReadBufferBytes = 16 * 1024;
    private const int MaxBridgeLineBytes = 512 * 1024;
    private const int CloseGraceSeconds = 2;

    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim ViewerSlot = new(1, 1);

    /// <summary>
    /// Handles one <c>/terminal</c> websocket session. <paramref name="homeDirectory"/>
    /// is supplied by the host (the runtime data directory) and becomes the bridge
    /// child's <c>HOME</c> so tmux, the TUI and ACP share one runtime home.
    /// <paramref name="sessionName"/> is the host-configured tmux session to attach.
    /// </summary>
    public static async Task HandleAsync(
        HttpContext context,
        string homeDirectory,
        string sessionName = "agentcontrol",
        AgentProcessLauncher? agentLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        agentLauncher ??= AgentProcessLauncher.Direct;

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!TerminalProtocol.IsSameOrigin(
                context.Request.Headers.Origin.ToString(),
                context.Request.Scheme,
                context.Request.Host.Value))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // The host owns the runtime-ready gate; the terminal additionally fails
        // closed when the bridge script or its configured target is unavailable.
        var bridgePath = Path.Combine(AppContext.BaseDirectory, BridgeDirectory, BridgeFileName);
        if (string.IsNullOrWhiteSpace(homeDirectory)
            || !TerminalProtocol.IsValidSessionName(sessionName)
            || !File.Exists(bridgePath))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (!ViewerSlot.Wait(0))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("HVO.AgentControl.Terminal");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        WebSocket? socket = null;
        Process? process = null;
        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeDescription = "Terminal detached.";

        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            process = StartBridge(homeDirectory, bridgePath, sessionName, agentLauncher);

            var output = PumpOutputAsync(socket, process.StandardOutput.BaseStream, lifetime.Token);
            var input = PumpInputAsync(socket, process.StandardInput, lifetime.Token);

            var first = await Task.WhenAny(output, input).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);

            // No sends happen after this point except the single close in finally.
            var failure = await ObserveAsync(first).ConfigureAwait(false);
            await ObserveAsync(output).ConfigureAwait(false);
            await ObserveAsync(input).ConfigureAwait(false);

            if (failure is TerminalMessageException protocol)
            {
                closeStatus = protocol.Status;
                closeDescription = protocol.Message;
            }
            else if (failure is not null)
            {
                logger?.LogInformation("Terminal session ended ({Category}).", failure.GetType().Name);
            }
        }
        catch (TerminalMessageException protocol)
        {
            closeStatus = protocol.Status;
            closeDescription = protocol.Message;
        }
        catch (OperationCanceledException)
        {
            // Browser disconnected or host cancelled; cleanup runs in finally.
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or WebSocketException)
        {
            logger?.LogWarning("Terminal session failed ({Category}).", ex.GetType().Name);
            closeStatus = WebSocketCloseStatus.InternalServerError;
            closeDescription = "Terminal session unavailable.";
            if (socket is not null)
            {
                using var send = new CancellationTokenSource(TimeSpan.FromSeconds(CloseGraceSeconds));
                await TrySendErrorAsync(socket, closeDescription, send.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (process is not null)
            {
                await StopBridgeAsync(process, agentLauncher, logger).ConfigureAwait(false);
            }

            if (socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(CloseGraceSeconds));
                try
                {
                    await socket.CloseOutputAsync(closeStatus, closeDescription, close.Token).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
                catch (OperationCanceledException)
                {
                }
            }

            ViewerSlot.Release();
        }
    }

    private static Process StartBridge(
        string homeDirectory,
        string bridgePath,
        string sessionName,
        AgentProcessLauncher agentLauncher)
    {
        var startInfo = CreateBridgeStartInfo(homeDirectory, bridgePath, sessionName, agentLauncher: agentLauncher);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The terminal bridge process did not start.");
        }

        _ = DrainStandardErrorAsync(process.StandardError.BaseStream);
        return process;
    }

    /// <summary>
    /// Builds the bridge child's start info with the shared credential-stripped
    /// environment (<see cref="ChildEnvironment.Build"/>) plus the terminal
    /// values the PTY bridge and tmux require. <paramref name="bridgePath"/> and
    /// <paramref name="sessionName"/> are passed through the argument list, never
    /// a shell. Exposed internally so the environment contract can be asserted
    /// without spawning a process.
    /// </summary>
    internal static ProcessStartInfo CreateBridgeStartInfo(
        string homeDirectory,
        string bridgePath,
        string sessionName,
        IReadOnlyDictionary<string, string?>? baseEnvironment = null,
        AgentProcessLauncher? agentLauncher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);

        var startInfo = new ProcessStartInfo
        {
            FileName = PythonExecutable,
            WorkingDirectory = homeDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Arguments are passed as a list; no shell parses the bridge path or target.
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(bridgePath);
        startInfo.ArgumentList.Add(sessionName);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = homeDirectory,
            ["TERM"] = TerminalType,
            ["COLORTERM"] = ColorTerm,
            ["LANG"] = DefaultLocale,
        };

        // Rebuild rather than mutate the inherited process block: the parent
        // environment carries host credentials and tmux state that must not
        // reach the bridge or its tmux attach client.
        var environment = ChildEnvironment.Build(overrides, baseEnvironment);
        environment.Remove("TMUX");
        environment.Remove("TMUX_PANE");

        startInfo.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        // The bridge and its tmux attach client belong to the agent identity, so
        // the launcher (when configured) replaces the interpreter invocation with
        // its fixed python/pty_bridge mapping.
        return (agentLauncher ?? AgentProcessLauncher.Direct).WrapPty(startInfo, homeDirectory, sessionName);
    }

    private static async Task PumpOutputAsync(WebSocket socket, Stream output, CancellationToken token)
    {
        var buffer = new byte[BridgeReadBufferBytes];
        using var line = new MemoryStream(4096);
        while (!token.IsCancellationRequested)
        {
            var read = await output.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    await SendBridgeLineAsync(socket, line, token).ConfigureAwait(false);
                    line.SetLength(0);
                }
                else if (value != (byte)'\r')
                {
                    line.WriteByte(value);
                    if (line.Length > MaxBridgeLineBytes)
                    {
                        throw new TerminalMessageException(WebSocketCloseStatus.MessageTooBig, "Terminal output exceeds the limit.");
                    }
                }
            }
        }
    }

    private static async Task SendBridgeLineAsync(WebSocket socket, MemoryStream line, CancellationToken token)
    {
        if (line.Length == 0)
        {
            return;
        }

        var length = (int)line.Length;
        var bytes = line.GetBuffer();
        string json;
        try
        {
            json = Encoding.UTF8.GetString(bytes, 0, length);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (!IsForwardableBridgeFrame(json))
        {
            return;
        }

        await socket.SendAsync(bytes.AsMemory(0, length), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    }

    private static bool IsForwardableBridgeFrame(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() is "output" or "error";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task PumpInputAsync(WebSocket socket, StreamWriter input, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var message = await ReceiveTextAsync(socket, TerminalProtocol.MaxClientMessageBytes, token).ConfigureAwait(false);
            if (message is null)
            {
                break;
            }

            if (!TerminalProtocol.TryParseClientFrame(message, out var frame))
            {
                throw new TerminalMessageException(WebSocketCloseStatus.PolicyViolation, "Unsupported terminal message.");
            }

            var command = frame.Kind switch
            {
                TerminalFrameKind.Input => JsonSerializer.Serialize(new { type = "input", data = frame.Data }),
                TerminalFrameKind.Resize => JsonSerializer.Serialize(new { type = "resize", cols = frame.Columns, rows = frame.Rows }),
                _ => throw new TerminalMessageException(WebSocketCloseStatus.PolicyViolation, "Unsupported terminal message."),
            };

            await input.WriteLineAsync(command.AsMemory(), token).ConfigureAwait(false);
            await input.FlushAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, int maxBytes, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var accumulator = new MemoryStream(8192);
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                throw new TerminalMessageException(WebSocketCloseStatus.InvalidPayloadData, "Binary terminal messages are not supported.");
            }

            if (accumulator.Length + result.Count > maxBytes)
            {
                throw new TerminalMessageException(WebSocketCloseStatus.MessageTooBig, "Terminal message exceeds the limit.");
            }

            accumulator.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(accumulator.GetBuffer(), 0, (int)accumulator.Length);
            }
        }
    }

    private static async Task StopBridgeAsync(Process process, AgentProcessLauncher agentLauncher, ILogger? logger)
    {
        try
        {
            try
            {
                if (!process.HasExited)
                {
                    // Closing stdin signals graceful bridge EOF cleanup.
                    process.StandardInput.Close();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
            }

            using var grace = new CancellationTokenSource(StopGrace);
            try
            {
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cross-UID: an isolated bridge runs as the agent identity, so
                // termination goes through the verified launcher signal.
                agentLauncher.TryTerminate(process, force: true);

                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never log bridge stderr or terminal content; category only.
            logger?.LogDebug("Terminal bridge cleanup did not complete ({Category}).", ex.GetType().Name);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DrainStandardErrorAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Discarded on purpose: stderr may contain bridge diagnostics.
        }
    }

    private static async Task TrySendErrorAsync(WebSocket socket, string message, CancellationToken token)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "error", message }));
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ex;
        }
    }

    private sealed class TerminalMessageException(WebSocketCloseStatus status, string description) : Exception(description)
    {
        public WebSocketCloseStatus Status { get; } = status;
    }
}
