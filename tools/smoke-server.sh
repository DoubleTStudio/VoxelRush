#!/usr/bin/env bash
# Runs a dedicated server build headless and checks its exit paths.
# Usage: tools/smoke-server.sh [build/server]           Linux build in an amd64 Ubuntu container
#        SMOKE_NATIVE=1 tools/smoke-server.sh build/server-mac   macOS build run directly (local dev)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$(cd "${1:-$ROOT/build/server}" && pwd)"
IMAGE="ubuntu:24.04"
BINARY="/srv/VoxelRushServer.x86_64"
# A hung or crashed-and-spinning player must fail the smoke test, not stall CI.
TIMEOUT_SECONDS="${SMOKE_TIMEOUT_SECONDS:-120}"

run_server() {
  if [[ "${SMOKE_NATIVE:-0}" == "1" ]]; then
    perl -e 'alarm shift; exec @ARGV' "$TIMEOUT_SECONDS" "$BUILD_DIR/VoxelRush" -batchmode -nographics -logFile - "$@"
    return
  fi
  docker run --rm --init --platform linux/amd64 -v "$BUILD_DIR:/srv:ro" "$IMAGE" \
    timeout --kill-after=10 "$TIMEOUT_SECONDS" "$BINARY" -batchmode -nographics -logFile - "$@"
}

fail() { echo "SMOKE FAIL: $*" >&2; exit 1; }

if [[ "${SMOKE_NATIVE:-0}" != "1" && "$(uname -m)" == "arm64" ]]; then
  echo "WARNING: arm64 host; amd64 runs under emulation. Mono JIT is known to abort under Docker Desktop's Rosetta emulation." >&2
fi

echo "== smoke: server exits 0 after 60 ticks"
set +e; out="$(run_server -port 7777 -smokeTestTicks 60 2>&1)"; code=$?; set -e
grep -q "event=server_started port=7777 maxPlayers=8 tickRate=30" <<<"$out" || { echo "$out"; fail "missing server_started"; }
grep -q "event=server_smoke_test_passed ticks=60" <<<"$out" || { echo "$out"; fail "missing server_smoke_test_passed"; }
[[ $code -eq 124 || $code -eq 137 || $code -eq 142 ]] && { echo "$out" | tail -40; fail "server did not exit within ${TIMEOUT_SECONDS}s"; }
[[ $code -eq 0 ]] || { echo "$out"; fail "expected exit 0, got $code"; }

echo "== smoke: invalid argument exits 2"
set +e; out="$(run_server -port 0 2>&1)"; code=$?; set -e
grep -q "event=server_invalid_argument" <<<"$out" || { echo "$out"; fail "missing server_invalid_argument"; }
[[ $code -eq 2 ]] || { echo "$out"; fail "expected exit 2, got $code"; }

echo "SMOKE PASS"
