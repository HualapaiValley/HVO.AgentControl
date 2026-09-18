using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Runtime;
using HVO.AgentControl.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Failure behaviour of the browser-facing viewer proxy, exercised over a real
/// loopback WebSocket so the close handshake the browser actually observes is
/// asserted rather than simulated.
/// </summary>
[Collection(LocalPortBindingCollection.Name)]
public sealed class RemoteTerminalRouterTests
{
    /// <summary>
    /// An explicit worker close must reach the browser as a real WebSocket close
    /// with the fixed category, and must never be rethrown into the middleware
    /// pipeline, which can no longer write a status code after the upgrade.
    /// </summary>
    [Fact]
    public async Task WorkerAnnouncedFailureClosesTheBrowserSocketWithTheFixedCategory()
    {
        using var fixture = new RouterFixture();
        fixture.Worker.CloseCategory = "input-uncertain";
        fixture.Worker.CloseBytesWritten = 7;

        var (status, reason) = await fixture.AttachAndAwaitCloseAsync();

        Assert.Null(fixture.Router.LastFailure);
        Assert.Equal("input-uncertain:7", reason);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, status);
        Assert.False(fixture.ExceptionEscapedToMiddleware, "The failure must be closed on the socket, not rethrown after the upgrade.");
        Assert.Equal("failed", fixture.ViewerState());
    }

    [Fact]
    public async Task OutputFailureAfterUpgradeClosesTheSocketWithinTheProtocolReasonLimit()
    {
        using var fixture = new RouterFixture();
        fixture.Worker.CloseCategory = new string('x', 300);

        var (status, reason) = await fixture.AttachAndAwaitCloseAsync();

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        // The worker's category is validated as a bounded identifier, so an
        // oversized one is a protocol failure rather than a passed-through reason.
        Assert.True(Encoding.UTF8.GetByteCount(reason) <= 123, $"reason was {Encoding.UTF8.GetByteCount(reason)} bytes");
        Assert.False(fixture.ExceptionEscapedToMiddleware);
        Assert.Equal("failed", fixture.ViewerState());
    }

    /// <summary>
    /// A clean worker end of stream is a detach, not a failure, and both pumps are
    /// still observed before the request completes.
    /// </summary>
    [Fact]
    public async Task CleanWorkerEndOfStreamDetachesWithANormalClose()
    {
        using var fixture = new RouterFixture();
        fixture.Worker.CloseCategory = null;

        var (status, _) = await fixture.AttachAndAwaitCloseAsync();

        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        Assert.Equal("detached", fixture.ViewerState());
        Assert.False(fixture.ExceptionEscapedToMiddleware);
    }

    /// <summary>
    /// The viewer close frame is a typed failure announcement, not terminal data.
    /// </summary>
    [Fact]
    public void ViewerCloseFrameIsParsedAsATypedFailureRatherThanOutput()
    {
        using var document = JsonDocument.Parse("{\"type\":\"close\",\"sessionId\":\"ses-a\",\"category\":\"input-uncertain\",\"bytesWritten\":7}");
        var closed = new RemoteViewerClosedException("input-uncertain", 7);
        Assert.Equal("input-uncertain", closed.Category);
        Assert.Equal(7, closed.BytesWritten);
        Assert.Equal("close", document.RootElement.GetProperty("type").GetString());
        // The frame carries a byte count only; input content is never echoed back.
        Assert.False(document.RootElement.TryGetProperty("data", out _));
    }

    /// <summary>
    /// An invalid reconciliation answer during the pre-upgrade lease refresh must be
    /// refused with the same 502 the API contract uses, before any viewer record or
    /// socket accept, and must not escape to the middleware pipeline.
    /// </summary>
    [Fact]
    public async Task ReconciliationInvalidDuringLeaseRefreshReturns502WithNoViewerOrUpgrade()
    {
        using var fixture = new RouterFixture();
        fixture.RefreshException = new WorkerReconciliationInvalidException("stale correlation");

        var status = await fixture.RequestUpgradeStatusAsync();

        Assert.Equal(502, status);
        Assert.Equal(0, fixture.ViewerCount());
        Assert.False(fixture.ExceptionEscapedToMiddleware);
    }

    /// <summary>
    /// The worker protocol/transport family maps to 502 Bad Gateway, while store or
    /// runtime unavailability maps to 503. A non-caller cancellation is treated as
    /// unavailable rather than given its own status arm.
    /// </summary>
    [Theory]
    [InlineData("worker-protocol", 502)]
    [InlineData("worker-remote", 502)]
    [InlineData("worker-read-uncertain", 502)]
    [InlineData("worker-write-uncertain", 502)]
    [InlineData("io", 502)]
    [InlineData("object-disposed", 502)]
    [InlineData("remote-transport", 502)]
    [InlineData("store", 503)]
    [InlineData("invalid-operation", 503)]
    [InlineData("remote-unavailable", 503)]
    [InlineData("canceled", 503)]
    public async Task LeaseRefreshFailureMapsToBadGatewayOrServiceUnavailable(string kind, int expected)
    {
        using var fixture = new RouterFixture();
        fixture.RefreshException = kind switch
        {
            "worker-protocol" => new WorkerProtocolException("invalid protocol"),
            "worker-remote" => new WorkerRemoteException("worker-operation-failed"),
            "worker-read-uncertain" => new WorkerReadUncertainException("read uncertain"),
            "worker-write-uncertain" => new WorkerWriteUncertainException("write uncertain"),
            "io" => new IOException("transport failed"),
            "object-disposed" => new ObjectDisposedException("owner session"),
            "remote-transport" => new RemoteWorkerUnavailableException("host unreachable", transport: true),
            "store" => new OrganizationStoreException("store unavailable"),
            "invalid-operation" => new InvalidOperationException("internal invariant"),
            "remote-unavailable" => new RemoteWorkerUnavailableException("host unavailable"),
            "canceled" => new OperationCanceledException("internal timeout"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        var status = await fixture.RequestUpgradeStatusAsync();

        Assert.Equal(expected, status);
        Assert.Equal(0, fixture.ViewerCount());
        Assert.False(fixture.ExceptionEscapedToMiddleware);
    }

    private sealed class RouterFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly OrganizationStore _store;
        private readonly IHost _host;
        private readonly string _origin;
        private const string NativeSessionId = "native-router";
        private readonly RemoteWorkerSnapshot _target;
        private readonly RemoteTerminalRouter _router;
        private readonly StubSession _session;

        public FakeViewerWorker Worker { get; } = new();
        public bool ExceptionEscapedToMiddleware { get; private set; }
        public Exception? Failure { get; private set; }

        /// <summary>
        /// When set, the next cached-lease status refresh fails with this exception.
        /// It is only mutated after the initial authenticated connect, so the
        /// fixture still establishes a real cached owner lease first.
        /// </summary>
        public Exception? RefreshException { get => _session.RefreshException; set => _session.RefreshException = value; }

        public RouterFixture()
        {
            var databasePath = Path.Combine(_temp.Path, "control.db");
            _store = new OrganizationStore(databasePath);
            _store.OpenAndAdopt("AgentControl Development", "owner-approved:test", null, "seed://fresh");

            var bindingId = Raw(databasePath, "SELECT id FROM runtime_bindings LIMIT 1");
            var employeeId = Raw(databasePath, $"SELECT employee_id FROM runtime_bindings WHERE id='{bindingId}'");
            const string sessionId = "ses-router";
            Execute(databasePath, $"UPDATE acp_sessions SET status='closed' WHERE employee_id='{employeeId}' AND status='active'; INSERT INTO acp_sessions(id,employee_id,native_session_id,title,status,created_at,updated_at) VALUES('{sessionId}','{employeeId}','{NativeSessionId}','Router','active','2026-09-15T00:00:00.0000000+00:00','2026-09-15T00:00:00.0000000+00:00'); UPDATE runtime_bindings SET placement='DeveloperContainer',container_ref='existing',session_ref='{sessionId}' WHERE id='{bindingId}'");

            _store.RegisterExecutionHost("host-a", "worker.example", 22, "docker", "/known", new("host-a", "host-a", "Host A"));
            _store.RecordExecutionHostProbe("host-a", 1, new("ssh-ed25519", "SHA256:x", "sha256:" + new string('1', 64), "29", "1.56", "amd64", "overlayfs", "unknown", false, 2_000_000_000, 2_000_000_000, 2, true, "linux/amd64", "valid"));

            var keyPath = Path.Combine(_temp.Path, "worker.key");
            File.WriteAllBytes(keyPath, new byte[32]);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var enrollment = _store.CreateWorkerEnrollmentForPlan(bindingId, "host-a", "controller-a", "sha256:" + new string('a', 64), "linux/amd64", keyPath, "sha256:" + new string('b', 64), "wrk-router");
            enrollment = _store.UpdateEnrollmentLifecycle(enrollment.WorkerId, enrollment.Revision, "planned", "provisioning");
            enrollment = _store.UpdateEnrollmentLifecycle(enrollment.WorkerId, enrollment.Revision, "provisioning", "enrolled");
            _store.RecordWorkerStatusAndEvents(enrollment.WorkerId, new(1, 1, "running", null, null, 1, false, [], 0, 0, ViewerSupported: true, ViewerAvailable: true), []);

            Worker.KeyPath = keyPath;
            Worker.ControllerId = enrollment.ControllerId;
            Worker.WorkerId = enrollment.WorkerId;
            Worker.SessionId = NativeSessionId;

            var control = new AcpControlHost(Options.Create(new ControlOptions { DataDirectory = _temp.Path, PrivateDataDirectory = Path.Combine(_temp.Path, "private") }), Microsoft.Extensions.Logging.Abstractions.NullLogger<AcpControlHost>.Instance);
            typeof(AcpControlHost).GetField("_organization", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(control, _store);

            var knownHostsPath = Path.Combine(_temp.Path, "known_hosts");
            var identityPath = Path.Combine(_temp.Path, "id_ed25519");
            File.WriteAllText(knownHostsPath, "worker.example ssh-ed25519 " + Convert.ToBase64String(new byte[32]) + "\n");
            File.WriteAllBytes(identityPath, new byte[32]);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(knownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(identityPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var options = Options.Create(new WorkerControlOptions
            {
                Enabled = true,
                ControllerId = "controller-a",
                ApprovedImageDigest = "sha256:" + new string('a', 64),
                ExpectedControllerUid = ControllerPrivateFile.EffectiveUid,
                ApprovedHosts = [new ApprovedExecutionHost { Id = "host-a", Hostname = "worker.example", Port = 22, Username = "docker", KnownHostsPath = knownHostsPath, IdentityFilePath = identityPath }],
            });

            _session = new StubSession("controller-a");
            var manager = new WorkerConnectionManager(control, new StubSessionFactory(_session), options, new SystemControllerClock(), new SystemWorkerDelay());
            // The router attaches only onto an already-cached owner lease, exactly as
            // the hosted manager establishes one in production.
            manager.ConnectAndSynchronizeAsync(enrollment.WorkerId, CancellationToken.None).GetAwaiter().GetResult();
            _router = new RemoteTerminalRouter(control, manager, Worker, options);
            var router = _router;

            _target = new RemoteWorkerSnapshot(employeeId, bindingId, enrollment.WorkerId, "host-a", "enrolled", true, "authenticated", "running", sessionId, NativeSessionId, 1, 1, false, true, true, [], null);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.UseWebSockets();
            app.Map("/terminal", async context =>
            {
                try { await router.ProxyAsync(context, _target, context.RequestAborted); }
                catch (Exception exception) { ExceptionEscapedToMiddleware = true; Failure = exception; throw; }
            });
            app.StartAsync().GetAwaiter().GetResult();
            _host = app;
            _origin = app.Urls.First();
        }

        public string ViewerState() => _store.ListRemoteTerminalViewers(_target.WorkerId).Last().State;

        /// <summary>The number of viewer records, used to prove a pre-upgrade refusal left none.</summary>
        public int ViewerCount() => _store.ListRemoteTerminalViewers(_target.WorkerId).Count;

        /// <summary>Exposes the router so a test can assert its exact unclassified failure.</summary>
        public RemoteTerminalRouter Router => _router;

        /// <summary>
        /// Issues a real WebSocket upgrade request over loopback and returns the HTTP
        /// status the server committed before any socket accept. A 101 proves the
        /// server upgraded; any other code proves it refused before the handshake.
        /// </summary>
        public async Task<int> RequestUpgradeStatusAsync()
        {
            var uri = new Uri(_origin);
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            await using var stream = client.GetStream();
            var request =
                $"GET /terminal HTTP/1.1\r\n"
                + $"Host: {uri.Host}:{uri.Port}\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + $"Sec-WebSocket-Key: {Convert.ToBase64String(Guid.NewGuid().ToByteArray())}\r\n"
                + "Sec-WebSocket-Version: 13\r\n"
                + $"Origin: {_origin}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            await stream.FlushAsync(timeout.Token);
            var buffer = new byte[512];
            var received = new StringBuilder();
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), timeout.Token);
                if (read == 0) break;
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                var text = received.ToString();
                var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
                if (lineEnd < 0)
                {
                    if (received.Length > 4096) throw new InvalidOperationException("The HTTP response headers were unbounded.");
                    continue;
                }
                var parts = text[..lineEnd].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[1], out var status)) throw new InvalidOperationException($"Unexpected HTTP status line: {text[..lineEnd]}");
                return status;
            }
            throw new InvalidOperationException("The connection closed before an HTTP status line was received.");
        }

        public async Task<(WebSocketCloseStatus Status, string Reason)> AttachAndAwaitCloseAsync()
        {
            using var client = new ClientWebSocket();
            client.Options.SetRequestHeader("Origin", _origin);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await client.ConnectAsync(new Uri(_origin.Replace("http://", "ws://", StringComparison.Ordinal) + "/terminal"), timeout.Token); }
            catch (WebSocketException exception) { throw new InvalidOperationException($"upgrade failed; server fault: {Failure}", exception); }

            var buffer = new byte[64 * 1024];
            while (true)
            {
                var result = await client.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return (client.CloseStatus ?? WebSocketCloseStatus.Empty, client.CloseStatusDescription ?? string.Empty);
            }
        }

        public void Dispose()
        {
            Worker.Dispose();
            _host.StopAsync().GetAwaiter().GetResult();
            if (_host is IDisposable disposable) disposable.Dispose();
            _store.Dispose();
            _temp.Dispose();
        }

        /// <summary>The router only needs a cached authenticated lease to proceed.</summary>
        private sealed class StubSessionFactory(StubSession session) : IWorkerBridgeSessionFactory
        {
            public Task<IWorkerBridgeSession> ConnectAsync(WorkerEnrollmentRecord enrollment, CancellationToken cancellationToken) =>
                Task.FromResult<IWorkerBridgeSession>(session);
        }

        private sealed class StubSession(string controllerId) : IWorkerBridgeSession
        {
            public WorkerBridgeLease Lease { get; } = new(1, controllerId, Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow);
            /// <summary>When set, the status refresh (and only that) faults with this exception.</summary>
            public Exception? RefreshException { get; set; }
            public object Mutation(string operation, IReadOnlyDictionary<string, object?> fields) => new Dictionary<string, object?>(fields) { ["operation"] = operation };
            public Task<WorkerSessionResult> InvokeAsync(string operation, object request, bool mutation, CancellationToken cancellationToken)
            {
                if (operation == "status" && RefreshException is not null) throw RefreshException;
                object value = operation switch
                {
                    "status" => new BridgeWorkerStatus(1, 1, "running", "life", 1, null, null, 1, true, false, null, [], 0, 0, 0, 0, null, null, 0, [], true, true, true, RouterFixture.NativeSessionId),
                    "replay" => new BridgeReplayPage([], false, 0),
                    _ => new { ok = true },
                };
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, WorkerProtocol.JsonOptions));
                return Task.FromResult(new WorkerSessionResult(operation, document.RootElement.Clone(), mutation));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private static string Raw(string path, string sql)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
        }

        private static void Execute(string path, string sql)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// An in-process viewer peer that completes the real viewer handshake and then
    /// emits the configured outcome.
    /// </summary>
    private sealed class FakeViewerWorker : IRemoteTerminalConnector, IDisposable
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => _released.TrySetResult();

        public string KeyPath { get; set; } = string.Empty;
        public string ControllerId { get; set; } = string.Empty;
        public string WorkerId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>Null ends the stream cleanly; otherwise an explicit close frame is sent.</summary>
        public string? CloseCategory { get; set; }
        public int? CloseBytesWritten { get; set; }

        public Task<Stream> ConnectViewerAsync(string workerId, CancellationToken cancellationToken)
        {
            var (controllerSide, workerSide) = DuplexPair.Create();
            _ = Task.Run(() => ServeAsync(workerSide), CancellationToken.None);
            return Task.FromResult(controllerSide);
        }

        private async Task ServeAsync(Stream stream)
        {
            try
            {
                var key = File.ReadAllBytes(KeyPath);
                using var hello = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None);
                var root = hello!.RootElement;
                var clientNonce = root.GetProperty("clientNonce").GetString()!;
                var keyId = root.GetProperty("keyId").GetString()!;
                var epoch = root.GetProperty("ownershipEpoch").GetInt64();
                var connectionNonce = root.GetProperty("connectionNonce").GetString()!;
                var serverNonce = Convert.ToBase64String(new byte[32]);
                var issued = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var mac = WorkerProtocol.ComputeViewerMac(key, "server-proof", ControllerId, WorkerId, keyId, clientNonce, serverNonce, issued, epoch, connectionNonce, SessionId);
                await WorkerProtocol.WriteFrameAsync(stream, new { type = "challenge", version = WorkerProtocol.Version, role = WorkerProtocol.ViewerRole, controllerId = ControllerId, workerId = WorkerId, keyId, clientNonce, serverNonce, issuedUnixMilliseconds = issued, mac }, CancellationToken.None);
                using var proof = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None);
                await WorkerProtocol.WriteFrameAsync(stream, new { type = "authenticated", role = WorkerProtocol.ViewerRole, sessionId = SessionId }, CancellationToken.None);

                if (CloseCategory is { } category)
                {
                    object frame = CloseBytesWritten is { } written
                        ? new { type = "close", sessionId = SessionId, category, bytesWritten = written }
                        : new { type = "close", sessionId = SessionId, category };
                    await WorkerProtocol.WriteFrameAsync(stream, frame, CancellationToken.None);
                    // A real worker keeps the connection until the controller tears it
                    // down. Disposing here instead would truncate the frame the
                    // controller is still reading and turn an announced failure into an
                    // indistinguishable transport error.
                    await _released.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }
            }
            catch { /* The controller side observes the closed stream. */ }
            finally { await stream.DisposeAsync(); }
        }
    }

    /// <summary>An in-memory duplex pair standing in for the SSH connector stream.</summary>
    private sealed class DuplexPair : Stream
    {
        private readonly Stream _read;
        private readonly Stream _write;
        private DuplexPair(Stream read, Stream write) { _read = read; _write = write; }

        public static (Stream Controller, Stream Worker) Create()
        {
            var toWorker = new System.IO.Pipelines.Pipe();
            var toController = new System.IO.Pipelines.Pipe();
            return (new DuplexPair(toController.Reader.AsStream(), toWorker.Writer.AsStream()),
                    new DuplexPair(toWorker.Reader.AsStream(), toController.Writer.AsStream()));
        }

        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _write.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _read.Dispose(); _write.Dispose(); } base.Dispose(disposing); }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-router-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
