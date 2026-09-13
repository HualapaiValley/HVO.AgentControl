using System.Diagnostics;
using System.Text;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Helper that materializes a deterministic fake ACP server (Python, no model
/// calls) and spawns it as a real child process for transport tests.
/// </summary>
internal static class AcpFakeServer
{
    public const string DefaultSessionId = "ses_fake_0001";

    private const string ScriptTemplate = """
        import json
        import os
        import sys
        import threading
        import time

        SCENARIO = "__SCENARIO__"
        if SCENARIO == "":
            SCENARIO = sys.argv[1] if len(sys.argv) > 1 else "happy"

        HOME = os.environ.get("HOME")
        CALLS = os.path.join(HOME, "calls.log") if HOME else None
        SESSION_ID = "ses_fake_0001"
        WRITE_LOCK = threading.Lock()

        def log_call(method):
            if not CALLS:
                return
            try:
                with open(CALLS, "a") as handle:
                    handle.write(method + "\n")
            except Exception:
                pass

        def send(payload):
            with WRITE_LOCK:
                sys.stdout.write(json.dumps(payload) + "\n")
                sys.stdout.flush()

        def respond_slow(request_id):
            time.sleep(0.30)
            send({"jsonrpc": "2.0", "id": request_id, "result": {"method": "test/slow"}})

        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                message = json.loads(line)
            except Exception:
                continue
            request_id = message.get("id")
            method = message.get("method")
            if method:
                log_call(method)
            if method == "initialize":
                if SCENARIO == "init_error":
                    send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32603, "message": "initialize exploded"}})
                elif SCENARIO.startswith("init_version_"):
                    raw = SCENARIO[len("init_version_"):]
                    result = {} if raw == "missing" else {"protocolVersion": "1" if raw == "string" else json.loads(raw)}
                    send({"jsonrpc": "2.0", "id": request_id, "result": result})
                else:
                    send({"jsonrpc": "2.0", "id": request_id, "result": {"protocolVersion": 1, "agentCapabilities": {}}})
            elif method == "session/new":
                send({"jsonrpc": "2.0", "id": request_id, "result": {"sessionId": SESSION_ID, "configOptions": []}})
                send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": SESSION_ID, "update": {"sessionUpdate": "current_mode_update", "currentModeId": "agentcontrol"}}})
            elif method == "session/load":
                if SCENARIO == "load_error":
                    send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32001, "message": "session not found"}})
                else:
                    send({"jsonrpc": "2.0", "id": request_id, "result": {}})
            elif method == "session/set_mode":
                send({"jsonrpc": "2.0", "id": request_id, "result": {}})
            elif method == "session/prompt":
                if SCENARIO == "prompt_fast":
                    send({"jsonrpc": "2.0", "id": request_id, "result": {"stopReason": "end_turn"}})
                elif SCENARIO == "prompt_error":
                    send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32603, "message": "prompt exploded"}})
                elif SCENARIO == "prompt_schema_error":
                    send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32602, "message": "invalid prompt"}})
                elif SCENARIO == "prompt_stop":
                    send({"jsonrpc": "2.0", "id": request_id, "result": {"stopReason": "max_tokens"}})
                elif SCENARIO == "prompt_hang":
                    # Never answers. The host's prompt deadline must bound the
                    # wait; the transport stays open so the session remains
                    # usable (and cancellable) afterwards.
                    pass
                else:
                    send({"jsonrpc": "2.0", "id": 9001, "method": "session/request_permission", "params": {"sessionId": SESSION_ID, "toolCall": {"toolCallId": "tc-1", "title": "Read secret", "kind": "read", "status": "pending"}, "options": [{"optionId": "allow_once", "name": "Allow once", "kind": "allow_once"}, {"optionId": "reject_once", "name": "Reject once", "kind": "reject_once"}]}})
                    permission_response = None
                    while True:
                        raw = sys.stdin.readline()
                        if not raw:
                            break
                        try:
                            candidate = json.loads(raw)
                        except Exception:
                            continue
                        if candidate.get("id") == 9001:
                            permission_response = candidate
                            break
                    send({"jsonrpc": "2.0", "id": request_id, "result": {"stopReason": "end_turn", "permissionResponse": permission_response}})
            elif method == "test/slow":
                threading.Thread(target=respond_slow, args=(request_id,), daemon=True).start()
            elif method == "test/fast":
                send({"jsonrpc": "2.0", "id": request_id, "result": {"method": "test/fast"}})
            elif method == "huge":
                sys.stdout.write("x" * 6000 + "\n")
                sys.stdout.flush()
            else:
                send({"jsonrpc": "2.0", "id": request_id, "result": {}})
        """;

    private static readonly Lazy<string> PythonScript = new(() => Materialize(null));

    public static string PythonScriptPath => PythonScript.Value;

    public static string CreateExecutable(string? scenario = null)
    {
        var path = Materialize(scenario);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return path;
    }

    public static Process Start(string? scenario, string home, int? maxFrameBytes = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "python3",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(PythonScriptPath);
        if (!string.IsNullOrEmpty(scenario))
        {
            startInfo.ArgumentList.Add(scenario);
        }

        startInfo.Environment["HOME"] = home;

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start the fake ACP process.");
        }

        return process;
    }

    private static string Materialize(string? scenario)
    {
        var directory = Directory.CreateTempSubdirectory("acp-fake-");
        var path = Path.Combine(directory.FullName, "fake_acp.py");
        var script = "#!/usr/bin/env python3\n"
            + ScriptTemplate.Replace("__SCENARIO__", scenario ?? string.Empty, StringComparison.Ordinal);
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
