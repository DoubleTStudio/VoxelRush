#!/usr/bin/env bash
# VR-002 join test: a dedicated server plus headless clients, checked via structured logs and exit codes.
#   Scenario 1: two clients join a server and stay connected.
#   Scenario 2: with -maxPlayers 1 the second client is rejected with reason=server_full.
# Usage: tools/smoke-join.sh native   macOS server + client builds (build/server-mac, build/client-mac)
#        tools/smoke-join.sh docker   Linux server image + Linux client build in containers (x86-64 hosts)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
LOGS="$ROOT/artifacts/smoke-join"
PORT=7777
IMAGE="voxelrush-server:smoke"
NETWORK="voxelrush-smoke"
CLIENT_IMAGE="ubuntu:24.04"
# Every process is time-boxed so a hang fails the test instead of stalling it.
PROCESS_TIMEOUT=90
pids=()

rm -rf "$LOGS" && mkdir -p "$LOGS"

fail() { echo "JOIN SMOKE FAIL: $*" >&2; cleanup; exit 1; }

cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  if [[ "$MODE" == "docker" ]]; then
    docker rm -f voxelrush-smoke-server >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

# start_server <log> <server args...> -> runs in background, sets SERVER_PID
start_server() {
  local log="$1"; shift
  if [[ "$MODE" == "native" ]]; then
    perl -e 'alarm shift; exec @ARGV' "$PROCESS_TIMEOUT" "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - -port "$PORT" "$@" >"$log" 2>&1 &
  else
    docker run --rm --init --name voxelrush-smoke-server --network "$NETWORK" --stop-timeout 5 "$IMAGE" -port "$PORT" "$@" >"$log" 2>&1 &
  fi
  SERVER_PID=$!
  pids+=("$SERVER_PID")
}

# start_client <log> <client args...> -> runs in background, sets CLIENT_PID (no subshell, so `wait` works)
start_client() {
  local log="$1"; shift
  if [[ "$MODE" == "native" ]]; then
    perl -e 'alarm shift; exec @ARGV' "$PROCESS_TIMEOUT" "$ROOT/build/client-mac/VoxelRushClient.app/Contents/MacOS/VoxelRush" -batchmode -nographics -logFile - \
      -connect "127.0.0.1:$PORT" "$@" >"$log" 2>&1 &
  else
    docker run --rm --init --network "$NETWORK" -v "$ROOT/build/client-linux:/client:ro" "$CLIENT_IMAGE" \
      timeout --kill-after=10 "$PROCESS_TIMEOUT" /client/VoxelRushClient.x86_64 -batchmode -nographics -logFile - -connect "voxelrush-smoke-server:$PORT" "$@" >"$log" 2>&1 &
  fi
  CLIENT_PID=$!
  pids+=("$CLIENT_PID")
}

wait_for_line() {
  local file="$1" pattern="$2" seconds="$3"
  for _ in $(seq 1 $((seconds * 2))); do
    grep -q "$pattern" "$file" 2>/dev/null && return 0
    sleep 0.5
  done
  return 1
}

# expect_exit <pid> <code> <message>: waits in this shell (never in $(...), which can't wait on our children).
expect_exit() {
  local code=0
  wait "$1" || code=$?
  [[ $code -eq $2 ]] || fail "$3 (exit $code, expected $2)"
}

if [[ "$MODE" == "docker" ]]; then
  docker build -q -f "$ROOT/docker/server/Dockerfile" -t "$IMAGE" "$ROOT/build/server" >/dev/null
  docker network create "$NETWORK" >/dev/null
fi

echo "== scenario 1: two clients join"
start_server "$LOGS/s1-server.log" -maxPlayers 8 -smokeTestTicks 900
wait_for_line "$LOGS/s1-server.log" "event=server_listening" 60 || fail "server never listened (see $LOGS/s1-server.log)"
start_client "$LOGS/s1-client1.log" -smokeTestSeconds 5; c1=$CLIENT_PID
start_client "$LOGS/s1-client2.log" -smokeTestSeconds 5; c2=$CLIENT_PID
expect_exit "$c1" 0 "client 1 failed (see $LOGS/s1-client1.log)"
expect_exit "$c2" 0 "client 2 failed (see $LOGS/s1-client2.log)"
grep -q "event=client_connected .*players=2" "$LOGS/s1-server.log" || fail "server never had 2 players"
# Clean client exits must free slots immediately (graceful disconnect), not after a transport timeout.
wait_for_line "$LOGS/s1-server.log" "event=client_disconnected .*players=0" 15 || fail "server did not see both clients disconnect"
expect_exit "$SERVER_PID" 0 "server exited non-zero (see $LOGS/s1-server.log)"
grep -h "event=client_connected\|event=client_disconnected" "$LOGS/s1-server.log"

if [[ "$MODE" == "docker" ]]; then sleep 1; fi

echo "== scenario 2: server full rejects the second client"
start_server "$LOGS/s2-server.log" -maxPlayers 1 -smokeTestTicks 900
wait_for_line "$LOGS/s2-server.log" "event=server_listening" 60 || fail "server never listened (see $LOGS/s2-server.log)"
start_client "$LOGS/s2-client1.log" -smokeTestSeconds 10; c1=$CLIENT_PID
wait_for_line "$LOGS/s2-server.log" "event=client_connected .*players=1" 60 || fail "first client never connected"
start_client "$LOGS/s2-client2.log" -smokeTestSeconds 5; c2=$CLIENT_PID
expect_exit "$c2" 3 "second client should be rejected with exit 3 (see $LOGS/s2-client2.log)"
grep -q "event=client_disconnected reason=server_full" "$LOGS/s2-client2.log" || fail "second client missing reason=server_full"
grep -q "event=client_rejected .*reason=server_full" "$LOGS/s2-server.log" || fail "server did not log the rejection"
expect_exit "$c1" 0 "first client failed (see $LOGS/s2-client1.log)"
expect_exit "$SERVER_PID" 0 "server exited non-zero (see $LOGS/s2-server.log)"
grep -h "event=client_connected\|event=client_rejected" "$LOGS/s2-server.log"

echo "JOIN SMOKE PASS ($MODE)"
