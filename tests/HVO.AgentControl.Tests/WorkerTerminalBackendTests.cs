using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Worker;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WorkerTerminalBackendTests
{
    [Fact]
    public async Task ActiveOutputEnumeratorTreatsRealSlaveCloseAsCleanEof()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Directory.CreateTempSubdirectory("worker-terminal-eof-");
        try
        {
            var path = Path.Combine(directory.FullName, "supervisor.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(2);
            var closeSlave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Task.Run(async () =>
            {
                using (var start = await listener.AcceptAsync())
                {
                    using var startStream = new NetworkStream(start, ownsSocket: false);
                    using var request = await WorkerProtocol.ReadFrameAsync(startStream, CancellationToken.None);
                    Assert.Equal("viewer-start", request!.RootElement.GetProperty("operation").GetString());
                    Assert.Equal(0, openpty(out var master, out var slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
                    try
                    {
                        SendDescriptor(start, Encoding.UTF8.GetBytes("{\"ok\":true,\"viewerHandle\":\"view-eof\"}\n"), master);
                        close(master);
                        await closeSlave.Task;
                    }
                    finally { close(slave); }
                }
                using var stop = await listener.AcceptAsync();
                using var stopStream = new NetworkStream(stop, ownsSocket: false);
                using var requestStop = await WorkerProtocol.ReadFrameAsync(stopStream, CancellationToken.None);
                stopped.SetResult(requestStop!.RootElement.GetProperty("viewerHandle").GetString()!);
                await WorkerProtocol.WriteFrameAsync(stopStream, new { ok = true }, CancellationToken.None);
            });

            var backend = new SupervisorWorkerTerminalBackend(path);
            await using (var session = await backend.AttachAsync("ses-eof", CancellationToken.None))
            {
                // The enumerator is cancellable so a failed wait disposes cleanly. An
                // enumerator disposed while its MoveNextAsync is still pending throws
                // NotSupportedException, which would replace the real failure with a
                // misleading one.
                using var enumeration = new CancellationTokenSource();
                var output = session.ReadOutputAsync(enumeration.Token).GetAsyncEnumerator();
                try
                {
                    var pending = output.MoveNextAsync().AsTask();
                    closeSlave.SetResult();
                    // The PTY poll loop is 100 ms; the bound is generous because this
                    // suite also runs under parallel load, where a tight bound measures
                    // the machine rather than the EOF behaviour under test.
                    Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(30)));
                }
                finally
                {
                    await enumeration.CancelAsync();
                    try { await output.DisposeAsync(); } catch (OperationCanceledException) { }
                }
            }
            Assert.Equal("view-eof", await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            await server.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task SupervisorBackendReceivesPtyTransfersIoResizesAndStopsOpaqueHandle()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Directory.CreateTempSubdirectory("worker-terminal-");
        try
        {
            var path = Path.Combine(directory.FullName, "supervisor.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(2);
            var inputReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resized = new TaskCompletionSource<(int Rows, int Columns)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Task.Run(async () =>
            {
                using (var start = await listener.AcceptAsync())
                {
                    using var startStream = new NetworkStream(start, ownsSocket: false);
                    using var request = await WorkerProtocol.ReadFrameAsync(startStream, CancellationToken.None);
                    Assert.Equal("viewer-start", request!.RootElement.GetProperty("operation").GetString());
                    Assert.Equal("ses-fixed", request.RootElement.GetProperty("sessionId").GetString());
                    Assert.Equal(0, openpty(out var master, out var slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
                    Assert.Equal(0, tcgetattr(slave, out var terminal));
                    cfmakeraw(ref terminal);
                    Assert.Equal(0, tcsetattr(slave, 0, ref terminal));
                    try
                    {
                        SendDescriptor(start, Encoding.UTF8.GetBytes("{\"ok\":true,\"viewerHandle\":\"view-fixed\"}\n"), master);
                        Assert.Equal(5, write(slave, Encoding.UTF8.GetBytes("ready"), 5));
                        using var input = new MemoryStream();
                        var chunk = new byte[1024];
                        while (input.Length < 16 * 1024)
                        {
                            var count = read(slave, chunk, Math.Min(chunk.Length, 16 * 1024 - checked((int)input.Length)));
                            Assert.True(count > 0);
                            input.Write(chunk, 0, count);
                        }
                        inputReceived.SetResult(Convert.ToHexString(input.ToArray()));
                        var size = new WinSize();
                        while (size.Rows != 37 || size.Columns != 119)
                        {
                            Assert.Equal(0, ioctl(slave, Tiocgwinsz, ref size));
                            if (size.Rows == 37 && size.Columns == 119) break;
                            await Task.Delay(10);
                        }
                        resized.SetResult((size.Rows, size.Columns));
                    }
                    finally { close(master); close(slave); }
                }
                using var stop = await listener.AcceptAsync();
                using var stopStream = new NetworkStream(stop, ownsSocket: false);
                using var requestStop = await WorkerProtocol.ReadFrameAsync(stopStream, CancellationToken.None);
                stopped.SetResult(requestStop!.RootElement.GetProperty("viewerHandle").GetString()!);
                await WorkerProtocol.WriteFrameAsync(stopStream, new { ok = true }, CancellationToken.None);
            });

            var backend = new SupervisorWorkerTerminalBackend(path);
            Assert.True(backend.Available);
            await using (var session = await backend.AttachAsync("ses-fixed", CancellationToken.None))
            {
                await using var output = session.ReadOutputAsync(CancellationToken.None).GetAsyncEnumerator();
                Assert.True(await output.MoveNextAsync());
                Assert.Equal("ready", Encoding.UTF8.GetString(output.Current.Data));
                var expectedInput = Enumerable.Repeat((byte)'x', 16 * 1024).ToArray();
                expectedInput[^1] = (byte)'\n';
                await session.WriteInputAsync(expectedInput, CancellationToken.None);
                Assert.Equal(Convert.ToHexString(expectedInput), await inputReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)));
                await session.ResizeAsync(37, 119, CancellationToken.None);
                Assert.Equal((37, 119), await resized.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            Assert.Equal("view-fixed", await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            await server.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { directory.Delete(true); }
    }

    /// <summary>
    /// A stop that never succeeds must not be reported as a clean teardown when the
    /// supervisor still sees the viewer process alive.
    /// </summary>
    [Fact]
    public async Task UnconfirmedStopWithASurvivingViewerIsRaisedAsUncertainAfterBoundedRetries()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Directory.CreateTempSubdirectory("worker-terminal-stop-uncertain-");
        try
        {
            var path = Path.Combine(directory.FullName, "supervisor.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(8);
            var stopAttempts = 0;
            var statusRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Task.Run(async () =>
            {
                using (var start = await listener.AcceptAsync())
                {
                    using var startStream = new NetworkStream(start, ownsSocket: false);
                    using var request = await WorkerProtocol.ReadFrameAsync(startStream, CancellationToken.None);
                    Assert.Equal("viewer-start", request!.RootElement.GetProperty("operation").GetString());
                    Assert.Equal(0, openpty(out var master, out var slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
                    SendDescriptor(start, Encoding.UTF8.GetBytes("{\"ok\":true,\"viewerHandle\":\"view-stuck\"}\n"), master);
                    close(master);
                    close(slave);
                }
                while (true)
                {
                    using var next = await listener.AcceptAsync();
                    using var stream = new NetworkStream(next, ownsSocket: false);
                    using var frame = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None);
                    var operation = frame!.RootElement.GetProperty("operation").GetString();
                    if (operation == "viewer-stop") { stopAttempts++; await WorkerProtocol.WriteFrameAsync(stream, new { ok = false, error = "viewer-not-found" }, CancellationToken.None); continue; }
                    // The supervisor can still see its own child, and it is alive.
                    statusRequested.TrySetResult();
                    await WorkerProtocol.WriteFrameAsync(stream, new { ok = true, state = "running" }, CancellationToken.None);
                    return;
                }
            });

            var backend = new SupervisorWorkerTerminalBackend(path);
            var session = await backend.AttachAsync("ses-stuck", CancellationToken.None);
            await Assert.ThrowsAsync<WorkerTerminalStopUncertainException>(async () => await session.DisposeAsync());

            await statusRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, stopAttempts);
        }
        finally { directory.Delete(true); }
    }

    /// <summary>
    /// Closing the PTY master hangs up the viewer's session leader, so a supervisor
    /// that reports the process gone means the stop really did take effect.
    /// </summary>
    [Fact]
    public async Task UnconfirmedStopWithAnExitedViewerIsACleanTeardown()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Directory.CreateTempSubdirectory("worker-terminal-stop-exited-");
        try
        {
            var path = Path.Combine(directory.FullName, "supervisor.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(8);
            var server = Task.Run(async () =>
            {
                using (var start = await listener.AcceptAsync())
                {
                    using var startStream = new NetworkStream(start, ownsSocket: false);
                    using var request = await WorkerProtocol.ReadFrameAsync(startStream, CancellationToken.None);
                    Assert.Equal(0, openpty(out var master, out var slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
                    SendDescriptor(start, Encoding.UTF8.GetBytes("{\"ok\":true,\"viewerHandle\":\"view-gone\"}\n"), master);
                    close(master);
                    close(slave);
                }
                while (true)
                {
                    using var next = await listener.AcceptAsync();
                    using var stream = new NetworkStream(next, ownsSocket: false);
                    using var frame = await WorkerProtocol.ReadFrameAsync(stream, CancellationToken.None);
                    if (frame!.RootElement.GetProperty("operation").GetString() == "viewer-stop") { await WorkerProtocol.WriteFrameAsync(stream, new { ok = false, error = "viewer-not-found" }, CancellationToken.None); continue; }
                    await WorkerProtocol.WriteFrameAsync(stream, new { ok = true, state = "exited" }, CancellationToken.None);
                    return;
                }
            });

            var backend = new SupervisorWorkerTerminalBackend(path);
            var session = await backend.AttachAsync("ses-gone", CancellationToken.None);
            await session.DisposeAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { directory.Delete(true); }
    }

    private static void SendDescriptor(Socket socket, byte[] payload, int descriptor)
    {
        var payloadHandle = GCHandle.Alloc(payload, GCHandleType.Pinned);
        var vectorPointer = Marshal.AllocHGlobal(Marshal.SizeOf<Iovec>());
        var controlLength = Align((nuint)Marshal.SizeOf<Cmsghdr>()) + Align(sizeof(int));
        var control = Marshal.AllocHGlobal(checked((int)controlLength));
        try
        {
            Marshal.StructureToPtr(new Iovec { Base = payloadHandle.AddrOfPinnedObject(), Length = (nuint)payload.Length }, vectorPointer, false);
            Marshal.StructureToPtr(new Cmsghdr { Length = Align((nuint)Marshal.SizeOf<Cmsghdr>()) + sizeof(int), Level = 1, Type = 1 }, control, false);
            Marshal.WriteInt32(control + checked((int)Align((nuint)Marshal.SizeOf<Cmsghdr>())), descriptor);
            var message = new Msghdr { Iov = vectorPointer, IovLength = 1, Control = control, ControlLength = controlLength };
            Assert.Equal(payload.Length, sendmsg(socket.Handle.ToInt32(), ref message, 0));
        }
        finally
        {
            Marshal.FreeHGlobal(control);
            Marshal.FreeHGlobal(vectorPointer);
            payloadHandle.Free();
        }
    }

    private static nuint Align(nuint value) { var alignment = (nuint)IntPtr.Size; return (value + alignment - 1) & ~(alignment - 1); }
    private const ulong Tiocgwinsz = 0x5413;
    [StructLayout(LayoutKind.Sequential)] private struct Iovec { public IntPtr Base; public nuint Length; }
    [StructLayout(LayoutKind.Sequential)] private struct Msghdr { public IntPtr Name; public uint NameLength; public IntPtr Iov; public nuint IovLength; public IntPtr Control; public nuint ControlLength; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Cmsghdr { public nuint Length; public int Level; public int Type; }
    [StructLayout(LayoutKind.Sequential)] private struct WinSize { public ushort Rows; public ushort Columns; public ushort XPixel; public ushort YPixel; }
    [StructLayout(LayoutKind.Sequential)] private struct Termios { public uint Input; public uint Output; public uint Control; public uint Local; public byte Line; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ControlChars; public uint InputSpeed; public uint OutputSpeed; }
    [DllImport("libc", SetLastError = true)] private static extern long sendmsg(int socket, ref Msghdr message, int flags);
    [DllImport("libutil.so.1", SetLastError = true)] private static extern int openpty(out int master, out int slave, IntPtr name, IntPtr termios, IntPtr winsize);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int descriptor, ulong request, ref WinSize size);
    [DllImport("libc", SetLastError = true)] private static extern int write(int descriptor, byte[] data, int count);
    [DllImport("libc", SetLastError = true)] private static extern int read(int descriptor, byte[] data, int count);
    [DllImport("libc", SetLastError = true)] private static extern int close(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern int tcgetattr(int descriptor, out Termios value);
    [DllImport("libc", SetLastError = true)] private static extern int tcsetattr(int descriptor, int actions, ref Termios value);
    [DllImport("libc")] private static extern void cfmakeraw(ref Termios value);
}
