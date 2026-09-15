using Microsoft.Win32.SafeHandles;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HVO.AgentControl.Worker;

public static class WorkerKeyBootstrap
{
    public const string KeyFileName = "bridge.key";
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string Bootstrap(string controlDirectory, string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlDirectory);
        var key = WorkerProtocol.ParseKey(input);
        try
        {
            var uid = EffectiveUid(); WorkerStore.ValidateControlDirectory(controlDirectory, uid);
            var path = Path.Combine(controlDirectory, KeyFileName);
            if (File.Exists(path)) return VerifyExisting(path, key, uid);
            var temporary = Path.Combine(controlDirectory, $".{KeyFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = OpenNewNoFollow(temporary))
                {
                    stream.Write(key); stream.Flush(true);
                    if (!OperatingSystem.IsWindows() && fchmod(stream.SafeFileHandle, (uint)PrivateMode) != 0) throw new WorkerProtocolException("The bridge key could not be secured.");
                }
                try { File.Move(temporary, path, false); }
                catch (IOException) when (File.Exists(path)) { TryDelete(temporary); return VerifyExisting(path, key, uid); }
                WorkerStore.ValidateAbsentOrPrivateRegular(path, allowAbsent: false, expectedUid: uid);
                FsyncDirectory(controlDirectory);
                return WorkerProtocol.KeyId(key);
            }
            catch { TryDelete(temporary); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] ReadKey(string controlDirectory)
    {
        var uid = EffectiveUid(); WorkerStore.ValidateControlDirectory(controlDirectory, uid);
        var path = Path.Combine(controlDirectory, KeyFileName); WorkerStore.ValidateAbsentOrPrivateRegular(path, allowAbsent: false, expectedUid: uid);
        using var stream = OpenExistingNoFollow(path); var bytes = new byte[33]; var count = stream.ReadAtLeast(bytes, 33, throwOnEndOfStream: false);
        if (count == 32) return bytes[..32];
        CryptographicOperations.ZeroMemory(bytes); throw new WorkerProtocolException("The enrolled bridge key has an invalid length.");
    }

    private static string VerifyExisting(string path, byte[] key, int uid)
    {
        WorkerStore.ValidateAbsentOrPrivateRegular(path, allowAbsent: false, expectedUid: uid);
        var existing = ReadExactNoFollow(path);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(existing, key)) throw new WorkerProtocolException("A different bridge key is already enrolled; bootstrap never overwrites it.");
            return WorkerProtocol.KeyId(existing);
        }
        finally { CryptographicOperations.ZeroMemory(existing); }
    }

    private static byte[] ReadExactNoFollow(string path)
    {
        using var stream = OpenExistingNoFollow(path); var bytes = new byte[33]; var count = stream.ReadAtLeast(bytes, 33, throwOnEndOfStream: false);
        if (count == 32) return bytes[..32]; CryptographicOperations.ZeroMemory(bytes); throw new WorkerProtocolException("The enrolled bridge key has an invalid length.");
    }

    private static FileStream OpenNewNoFollow(string path)
    {
        if (!OperatingSystem.IsLinux()) return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        var fd = open(path, 0xC1 | 0x20000 | 0x80000, 0x180); // O_WRONLY|O_CREAT|O_EXCL|O_NOFOLLOW|O_CLOEXEC, 0600
        if (fd < 0) throw new WorkerProtocolException("The bridge key temporary could not be created safely.");
        return new FileStream(new SafeFileHandle((IntPtr)fd, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
    }

    private static FileStream OpenExistingNoFollow(string path)
    {
        if (!OperatingSystem.IsLinux()) return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fd = open(path, 0x20000 | 0x80000, 0); // O_RDONLY|O_NOFOLLOW|O_CLOEXEC
        if (fd < 0) throw new WorkerProtocolException("The enrolled bridge key could not be opened safely.");
        return new FileStream(new SafeFileHandle((IntPtr)fd, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
    }

    private static int EffectiveUid() => OperatingSystem.IsWindows() ? -1 : checked((int)geteuid());
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static void FsyncDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var fd = open(path, 0x10000 | 0x80000 | 0x20000, 0);
        if (fd < 0) throw new WorkerProtocolException("The worker control directory could not be synchronized.");
        try { if (fsync(fd) != 0) throw new WorkerProtocolException("The worker control directory could not be synchronized."); }
        finally { if (close(fd) != 0) throw new WorkerProtocolException("The worker control directory descriptor could not be closed cleanly."); }
    }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc", SetLastError = true)] private static extern int fchmod(SafeFileHandle fd, uint mode);
}
