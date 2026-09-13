using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class AcpFakeServerTests
{
    [Fact]
    public void CreatingExecutableLeavesCanonicalFixtureUnchanged()
    {
        var canonicalPath = AcpFakeServer.PythonScriptPath;
        Assert.True(File.Exists(canonicalPath));

        var canonical = new FileInfo(canonicalPath);
        Assert.Null(canonical.LinkTarget);
        Assert.False(File.Exists(Path.Combine(canonical.DirectoryName!, AcpFakeServer.ScenarioSidecarFileName)));

        var bytesBefore = File.ReadAllBytes(canonicalPath);
        var writtenBefore = File.GetLastWriteTimeUtc(canonicalPath);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(canonicalPath).HasFlag(UnixFileMode.UserExecute));
        }

        var executable = AcpFakeServer.CreateExecutable("init_error");
        try
        {
            // Creating a per-test executable must never touch the canonical file
            // or drop a sidecar next to it: the canonical inode stays stable.
            Assert.True(File.Exists(executable));
        }
        finally
        {
            DeleteOwnedExecutable(executable);
        }

        Assert.Equal(bytesBefore, File.ReadAllBytes(canonicalPath));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(canonicalPath));
    }

    [Fact]
    public void CreateExecutableReturnsUniqueSymlinkWithNonExecutableScenarioSidecar()
    {
        var first = AcpFakeServer.CreateExecutable("init_error");
        var second = AcpFakeServer.CreateExecutable("prompt_fast");
        try
        {
            Assert.NotEqual(first, second);
            var firstDirectory = Path.GetDirectoryName(first)!;
            var secondDirectory = Path.GetDirectoryName(second)!;
            Assert.NotEqual(firstDirectory, secondDirectory);

            var target = File.ResolveLinkTarget(first, returnFinalTarget: true);
            Assert.NotNull(target);
            Assert.Equal(
                Path.GetFullPath(AcpFakeServer.PythonScriptPath),
                Path.GetFullPath(target!.FullName));

            var sidecar = Path.Combine(firstDirectory, AcpFakeServer.ScenarioSidecarFileName);
            Assert.Equal("init_error", File.ReadAllText(sidecar));
            if (!OperatingSystem.IsWindows())
            {
                Assert.False(File.GetUnixFileMode(sidecar).HasFlag(UnixFileMode.UserExecute));
            }
        }
        finally
        {
            DeleteOwnedExecutable(first);
            DeleteOwnedExecutable(second);
        }
    }

    [Fact]
    public async Task SidecarScenarioIsAuthoritativeForSymlinksAndArgumentsForTheCanonicalFile()
    {
        var symlink = AcpFakeServer.CreateExecutable("init_error");
        var symlinkHome = Directory.CreateTempSubdirectory("acp-home-").FullName;
        var canonicalHome = Directory.CreateTempSubdirectory("acp-home-").FullName;
        try
        {
            // The runtime path passes its own argv ("acp"); the sidecar must win.
            var viaSymlink = await InitializeIsRejectedAsync(StartExecutable(symlink, symlinkHome, "acp"));
            Assert.True(viaSymlink);

            // The Start helper invokes the canonical file with the scenario as an
            // argument because no sidecar exists next to it.
            var viaCanonical = await InitializeIsRejectedAsync(AcpFakeServer.Start("init_error", canonicalHome));
            Assert.True(viaCanonical);
        }
        finally
        {
            DeleteOwnedExecutable(symlink);
            DeleteDirectory(symlinkHome);
            DeleteDirectory(canonicalHome);
        }
    }

    [Fact]
    public async Task ConcurrentSymlinksKeepTheirOwnScenarios()
    {
        var rejected = AcpFakeServer.CreateExecutable("init_error");
        var happy = AcpFakeServer.CreateExecutable("happy");
        var rejectedHome = Directory.CreateTempSubdirectory("acp-home-").FullName;
        var happyHome = Directory.CreateTempSubdirectory("acp-home-").FullName;
        try
        {
            var rejectedTask = InitializeIsRejectedAsync(StartExecutable(rejected, rejectedHome, "acp"));
            var happyTask = InitializeIsRejectedAsync(StartExecutable(happy, happyHome, "acp"));

            Assert.True(await rejectedTask);
            Assert.False(await happyTask);
        }
        finally
        {
            DeleteOwnedExecutable(rejected);
            DeleteOwnedExecutable(happy);
            DeleteDirectory(rejectedHome);
            DeleteDirectory(happyHome);
        }
    }

    [Fact]
    public async Task ConcurrentExecutableCreatesAndStartsLeaveCanonicalUntouched()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var canonicalPath = AcpFakeServer.PythonScriptPath;
        var bytesBefore = File.ReadAllBytes(canonicalPath);
        var writtenBefore = File.GetLastWriteTimeUtc(canonicalPath);

        const int workers = 6;
        const int iterations = 6;
        var executables = new ConcurrentBag<string>();
        var homes = new ConcurrentBag<string>();

        try
        {
            var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    var expectRejected = (worker + iteration) % 2 == 0;
                    var executable = AcpFakeServer.CreateExecutable(expectRejected ? "init_error" : "happy");
                    executables.Add(executable);
                    var home = Directory.CreateTempSubdirectory("acp-home-").FullName;
                    homes.Add(home);

                    var rejected = await InitializeIsRejectedAsync(StartExecutable(executable, home, "acp"));
                    Assert.Equal(expectRejected, rejected);
                }
            })).ToArray();

            // Each process interaction is bounded; await all workers before cleanup.
            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var home in homes)
            {
                DeleteDirectory(home);
            }

            foreach (var executable in executables)
            {
                DeleteOwnedExecutable(executable);
            }
        }

        Assert.Equal(bytesBefore, File.ReadAllBytes(canonicalPath));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(canonicalPath));
    }

    private static Process StartExecutable(string executable, string home, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["HOME"] = home;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the fake ACP executable.");
    }

    private static async Task<bool> InitializeIsRejectedAsync(Process process)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}");
            await process.StandardInput.FlushAsync();

            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(string.IsNullOrWhiteSpace(line));

            using var document = JsonDocument.Parse(line!);
            return document.RootElement.TryGetProperty("error", out _);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }

            process.Dispose();
        }
    }

    private static void DeleteOwnedExecutable(string executable)
    {
        var directory = Path.GetDirectoryName(executable);
        if (directory is not null && Path.GetFileName(directory).StartsWith("acp-fake-", StringComparison.Ordinal))
        {
            DeleteDirectory(directory);
        }
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
