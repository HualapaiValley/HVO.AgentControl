FROM node:22-bookworm-slim AS opencode
ARG OPENCODE_VERSION=1.18.30
RUN npm install --global opencode-ai@${OPENCODE_VERSION}

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/HVO.AgentControl/HVO.AgentControl.csproj -c Release -o /app
RUN dotnet publish src/HVO.AgentControl.Worker/HVO.AgentControl.Worker.csproj -c Release -o /worker-app
RUN dotnet publish src/HVO.AgentControl.DockerHelper/HVO.AgentControl.DockerHelper.csproj -c Release -o /docker-helper-app

# The privileged launcher is the container's only setuid component. It is built
# from source in its own stage so the runtime image never carries a compiler,
# and statically linked so it does not depend on the runtime image's loader.
FROM debian:trixie-slim AS launcher
RUN apt-get update \
    && apt-get install -y --no-install-recommends gcc libc6-dev \
    && rm -rf /var/lib/apt/lists/*
COPY src/launcher/agentcontrol-launch.c /src/agentcontrol-launch.c
RUN gcc -std=c11 -O2 -static -Wall -Wextra -Werror -Wformat=2 -Wconversion \
        -D_FORTIFY_SOURCE=2 -fstack-protector-strong \
        -o /agentcontrol-launch /src/agentcontrol-launch.c \
    && strip /agentcontrol-launch

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS control-runtime
RUN apt-get update && apt-get install -y --no-install-recommends python3 tmux tini ca-certificates git util-linux \
    && rm -rf /var/lib/apt/lists/*
COPY --from=opencode /usr/local/bin/node /usr/local/bin/node
COPY --from=opencode /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/opencode-ai/bin/opencode.exe /usr/local/bin/opencode

# Two unprivileged identities in one container: the agent owns /data/home and
# /data/workspace (OpenCode state, tmux); the controller owns /control-data and
# the owner secret. The entrypoint establishes ownership and then drops to the
# controller.
#
# UID/GID 1000 is kept for the agent so the existing /data volume - written by
# the pre-isolation single identity - stays owned by the same numeric id and no
# data has to be moved or re-owned. The base image's placeholder `ubuntu`
# account holds that id, so it is removed first.
#
# The agent needs a real login shell. tmux resolves `default-shell` from the
# account and starts the server through it: with /usr/sbin/nologin a bare
# `tmux new-session` leaves "no server running" (measured), which breaks the
# recovery and diagnostic paths even though the normal attach always supplies an
# explicit command vector. The controller never needs a shell and keeps nologin.
#
# /data itself is root-owned: an agent-owned parent would let the agent rename
# home/workspace and leave a symlink for the next root start to act on. The two
# subdirectories below it are agent-owned, so no agent data moves.
#
# `userdel` must succeed: if the placeholder account survived, the `useradd`
# below would fail on a taken UID and the image would never reach a two-identity
# layout. Only `groupdel` is tolerated, because `userdel` already removes the
# account's private group on most base images. The braces matter - without them
# `|| true` binds to the whole `&&` chain and would swallow a failed `userdel`.
RUN userdel ubuntu \
    && { groupdel ubuntu 2>/dev/null || true; } \
    && groupadd --gid 1000 agent \
    && useradd --uid 1000 --gid 1000 --home-dir /data/home --shell /bin/bash --no-create-home agent \
    && groupadd --gid 1001 control \
    && useradd --uid 1001 --gid 1001 --home-dir /control-data --shell /usr/sbin/nologin --no-create-home control \
    && mkdir -p /data/home /data/workspace /control-data /agent-config \
    && chown 0:0 /data && chmod 0755 /data \
    && chown 1000:1000 /data/home /data/workspace \
    && chmod 0700 /data/home /data/workspace \
    && chown 1001:1001 /control-data /agent-config \
    && chmod 0700 /control-data && chmod 0755 /agent-config

WORKDIR /app
COPY --from=build /app/ ./

# Strip every inherited setuid/setgid bit (su, mount, passwd, chsh, ...) before
# installing the launcher. The controller runs without no-new-privileges so the
# launcher can elevate; leaving other setuid binaries reachable would turn that
# into a general escalation surface.
RUN find / -xdev -perm /6000 -type f -exec chmod a-s {} + \
    && chmod -R go-w /app

# root:control 4750 - the agent identity cannot execute it at all, and the
# launcher independently rejects any caller that is not the controller UID.
COPY --from=launcher /agentcontrol-launch /usr/local/bin/agentcontrol-launch
COPY src/container/control-entrypoint.sh /usr/local/bin/control-entrypoint
COPY src/container/prepare-layout.py /usr/local/bin/agentcontrol-prepare-layout
RUN chown root:control /usr/local/bin/agentcontrol-launch \
    && chmod 4750 /usr/local/bin/agentcontrol-launch \
    && chown root:root /usr/local/bin/control-entrypoint /usr/local/bin/agentcontrol-prepare-layout \
    && chmod 0755 /usr/local/bin/control-entrypoint /usr/local/bin/agentcontrol-prepare-layout

ENV ASPNETCORE_HTTP_PORTS=8080 HOME=/control-data LANG=C.UTF-8 TERM=xterm-256color
EXPOSE 8080
# tini is PID 1. The controller reaps its own direct children through the normal
# Process/wait path; tini exists for descendants that outlive their parent - an
# orphaned tmux or bridge child is reparented to PID 1 and reaped there instead
# of accumulating as a zombie.
#
# The entrypoint runs as tini's child and `exec`s the controller in its own
# place, so the controller keeps the entrypoint's PID. That PID is *not* 1 (tini
# holds it) and is not a fixed number to hard-code: it is whatever the PID
# namespace assigned to tini's child. The entrypoint records it in
# /control-data/controller.pid before the exec, which is how operators inspect
# the controller:
#
#   docker compose exec -T control sh -c \
#     'grep CapBnd /proc/$(cat /control-data/controller.pid)/status'
#
# Do not substitute `pgrep -f HVO.AgentControl.dll`: the pattern appears in the
# inspecting command's own command line, so pgrep matches that shell and reports
# a PID even when no controller is running (measured). The PID file is only
# meaningful while the container runs; a stopped container leaves a stale value.
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/control-entrypoint", "dotnet", "HVO.AgentControl.dll"]

FROM control-runtime AS control

# Independent worker artifact. It is not used by the control service unless the
# optional Compose profile is explicitly selected. PID 1 is the fixed-operation
# root supervisor; bridge and employee processes are distinct unprivileged UIDs.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS worker
RUN apt-get update && apt-get install -y --no-install-recommends python3 ca-certificates git tmux util-linux \
    && rm -rf /var/lib/apt/lists/*
COPY --from=opencode /usr/local/bin/node /usr/local/bin/node
COPY --from=opencode /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/opencode-ai/bin/opencode.exe /usr/local/bin/opencode \
    && userdel ubuntu \
    && { groupdel ubuntu 2>/dev/null || true; } \
    && groupadd --gid 1101 bridge \
    && useradd --uid 1101 --gid 1101 --home-dir /control --shell /usr/sbin/nologin --no-create-home bridge \
    && groupadd --gid 1102 employee \
    && useradd --uid 1102 --gid 1102 --home-dir /home/worker --shell /bin/bash --no-create-home employee \
    && mkdir -p /app /control /home/worker /workspace /session \
    && chown 1101:1101 /control \
    && chown 1102:1102 /home/worker /workspace /session \
    && chmod 0700 /control /home/worker /workspace /session
COPY --from=build /worker-app/ /app/
COPY src/container/worker-supervisor.py /usr/local/bin/worker-supervisor
COPY src/container/profile-image-verify.py /usr/local/bin/profile-image-verify
COPY src/container/workspace-task-verify.py /usr/local/bin/workspace-task-verify
RUN find / -xdev -perm /6000 -type f -exec chmod a-s {} + \
    && chown -R root:root /app /usr/local/bin/worker-supervisor /usr/local/bin/profile-image-verify /usr/local/bin/workspace-task-verify \
    && chmod -R go-w /app \
    && chmod 0755 /usr/local/bin/worker-supervisor /usr/local/bin/profile-image-verify /usr/local/bin/workspace-task-verify
ENV LANG=C.UTF-8
ENTRYPOINT ["/usr/bin/python3", "-I", "-S", "/usr/local/bin/worker-supervisor"]

# Privileged daemon helper: the only service with the Docker socket. Debian's
# docker.io package supplies the CLI from the same signed distribution archive
# used by the base image, avoiding an additional third-party apt trust root.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS docker-helper
# The empty named socket volume copies the mountpoint metadata on first use.
# GID 1001 is the controller group; the helper keeps UID 1002 and Compose adds
# the host Docker-socket gid only as a supplementary group.
RUN apt-get update && apt-get install -y --no-install-recommends docker.io ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && userdel ubuntu \
    && { groupdel ubuntu 2>/dev/null || true; } \
    && groupadd --gid 1002 dockerhelper \
    && useradd --uid 1002 --gid 1002 --home-dir /nonexistent --shell /usr/sbin/nologin --no-create-home dockerhelper \
    && mkdir -p /app /run/agentcontrol-docker-helper \
    && chown 1002:1001 /run/agentcontrol-docker-helper \
    && chmod 0750 /run/agentcontrol-docker-helper
COPY --from=build /docker-helper-app/ /app/
RUN find / -xdev -perm /6000 -type f -exec chmod a-s {} + && chmod -R go-w /app
USER 1002:1002
WORKDIR /app
ENTRYPOINT ["dotnet", "HVO.AgentControl.DockerHelper.dll"]

# Preserve the historical default build result for existing control-image jobs;
# the worker remains available only through the explicit `--target worker`.
FROM control AS final
