using System.Diagnostics;
using System.Text;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Exercises the release validator that the release workflow uses to interpret
/// the operator-supplied version. The validator must reject anything that is
/// not a strict, shell-safe semantic version and must never be trusted to run
/// a shell.
/// </summary>
public sealed class ReleaseValidatorTests
{
    [Fact]
    public void VersionMatchingDirectoryBuildPropsIsAccepted()
    {
        var python = RequirePython();
        var repo = RequireRepositoryRoot();
        if (python is null || repo is null)
        {
            // python3 is not present in this environment; CI installs it.
            return;
        }

        using var fixture = new TempDirectory();
        var props = fixture.WriteFile("Directory.Build.props", Props("999.999.999"));

        var result = RunValidator(python, "--version", "999.999.999", "--props", props, "--repo", repo);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("version=999.999.999", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("tag=v999.999.999", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionNotMatchingDirectoryBuildPropsIsRejected()
    {
        var python = RequirePython();
        var repo = RequireRepositoryRoot();
        if (python is null || repo is null)
        {
            return;
        }

        using var fixture = new TempDirectory();
        var props = fixture.WriteFile("Directory.Build.props", Props("0.1.0"));

        var result = RunValidator(python, "--version", "0.2.0", "--props", props, "--repo", repo);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not match", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("01.0.0")]
    [InlineData("1.2.3-alpha+meta")]
    [InlineData("1.2.3; rm -rf /")]
    [InlineData("1.2.3\n4.5.6")]
    [InlineData("1.2.3\n")]
    [InlineData("v1.2.3")]
    [InlineData("")]
    public void MalformedVersionsAreRejectedWithoutReachingAGitTag(string version)
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        var result = RunValidator(python, "--version", version);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("strict semantic version", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingGitTagIsRejected()
    {
        var python = RequirePython();
        var git = FindOnPath("git");
        if (python is null || git is null)
        {
            return;
        }

        using var fixture = new TempDirectory();
        var props = fixture.WriteFile("Directory.Build.props", Props("1.2.3"));
        RunProcess(git, fixture.Path, "-c", "init.defaultBranch=main", "init", "-q");
        RunProcess(git, fixture.Path, "config", "user.email", "ci@example.invalid");
        RunProcess(git, fixture.Path, "config", "user.name", "CI");
        fixture.WriteFile("README.md", "fixture\n");
        RunProcess(git, fixture.Path, "add", "README.md");
        RunProcess(git, fixture.Path, "commit", "-q", "-m", "fixture");
        RunProcess(git, fixture.Path, "tag", "v1.2.3");

        var result = RunValidator(python, "--version", "1.2.3", "--props", props, "--repo", fixture.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("already exists", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangelogSectionIsExtractedForReleaseNotes()
    {
        var python = RequirePython();
        if (python is null)
        {
            return;
        }

        using var fixture = new TempDirectory();
        var changelog = fixture.WriteFile(
            "CHANGELOG.md",
            "# Changelog\n\n## [0.1.0] - 2026-09-12\n\n### Added\n- First slice.\n\n## [0.0.1]\n- Old.\n");

        var result = RunValidator(python, "--extract-notes", "0.1.0", changelog);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("First slice.", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Old.", result.StandardOutput, StringComparison.Ordinal);
    }

    private static string Props(string version) =>
        $"<Project>\n  <PropertyGroup>\n    <Version>{version}</Version>\n  </PropertyGroup>\n</Project>\n";

    private static (int ExitCode, string StandardOutput, string StandardError) RunValidator(string python, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(RequireScript());
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Run(startInfo);
    }

    private static void RunProcess(string executable, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var result = Run(startInfo);
        Assert.True(result.ExitCode == 0, $"{executable} failed: {result.StandardError}");
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Run(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{startInfo.FileName} did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }

    private static string RequireScript() =>
        RequireRepositoryRoot() is { } root
            ? Path.Combine(root, "scripts", "check-release.py")
            : throw new InvalidOperationException("could not locate scripts/check-release.py");

    private static string? RequireRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "check-release.py")))
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcontrol-release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteFile(string name, string contents)
        {
            var target = System.IO.Path.Combine(Path, name);
            var directory = System.IO.Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(target, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return target;
        }

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
