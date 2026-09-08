# The upstream image uses Alpine, the musl executable, libgcc/libstdc++ and ripgrep.
# Pin the multi-platform manifest; do not upgrade a running native server implicitly.
FROM ghcr.io/anomalyco/opencode:1.18.29@sha256:ecc3bf96ee55dad226d9cde50d79aaa8a1215c47860c0fcdc71570461bf438b8

USER root
RUN apk add --no-cache curl \
    && addgroup -g 1000 opencode \
    && adduser -D -u 1000 -G opencode -h /var/lib/opencode opencode \
    && mkdir -p /var/lib/opencode/data /var/lib/opencode/state /var/lib/opencode/cache /var/lib/opencode/config /var/lib/opencode/workspaces/control \
    && chown -R 1000:1000 /var/lib/opencode

ENV HOME=/var/lib/opencode \
    XDG_DATA_HOME=/var/lib/opencode/data \
    XDG_STATE_HOME=/var/lib/opencode/state \
    XDG_CACHE_HOME=/var/lib/opencode/cache \
    XDG_CONFIG_HOME=/var/lib/opencode/config \
    OPENCODE_CONFIG=/etc/opencode/control.json \
    OPENCODE_SERVER_PASSWORD_FILE=/run/secrets/opencode-server-password

COPY control.json /etc/opencode/control.json
COPY --chmod=755 entrypoint.sh healthcheck.sh /usr/local/bin/
COPY password.sh /usr/local/lib/opencode-password.sh
COPY identity.sh /usr/local/lib/opencode-identity.sh

USER 1000:1000
WORKDIR /var/lib/opencode/workspaces/control
EXPOSE 4096
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 CMD ["/usr/local/bin/healthcheck.sh"]
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["serve", "--hostname", "0.0.0.0", "--port", "4096"]
