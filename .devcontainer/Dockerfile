# Pin the base operating system; the .NET SDK is installed by the dev container feature.
FROM mcr.microsoft.com/devcontainers/base:3.0.7-ubuntu24.04@sha256:456e33716a8570448b7deca1ffd98fc337adc7b446aceeadb8beaf8d660345b4

ENV NUGET_XMLDOC_MODE=skip
ENV PATH="/home/vscode/.dotnet/tools:${PATH}"

USER root
SHELL ["/bin/bash", "-o", "pipefail", "-c"]

# hadolint ignore=DL3008
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        jq \
        ripgrep \
        shellcheck \
    && rm -rf /var/lib/apt/lists/*

# Create the certificate mount's parent directories before Docker mounts the volume.
RUN mkdir -p /home/vscode/.dotnet/corefx/cryptography/x509stores \
    && chown -R vscode:vscode /home/vscode/.dotnet

USER vscode
