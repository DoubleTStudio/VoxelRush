#!/usr/bin/env bash
# Builds a player into build/<target>/.
# Usage: tools/build.sh <android|ios|server|server-mac> [--development]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOGS="$ROOT/artifacts/build-logs"
mkdir -p "$LOGS"

case "${1:-}" in
  android) METHOD=BuildAndroid;     OUTPUT="$ROOT/build/android/VoxelRush.apk" ;;
  ios)     METHOD=BuildIos;         OUTPUT="$ROOT/build/ios/Xcode" ;;
  server)  METHOD=BuildLinuxServer; OUTPUT="$ROOT/build/server/VoxelRushServer.x86_64" ;;
  server-mac) METHOD=BuildMacServer; OUTPUT="$ROOT/build/server-mac" ;;
  *)       echo "Usage: $0 <android|ios|server|server-mac> [--development]" >&2; exit 2 ;;
esac

EXTRA=()
[[ "${2:-}" == "--development" ]] && EXTRA+=(-developmentBuild)

"$ROOT/tools/unity.sh" -quit -executeMethod "VoxelRush.Editor.BuildEntryPoints.$METHOD" \
  -buildOutput "$OUTPUT" -logFile "$LOGS/$1.log" "${EXTRA[@]+"${EXTRA[@]}"}"
echo "Built $1 -> $OUTPUT"
