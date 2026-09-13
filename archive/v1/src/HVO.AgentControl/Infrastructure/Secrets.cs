using System.Text.RegularExpressions;
using System.Security.Cryptography;
using HVO.AgentControl.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class Secrets(IOptions<ControlOptions> options, IDataProtectionProvider? protection = null)
{
    private const string VaultPrefix = "vault-";
    public string PathFor(string reference)
    {
        if (!NamePattern().IsMatch(reference))
            throw new ControlException("Secret references must be file names containing letters, digits, dots, underscores or hyphens.", 400);
        var encrypted = reference.StartsWith(VaultPrefix, StringComparison.Ordinal);
        if (encrypted && !Guid.TryParseExact(reference[VaultPrefix.Length..], "N", out _))
            throw new ControlException("Invalid encrypted credential reference.", 400);
        var root = Path.GetFullPath(encrypted ? Path.Combine(options.Value.DataDirectory, "credentials") : options.Value.SecretsDirectory);
        if (new DirectoryInfo(root).LinkTarget is not null) throw new ControlException("Secret directories cannot be symbolic links.", 400);
        var path = Path.Combine(root, reference);
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            throw new ControlException("Secret references cannot be symbolic links.", 400);
        return path;
    }

    public string Read(string reference)
    {
        var path = PathFor(reference);
        if (!File.Exists(path)) throw new ControlException($"Secret '{reference}' is missing.", 400);
        if (new FileInfo(path).Length > 128 * 1024) throw new ControlException("Secret file exceeds the supported size.", 400);
        var text = File.ReadAllText(path);
        if (!reference.StartsWith(VaultPrefix, StringComparison.Ordinal)) return text.TrimEnd('\r', '\n');
        try { return Protector(reference).Unprotect(text); }
        catch (CryptographicException) { throw new ControlException("Stored credential cannot be decrypted. Restore its original data protection keys or enter the credential again."); }
    }

    public string StoreEncrypted(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64000 || value.Contains('\0')) throw new ControlException("Credential must contain 1–64000 characters without NUL.", 400);
        var reference = VaultPrefix + Guid.NewGuid().ToString("N");
        var path = PathFor(reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var content = Protector(reference).Protect(value);
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, fileOptions);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
        return reference;
    }

    private IDataProtector Protector(string reference) => (protection ?? throw new InvalidOperationException("Persistent data protection is required for stored credentials."))
        .CreateProtector("HVO.AgentControl.StoredCredential.v1", reference);

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,100}$")]
    private static partial Regex NamePattern();
}

public sealed class ReplicaLock : IDisposable
{
    private readonly FileStream stream;
    public ReplicaLock(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        try { stream = new FileStream(Path.Combine(dataDirectory, "control.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another AgentControl process owns this data directory. SQLite supports one replica."); }
    }
    public void Dispose() => stream.Dispose();
}
