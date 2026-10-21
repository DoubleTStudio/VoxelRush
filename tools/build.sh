#!/usr/bin/env bash
# Builds a player into build/<target>/.
# Usage: tools/build.sh <android|ios|server|server-mac|bot-linux|bot-mac> [--development|--release]
# --release: signed with the upload key from VOXELRUSH_ANDROID_* environment variables (see Editor/ReleaseSettings.cs).
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
  bot-linux)    METHOD=BuildLinuxBot;    TARGET=(-buildTarget Linux64 -standaloneBuildSubtarget Server); OUTPUT="$ROOT/build/bot-linux/VoxelRushBot.x86_64" ;;
  bot-mac)      METHOD=BuildMacBot;      TARGET=(-buildTarget OSXUniversal -standaloneBuildSubtarget Server); OUTPUT="$ROOT/build/bot-mac" ;;
  *)            echo "Usage: $0 <android|ios|server|server-mac|bot-linux|bot-mac> [--development|--release]" >&2; exit 2 ;;
esac

EXTRA=()
[[ "${2:-}" == "--development" ]] && EXTRA+=(-developmentBuild)
[[ "${2:-}" == "--release" ]] && EXTRA+=(-releaseBuild)

"$ROOT/tools/unity.sh" "${TARGET[@]}" -quit -executeMethod "VoxelRush.Editor.BuildEntryPoints.$METHOD" \
  -buildOutput "$OUTPUT" -logFile "$LOGS/$1.log" "${EXTRA[@]+"${EXTRA[@]}"}"
echo "Built $1 -> $OUTPUT"
