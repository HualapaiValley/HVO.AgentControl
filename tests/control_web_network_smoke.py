#!/usr/bin/env python3
"""Actual Docker/Compose test of host-side control-network reconciliation.

Only uniquely labelled disposable containers/networks are mutated. A tiny HTTP
server stands in for each service; no active deployment, credential or model is used.
"""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import urllib.request
import uuid


ROOT = Path(__file__).resolve().parents[1]
IMAGE = "ghcr.io/anomalyco/opencode:1.18.29@sha256:ecc3bf96ee55dad226d9cde50d79aaa8a1215c47860c0fcdc71570461bf438b8"
LABEL = "com.hvo.agentcontrol.control-web-smoke"


def docker(*args, **kwargs):
    return subprocess.run(["docker", *args], check=True, text=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60, **kwargs).stdout.strip()


def inspect(kind, identity):
    return json.loads(docker(kind, "inspect", identity))[0]


def server_command(text):
    return ("while true; do printf 'HTTP/1.1 200 OK\\r\\nContent-Length: " + str(len(text))
            + "\\r\\nConnection: close\\r\\n\\r\\n" + text + "' | /bin/busybox nc -l -p 8080; done")


def run():
    owner = uuid.uuid4().hex
    project = "hvo-web-network-" + owner
    control_project = project + "-control"
    containers, networks = set(), []
    with tempfile.TemporaryDirectory(prefix=project + "-") as temp:
        # Guard the shipped overlay before applying it to any running fixture:
        # it may add the opt-in label, never production mounts, ports or services.
        rendering = {**os.environ, "DEMO_BIND_ADDRESS": "127.0.0.1",
                     "CONTROL_OPENCODE_NETWORK": project + "-control"}
        original = json.loads(docker("compose", "-f", str(ROOT / "compose.demo.yaml"),
                                     "config", "--format", "json", env=rendering))
        overlay = json.loads(docker("compose", "-f", str(ROOT / "compose.demo.yaml"),
                                    "-f", str(ROOT / "compose.control.web.yaml"),
                                    "config", "--format", "json", env=rendering))
        original_labels = original["services"]["agentcontrol"].pop("labels", {})
        actual_labels = overlay["services"]["agentcontrol"].pop("labels", {})
        assert actual_labels == {**original_labels, "com.hvo.agentcontrol.control-network": project + "-control"}
        assert overlay == original
        base = Path(temp) / "compose.json"
        base.write_text(json.dumps({"services": {"agentcontrol": {
            "image": IMAGE, "entrypoint": ["/bin/sh", "-c", server_command("agentcontrol-web")],
            "network_mode": "bridge", "read_only": True, "user": "1000:1000", "cap_drop": ["ALL"],
            "ports": ["127.0.0.1::8080"], "labels": {LABEL: owner}}}}))

        def create(*args):
            identity = docker("create", "--label", LABEL + "=" + owner, *args)
            containers.add(identity)
            docker("start", identity)
            return identity

        def compose_web(required_network=None):
            compose = ["compose", "--project-name", project, "-f", str(base)]
            if required_network is not None:
                compose += ["-f", str(ROOT / "compose.control.web.yaml")]
            environment = {**os.environ, "CONTROL_OPENCODE_NETWORK": required_network or ""}
            old = set(docker("ps", "-aq", "--no-trunc", "--filter", "label=" + LABEL + "=" + owner,
                             "--filter", "label=com.docker.compose.project=" + project).splitlines())
            try:
                docker(*compose, "up", "-d", "--no-build", "--force-recreate", env=environment)
            finally:
                current = set(docker("ps", "-aq", "--no-trunc", "--filter", "label=" + LABEL + "=" + owner,
                                     "--filter", "label=com.docker.compose.project=" + project).splitlines())
                containers.difference_update(old - current)
                containers.update(current)
            assert len(current) == 1
            return next(iter(current))

        def reconcile(*extra, success=True):
            result = subprocess.run([sys.executable, str(ROOT / "scripts/connect-control-network.py"),
                                     "--project", project, "--control-project", control_project, *extra],
                                    text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120)
            assert (result.returncode == 0) == success, result.stderr
            return json.loads(result.stdout) if success else result.stderr

        def topology():
            return {identity: inspect("container", identity)["NetworkSettings"]["Networks"]
                    for identity in containers}

        try:
            network = docker("network", "create", "--label", LABEL + "=" + owner,
                             "--label", "com.docker.compose.project=" + control_project,
                             "--label", "com.docker.compose.network=control", project + "-control")
            networks.append(network)
            network_name = inspect("network", network)["Name"]
            unrelated = docker("network", "create", "--label", LABEL + "=" + owner, project + "-unrelated")
            networks.append(unrelated)
            legacy = create("--network", "bridge", "--read-only", "--user", "1000:1000", "--cap-drop", "ALL",
                            "--entrypoint", "/bin/sh", IMAGE, "-c", server_command("legacy-worker"))
            sidecar = create("--network", network, "--network-alias", "opencode-control", "--read-only",
                             "--user", "1000:1000", "--cap-drop", "ALL",
                             "--label", "com.docker.compose.project=" + control_project,
                             "--label", "com.docker.compose.service=opencode-control",
                             "--entrypoint", "/bin/sh", IMAGE, "-c", server_command("control-sidecar"))
            sidecar_state = inspect("container", sidecar)["State"]
            legacy_ip = inspect("container", legacy)["NetworkSettings"]["Networks"]["bridge"]["IPAddress"]
            assert not inspect("container", sidecar)["HostConfig"]["PortBindings"]

            web = compose_web()
            before = topology()
            assert "no explicit control-network requirement" in reconcile(success=False)
            assert topology() == before

            web = compose_web(inspect("network", unrelated)["Name"])
            before = topology()
            assert "required network name does not match" in reconcile(success=False)
            assert topology() == before

            web = compose_web(network_name)
            before = topology()
            assert "expected deployment identity" in reconcile("--expected-network-id", unrelated, success=False)
            assert "expected deployment identity" in reconcile("--expected-container-id", legacy, success=False)
            assert topology() == before

            for recreated in [False, True]:
                if recreated:
                    previous_web = web
                    web = compose_web(network_name)
                    assert web != previous_web
                    assert set(inspect("container", web)["NetworkSettings"]["Networks"]) == {"bridge"}
                before = topology()
                receipt = reconcile("--expected-container-id", web, "--expected-network-id", network)
                assert receipt["webContainerId"] == web and receipt["networkId"] == network
                assert receipt["controlContainerId"] == sidecar and not receipt["alreadyConnected"]
                after = topology()
                assert all(after[identity] == value for identity, value in before.items() if identity != web)
                assert after[web]["bridge"] == before[web]["bridge"]
                assert set(after[web]) == {"bridge", network_name}
                assert after[web][network_name]["GwPriority"] == -1
                assert reconcile()["alreadyConnected"]
                assert topology() == after
                assert docker("exec", web, "busybox", "wget", "-q", "-O", "-", "-T", "10",
                              "http://" + legacy_ip + ":8080") == "legacy-worker"
                assert docker("exec", web, "busybox", "wget", "-q", "-O", "-", "-T", "10",
                              "http://opencode-control:8080") == "control-sidecar"
                binding = inspect("container", web)["NetworkSettings"]["Ports"]["8080/tcp"][0]
                assert binding["HostIp"] == "127.0.0.1"
                with urllib.request.urlopen("http://127.0.0.1:" + binding["HostPort"], timeout=10) as response:
                    assert response.read().decode() == "agentcontrol-web"
                current_state = inspect("container", sidecar)["State"]
                assert (current_state["Pid"], current_state["StartedAt"]) == (sidecar_state["Pid"], sidecar_state["StartedAt"])
            print(json.dumps({"composeVersion": docker("compose", "version", "--short"),
                              "legacyBridgeConnectivity": True, "privateControlDnsConnectivity": True,
                              "publishedWebPortPreserved": True, "idempotentAttachment": True,
                              "webRecreationReconciled": True, "sidecarProcessUnchanged": True,
                              "missingRequirementRejected": True, "wrongIdentityRejected": True,
                              "unrelatedMembershipsUnchanged": True}))
        finally:
            failures = []
            for identity in list(containers):
                try:
                    assert inspect("container", identity)["Config"]["Labels"].get(LABEL) == owner
                    docker("rm", "-f", identity)
                except Exception as error:
                    failures.append(str(error))
            for identity in reversed(networks):
                try:
                    assert inspect("network", identity)["Labels"].get(LABEL) == owner
                    docker("network", "rm", identity)
                except Exception as error:
                    failures.append(str(error))
            if failures:
                raise RuntimeError("Owned network fixture cleanup failed: " + "; ".join(failures))


if __name__ == "__main__":
    run()
