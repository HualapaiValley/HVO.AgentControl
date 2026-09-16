using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HVO.AgentControl.RemoteWorker;

[Flags]
public enum ControllerFileModes
{
    Private0600 = 1,
    PublicKey0644 = 2,
}

/// <summary>Opens a controller-owned Linux file without following the final path component and verifies the opened inode.</summary>
public static class ControllerPrivateFile
{
    private const int O_RDONLY = 0;
    private const int O_CLOEXEC = 0x80000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_DIRECTORY = 0x10000;
    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const int AT_EMPTY_PATH = 0x1000;
    private const uint STATX_BASIC_STATS = 0x7ff;
    private const ushort S_IFMT = 0xf000;
    private const ushort S_IFREG = 0x8000;

    public static FileStream OpenRead(string path, int expectedUid, ControllerFileModes allowedModes)
    {
        if (!OperatingSystem.IsLinux())
            return OpenPortable(path, allowedModes);
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Controller-private file path must be absolute.");

        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Controller-private file has no parent directory.");
        var leaf = Path.GetFileName(fullPath);
        if (leaf.Length == 0 || leaf is "." or "..") throw new InvalidOperationException("Controller-private file name is invalid.");

        var directoryFd = open(parent, O_RDONLY | O_CLOEXEC | O_DIRECTORY | O_NOFOLLOW);
        if (directoryFd < 0) throw Failure("Controller-private file directory could not be opened safely.");
        try
        {
            var fd = openat(directoryFd, leaf, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
            if (fd < 0) throw Failure("Controller-private file could not be opened safely.");
            var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            try
            {
                var descriptor = StatDescriptor(fd);
                var pathStat = StatPath(directoryFd, leaf);
                Validate(descriptor, expectedUid, allowedModes);
                if (descriptor.DeviceMajor != pathStat.DeviceMajor || descriptor.DeviceMinor != pathStat.DeviceMinor || descriptor.Inode != pathStat.Inode)
                    throw new InvalidOperationException("Controller-private file path changed while it was opened.");
                return new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally { close(directoryFd); }
    }

    public static byte[] ReadExact(string path, int expectedUid, ControllerFileModes allowedModes, int exactBytes)
    {
        using var stream = OpenRead(path, expectedUid, allowedModes);
        var buffer = GC.AllocateUninitializedArray<byte>(exactBytes + 1);
        try
        {
            var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (count != exactBytes) throw new InvalidOperationException($"Controller-private file must contain exactly {exactBytes} bytes.");
            return buffer[..exactBytes];
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    public static int EffectiveUid => OperatingSystem.IsLinux() ? checked((int)geteuid()) : -1;

    public static void EnsurePrivateDirectory(string path, int expectedUid)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Controller-private directory path must be absolute.");
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!OperatingSystem.IsLinux()) return;
        var fd = open(path, O_RDONLY | O_CLOEXEC | O_DIRECTORY | O_NOFOLLOW);
        if (fd < 0) throw Failure("Controller-private directory could not be opened safely.");
        try
        {
            var value = StatDescriptor(fd);
            if ((value.Mode & S_IFMT) != 0x4000 || value.Uid != checked((uint)expectedUid) || (value.Mode & 0x1ff) != 0x1c0)
                throw new InvalidOperationException("Controller-private directory owner or mode is invalid.");
        }
        finally { close(fd); }
    }

    public static void PublishExclusive(string path, ReadOnlySpan<byte> bytes, int expectedUid)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new InvalidOperationException("Controller-private file has no parent directory.");
        EnsurePrivateDirectory(parent, expectedUid);
        var temporary = Path.Combine(parent, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using (OpenRead(temporary, expectedUid, ControllerFileModes.Private0600)) { }
            File.Move(temporary, path, false);
            using (OpenRead(path, expectedUid, ControllerFileModes.Private0600)) { }
        }
        catch { try { SecureUnlink(temporary, expectedUid, allowAbsent: true); } catch { } throw; }
    }

    public static void SecureUnlink(string path, int expectedUid, bool allowAbsent = false)
    {
        if (!OperatingSystem.IsLinux())
        {
            if (!File.Exists(path)) { if (allowAbsent) return; throw new FileNotFoundException("Controller-private file is absent.", path); }
            using (OpenRead(path, expectedUid, ControllerFileModes.Private0600)) { }
            File.Delete(path);
            return;
        }
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Controller-private file has no parent directory.");
        var leaf = Path.GetFileName(fullPath);
        var directoryFd = open(parent, O_RDONLY | O_CLOEXEC | O_DIRECTORY | O_NOFOLLOW);
        if (directoryFd < 0) throw Failure("Controller-private file directory could not be opened safely.");
        try
        {
            var fd = openat(directoryFd, leaf, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
            if (fd < 0)
            {
                if (allowAbsent && Marshal.GetLastPInvokeError() == 2) return;
                throw Failure("Controller-private file could not be opened safely for removal.");
            }
            try
            {
                var descriptor = StatDescriptor(fd); var pathStat = StatPath(directoryFd, leaf); Validate(descriptor, expectedUid, ControllerFileModes.Private0600);
                if (descriptor.DeviceMajor != pathStat.DeviceMajor || descriptor.DeviceMinor != pathStat.DeviceMinor || descriptor.Inode != pathStat.Inode) throw new InvalidOperationException("Controller-private file changed before removal.");
                if (unlinkat(directoryFd, leaf, 0) != 0) throw Failure("Controller-private file could not be removed safely.");
            }
            finally { close(fd); }
        }
        finally { close(directoryFd); }
    }

    private static FileStream OpenPortable(string path, ControllerFileModes allowedModes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            throw new InvalidOperationException("Controller-private file must be a regular non-link file.");
        if (!OperatingSystem.IsWindows())
        {
            var mode = (int)File.GetUnixFileMode(path) & 0x1ff;
            if (!ModeAllowed(mode, allowedModes)) throw new InvalidOperationException("Controller-private file mode is invalid.");
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static void Validate(Statx value, int expectedUid, ControllerFileModes allowedModes)
    {
        if ((value.Mode & S_IFMT) != S_IFREG || value.Links != 1) throw new InvalidOperationException("Controller-private file must be a regular single-link file.");
        if (value.Uid != checked((uint)expectedUid)) throw new InvalidOperationException("Controller-private file owner is invalid.");
        if (!ModeAllowed(value.Mode & 0x1ff, allowedModes)) throw new InvalidOperationException("Controller-private file mode is invalid.");
    }

    private static bool ModeAllowed(int mode, ControllerFileModes allowedModes) =>
        mode == 0x180 && allowedModes.HasFlag(ControllerFileModes.Private0600) ||
        mode == 0x1a4 && allowedModes.HasFlag(ControllerFileModes.PublicKey0644);

    private static Statx StatDescriptor(int fd)
    {
        if (statx(fd, string.Empty, AT_EMPTY_PATH | AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, out var value) != 0)
            throw Failure("Controller-private file descriptor could not be inspected.");
        return value;
    }

    private static Statx StatPath(int directoryFd, string leaf)
    {
        if (statx(directoryFd, leaf, AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, out var value) != 0)
            throw Failure("Controller-private file path could not be inspected.");
        return value;
    }

    private static Exception Failure(string message) => new InvalidOperationException(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    internal struct StatxTimestamp { public long Seconds; public uint Nanoseconds; public int Reserved; }

    /// <summary>
    /// The exact kernel <c>struct statx</c> (256 bytes, linux/stat.h). The field
    /// order matters: <c>stx_rdev_*</c> precedes <c>stx_dev_*</c>, and reading them
    /// the other way round would silently compare the device of a regular file
    /// (always zero) instead of the filesystem it lives on. The trailing reserved
    /// words are explicit fields rather than a marshalled array so the struct stays
    /// blittable and needs no per-call marshalling.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    internal struct Statx
    {
        public uint Mask; public uint BlockSize; public ulong Attributes; public uint Links; public uint Uid; public uint Gid; public ushort Mode; public ushort Spare0;
        public ulong Inode; public ulong Size; public ulong Blocks; public ulong AttributesMask;
        public StatxTimestamp Access; public StatxTimestamp Birth; public StatxTimestamp Change; public StatxTimestamp Modification;
        public uint RDeviceMajor; public uint RDeviceMinor; public uint DeviceMajor; public uint DeviceMinor;
        public ulong MountId; public uint DirectIoMemoryAlign; public uint DirectIoOffsetAlign;
        public ulong Spare1; public ulong Spare2; public ulong Spare3; public ulong Spare4; public ulong Spare5; public ulong Spare6;
        public ulong Spare7; public ulong Spare8; public ulong Spare9; public ulong Spare10; public ulong Spare11; public ulong Spare12;
    }

    /// <summary>Test hook: the exact kernel-reported metadata for one path, with no policy applied.</summary>
    internal static Statx StatForTests(string path)
    {
        if (statx(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, out var value) != 0) throw Failure("statx failed.");
        return value;
    }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int openat(int directoryFd, string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int directoryFd, string path, int flags, uint mask, out Statx value);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int directoryFd, string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc")] private static extern uint geteuid();
}
