FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS tools
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    openssh-server tmux lsof curl ca-certificates git unzip zip jq ripgrep gh \
    python3 python3-venv nodejs npm build-essential shellcheck sudo procps iproute2 \
    && rm -rf /var/lib/apt/lists/* \
    && (getent passwd 1000 >/dev/null && userdel -r "$(getent passwd 1000 | cut -d: -f1)" || true) \
    && useradd --uid 1000 --create-home --shell /bin/bash agent && passwd -d agent \
    && mkdir -p /run/sshd /home/agent/workspaces \
    && chown -R agent:agent /home/agent \
    && printf 'agent ALL=(ALL) NOPASSWD: ALL\n' > /etc/sudoers.d/agent \
    && chmod 440 /etc/sudoers.d/agent \
    && printf '\nPasswordAuthentication no\nPermitRootLogin no\nAllowUsers agent\n' >> /etc/ssh/sshd_config \
    && rm -f /etc/ssh/ssh_host_*
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
ENV PATH="/home/agent/.local/bin:/home/agent/.dotnet/tools:${PATH}"

FROM tools AS ssh-runtime
COPY docker/development/entrypoint.sh /usr/local/bin/agentcontrol-development
EXPOSE 22
ENTRYPOINT ["/bin/bash", "/usr/local/bin/agentcontrol-development"]
