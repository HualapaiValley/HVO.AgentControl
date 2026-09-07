FROM ubuntu:24.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends openssh-server tmux lsof curl ca-certificates git unzip python3 && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --shell /bin/bash agent && passwd -d agent && mkdir -p /run/sshd /home/agent/.ssh /home/agent/workspaces/a /home/agent/workspaces/b \
    && chmod 700 /home/agent/.ssh && chown -R agent:agent /home/agent \
    && printf '\nPasswordAuthentication no\nPermitRootLogin no\nAllowUsers agent\n' >> /etc/ssh/sshd_config \
    && rm -f /etc/ssh/ssh_host_*
COPY fixture-opencode.py /usr/local/bin/fixture-opencode
RUN chmod 755 /usr/local/bin/fixture-opencode
EXPOSE 22
CMD ["/bin/sh", "-c", "ssh-keygen -A >/dev/null && exec /usr/sbin/sshd -D -e"]
