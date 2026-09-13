using System.Diagnostics;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Exercises <c>scripts/init-secrets.py</c> without Docker. The container-side
/// initializer is loaded with <c>runpy</c> (the <c>__main__</c> guard keeps the
/// Docker calls from running) and then executed against a temporary directory to
/// prove atomic, non-overwriting publication and that the credential is never
/// printed.
/// </summary>
public sealed class SecretInitializationTests
{
    [Fact]
    public void ScriptIsSplitIntoGuardAndRemoteInitializer()
    {
        var script = RequireScript();
        var text = File.ReadAllText(script);

        Assert.Contains("if __name__ == \"__main__\"", text, StringComparison.Ordinal);
        Assert.Contains("REMOTE_CODE", text, StringComparison.Ordinal);
        Assert.Contains("stream.write(secrets.token_urlsafe(32)", text, StringComparison.Ordinal);
        Assert.Contains("os.chmod(temp_path, 0o600)", text, StringComparison.Ordinal);
        Assert.Contains("os.chown(temp_path, 1000, 1000)", text, StringComparison.Ordinal);
        Assert.Contains("os.link(temp_path, path)", text, StringComparison.Ordinal);
        Assert.Contains("refusing to overwrite", text, StringComparison.Ordinal);

        // The credential is written to the file stream, never printed.
        Assert.DoesNotContain("print(secrets.token_urlsafe", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFilePublishesAUsablePasswordAtomically()
    {
        var python = RequirePython();
        if (python is null)
        {
            return; // python3 is provided in CI.
        }

        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "owner-password");

        var result = RunRemoteInitializer(python, target);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.True(File.Exists(target));
        Assert.Contains("Created owner password", result.StandardOutput, StringComparison.Ordinal);

        var password = File.ReadAllText(target).Trim();
        Assert.True(password.Length >= 24, $"generated password was only {password.Length} characters");
        Assert.DoesNotContain(password, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(password, result.StandardError, StringComparison.Ordinal);

        // Ownership/permissions are applied before the file is linked into place.
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(target);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        // No partially written temporary file remains at a discoverable path.
        Assert.Empty(Directory.GetFiles(directory.Path, ".owner-password.*"));
    }

    [Fact]
    public void ExistingValidPasswordIsPreserved()
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        const string existing = "existing-owner-password-value-0000000000";
        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "owner-password");
        File.WriteAllText(target, existing);

        var result = RunRemoteInitializer(python, target);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("preserved", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existing, File.ReadAllText(target));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("short")]
    public void ExistingBlankOrShortPasswordFailsWithoutOverwriting(string existing)
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "owner-password");
        File.WriteAllText(target, existing);

        var result = RunRemoteInitializer(python, target);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("refusing to overwrite", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(existing, File.ReadAllText(target));
    }

    [Fact]
    public void ExistingShortSecretIsNeverPrinted()
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        const string secret = "leaky-existing-secret";
        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "owner-password");
        File.WriteAllText(target, secret);

        var result = RunRemoteInitializer(python, target);

        Assert.DoesNotContain(secret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.StandardError, StringComparison.Ordinal);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunRemoteInitializer(string python, string target)
    {
        var code = LoadRemoteCode(python);
        Assert.False(string.IsNullOrWhiteSpace(code));

        return Run(python, new[] { "-c", code, target });
    }

    private static string LoadRemoteCode(string python)
    {
        const string harness = """
            import runpy
            import sys

            module = runpy.run_path(sys.argv[1])
            sys.stdout.write(module["REMOTE_CODE"])
            """;

        var result = Run(python, new[] { "-c", harness, RequireScript() });
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput;
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Run(string python, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("python3 did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }

    private static string RequireScript() =>
        RequireRepositoryRoot() is { } root
            ? Path.Combine(root, "scripts", "init-secrets.py")
            : throw new InvalidOperationException("could not locate scripts/init-secrets.py");

    private static string? RequireRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "init-secrets.py")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? RequirePython() => FindOnPath("python3");

    private static string? FindOnPath(string name)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-secrets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
