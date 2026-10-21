#!/usr/bin/env bash
# Publishes the host agent to build/host-agent (framework-dependent; the image provides the ASP.NET runtime).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rm -rf "$ROOT/build/host-agent"
dotnet publish "$ROOT/backend/VoxelRush.HostAgent/VoxelRush.HostAgent.csproj" -c Release -o "$ROOT/build/host-agent" --nologo -v quiet
echo "Built host agent -> $ROOT/build/host-agent"
