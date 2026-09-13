using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using HVO.AgentControl.Ssh;
using Microsoft.AspNetCore.Antiforgery;
using Renci.SshNet;

namespace HVO.AgentControl.Services;

public sealed class TerminalService(ControlStore store, Secrets secrets, ILogger<TerminalService> logger)
{
    private readonly SemaphoreSlim capacity = new(4);

    public async Task Connect(HttpContext context, string id, IAntiforgery antiforgery)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (!Uri.TryCreate(context.Request.Headers.Origin, UriKind.Absolute, out var origin) ||
            origin.GetLeftPart(UriPartial.Authority) != context.Request.Scheme + "://" + context.Request.Host)
        { context.Response.StatusCode = 403; return; }
        var runtime = await store.Read(async db => await db.Runtimes.FindAsync(id)) ?? throw new ControlException("Runtime not found.", 404);
        if (runtime.ConnectionKind == RuntimeConnections.ManagedDraft) throw new ControlException("Managed runtime enrollment is pending; terminals are unavailable until transport ownership is verified.");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var expires = long.TryParse(context.User.FindFirst("hvo:expires")?.Value, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) - DateTimeOffset.UtcNow : TimeSpan.Zero;
        if (expires <= TimeSpan.Zero) { socket.Abort(); return; }
        lifetime.CancelAfter(expires);
        var token = lifetime.Token;
        var acquired = false;
        var opened = false;
        var leased = false;
        var terminalId = Guid.NewGuid().ToString("N");
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                using var hello = JsonDocument.Parse(await Receive(socket, handshake.Token));
                context.Request.Headers["X-CSRF-TOKEN"] = hello.RootElement.GetProperty("token").GetString();
                await antiforgery.ValidateRequestAsync(context);
            }
            if (!await capacity.WaitAsync(0, token)) throw new ControlException("Four terminals are already open. Close one before opening another.");
            acquired = true;
            runtime = await store.AcquireTerminalRuntime(id, token); leased = true;
            using var key = runtime.Authentication == "privateKey" ? new PrivateKeyFile(
                new MemoryStream(Encoding.UTF8.GetBytes(secrets.Read(runtime.CredentialReference))),
                runtime.PassphraseReference is { Length: > 0 } phrase ? secrets.Read(phrase) : null) : null;
            AuthenticationMethod auth = key is null ? new PasswordAuthenticationMethod(runtime.Username, secrets.Read(runtime.CredentialReference))
                : new PrivateKeyAuthenticationMethod(runtime.Username, key);
            var info = new Renci.SshNet.ConnectionInfo(runtime.Host, runtime.Port, runtime.Username, auth) { Timeout = TimeSpan.FromSeconds(15) };
            var algorithm = info.HostKeyAlgorithms[runtime.HostKeyAlgorithm];
            info.HostKeyAlgorithms.Clear(); info.HostKeyAlgorithms.Add(runtime.HostKeyAlgorithm, algorithm);
            using var ssh = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(10) };
            SshRuntimeTransportFactory.AttachHostKey(ssh, runtime.HostKeySha256);
            await ssh.ConnectAsync(token);
            using var shell = ssh.CreateShellStream("xterm-256color", 100, 30, 0, 0, 16384);
            if (runtime.InstalledExecutable.Length > 0)
            {
                ControlStore.ValidatePath(runtime.InstalledExecutable);
                var directory = runtime.InstalledExecutable[..Math.Max(1, runtime.InstalledExecutable.LastIndexOf('/'))];
                var prepare = "export PATH=" + BootstrapScript.Quote(directory) + ":\"$PATH\"; exec \"${SHELL:-/bin/sh}\" -i";
                shell.WriteLine("exec /bin/sh -c " + BootstrapScript.Quote(prepare)); shell.Flush();
            }
            await Audit("TerminalOpened"); opened = true;
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"status\":\"Terminal connected.\"}"), WebSocketMessageType.Text, true, token);
            var closed = 0;
            shell.Closed += (_, _) => Interlocked.Exchange(ref closed, 1);
            shell.ErrorOccurred += (_, _) => lifetime.Cancel();
            shell.DataReceived += (_, _) => { if (shell.Length > 1024 * 1024) lifetime.Cancel(); };
            var output = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                while (!token.IsCancellationRequested)
                {
                    if (shell.DataAvailable)
                    {
                        var read = shell.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;
                        await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, token);
                    }
                    else if (Volatile.Read(ref closed) != 0 || !ssh.IsConnected) break;
                    else await Task.Delay(20, token);
                }
            }, token);
            var input = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    idle.CancelAfter(TimeSpan.FromSeconds(90));
                    using var message = JsonDocument.Parse(await Receive(socket, idle.Token));
                    var root = message.RootElement;
                    switch (root.GetProperty("type").GetString())
                    {
                        case "input":
                            var data = Encoding.UTF8.GetBytes(root.GetProperty("data").GetString() ?? "");
                            shell.Write(data, 0, data.Length); shell.Flush(); break;
                        case "resize":
                            shell.ChangeWindowSize((uint)Math.Clamp(root.GetProperty("cols").GetInt32(), 20, 300),
                                (uint)Math.Clamp(root.GetProperty("rows").GetInt32(), 5, 100), 0, 0); break;
                        case "ping": break;
                        default: throw new ControlException("Unsupported terminal message.");
                    }
                }
            }, token);
            await Task.WhenAny(input, output);
            await lifetime.CancelAsync();
            try { await Task.WhenAll(input, output); } catch (OperationCanceledException) { }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogInformation("Terminal {TerminalId} closed ({Category})", terminalId, ex.GetType().Name);
            // Never log shell contents, credentials or remote exception details.
        }
        finally
        {
            if (leased) await store.ReleaseTerminalRuntime(id);
            if (acquired) capacity.Release();
            if (opened) await Audit("TerminalClosed");
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Shell closed. Reopen to start a new shell.", close.Token); }
                catch (WebSocketException) { }
                catch (OperationCanceledException) { }
            }
        }

        Task<bool> Audit(string type) => store.Write(db =>
        { ControlStore.Event(db, type, id, payload: new { terminalId }, provenance: "user"); return Task.FromResult(true); });
    }

    private static async Task<string> Receive(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16384]; var length = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), token);
            if (result.MessageType != WebSocketMessageType.Text) throw new ControlException("Terminal connection closed or invalid message.");
            length += result.Count;
            if (result.EndOfMessage) return Encoding.UTF8.GetString(buffer, 0, length);
            if (length == buffer.Length) throw new ControlException("Terminal input exceeds the message limit.");
        }
    }
}
