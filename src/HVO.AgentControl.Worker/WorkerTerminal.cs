using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace HVO.AgentControl.Worker;

public sealed record WorkerTerminalOutput(byte[] Data);

public sealed class WorkerTerminalWriteUncertainException(string message, int bytesWritten, Exception? inner = null) : IOException(message, inner)
{
    public int BytesWritten { get; } = bytesWritten;
}

public interface IWorkerTerminalSession : IAsyncDisposable
{
    Task WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken token);
    Task ResizeAsync(int rows, int columns, CancellationToken token);
    IAsyncEnumerable<WorkerTerminalOutput> ReadOutputAsync(CancellationToken token);
}

public interface IWorkerTerminalBackend
{
    bool Available { get; }
    Task<IWorkerTerminalSession> AttachAsync(string sessionId, CancellationToken token);
}

public sealed class UnavailableWorkerTerminalBackend : IWorkerTerminalBackend
{
    public bool Available => false;
    public Task<IWorkerTerminalSession> AttachAsync(string sessionId, CancellationToken token) => throw new WorkerProtocolException("Worker terminal backend is unavailable in this build.");
}

/// <summary>Linux production backend for the fixed worker-supervisor PTY operation.</summary>
public sealed partial class SupervisorWorkerTerminalBackend(string socketPath = "/run/worker-supervisor.sock") : IWorkerTerminalBackend
{
    private const int SupervisorResponseLimit = 4096;
    private const int MsgCmsgCloexec = 0x40000000;
    private const int MsgCtrunc = 0x08;
    public bool Available => OperatingSystem.IsLinux();

    public async Task<IWorkerTerminalSession> AttachAsync(string sessionId, CancellationToken token)
    {
        if (!Available) throw new WorkerProtocolException("The supervisor terminal backend requires Linux.");
        WorkerProtocol.ValidateIdentifier(sessionId, WorkerProtocol.MaxIdentifierLength, "session id");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { ReceiveTimeout = 5000, SendTimeout = 5000 };
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token).ConfigureAwait(false);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { operation = "viewer-start", sessionId, rows = 24, columns = 80 }, WorkerProtocol.JsonOptions);
        var frame = GC.AllocateUninitializedArray<byte>(request.Length + 1); request.CopyTo(frame, 0); frame[^1] = (byte)'\n';
        await socket.SendAsync(frame, SocketFlags.None, token).ConfigureAwait(false);
        var received = await Task.Run(() => ReceiveDescriptor(socket), token).ConfigureAwait(false);
        try
        {
            using var response = JsonDocument.Parse(received.Payload, new JsonDocumentOptions { MaxDepth = 8 });
            var root = response.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("ok").ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("viewerHandle", out var handleValue) || handleValue.ValueKind != JsonValueKind.String ||
                root.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "ok", "viewerHandle" }.Order(StringComparer.Ordinal), StringComparer.Ordinal) is false)
                throw new WorkerProtocolException("The fixed supervisor viewer start result is invalid.");
            var handle = handleValue.GetString()!;
            WorkerProtocol.ValidateIdentifier(handle, WorkerProtocol.MaxIdentifierLength, "viewer handle");
            if (received.FileDescriptor < 0) throw new WorkerProtocolException("The supervisor did not return the viewer PTY descriptor.");
            return new SupervisorWorkerTerminalSession(socketPath, handle, received.FileDescriptor);
        }
        catch
        {
            if (received.FileDescriptor >= 0) close(received.FileDescriptor);
            throw;
        }
    }

    private static ReceivedDescriptor ReceiveDescriptor(Socket socket)
    {
        if (!socket.Poll(TimeSpan.FromSeconds(5), SelectMode.SelectRead)) throw new WorkerProtocolException("The supervisor viewer response timed out.");
        var payload = new byte[SupervisorResponseLimit + 1];
        var controlLength = Align((nuint)Marshal.SizeOf<Cmsghdr>()) + Align(sizeof(int));
        var payloadHandle = GCHandle.Alloc(payload, GCHandleType.Pinned);
        var control = Marshal.AllocHGlobal(checked((int)controlLength));
        try
        {
            Marshal.Copy(new byte[checked((int)controlLength)], 0, control, checked((int)controlLength));
            var vector = new Iovec { Base = payloadHandle.AddrOfPinnedObject(), Length = (nuint)payload.Length };
            var vectorPointer = Marshal.AllocHGlobal(Marshal.SizeOf<Iovec>());
            try
            {
                Marshal.StructureToPtr(vector, vectorPointer, false);
                var message = new Msghdr { Iov = vectorPointer, IovLength = 1, Control = control, ControlLength = controlLength };
                var count = recvmsg(socket.Handle.ToInt32(), ref message, MsgCmsgCloexec);
                if (count < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "recvmsg failed for the supervisor viewer response.");
                if (count == 0 || count > SupervisorResponseLimit) throw new WorkerProtocolException("The supervisor viewer response framing is invalid.");
                var byteCount = checked((int)count);
                if (payload[byteCount - 1] != (byte)'\n' || payload.AsSpan(0, byteCount).Count((byte)'\n') != 1)
                    throw new WorkerProtocolException("The supervisor viewer response framing is invalid.");
                if ((message.Flags & MsgCtrunc) != 0) throw new WorkerProtocolException("The supervisor viewer descriptor was truncated.");
                var descriptor = -1;
                var required = Align((nuint)Marshal.SizeOf<Cmsghdr>()) + sizeof(int);
                if (message.ControlLength != Align((nuint)Marshal.SizeOf<Cmsghdr>()) + Align(sizeof(int))) throw new WorkerProtocolException("The supervisor returned an invalid ancillary descriptor set.");
                var header = Marshal.PtrToStructure<Cmsghdr>(control);
                if (header.Length != required || header.Level != 1 || header.Type != 1) throw new WorkerProtocolException("The supervisor returned an invalid ancillary descriptor set.");
                descriptor = Marshal.ReadInt32(control + checked((int)Align((nuint)Marshal.SizeOf<Cmsghdr>())));
                return new(payload.AsMemory(0, byteCount - 1).ToArray(), descriptor);
            }
            finally { Marshal.FreeHGlobal(vectorPointer); }
        }
        finally
        {
            Marshal.FreeHGlobal(control);
            payloadHandle.Free();
        }
    }

    private static nuint Align(nuint value)
    {
        var alignment = (nuint)IntPtr.Size;
        return (value + alignment - 1) & ~(alignment - 1);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Iovec { public IntPtr Base; public nuint Length; }
    [StructLayout(LayoutKind.Sequential)] private struct Msghdr { public IntPtr Name; public uint NameLength; public IntPtr Iov; public nuint IovLength; public IntPtr Control; public nuint ControlLength; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Cmsghdr { public nuint Length; public int Level; public int Type; }
    private sealed record ReceivedDescriptor(byte[] Payload, int FileDescriptor);

    [LibraryImport("libc", SetLastError = true)] private static partial long recvmsg(int socket, ref Msghdr message, int flags);
    [LibraryImport("libc", SetLastError = true)] private static partial int close(int descriptor);

    private sealed partial class SupervisorWorkerTerminalSession : IWorkerTerminalSession
    {
        private const ulong Tiocswinsz = 0x5414;
        private readonly string _socketPath;
        private readonly string _handle;
        private readonly SafeFileHandle _fileHandle;
        private int _disposed;

        public SupervisorWorkerTerminalSession(string socketPath, string handle, int descriptor)
        {
            _socketPath = socketPath;
            _handle = handle;
            _fileHandle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            var flags = fcntl(descriptor, 3, 0);
            if (flags < 0 || fcntl(descriptor, 4, flags | 0x800) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "PTY nonblocking mode failed.");
        }

        public unsafe Task WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken token)
        {
            if (input.Length > WorkerProtocol.MaxViewerInputBytes) throw new WorkerProtocolException("Viewer input exceeds the fixed limit.");
            if (input.IsEmpty) return Task.CompletedTask;
            var written = 0;
            try
            {
                using var pin = input.Pin();
                var descriptor = _fileHandle.DangerousGetHandle().ToInt32();
                while (written < input.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var count = write(descriptor, (IntPtr)((byte*)pin.Pointer + written), (nuint)(input.Length - written));
                    if (count > 0) { written += checked((int)count); continue; }
                    if (count == 0) throw new IOException("PTY write made no progress.");
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 4) continue;
                    if (error is 11 or 35)
                    {
                        var ready = new PollFd { Descriptor = descriptor, Events = PollOut };
                        while (true)
                        {
                            var polled = poll(ref ready, 1, 100);
                            if (polled > 0)
                            {
                                if ((ready.ReturnedEvents & (PollErr | PollHup | PollNval)) != 0) throw new IOException("PTY write endpoint closed.");
                                break;
                            }
                            if (polled == 0) { token.ThrowIfCancellationRequested(); continue; }
                            if (Marshal.GetLastPInvokeError() == 4) continue;
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "PTY write poll failed.");
                        }
                        continue;
                    }
                    throw new Win32Exception(error, "PTY write failed.");
                }
                return Task.CompletedTask;
            }
            catch (Exception exception) when (written > 0 && exception is not WorkerTerminalWriteUncertainException)
            {
                throw new WorkerTerminalWriteUncertainException("PTY input delivery is uncertain after a partial write.", written, exception);
            }
        }

        public Task ResizeAsync(int rows, int columns, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (rows is < 1 or > 500 || columns is < 1 or > 500) throw new WorkerProtocolException("Viewer dimensions exceed the fixed limit.");
            var size = new WinSize { Rows = checked((ushort)rows), Columns = checked((ushort)columns) };
            if (ioctl(_fileHandle, Tiocswinsz, ref size) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "PTY resize failed.");
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<WorkerTerminalOutput> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            var descriptorValue = _fileHandle.DangerousGetHandle().ToInt32();
            var hungUp = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var descriptor = new PollFd { Descriptor = descriptorValue, Events = PollIn };
                var result = poll(ref descriptor, 1, 100);
                if (result < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 4) continue;
                    if (_disposed != 0) yield break;
                    throw new Win32Exception(error, "PTY read poll failed.");
                }
                if (result == 0)
                {
                    await Task.Yield();
                    continue;
                }

                hungUp |= (descriptor.ReturnedEvents & PollHup) != 0;
                if ((descriptor.ReturnedEvents & PollNval) != 0)
                {
                    if (_disposed != 0) yield break;
                    throw new IOException("PTY descriptor is invalid.");
                }
                if ((descriptor.ReturnedEvents & PollErr) != 0 && (descriptor.ReturnedEvents & PollIn) == 0 && !hungUp)
                    throw new IOException("PTY read endpoint failed.");
                if ((descriptor.ReturnedEvents & PollIn) == 0)
                {
                    if (hungUp) yield break;
                    continue;
                }

                var bytes = GC.AllocateUninitializedArray<byte>(WorkerProtocol.MaxViewerOutputBytes);
                var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                long count;
                int readError;
                try
                {
                    count = read(descriptorValue, pinned.AddrOfPinnedObject(), (nuint)bytes.Length);
                    readError = count < 0 ? Marshal.GetLastPInvokeError() : 0;
                }
                finally { pinned.Free(); }
                if (count > 0)
                {
                    if (count != bytes.Length) Array.Resize(ref bytes, checked((int)count));
                    yield return new WorkerTerminalOutput(bytes);
                    continue;
                }
                if (count == 0) yield break;

                if (readError == 4) continue;
                if (readError is 11 or 35) continue;
                if (readError == 5 && hungUp) yield break;
                if (_disposed != 0) yield break;
                throw new Win32Exception(readError, "PTY read failed.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), timeout.Token).ConfigureAwait(false);
                using var stream = new NetworkStream(socket);
                await WorkerProtocol.WriteFrameAsync(stream, new { operation = "viewer-stop", viewerHandle = _handle }, timeout.Token).ConfigureAwait(false);
                using var response = await WorkerProtocol.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
                if (response is null || response.RootElement.ValueKind != JsonValueKind.Object ||
                    !response.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                    throw new WorkerProtocolException("The fixed supervisor viewer stop result is uncertain.");
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or WorkerProtocolException)
            {
                // The descriptor is already closed. Supervisor PID1 independently reaps an exited viewer
                // and terminates any surviving viewer when the container stops.
            }
            finally { _fileHandle.Dispose(); }
        }

        private const short PollIn = 0x001;
        private const short PollOut = 0x004;
        private const short PollErr = 0x008;
        private const short PollHup = 0x010;
        private const short PollNval = 0x020;
        [StructLayout(LayoutKind.Sequential)] private struct WinSize { public ushort Rows; public ushort Columns; public ushort XPixel; public ushort YPixel; }
        [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Descriptor; public short Events; public short ReturnedEvents; }
        [LibraryImport("libc", SetLastError = true)] private static partial int ioctl(SafeFileHandle descriptor, ulong request, ref WinSize size);
        [LibraryImport("libc", SetLastError = true)] private static partial int fcntl(int descriptor, int command, int argument);
        [LibraryImport("libc", SetLastError = true)] private static partial long write(int descriptor, IntPtr buffer, nuint count);
        [LibraryImport("libc", SetLastError = true)] private static partial long read(int descriptor, IntPtr buffer, nuint count);
        [LibraryImport("libc", SetLastError = true)] private static partial int poll(ref PollFd descriptor, nuint count, int timeoutMilliseconds);
    }
}

internal static class WorkerViewerProtocol
{
    public static byte[] DecodeInput(string encoded)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException exception) { throw new WorkerProtocolException("Viewer input encoding is invalid.", exception); }
        if (bytes.Length > WorkerProtocol.MaxViewerInputBytes) { CryptographicOperations.ZeroMemory(bytes); throw new WorkerProtocolException("Viewer input exceeds the fixed limit."); }
        try { _ = new UTF8Encoding(false, true).GetString(bytes); return bytes; }
        catch (DecoderFallbackException exception) { CryptographicOperations.ZeroMemory(bytes); throw new WorkerProtocolException("Viewer input must be valid UTF-8.", exception); }
    }
}
