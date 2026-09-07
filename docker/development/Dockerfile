FROM mcr.microsoft.com/dotnet/sdk:10.0.400
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    openssh-server tmux lsof curl ca-certificates git unzip jq ripgrep gh \
    && rm -rf /var/lib/apt/lists/* \
    && (getent passwd 1000 >/dev/null && userdel -r "$(getent passwd 1000 | cut -d: -f1)" || true) \
    && useradd --uid 1000 --create-home --shell /bin/bash agent && passwd -d agent \
    && mkdir -p /run/sshd /home/agent/workspaces \
    && chown -R agent:agent /home/agent \
    && printf '\nPasswordAuthentication no\nPermitRootLogin no\nAllowUsers agent\n' >> /etc/ssh/sshd_config \
    && rm -f /etc/ssh/ssh_host_*
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
COPY docker/development/entrypoint.sh /usr/local/bin/agentcontrol-development
EXPOSE 22
ENTRYPOINT ["/bin/bash", "/usr/local/bin/agentcontrol-development"]
