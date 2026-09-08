#!/usr/bin/env python3
"""Disposable real-Docker smoke for the control sidecar; no model credentials/inference.

Render the shipped Compose configuration and use its settings for owned Docker creates.
Cleanup uses returned container/network IDs and a uniquely labelled volume only. No live
AgentControl deployment, worker registration, existing volume or provider is contacted.
"""

import base64
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import secrets
import selectors
import subprocess
import tempfile
import time
import uuid


ROOT = Path(__file__).resolve().parents[1]
LABEL = "com.hvo.agentcontrol.sidecar-smoke"
DIRECTORY = "/var/lib/opencode/workspaces/control"
MANIFEST_PATH = "/file/content?path=.agentcontrol-service.json&directory=" + DIRECTORY
INSTANCE_FILE = "/var/lib/opencode/state/agentcontrol-instance-id"


def docker(*args, **kwargs):
    return subprocess.run(["docker", *args], check=True, text=True,
                          stdout=subprocess.PIPE, stderr=kwargs.pop("stderr", subprocess.PIPE),
                          timeout=kwargs.pop("timeout", 120), **kwargs).stdout.strip()


def inspect(kind, identity):
    return json.loads(docker(kind, "inspect", identity))[0]


def run():
    owner = uuid.uuid4().hex
    containers, network, volume, stream = [], None, None, None
    with tempfile.TemporaryDirectory(prefix="hvo-sidecar-") as temp:
        # Directory is traversable by the container's fixed non-root UID; only this
        # random disposable credential is readable. Never copy a user's credential.
        os.chmod(temp, 0o755)
        password = secrets.token_hex(32)
        password_file = Path(temp) / "server-password"
        password_file.write_text(password + "\n")
        password_file.chmod(0o444)
        environment = {**os.environ, "CONTROL_OPENCODE_PASSWORD_FILE": str(password_file)}
        config = json.loads(docker("compose", "-f", str(ROOT / "compose.control.yaml"),
                                   "config", "--format", "json", env=environment))
        service = config["services"]["opencode-control"]
        assert not service.get("ports") and not service.get("network_mode")
        assert set(service["networks"]) == {"control"}
        assert not config["networks"]["control"].get("internal", False)
        assert service["user"] == "1000:1000" and service["read_only"] and service["init"]
        assert service["cap_drop"] == ["ALL"] and service["security_opt"] == ["no-new-privileges:true"]
        assert len(service["volumes"]) == 1 and service["volumes"][0]["target"] == "/var/lib/opencode"
        assert service["secrets"] == [{"source": "opencode-server-password", "target": "opencode-server-password"}]
        # Use the returned immutable build identity. A concurrent checkout's build
        # must not move a shared tag between our build, create and recreation.
        image_file = Path(temp) / "image-id"
        docker("build", "--iidfile", str(image_file), service["build"]["context"], timeout=300)
        image = image_file.read_text().strip()

        def create(*arguments):
            identity = docker("create", "--label", f"{LABEL}={owner}", *arguments)
            assert len(identity) == 64 and all(c in "0123456789abcdef" for c in identity)
            containers.append(identity)
            docker("start", identity)
            return identity

        def server(wait_ready=True):
            # These are rendered production settings, not separate fixture defaults.
            arguments = ["--network", network,
                         "--user", service["user"], "--read-only", "--init",
                         "--cpus", str(service["cpus"]), "--memory", str(service["mem_limit"]),
                         "--pids-limit", str(service["pids_limit"]),
                         "--restart", service["restart"] if wait_ready else "no",
                         "--mount", f"type=volume,src={volume},dst=/var/lib/opencode",
                         "--mount", f"type=bind,src={password_file},dst=/run/secrets/opencode-server-password,readonly"]
            # Failed-start probes must not alter DNS for the healthy service or be
            # restarted forever by the production unless-stopped policy.
            if wait_ready:
                arguments += ["--network-alias", "opencode-control"]
            for value in service["cap_drop"]:
                arguments += ["--cap-drop", value]
            for value in service["security_opt"]:
                arguments += ["--security-opt", value]
            for value in service["tmpfs"]:
                arguments += ["--tmpfs", value]
            identity = create(*arguments, image)
            if not wait_ready:
                return identity
            deadline = time.monotonic() + 100
            while time.monotonic() < deadline:
                state = inspect("container", identity)["State"]
                if state.get("Health", {}).get("Status") == "healthy":
                    return identity
                assert state["Running"], "Native sidecar exited before readiness"
                time.sleep(1)
            raise AssertionError("Native sidecar did not become healthy")

        def client():
            return create("--network", network, "--read-only", "--user", service["user"],
                          "--entrypoint", "/bin/sh", image, "-c", "sleep 300")

        def curl_config(path, method="GET", payload=None, authenticated=True):
            lines = ["silent", "show-error", "max-time = 20",
                     'url = "http://opencode-control:4096' + path + '"',
                     "request = " + json.dumps(method)]
            if authenticated:
                auth = base64.b64encode(("opencode:" + password).encode()).decode()
                lines.append('header = "Authorization: Basic ' + auth + '"')
            if payload is not None:
                lines += ['header = "Content-Type: application/json"',
                          "data = " + json.dumps(json.dumps(payload))]
            return "\n".join(lines) + "\n"

        def request(client_id, path, method="GET", payload=None, authenticated=True, expected=200):
            result = docker("exec", "-i", client_id, "curl", "--config", "-",
                            "--write-out", "\n%{http_code}",
                            input=curl_config(path, method, payload, authenticated))
            body, _, status = result.rpartition("\n")
            assert int(status) == expected, f"{method} {path} returned HTTP {status}"
            return json.loads(body) if expected == 200 else body

        def remove(identity):
            assert inspect("container", identity)["Config"]["Labels"].get(LABEL) == owner
            docker("rm", "-f", identity)
            containers.remove(identity)

        def manifest(client_id):
            response = request(client_id, MANIFEST_PATH)
            assert response["type"] == "text" and isinstance(response["content"], str)
            value = json.loads(response["content"])
            assert set(value) == {"schemaVersion", "instanceId", "incarnationId", "startedAt", "directory"}
            assert value["schemaVersion"] == 1 and value["directory"] == DIRECTORY
            for field in ["instanceId", "incarnationId"]:
                assert str(uuid.UUID(value[field])) == value[field]
            started = datetime.fromisoformat(value["startedAt"].replace("Z", "+00:00"))
            assert started.tzinfo == timezone.utc and started <= datetime.now(timezone.utc)
            return value

        def volume_command(command):
            identity = create("--network", "none", "--user", service["user"], "--read-only",
                              "--mount", f"type=volume,src={volume},dst=/var/lib/opencode",
                              "--entrypoint", "/bin/sh", image, "-c", command)
            assert docker("wait", identity, timeout=20) == "0"
            result = docker("logs", identity)
            remove(identity)
            return result

        try:
            # Fail closed before listening when the secret is absent.
            missing = create("--network", "none", image)
            assert docker("wait", missing, timeout=20) != "0"
            assert "password file is missing or unreadable" in docker("logs", missing, stderr=subprocess.STDOUT)
            remove(missing)

            network = docker("network", "create", "--label", f"{LABEL}={owner}", "hvo-control-" + owner)
            volume_name = "hvo-control-" + owner
            # Docker volume create can return an existing volume: refuse it up front
            # and verify the unique ownership label before either use or cleanup.
            assert not docker("volume", "ls", "-q", "--filter", "name=^" + volume_name + "$")
            created = docker("volume", "create", "--label", f"{LABEL}={owner}", volume_name)
            assert inspect("volume", created)["Labels"].get(LABEL) == owner
            volume = created
            native = server()
            original = inspect("container", native)
            assert not original["HostConfig"]["PortBindings"]
            assert docker("exec", native, "id", "-u") == "1000"
            assert docker("exec", native, "sh", "-c",
                          "for tool in ssh sshd tmux git dotnet docker; do command -v \"$tool\" && exit 1; done; exit 0") == ""
            first_client = client()
            request(first_client, "/session", authenticated=False, expected=401)
            health = request(first_client, "/global/health")
            assert health == {"healthy": True, "version": "1.18.29"}
            schema = request(first_client, "/doc")
            for route in ["/global/event", "/session", "/session/{sessionID}/prompt_async",
                          "/session/{sessionID}/message", "/session/status", "/provider", "/path", "/file/content"]:
                assert route in schema["paths"]
            path = request(first_client, "/path")
            assert path["directory"] == DIRECTORY
            request(first_client, MANIFEST_PATH, authenticated=False, expected=401)
            original_manifest = manifest(first_client)

            # Exercise actual entrypoint -> exec OpenCode descriptor inheritance.
            # A second real sidecar must fail before replacing the active manifest.
            competing = server(wait_ready=False)
            assert docker("wait", competing, timeout=20) != "0"
            assert "state is already owned by another process" in docker("logs", competing, stderr=subprocess.STDOUT)
            assert manifest(first_client) == original_manifest
            assert inspect("container", native)["State"]["Pid"] == original["State"]["Pid"]
            remove(competing)

            stream = subprocess.Popen(["docker", "exec", "-i", first_client, "curl", "-N", "--config", "-"],
                                      stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            stream.stdin.write(curl_config("/global/event").encode())
            stream.stdin.close()
            with selectors.DefaultSelector() as selector:
                selector.register(stream.stdout, selectors.EVENT_READ)
                assert selector.select(10), "No native SSE connection event"
                assert b"server.connected" in os.read(stream.stdout.fileno(), 65536)

            sessions = []
            for title in ["Host operations fixture", "Workgroup fixture"]:
                session = request(first_client, "/session", "POST", {"title": title})
                session_id = session["id"]
                request(first_client, f"/session/{session_id}/message", "POST",
                        {"noReply": True, "parts": [{"type": "text", "text": title}]})
                history = request(first_client, f"/session/{session_id}/message")
                assert len(history) == 1 and history[0]["info"]["role"] == "user"
                assert any(p.get("text") == title for p in history[0]["parts"])
                sessions.append((session_id, title, history))
            assert sessions[0][0] != sessions[1][0]
            stream.terminate()
            stream.wait(timeout=10)
            stream = None
            remove(first_client)
            second_client = client()
            after = inspect("container", native)["State"]
            assert (after["Pid"], after["StartedAt"]) == (original["State"]["Pid"], original["State"]["StartedAt"])
            assert manifest(second_client) == original_manifest
            for identity, title, history in sessions:
                assert request(second_client, f"/session/{identity}")["title"] == title
                assert request(second_client, f"/session/{identity}/message") == history

            # Recreate the process/container, retaining only the native-state volume.
            docker("stop", "--timeout", "15", native)
            remove(native)
            replacement = server()
            assert replacement != native
            replacement_manifest = manifest(second_client)
            assert replacement_manifest["instanceId"] == original_manifest["instanceId"]
            assert replacement_manifest["incarnationId"] != original_manifest["incarnationId"]
            for identity, title, history in sessions:
                assert request(second_client, f"/session/{identity}")["title"] == title
                assert request(second_client, f"/session/{identity}/message") == history
            assert request(second_client, "/session/status") == {}

            # Corruption is an operator repair boundary, never silent reidentity.
            # Stop the sole writer before deliberately changing this owned fixture.
            docker("stop", "--timeout", "15", replacement)
            remove(replacement)
            volume_command("printf 'corrupted-identity\\n' > " + INSTANCE_FILE)
            corrupted = server(wait_ready=False)
            assert docker("wait", corrupted, timeout=20) != "0"
            assert "instance identity is invalid" in docker("logs", corrupted, stderr=subprocess.STDOUT)
            remove(corrupted)
            assert json.loads(volume_command("cat " + DIRECTORY + "/.agentcontrol-service.json")) == replacement_manifest
            volume_command("rm " + INSTANCE_FILE)
            missing_identity = server(wait_ready=False)
            assert docker("wait", missing_identity, timeout=20) != "0"
            assert "identity is missing for existing state" in docker("logs", missing_identity, stderr=subprocess.STDOUT)
            remove(missing_identity)
            # Restoring the original identity permits restart with the same native
            # history, proving the failed starts did not rewrite the native state.
            volume_command("printf '%s\\n' '" + original_manifest["instanceId"] + "' > " + INSTANCE_FILE)
            repaired = server()
            assert manifest(second_client)["instanceId"] == original_manifest["instanceId"]
            for identity, title, history in sessions:
                assert request(second_client, f"/session/{identity}/message") == history
            print(json.dumps({"version": health["version"], "architecture": inspect("image", image)["Architecture"],
                              "imageBytes": inspect("image", image)["Size"], "sessionCount": len(sessions),
                              "authenticatedHttpAndSse": True, "clientReplacementPreservedProcess": True,
                              "containerRecreationPreservedHistory": True,
                              "authenticatedIdentityManifest": True, "restartChangedIncarnationOnly": True,
                              "exclusiveStateWriter": True, "identityCorruptionFailedClosed": True,
                              "modelInferenceExercised": False}))
        finally:
            failures = []
            if stream is not None:
                try:
                    stream.terminate()
                    stream.wait(timeout=10)
                except Exception as error:
                    failures.append(str(error))
            # Attempt every owned cleanup even if one fails; never remove by a guessed name.
            for identity in list(reversed(containers)):
                try:
                    remove(identity)
                except Exception as error:
                    failures.append(str(error))
            for kind, identity in [("volume", volume), ("network", network)]:
                if identity is not None:
                    try:
                        assert inspect(kind, identity)["Labels"].get(LABEL) == owner
                        docker(kind, "rm", identity)
                    except Exception as error:
                        failures.append(str(error))
            if failures:
                raise RuntimeError("Owned sidecar fixture cleanup failed: " + "; ".join(failures))


if __name__ == "__main__":
    run()
