using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Organization;

namespace HVO.AgentControl.Runtime;

/// <summary>Publishes controller-owned, agent-readable orientation artifacts atomically.</summary>
/// <remarks>
/// Publication is serialized process-wide. The rename itself is atomic, but the
/// surrounding write/verify sequence is not: two concurrent publishers could
/// otherwise verify an inode a competing publisher had already replaced, and the
/// post-rename ownership/link checks would observe a transient foreign state.
/// The control host owns a single agent, so an in-process gate is sufficient;
/// cross-process publication is not a supported configuration.
/// </remarks>
public static class OrientationArtifactPublisher
{
    private static readonly object PublicationGate = new();

    public static void Publish(string directory, OrientationArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(artifact);
        lock (PublicationGate)
        {
            PublishExclusive(directory, artifact);
        }
    }

    private static void PublishExclusive(string directory, OrientationArtifact artifact)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new IOException("The orientation directory does not exist.");
        }

        if (Path.GetFileName(artifact.ArtifactFileName) != artifact.ArtifactFileName
            || artifact.ArtifactFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new IOException("The orientation artifact file name is invalid.");
        }

        EnsureDirectoryIsNotLink(root);
        var destination = Path.Combine(root, artifact.ArtifactFileName);
        ValidateExistingDestination(destination);
        var temporary = Path.Combine(
            root,
            "." + artifact.ArtifactFileName + "." + RandomNumberGenerator.GetHexString(8) + ".tmp");
        var bytes = new UTF8Encoding(false, true).GetBytes(artifact.Content);

        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            SetReadableMode(temporary);
            ValidateRegularSingleLinkOwnedFile(temporary);
            File.Move(temporary, destination, overwrite: true);
            SetReadableMode(destination);

            // Open the published name with no-follow and verify the inode content
            // after the atomic rename, rather than trusting the temporary handle.
            using var published = OpenPublishedNoFollow(destination);
            ValidateRegularSingleLinkOwnedFile(published.SafeFileHandle, destination);
            using var retained = new MemoryStream();
            published.CopyTo(retained);
            var retainedBytes = retained.ToArray();
            if (!retainedBytes.AsSpan().SequenceEqual(bytes)
                || !string.Equals(
                    OrientationComposer.Version(Encoding.UTF8.GetString(retainedBytes)),
                    artifact.OrientationVersion,
                    StringComparison.Ordinal))
            {
                throw new IOException("The published orientation artifact did not verify against its assigned version.");
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void EnsureDirectoryIsNotLink(string root)
    {
        var info = new DirectoryInfo(root);
        if (info.LinkTarget is not null)
        {
            throw new IOException("The orientation directory must not be a symbolic link.");
        }
    }

    private static void ValidateExistingDestination(string destination)
    {
        if (!File.Exists(destination))
        {
            return;
        }

        if (new FileInfo(destination).LinkTarget is not null)
        {
            throw new IOException("The orientation artifact must not be a symbolic link.");
        }

        ValidateRegularSingleLinkOwnedFile(destination);
    }

    private static FileStream OpenPublishedNoFollow(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        const int readOnly = 0;
        const int closeOnExec = 0x80000;
        const int noFollow = 0x20000;
        var descriptor = Open(path, readOnly | closeOnExec | noFollow);
        if (descriptor < 0)
        {
            throw new IOException(
                "The published orientation artifact could not be opened safely.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        return new FileStream(handle, FileAccess.Read);
    }

    private static void ValidateRegularSingleLinkOwnedFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        ValidateRegularSingleLinkOwnedFile(stream.SafeFileHandle, path);
    }

    private static void ValidateRegularSingleLinkOwnedFile(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        EnsureSupportedLinuxArchitecture(RuntimeInformation.ProcessArchitecture);
        if (FStat(handle.DangerousGetHandle().ToInt32(), out var status) != 0)
        {
            throw new IOException(
                $"Could not inspect orientation artifact '{path}'.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        const uint fileTypeMask = 0xF000;
        const uint regularFile = 0x8000;
        if ((status.Mode & fileTypeMask) != regularFile)
        {
            throw new IOException("The orientation artifact must be a regular file.");
        }

        if (status.LinkCount != 1)
        {
            throw new IOException("The orientation artifact must have exactly one hard link.");
        }

        if (status.UserId != GetEffectiveUserId())
        {
            throw new IOException("The orientation artifact must be owned by the controller identity.");
        }
    }

    internal static void EnsureSupportedLinuxArchitecture(Architecture architecture)
    {
        if (architecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "Secure orientation publication currently supports Linux x64 only; refusing to validate with an incompatible native stat layout.");
        }
    }

    private static void SetReadableMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string pathname, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, out LinuxStat status);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public uint Padding;
        public ulong RDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public long AccessSeconds;
        public long AccessNanoseconds;
        public long ModifySeconds;
        public long ModifyNanoseconds;
        public long ChangeSeconds;
        public long ChangeNanoseconds;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }
}
