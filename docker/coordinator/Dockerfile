FROM ubuntu:24.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends openssh-server tmux lsof curl ca-certificates git unzip \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --shell /bin/bash agent && passwd -d agent \
    && mkdir -p /run/sshd /home/agent/workspaces/coordinator \
    && chown -R agent:agent /home/agent \
    && printf '\nPasswordAuthentication no\nPermitRootLogin no\nAllowUsers agent\nAuthorizedKeysFile .ssh/authorized_keys\n' >> /etc/ssh/sshd_config \
    && rm -f /etc/ssh/ssh_host_*
EXPOSE 22
CMD ["/bin/sh", "-c", "mkdir -p /home/agent/.ssh && cp /run/agentcontrol/authorized_keys /home/agent/.ssh/authorized_keys && chown -R agent:agent /home/agent/.ssh && chmod 700 /home/agent/.ssh && chmod 600 /home/agent/.ssh/authorized_keys && ssh-keygen -A >/dev/null && exec /usr/sbin/sshd -D -e -o AuthorizedKeysFile=.ssh/authorized_keys"]
