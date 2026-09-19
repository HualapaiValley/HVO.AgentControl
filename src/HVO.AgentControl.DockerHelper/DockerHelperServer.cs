using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.DockerHelper.Protocol;
using HVO.AgentControl.RemoteWorker;
using HVO.AgentControl.Worker;

namespace HVO.AgentControl.DockerHelper;

public sealed record DockerHelperOptions(string SocketPath, int ClientUid, int SocketGid, DockerPolicy Policy, int MaxConcurrency = 4)
{
    public static DockerHelperOptions FromEnvironment()
    {
        static int Int(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
        static long Long(string name, long fallback) => long.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
        static decimal Decimal(string name, decimal fallback) => decimal.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
        var digest = Environment.GetEnvironmentVariable("AGENTCONTROL_DOCKER_HELPER_APPROVED_BASE_DIGEST") ?? string.Empty;
        var platform = Environment.GetEnvironmentVariable("AGENTCONTROL_DOCKER_HELPER_APPROVED_PLATFORM") ?? "linux/amd64";
        return new(Environment.GetEnvironmentVariable("AGENTCONTROL_DOCKER_HELPER_SOCKET") ?? DockerHelperProtocol.DefaultSocketPath, Int("AGENTCONTROL_DOCKER_HELPER_CLIENT_UID", 1001), Int("AGENTCONTROL_DOCKER_HELPER_GID", 1001), new DockerPolicy(digest, platform, Long("AGENTCONTROL_DOCKER_HELPER_MAX_MEMORY_BYTES", 2L * 1024 * 1024 * 1024), Decimal("AGENTCONTROL_DOCKER_HELPER_MAX_CPU", 2), Int("AGENTCONTROL_DOCKER_HELPER_MAX_PIDS", 256), RequireAgentControlPrefixes: true), Int("AGENTCONTROL_DOCKER_HELPER_MAX_CONCURRENCY", 4));
    }
}

public interface IPeerCredentialProvider { int GetUid(Socket socket); }

/// <summary>
/// Reads the kernel-supplied <c>SO_PEERCRED</c> identity of the connected peer.
/// This is the helper's only authorization input: the uid is attributed by the
/// kernel at connect time and cannot be asserted or spoofed by the peer itself.
/// </summary>
/// <remarks>
/// The syscall is invoked directly rather than through
/// <see cref="Socket.GetSocketOption(SocketOptionLevel, SocketOptionName, byte[])"/>:
/// the managed API maps option names through its own table and rejects
/// <c>SO_PEERCRED</c> with "Operation not supported" (measured), which would make
/// every connection unverifiable.
/// </remarks>
public sealed class LinuxPeerCredentialProvider : IPeerCredentialProvider
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    public int GetUid(Socket socket)
    {
        var bytes = new byte[12];
        var length = bytes.Length;
        var handle = socket.SafeHandle;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (getsockopt(handle.DangerousGetHandle().ToInt32(), SolSocket, SoPeerCred, bytes, ref length) != 0 || length != bytes.Length)
                throw new IOException("The peer credentials of the helper connection could not be read.");
        }
        finally { if (added) handle.DangerousRelease(); }
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
    }

    [DllImport("libc", SetLastError = true)] private static extern int getsockopt(int fd, int level, int optionName, byte[] optionValue, ref int optionLength);
}

public interface IDockerProcessRunner
{
    Task<DockerHelperResult> RunAsync(string id, DockerOperation operation, string[] argv, byte[]? input, TimeSpan timeout, CancellationToken token);
    Task<Process> StartStreamAsync(string[] argv, CancellationToken token);
}

public sealed class DockerProcessRunner : IDockerProcessRunner
{
    public async Task<DockerHelperResult> RunAsync(string id, DockerOperation operation, string[] argv, byte[]? input, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        using var process = Start(argv);
        try
        {
            if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = Encoding.UTF8.GetString(await stdout.ConfigureAwait(false));
            var error = Encoding.UTF8.GetString(await stderr.ConfigureAwait(false));
            return new("result", id, process.ExitCode, output, Classify(operation, process.ExitCode, error));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            TryKill(process); return new("result", id, -1, string.Empty, "timeout");
        }
        catch { TryKill(process); throw; }
        finally { if (input is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(input); }
    }

    public Task<Process> StartStreamAsync(string[] argv, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Start(argv)); }
    private static Process Start(string[] argv) { var start = new ProcessStartInfo(argv[0]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; foreach (var item in argv.Skip(1)) start.ArgumentList.Add(item); return Process.Start(start) ?? throw new IOException("The fixed Docker CLI could not start."); }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token) { using var memory = new MemoryStream(); var buffer = new byte[8192]; while (true) { var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) return memory.ToArray(); if (memory.Length + read > DockerHelperProtocol.MaxOutputBytes) throw new IOException("Docker CLI output exceeded the fixed limit."); memory.Write(buffer, 0, read); } }
    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
    private static string Classify(DockerOperation operation, int exitCode, string stderr) { if (exitCode == 0) return "none"; if (exitCode != 1) return "docker-command-failed"; var expected = operation switch { DockerOperation.ContainerInspect => "no such container", DockerOperation.VolumeInspect => "no such volume", DockerOperation.ImageInspect or DockerOperation.ImageRemove or DockerOperation.ImageTag => "no such image", _ => null }; return expected is not null && stderr.Length <= 4096 && stderr.Contains(expected, StringComparison.OrdinalIgnoreCase) ? "not-found" : "docker-command-failed"; }
}

public sealed class DockerHelperServer(DockerHelperOptions options, IPeerCredentialProvider? credentials = null, IDockerProcessRunner? runner = null) : IAsyncDisposable
{
    private readonly IPeerCredentialProvider _credentials = credentials ?? new LinuxPeerCredentialProvider();
    private readonly IDockerProcessRunner _runner = runner ?? new DockerProcessRunner();
    private readonly SemaphoreSlim _concurrency = new(options.MaxConcurrency, options.MaxConcurrency);
    private Socket? _listener;

    public async Task RunAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The Docker helper requires Linux Unix sockets and peer credentials.");
        if (!Path.IsPathRooted(options.SocketPath)) throw new InvalidOperationException("Helper socket path must be absolute.");
        var directory = Path.GetDirectoryName(options.SocketPath)!; Directory.CreateDirectory(directory); File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        if (File.Exists(options.SocketPath)) File.Delete(options.SocketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); _listener.Bind(new UnixDomainSocketEndPoint(options.SocketPath)); File.SetUnixFileMode(options.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite); if (options.SocketGid >= 0) _ = chown(options.SocketPath, uint.MaxValue, checked((uint)options.SocketGid)); _listener.Listen(128);
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            _ = Task.Run(() => HandleAsync(socket, token), CancellationToken.None);
        }
    }

    internal async Task HandleAsync(Socket socket, CancellationToken token)
    {
        using (socket) using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            string id = string.Empty;
            try
            {
                if (_credentials.GetUid(socket) != options.ClientUid) { await WorkerProtocol.WriteFrameAsync(stream, new DockerHelperError("error", id, "peer-unauthorized"), token); return; }
                using var document = await WorkerProtocol.ReadFrameAsync(stream, token).ConfigureAwait(false) ?? throw new InvalidDataException();
                var request = JsonSerializer.Deserialize<DockerHelperRequest>(document.RootElement.GetRawText(), DockerHelperProtocol.JsonOptions) ?? throw new InvalidDataException(); id = request.Id;
                ValidateEnvelope(request);
                var input = request.BinaryLength == 0 ? null : await ReadExactAsync(stream, request.BinaryLength, token).ConfigureAwait(false);
                string[] argv = Build(request);
                if (request.Operation is DockerOperation.Connector or DockerOperation.Viewer) { await StreamAsync(stream, request, argv, token).ConfigureAwait(false); return; }
                if (!await _concurrency.WaitAsync(0, token).ConfigureAwait(false)) { await WorkerProtocol.WriteFrameAsync(stream, new DockerHelperError("error", id, "capacity-exhausted"), token); return; }
                try { var result = await _runner.RunAsync(id, request.Operation, argv, input, TimeSpan.FromSeconds(request.TimeoutSeconds), token).ConfigureAwait(false); await WriteAsync(stream, result, token).ConfigureAwait(false); }
                finally { _concurrency.Release(); }
            }
            catch (DockerGrammarException) { await SafeErrorAsync(stream, id, "request-rejected", token).ConfigureAwait(false); }
            catch (JsonException) { await SafeErrorAsync(stream, id, "protocol-error", token).ConfigureAwait(false); }
            catch (InvalidDataException) { await SafeErrorAsync(stream, id, "protocol-error", token).ConfigureAwait(false); }
            catch (IOException) { await SafeErrorAsync(stream, id, "output-limit", token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { await SafeErrorAsync(stream, id, "helper-failed", token).ConfigureAwait(false); }
        }
    }

    private string[] Build(DockerHelperRequest request) => request.Operation switch { DockerOperation.VolumeCreate when request.VolumeCreate is not null => DockerArgv.BuildVolumeCreate(request.VolumeCreate), DockerOperation.ContainerCreate when request.ContainerCreate is not null => DockerArgv.BuildContainerCreate(request.ContainerCreate, options.Policy), DockerOperation.Bootstrap when request.Bootstrap is not null => DockerArgv.BuildBootstrap(request.Bootstrap, options.Policy), DockerOperation.ImageBuild when request.ImageBuild is not null => DockerArgv.BuildImageBuild(request.ImageBuild), _ => DockerArgv.Build(request.Operation, request.Tokens ?? [], options.Policy) };
    private static void ValidateEnvelope(DockerHelperRequest request) { if (request.Type != "request" || request.Id is not { Length: >= 1 and <= 128 } || request.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not ':') || request.TimeoutSeconds is < 1 or > DockerHelperProtocol.MaxTimeoutSeconds || request.BinaryLength is < 0 or > DockerHelperProtocol.MaxBinaryBytes) throw new InvalidDataException(); var needsBinary = request.Operation is DockerOperation.Bootstrap or DockerOperation.ImageBuild; if (needsBinary != (request.BinaryLength > 0)) throw new InvalidDataException(); if (request.Operation is DockerOperation.Connector or DockerOperation.Viewer && request.BinaryLength != 0) throw new InvalidDataException(); }
    private async Task StreamAsync(Stream stream, DockerHelperRequest request, string[] argv, CancellationToken token) { if (!await _concurrency.WaitAsync(0, token).ConfigureAwait(false)) { await WorkerProtocol.WriteFrameAsync(stream, new DockerHelperError("error", request.Id, "capacity-exhausted"), token); return; } Process? process = null; try { process = await _runner.StartStreamAsync(argv, token).ConfigureAwait(false); await WorkerProtocol.WriteFrameAsync(stream, new DockerHelperStreamOpen("stream-open", request.Id), token).ConfigureAwait(false); using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); var input = stream.CopyToAsync(process.StandardInput.BaseStream, lifetime.Token); var output = process.StandardOutput.BaseStream.CopyToAsync(stream, lifetime.Token); var completed = await Task.WhenAny(input, output, process.WaitForExitAsync(lifetime.Token)).ConfigureAwait(false); lifetime.Cancel(); try { await completed.ConfigureAwait(false); } catch { } } finally { try { if (process is not null && !process.HasExited) process.Kill(true); } catch { } process?.Dispose(); _concurrency.Release(); } }
    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken token) { var bytes = GC.AllocateUninitializedArray<byte>(length); var offset = 0; while (offset < length) { var read = await stream.ReadAsync(bytes.AsMemory(offset), token).ConfigureAwait(false); if (read == 0) throw new InvalidDataException(); offset += read; } return bytes; }
    private static ValueTask WriteAsync(Stream stream, DockerHelperResult result, CancellationToken token) => WorkerProtocol.WriteFrameAsync(stream, result, token);
    private static async Task SafeErrorAsync(Stream stream, string id, string category, CancellationToken token) { try { await WorkerProtocol.WriteFrameAsync(stream, new DockerHelperError("error", id, category), token); } catch { } }
    public ValueTask DisposeAsync() { try { _listener?.Dispose(); if (File.Exists(options.SocketPath)) File.Delete(options.SocketPath); } catch { } _concurrency.Dispose(); return ValueTask.CompletedTask; }
    [DllImport("libc", SetLastError = true)] private static extern int chown(string path, uint owner, uint group);
}
