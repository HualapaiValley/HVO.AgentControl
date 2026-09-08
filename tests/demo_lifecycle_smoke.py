#!/usr/bin/env python3
"""Demo deployment script regression with disposable data and recorded command fakes.

The real Docker topology/attachment is covered by control_web_network_smoke.py.
This test invokes the shipped shell script and checks its command ordering,
persisted opt-in and failure boundaries without contacting a Docker daemon.
"""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[1]
NETWORK = "hvo-agentcontrol-control_control"


def run():
    with tempfile.TemporaryDirectory(prefix="hvo-demo-lifecycle-") as temp:
        root = Path(temp)
        state = root / "state with spaces"
        (state / "demo-data").mkdir(parents=True)
        (state / "demo-secrets").mkdir()
        (state / "demo-data/agentcontrol.db").touch()
        (state / "demo-secrets/owner-password").touch()
        marker = state / "control-network-name"
        log = root / "commands.jsonl"
        commands = root / "bin"
        commands.mkdir()
        fake = '''import json, os, pathlib, sys
kind = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
with open(os.environ["HVO_DEMO_TEST_LOG"], "a") as log:
    log.write(json.dumps({"kind": kind, "args": args, "data": os.environ.get("DEMO_DATA_DIRECTORY"), "secrets": os.environ.get("DEMO_SECRETS_DIRECTORY"), "network": os.environ.get("CONTROL_OPENCODE_NETWORK")}) + "\\n")
if kind == "systemctl":
    sys.exit(1)
if kind == "python3":
    if args and args[0].endswith("/scripts/connect-control-network.py"):
        if os.environ.get("HVO_DEMO_TEST_HELPER_FAIL") == "1":
            print("Required sidecar/network unavailable", file=sys.stderr)
            sys.exit(1)
        print(json.dumps({"networkName": os.environ.get("HVO_DEMO_TEST_RECEIPT_NETWORK", os.environ["CONTROL_OPENCODE_NETWORK"])}))
        sys.exit(0)
    os.execv(sys.executable, [sys.executable, *args])
if args and args[0] == "ps":
    print(os.environ.get("HVO_DEMO_TEST_EXISTING", ""))
elif args and args[0] == "inspect":
    print(os.environ.get("HVO_DEMO_TEST_LABEL", ""))
elif args and args[0] == "compose":
    if "ps" in args:
        print("Web status after successful start")
'''
        for name in ["docker", "systemctl", "python3"]:
            script = commands / name
            script.write_text("#!" + sys.executable + "\n" + fake)
            script.chmod(0o755)
        environment = {**os.environ, "PATH": str(commands) + os.pathsep + os.environ["PATH"],
                       "HVO_DEMO_STATE_ROOT": str(state), "HVO_DEMO_TEST_LOG": str(log),
                       "COMPOSE_PROFILES": "coordinator", "COMPOSE_PROJECT_NAME": "hvo-agentcontrol-demo"}

        def invoke(action, success=True, **changes):
            log.write_text("")
            result = subprocess.run(["bash", str(ROOT / "scripts/demo-server.sh"), action],
                                    env={**environment, **changes}, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=20)
            assert (result.returncode == 0) == success, result.stderr
            rows = [json.loads(line) for line in log.read_text().splitlines()]
            compose = [row for row in rows if row["kind"] == "docker" and row["args"][0] == "compose"]
            for row in compose:
                assert row["args"][-1] == "agentcontrol", "A lifecycle command targeted services beyond the web host"
                assert row["data"] == str(state / "demo-data") and row["secrets"] == str(state / "demo-secrets")
                assert row["args"][row["args"].index("--project-directory") + 1] == str(ROOT)
            return result, rows, compose

        def helpers(rows):
            return [row for row in rows if row["kind"] == "python3" and row["args"]
                    and row["args"][0].endswith("/scripts/connect-control-network.py")]

        for action in ["start", "restart"]:
            _, rows, compose = invoke(action)
            assert not helpers(rows) and not marker.exists()
            assert all(str(ROOT / "compose.control.web.yaml") not in row["args"] for row in compose)
            assert any("up" in row["args"] and "--no-deps" in row["args"] for row in compose)
            assert sum("stop" in row["args"] for row in compose) == (action == "restart")

        marker.write_text(NETWORK + "\n")
        for action in ["start", "restart"]:
            _, rows, compose = invoke(action)
            assert len(helpers(rows)) == 1 and helpers(rows)[0]["network"] == NETWORK
            assert all(str(ROOT / "compose.control.web.yaml") in row["args"] for row in compose)
            assert rows.index(helpers(rows)[0]) < next(i for i, row in enumerate(rows)
                                                       if row["kind"] == "docker" and row["args"][0] == "compose" and "ps" in row["args"])
            assert marker.read_text() == NETWORK + "\n"

        result, rows, compose = invoke("restart", success=False, HVO_DEMO_TEST_HELPER_FAIL="1")
        assert helpers(rows) and "Web status" not in result.stdout
        assert not any("ps" in row["args"] for row in compose)
        marker.unlink()
        for changes in [{"HVO_DEMO_TEST_HELPER_FAIL": "1"}, {"HVO_DEMO_TEST_RECEIPT_NETWORK": "wrong-network"}]:
            result, _, compose = invoke("start", success=False, HVO_DEMO_TEST_EXISTING="owned-web-id",
                                         HVO_DEMO_TEST_LABEL=NETWORK, **changes)
            assert not marker.exists() and "Web status" not in result.stdout
            assert not any("ps" in row["args"] for row in compose)

        _, rows, _ = invoke("start", HVO_DEMO_TEST_EXISTING="owned-web-id", HVO_DEMO_TEST_LABEL=NETWORK)
        assert helpers(rows) and marker.read_text() == NETWORK + "\n" and marker.stat().st_mode & 0o777 == 0o600
        # The next restart no longer needs the fallback label: persisted state alone
        # restores the opt-in overlay and runs reconciliation before status output.
        _, rows, _ = invoke("restart")
        assert helpers(rows)

        for malformed in [NETWORK + "\n\n", "bad/name\n", "x" * 257, "$(false)\n"]:
            marker.write_text(malformed)
            _, rows, compose = invoke("restart", success=False)
            assert not compose and not helpers(rows)
        marker.unlink()
        marker.symlink_to(state / "demo-secrets/owner-password")
        _, _, compose = invoke("restart", success=False)
        assert not compose
        marker.unlink()
        _, _, compose = invoke("start", success=False, HVO_DEMO_TEST_EXISTING="first-id\nsecond-id")
        assert not compose

        for action in ["stop", "status", "logs"]:
            _, rows, compose = invoke(action)
            assert len(compose) == 1 and not helpers(rows)
        print(json.dumps({"disabledStartAndRestart": True, "persistedOptInSurvivesRestart": True,
                          "verifiedLabelFallbackPersisted": True, "failureDoesNotClaimReadiness": True,
                          "unverifiedOrMalformedRequirementRejected": True, "lifecycleTargetsWebOnly": True,
                          "externalStateRootPreservesSourceBuildContext": True, "productionMutations": False}))


if __name__ == "__main__":
    run()
