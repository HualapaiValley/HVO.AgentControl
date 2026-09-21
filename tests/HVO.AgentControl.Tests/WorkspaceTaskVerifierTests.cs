using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class WorkspaceTaskVerifierTests
{
    [Fact]
    public void VerifierRejectsTraversalSymlinksAndBoundsAndProducesDeterministicManifest()
    {
        using var temp = new TemporaryDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        var project = Path.Combine(workspace, "project");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "src", "b.txt"), "b");
        File.WriteAllText(Path.Combine(project, "src", "a.txt"), "a");
        var fake = FakeDotnet(temp.Path, "pass");

        var first = Run(workspace, fake, "project", ["src"], 10, 1024);
        var second = Run(workspace, fake, "project", ["src"], 10, 1024);
        Assert.Equal("passed", first.GetProperty("state").GetString());
        Assert.Equal(first.GetProperty("manifest").GetRawText(), second.GetProperty("manifest").GetRawText());
        Assert.Equal(["src/a.txt", "src/b.txt"], first.GetProperty("manifest").GetProperty("files").EnumerateArray().Select(x => x.GetProperty("path").GetString()!).ToArray());

        var traversal = Run(workspace, fake, "../project", ["src"], 10, 1024);
        Assert.Equal("failed", traversal.GetProperty("state").GetString());
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(project, "link"), Path.Combine(project, "src"));
            var symlink = Run(workspace, fake, "project", ["link"], 10, 1024);
            Assert.Equal("symlink-refused", symlink.GetProperty("failureDetail").GetString());
        }
        Assert.Equal("file-count-exceeded", Run(workspace, fake, "project", ["src"], 1, 1024).GetProperty("failureDetail").GetString());
        Assert.Equal("file-bytes-exceeded", Run(workspace, fake, "project", ["src"], 10, 1).GetProperty("failureDetail").GetString());
    }

    [Fact]
    public void VerifierRunsRealDotnetAgainstWritableCopyOfReadOnlySource()
    {
        if (!File.Exists("/usr/bin/dotnet")) return;
        using var temp = new TemporaryDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        var project = Path.Combine(workspace, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "CopyFixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Class1.cs"), "public static class Class1 { public static int Value => 1; }");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(project, "CopyFixture.csproj"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.SetUnixFileMode(Path.Combine(project, "Class1.cs"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        var result = RunReal(workspace, "project", ["."], 20, 1024 * 1024, 60);
        Assert.Equal("passed", result.GetProperty("state").GetString());
        Assert.False(Directory.Exists(Path.Combine(project, "obj")));
        Assert.False(Directory.Exists(Path.Combine(project, "bin")));
    }

    [Theory]
    [InlineData("pass", 0, "passed", false)]
    [InlineData("fail", 0, "failed", false)]
    [InlineData("timeout", 1, "failed", true)]
    [InlineData("large", 0, "passed", false)]
    public void VerifierReportsPassFailTimeoutAndOutputBounds(string mode, int timeoutOverride, string state, bool timedOut)
    {
        using var temp = new TemporaryDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        var project = Path.Combine(workspace, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "x.txt"), "x");
        var result = Run(workspace, FakeDotnet(temp.Path, mode), "project", ["."], 10, 1024, timeoutOverride == 0 ? 10 : timeoutOverride, mode);
        Assert.Equal(state, result.GetProperty("state").GetString());
        var summary = result.GetProperty("testSummary");
        Assert.Equal(timedOut, summary.GetProperty("timedOut").GetBoolean());
        Assert.True(summary.GetProperty("stdout").GetString()!.Length <= 64 * 1024);
        if (mode == "large") Assert.True(summary.GetProperty("stdoutTruncated").GetBoolean());
    }

    private static JsonElement Run(string workspace, string fakeDotnet, string root, string[] allowed, int maxFiles, long maxBytes, int timeout = 10, string mode = "pass")
    {
        var script = Path.Combine(ControllerIsolationLayoutTests.RepositoryRoot(), "src/container/workspace-task-verify.py");
        var testRoot = Directory.GetParent(workspace)!.FullName;
        var shadow = Path.Combine(testRoot, "shadow.py");
        var copyRoot = Path.Combine(testRoot, "copy-" + Guid.NewGuid().ToString("N"));
        var text = File.ReadAllText(script).Replace("WORKSPACE = \"/workspace\"", $"WORKSPACE = {JsonSerializer.Serialize(workspace)}", StringComparison.Ordinal)
            .Replace("COPY_ROOT = \"/tmp/workspace-copy\"", $"COPY_ROOT = {JsonSerializer.Serialize(copyRoot)}", StringComparison.Ordinal)
            .Replace("DOTNET = \"/usr/bin/dotnet\"", "DOTNET = \"/usr/bin/python3\"", StringComparison.Ordinal)
            .Replace("argv = [DOTNET, \"test\", \"--configuration\", \"Release\"]", $"argv = [DOTNET, {JsonSerializer.Serialize(fakeDotnet)}]", StringComparison.Ordinal);
        File.WriteAllText(shadow, text);
        using var process = new Process { StartInfo = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var value in new[] { "-I", "-S", shadow, "--root", root, "--recipe", "dotnet-test-release", "--maximum-seconds", timeout.ToString(), "--max-files", maxFiles.ToString(), "--max-bytes", maxBytes.ToString() }) process.StartInfo.ArgumentList.Add(value);
        foreach (var path in allowed) { process.StartInfo.ArgumentList.Add("--allowed-path"); process.StartInfo.ArgumentList.Add(path); }
        var modeDirectory = Path.GetFullPath(Path.Combine(workspace, root));
        if (modeDirectory.StartsWith(Path.GetFullPath(workspace) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Directory.Exists(modeDirectory))
            File.WriteAllText(Path.Combine(modeDirectory, ".fake-dotnet-mode"), mode);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20_000), "verifier timed out");
        Assert.Equal(string.Empty, error);
        return JsonDocument.Parse(output).RootElement.Clone();
    }

    private static JsonElement RunReal(string workspace, string root, string[] allowed, int maxFiles, long maxBytes, int timeout)
    {
        var script = Path.Combine(ControllerIsolationLayoutTests.RepositoryRoot(), "src/container/workspace-task-verify.py");
        var shadow = Path.Combine(Path.GetDirectoryName(workspace)!, "real-shadow.py");
        var copyRoot = Path.Combine(Path.GetDirectoryName(workspace)!, "real-copy");
        var text = File.ReadAllText(script)
            .Replace("WORKSPACE = \"/workspace\"", $"WORKSPACE = {JsonSerializer.Serialize(workspace)}", StringComparison.Ordinal)
            .Replace("COPY_ROOT = \"/tmp/workspace-copy\"", $"COPY_ROOT = {JsonSerializer.Serialize(copyRoot)}", StringComparison.Ordinal)
            .Replace("value.st_uid != 0", "value.st_uid != os.stat(DOTNET).st_uid", StringComparison.Ordinal);
        File.WriteAllText(shadow, text);
        using var process = new Process { StartInfo = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var value in new[] { "-I", "-S", shadow, "--root", root, "--recipe", "dotnet-test-release", "--maximum-seconds", timeout.ToString(), "--max-files", maxFiles.ToString(), "--max-bytes", maxBytes.ToString() }) process.StartInfo.ArgumentList.Add(value);
        foreach (var path in allowed) { process.StartInfo.ArgumentList.Add("--allowed-path"); process.StartInfo.ArgumentList.Add(path); }
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit((timeout + 10) * 1000), "real verifier timed out");
        Assert.Equal(string.Empty, error);
        return JsonDocument.Parse(output).RootElement.Clone();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "workspace-verifier-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string FakeDotnet(string directory, string mode)
    {
        _ = mode;
        _ = directory;
        return Path.Combine(ControllerIsolationLayoutTests.RepositoryRoot(), "tests/HVO.AgentControl.Tests/Fixtures/fake-dotnet.py");
    }
}
