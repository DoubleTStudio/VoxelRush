#!/usr/bin/env bash
# Publishes the metadata API to build/api (framework-dependent; the image provides the ASP.NET runtime).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rm -rf "$ROOT/build/api"
dotnet publish "$ROOT/backend/VoxelRush.Api/VoxelRush.Api.csproj" -c Release -o "$ROOT/build/api" --nologo -v quiet
echo "Built api -> $ROOT/build/api"
