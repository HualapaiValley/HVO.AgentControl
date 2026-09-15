using System.Diagnostics;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class TerminalBridgeScriptTests
{
    [Fact]
    public void BridgeScriptExistsUnderTerminalOwnership()
    {
        Assert.NotNull(ResolveBridgePath());
    }

    [Fact]
    public void BridgeScriptAttachesTmuxWithoutFallbackShellOrNetworkListener()
    {
        var path = ResolveBridgePath();
        Assert.NotNull(path);

        var text = File.ReadAllText(path!);
        Assert.Contains("attach-session", text, StringComparison.Ordinal);
        Assert.Contains("\"-t\"", text, StringComparison.Ordinal);
        Assert.Contains("TIOCSCTTY", text, StringComparison.Ordinal);
        Assert.Contains("base64.b64encode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("incrementaldecoder", text, StringComparison.Ordinal);
        Assert.Contains("attach_failure_message", text, StringComparison.Ordinal);

        // No fallback shell and no socket server.
        Assert.DoesNotContain("pty.fork", text, StringComparison.Ordinal);
        Assert.DoesNotContain("import socket", text, StringComparison.Ordinal);
        Assert.DoesNotContain("socket.socket", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeHelpersMatchControllerBounds()
    {
        var scriptPath = ResolveBridgePath();
        Assert.NotNull(scriptPath);

        var python = FindPython();
        if (python is null)
        {
            // The static assertions above still cover the checked-in script.
            return;
        }

        const string harness = """
            import importlib.util
            import sys

            spec = importlib.util.spec_from_file_location("pty_bridge", sys.argv[1])
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)

            assert module.clamp_size(1, 1) == (20, 5)
            assert module.clamp_size(500, 500) == (300, 100)
            assert module.clamp_size(80, 24) == (80, 24)
            assert module.parse_command('{"type":"input","data":"ls"}') == {"type": "input", "data": "ls"}
            assert module.parse_command('{"type":"resize","cols":5,"rows":5}') == {"type": "resize", "cols": 20, "rows": 5}
            assert module.parse_command('{"type":"bogus"}') is None
            assert module.parse_command('not json') is None
            assert module.parse_command('{"type":"input","data":42}') is None
            assert module.parse_command('{"type":"resize","cols":true,"rows":24}') is None
            assert module.parse_command('{"type":"input","data":"h\u00e9llo \U0001f642"}') == {"type": "input", "data": "h\u00e9llo \U0001f642"}
            assert module.attach_failure_message(0) is None
            assert module.attach_failure_message(None) is None
            assert module.attach_failure_message(1) == "tmux attach exited with status 1."
            assert module.attach_failure_message(-9) == "tmux attach was terminated by signal 9."
            bridge = module.Bridge("test")
            frames = []
            bridge._write_frame = frames.append
            bridge._emit_output(b"\x1b[31mplain\xff")
            assert frames == [{"type": "output", "encoding": "base64", "data": "G1szMW1wbGFpbv8="}]

            drained = module.Bridge("test")
            drained.master_fd = 9
            drain_frames = []
            drained._emit_output = drain_frames.append
            reads = [b"tail\xff"]
            def fake_read(*_):
                if reads:
                    return reads.pop(0)
                raise BlockingIOError()
            original_read = module.os.read
            module.os.read = fake_read
            try:
                drained._drain_master()
            finally:
                module.os.read = original_read
            assert drain_frames == [b"tail\xff"]
            assert not hasattr(drained, "decoder")

            events = []
            class Process:
                def poll(self): return 7
                def wait(self, timeout=None): events.append(("wait", timeout)); return 7
            failing = module.Bridge("test")
            failing.start = lambda: None
            failing.process = Process()
            failing._child_exited = lambda: True
            failing._drain_master = lambda: (_ for _ in ()).throw(RuntimeError("drain failed"))
            failing._emit_error = lambda message: events.append(("error", message))
            original_cleanup = failing.cleanup
            failing.cleanup = lambda: (original_cleanup(), events.append(("cleanup", None)))
            original_select = module.select.select
            module.select.select = lambda *_: ([], [], [])
            try:
                try:
                    failing.run()
                    raise AssertionError("drain exception did not propagate")
                except RuntimeError as error:
                    assert str(error) == "drain failed"
            finally:
                module.select.select = original_select
            assert events == [("error", "tmux attach exited with status 7."), ("wait", 0), ("cleanup", None)]
            """;

        var (exitCode, standardError) = RunPython(python, harness, scriptPath!);
        Assert.True(exitCode == 0, standardError);
    }

    private static string? ResolveBridgePath()
    {
        var outputCandidate = Path.Combine(AppContext.BaseDirectory, "Terminal", "pty_bridge.py");
        if (File.Exists(outputCandidate))
        {
            return outputCandidate;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var sourceCandidate = Path.Combine(directory.FullName, "src", "HVO.AgentControl", "Terminal", "pty_bridge.py");
            if (File.Exists(sourceCandidate))
            {
                return sourceCandidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindPython()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "python3");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static (int ExitCode, string StandardError) RunPython(string python, string code, string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(code);
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("python3 did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        _ = standardOutput.GetAwaiter().GetResult();
        return (process.ExitCode, standardError.GetAwaiter().GetResult());
    }
}
