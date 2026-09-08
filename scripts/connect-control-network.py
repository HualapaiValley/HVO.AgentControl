#!/usr/bin/env python3
"""Reconcile the opted-in demo web container's private control network on the host.

Docker Compose 5.5 cannot declare the built-in bridge as a service network without
unsupported aliases. Keep network_mode: bridge and run this after every web
create/recreate. No container receives Docker access or provider credentials.
"""

import argparse
import json
import subprocess
import sys


REQUIREMENT = "com.hvo.agentcontrol.control-network"
PROJECT = "com.docker.compose.project"
SERVICE = "com.docker.compose.service"
NETWORK = "com.docker.compose.network"


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def docker(*args):
    result = subprocess.run(["docker", *args], check=True, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30)
    return result.stdout.strip()


def inspect(kind, identity):
    values = json.loads(docker(kind, "inspect", identity))
    require(len(values) == 1 and values[0]["Id"] == identity, "Docker identity changed during inspection.")
    return values[0]


def unique(kind, labels):
    args = ["ps", "-aq", "--no-trunc"] if kind == "container" else ["network", "ls", "-q", "--no-trunc"]
    for key, value in labels.items():
        args += ["--filter", "label=" + key + "=" + value]
    identities = docker(*args).splitlines()
    require(len(identities) == 1, "Expected exactly one " + kind + " with the requested Compose ownership labels.")
    value = inspect(kind, identities[0])
    actual = value["Config"]["Labels"] if kind == "container" else value["Labels"]
    require(all(actual.get(key) == expected for key, expected in labels.items()), "Compose ownership labels changed.")
    return value


def memberships(container):
    return {name: (value["NetworkID"], value["IPAddress"], value.get("GlobalIPv6Address", ""))
            for name, value in container["NetworkSettings"]["Networks"].items()}


def reconcile(args):
    web_labels = {PROJECT: args.project, SERVICE: args.service}
    network_labels = {PROJECT: args.control_project, NETWORK: args.control_network}
    sidecar_labels = {PROJECT: args.control_project, SERVICE: args.control_service}
    web = unique("container", web_labels)
    web_id = web["Id"]
    require(not args.expected_container_id or args.expected_container_id == web_id,
            "The web container does not match the expected deployment identity.")
    required_name = web["Config"]["Labels"].get(REQUIREMENT, "")
    require(bool(required_name.strip()), "The web container has no explicit control-network requirement label.")
    require(web["State"]["Running"], "The web container must be running.")
    require(web["Config"]["Labels"].get("com.docker.compose.oneoff", "false").lower() == "false",
            "A one-off Compose container cannot own the web deployment.")
    require(web["HostConfig"]["NetworkMode"] == "bridge", "This transitional helper requires network_mode: bridge.")
    before = memberships(web)
    require("bridge" in before and bool(before["bridge"][1]), "The existing Docker bridge connection is missing.")
    legacy = inspect("network", before["bridge"][0])
    require(legacy["Name"] == "bridge" and legacy.get("Options", {}).get("com.docker.network.bridge.default_bridge") == "true",
            "The existing bridge is not Docker's built-in bridge.")

    network = unique("network", network_labels)
    network_id, network_name = network["Id"], network["Name"]
    require(network_name == required_name, "The required network name does not match the owned control network.")
    require(not args.expected_network_id or args.expected_network_id == network_id,
            "The control network does not match the expected deployment identity.")
    require(network["Driver"] == "bridge" and network["Scope"] == "local"
            and not network["Internal"] and not network.get("Ingress", False)
            and network.get("Options", {}).get("com.docker.network.bridge.default_bridge") != "true",
            "The control network must be a user-defined local bridge with provider egress.")
    sidecar = unique("container", sidecar_labels)
    require(sidecar["State"]["Running"], "The owned control sidecar must be running.")
    endpoint = sidecar["NetworkSettings"]["Networks"].get(network_name, {})
    require(endpoint.get("NetworkID") == network_id and "opencode-control" in (endpoint.get("Aliases") or []),
            "The owned control sidecar has no opencode-control alias on the required network.")
    require(not sidecar["HostConfig"]["PortBindings"], "The control sidecar must not publish host ports.")
    require(network_name not in before or before[network_name][0] == network_id,
            "The web container is attached to a different network with the required name.")

    already_connected = network_name in before
    # Resolve again immediately before the only mutation. IDs, never mutable names,
    # are passed to Docker so concurrent replacement cannot target another resource.
    require(unique("container", web_labels)["Id"] == web_id
            and unique("network", network_labels)["Id"] == network_id,
            "Deployment identities changed before network attachment.")
    if not already_connected:
        try:
            docker("network", "connect", "--gw-priority=-1", network_id, web_id)
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
            # A lost response or competing identical attachment can still have
            # succeeded. Reconcile observable membership; do not blindly retry.
            observed = memberships(inspect("container", web_id))
            require(observed.get(network_name, (None,))[0] == network_id,
                    "Network attachment did not reach a verified state; rerun reconciliation after inspection.")

    after = unique("container", web_labels)
    require(after["Id"] == web_id and unique("network", network_labels)["Id"] == network_id,
            "Deployment identities changed during network attachment.")
    actual = memberships(after)
    require(all(actual.get(name) == value for name, value in before.items()),
            "An existing web network connection changed during attachment.")
    require(actual.get(network_name, (None,))[0] == network_id,
            "The web container's control-network membership was not verified.")
    endpoints = after["NetworkSettings"]["Networks"]
    require(endpoints[network_name].get("GwPriority", 0) < endpoints["bridge"].get("GwPriority", 0),
            "The control attachment must have lower gateway priority than the existing bridge; inspect and recreate it safely.")
    return {"webContainerId": web_id, "controlContainerId": sidecar["Id"], "networkId": network_id,
            "networkName": network_name, "alreadyConnected": already_connected,
            "legacyBridgeAddress": actual["bridge"][1]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", default="hvo-agentcontrol-demo")
    parser.add_argument("--service", default="agentcontrol")
    parser.add_argument("--control-project", default="hvo-agentcontrol-control")
    parser.add_argument("--control-service", default="opencode-control")
    parser.add_argument("--control-network", default="control")
    parser.add_argument("--expected-container-id")
    parser.add_argument("--expected-network-id")
    args = parser.parse_args()
    try:
        print(json.dumps(reconcile(args)))
    except (RuntimeError, KeyError, ValueError, subprocess.SubprocessError) as error:
        print("Control network attachment refused: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
