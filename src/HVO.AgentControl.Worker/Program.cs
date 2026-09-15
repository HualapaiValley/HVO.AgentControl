using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.AgentControl.Worker;

internal static class WorkerEntryPoint
{
    public static Task<int> Main(string[] args) => WorkerProgram.RunAsync(args);
}

internal static class WorkerProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is ["--worker-bootstrap-key"])
            {
                var directory = RequiredEnvironment("WORKER_CONTROL_DIRECTORY");
                var input = await Console.In.ReadToEndAsync().ConfigureAwait(false);
                Console.Out.WriteLine(WorkerKeyBootstrap.Bootstrap(directory, input));
                return 0;
            }
            if (args is ["--worker-bridge"])
            {
                var options = Options();
                var key = WorkerKeyBootstrap.ReadKey(options.ControlDirectory);
                var store = new WorkerStore(options);
                var processGeneration = store.BeginProcessStart();
                var inputFd = int.Parse(RequiredEnvironment("WORKER_ACP_READ_FD"), System.Globalization.CultureInfo.InvariantCulture);
                var outputFd = int.Parse(RequiredEnvironment("WORKER_ACP_WRITE_FD"), System.Globalization.CultureInfo.InvariantCulture);
                SupervisorStart start;
                try
                {
                    start = await RequestSupervisorStartAsync(processGeneration).ConfigureAwait(false);
                    store.CompleteProcessStart(start.LifecycleHandle, start.Pid);
                }
                catch (WorkerProtocolException)
                {
                    store.FailProcessStart(uncertain: true);
                    throw;
                }
                var runtime = new WorkerRuntime(store, WorkerRuntime.OpenInheritedFd(inputFd, FileAccess.Read), WorkerRuntime.OpenInheritedFd(outputFd, FileAccess.Write));
                runtime.Start(start.Pid);
                await using var bridge = new WorkerBridge(options, store, runtime, key);
                using var shutdown = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
                AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
                await bridge.RunAsync(shutdown.Token).ConfigureAwait(false);
                return 0;
            }
            if (args is ["--worker-connector"])
                return await RunConnectorAsync(Options()).ConfigureAwait(false);
            Console.Error.WriteLine("Use one fixed mode: --worker-bootstrap-key, --worker-bridge, or --worker-connector.");
            return 64;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception switch
            {
                WorkerProtocolException => "Worker operation was rejected by the protocol safety policy.",
                WorkerStoreException => "Worker durable state failed closed and requires operator reconciliation.",
                OperationCanceledException => "Worker operation timed out.",
                IOException or SocketException or UnauthorizedAccessException => "Worker host operation failed closed.",
                _ => "Worker host terminated after an unexpected internal failure.",
            });
            return 1;
        }
    }

    private static WorkerOptions Options() => WorkerOptions.Production(
        RequiredEnvironment("WORKER_CONTROL_DIRECTORY"), RequiredEnvironment("WORKER_ID"), RequiredEnvironment("WORKER_CONTROLLER_ID"));

    private static async Task<int> RunConnectorAsync(WorkerOptions options)
    {
        var keyText = await Console.In.ReadLineAsync().ConfigureAwait(false) ?? throw new WorkerProtocolException("Connector key is required on the first stdin line.");
        var key = WorkerProtocol.ParseKey(keyText);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath)).ConfigureAwait(false);
            using var stream = new NetworkStream(socket);
            var keyId = WorkerProtocol.KeyId(key);
            var clientNonceBytes = RandomNumberGenerator.GetBytes(WorkerProtocol.NonceBytes);
            string clientNonce;
            try { clientNonce = Convert.ToBase64String(clientNonceBytes); }
            finally { CryptographicOperations.ZeroMemory(clientNonceBytes); }
            await WorkerProtocol.WriteFrameAsync(stream, new { type = "hello", version = WorkerProtocol.Version, role = "controller", controllerId = options.ControllerId, workerId = options.WorkerId, keyId, clientNonce }, CancellationToken.None);
            using var challenge = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None).ConfigureAwait(false) ?? throw new WorkerProtocolException("Bridge closed during authentication.");
            var c = challenge.RootElement;
            RequireExactFields(c, "type", "version", "role", "controllerId", "workerId", "keyId", "clientNonce", "serverNonce", "issuedUnixMilliseconds", "mac");
            if (Required(c, "type") != "challenge" || Required(c, "version") != WorkerProtocol.Version || Required(c, "role") != "controller" || Required(c, "controllerId") != options.ControllerId || Required(c, "workerId") != options.WorkerId || Required(c, "keyId") != keyId || Required(c, "clientNonce") != clientNonce) throw new WorkerProtocolException("Bridge challenge identity is invalid.");
            var serverNonce = Required(c, "serverNonce"); using var nonce = new ZeroingBuffer(WorkerProtocol.ParseNonce(serverNonce, "server nonce"));
            if (!c.TryGetProperty("issuedUnixMilliseconds", out var issuedElement) || !issuedElement.TryGetInt64(out var issued) || !WorkerBridge.IsProofFresh(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), issued, options.ChallengeLifetime)) throw new WorkerProtocolException("Bridge challenge is stale.");
            var serverMac = Required(c, "mac");
            var expected = WorkerProtocol.ComputeMac(key, "server-proof", "controller", options.ControllerId, options.WorkerId, keyId, clientNonce, serverNonce, issued);
            if (!WorkerProtocol.VerifyMac(expected, serverMac)) throw new WorkerProtocolException("Bridge server proof rejected.");
            var clientMac = WorkerProtocol.ComputeMac(key, "client-proof", "controller", options.ControllerId, options.WorkerId, keyId, clientNonce, serverNonce, issued);
            await WorkerProtocol.WriteFrameAsync(stream, new { type = "proof", mac = clientMac }, CancellationToken.None);
            using var authenticated = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None).ConfigureAwait(false) ?? throw new WorkerProtocolException("Bridge closed before lease acquisition.");
            Console.Out.WriteLine(authenticated.RootElement.GetRawText());
            string? command;
            while ((command = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(command)) continue;
                using var document = JsonDocument.Parse(command);
                await WorkerProtocol.WriteFrameAsync(stream, document.RootElement, CancellationToken.None);
                using var response = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None).ConfigureAwait(false);
                if (response is null) break;
                Console.Out.WriteLine(response.RootElement.GetRawText());
            }
            return 0;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task<SupervisorStart> RequestSupervisorStartAsync(long processGeneration)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint("/run/worker-supervisor.sock"), timeout.Token).ConfigureAwait(false);
        using var stream = new NetworkStream(socket);
        await WorkerProtocol.WriteFrameAsync(stream, new { operation = "start", processGeneration }, timeout.Token).ConfigureAwait(false);
        using var response = await WorkerProtocol.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
        if (response is null || !response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean() ||
            !response.RootElement.TryGetProperty("lifecycleHandle", out var handle) || handle.ValueKind != JsonValueKind.String)
            throw new WorkerProtocolException("The fixed worker supervisor start result is uncertain.");
        var lifecycleHandle = handle.GetString()!;
        WorkerProtocol.ValidateIdentifier(lifecycleHandle, WorkerProtocol.MaxIdentifierLength, "lifecycle handle");
        long? pid = response.RootElement.TryGetProperty("pid", out var processId) && processId.TryGetInt64(out var value) ? value : null;
        return new SupervisorStart(lifecycleHandle, pid);
    }

    private static string Required(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } value ? value : throw new WorkerProtocolException($"Missing {name}.");
    private static void RequireExactFields(JsonElement element, params string[] fields) { if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new WorkerProtocolException("Connector authentication fields are invalid."); }
    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 and <= 512 } value ? value : throw new WorkerProtocolException($"Required fixed configuration {name} is missing or invalid.");
    private sealed class ZeroingBuffer(byte[] value) : IDisposable { public void Dispose() => CryptographicOperations.ZeroMemory(value); }
    private sealed record SupervisorStart(string LifecycleHandle, long? Pid);
}
