#!/usr/bin/env bash
# Builds a player into build/<target>/.
# Usage: tools/build.sh <android|ios|server|server-mac|client-linux|client-mac> [--development]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOGS="$ROOT/artifacts/build-logs"
mkdir -p "$LOGS"

# The editor must start on the target/subtarget it builds: scripts compile with that target's defines
# (UNITY_SERVER strips client code) before scenes are serialized.
case "${1:-}" in
  android)      METHOD=BuildAndroid;     TARGET=(-buildTarget Android);  OUTPUT="$ROOT/build/android/VoxelRush.apk" ;;
  ios)          METHOD=BuildIos;         TARGET=(-buildTarget iOS);      OUTPUT="$ROOT/build/ios/Xcode" ;;
  server)       METHOD=BuildLinuxServer; TARGET=(-buildTarget Linux64 -standaloneBuildSubtarget Server); OUTPUT="$ROOT/build/server/VoxelRushServer.x86_64" ;;
  server-mac)   METHOD=BuildMacServer;   TARGET=(-buildTarget OSXUniversal -standaloneBuildSubtarget Server); OUTPUT="$ROOT/build/server-mac" ;;
  client-linux) METHOD=BuildLinuxClient; TARGET=(-buildTarget Linux64 -standaloneBuildSubtarget Player); OUTPUT="$ROOT/build/client-linux/VoxelRushClient.x86_64" ;;
  client-mac)   METHOD=BuildMacClient;   TARGET=(-buildTarget OSXUniversal -standaloneBuildSubtarget Player); OUTPUT="$ROOT/build/client-mac/VoxelRushClient.app" ;;
  *)            echo "Usage: $0 <android|ios|server|server-mac|client-linux|client-mac> [--development]" >&2; exit 2 ;;
esac

EXTRA=()
[[ "${2:-}" == "--development" ]] && EXTRA+=(-developmentBuild)

"$ROOT/tools/unity.sh" "${TARGET[@]}" -quit -executeMethod "VoxelRush.Editor.BuildEntryPoints.$METHOD" \
  -buildOutput "$OUTPUT" -logFile "$LOGS/$1.log" "${EXTRA[@]+"${EXTRA[@]}"}"
echo "Built $1 -> $OUTPUT"
