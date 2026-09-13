#!/usr/bin/env bash
set -euo pipefail

# Repair ownership from existing containers and the persisted certificate volume.
sudo chown -R "$(id -u):$(id -g)" "$HOME/.dotnet"

dotnet --info
dotnet restore HVO.AgentControl.slnx
dotnet dev-certs https
