#!/usr/bin/env bash
# VR-012 DoD: two predicted cars on an authoritative server at 100 ms RTT, plus a speed-hacking client.
# Bots are development builds (latency simulation and cheat mode are dev-only).
# Usage: tools/smoke-drive.sh native   macOS server + dev bot (build/server-mac, build/bot-mac)
#        tools/smoke-drive.sh docker   Linux server image + dev Linux bots on one Docker network (x86-64 hosts)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
LOGS="$ROOT/artifacts/smoke-drive"
PORT=7777
RTT_MS=100
DRIVE_SECONDS=22
PROCESS_TIMEOUT=90
IMAGE="voxelrush-server:smoke"
NETWORK="voxelrush-drive"
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
pids=()

rm -rf "$LOGS" && mkdir -p "$LOGS"

cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  if [[ "$MODE" == "docker" ]]; then
    docker rm -f voxelrush-drive-server >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

fail() { echo "DRIVE SMOKE FAIL: $*" >&2; exit 1; }

# last <file> <event> <field>: value of field in the last matching event line
last() { grep "event=$2 " "$1" | tail -1 | sed -nE "s/.* $3=\"?([^ \"]+).*/\1/p"; }

# check <description> <awk condition using v>
check() { awk -v v="$2" "BEGIN { exit !($3) }" || fail "$1 (got '$2')"; }

if [[ "$MODE" == "docker" ]]; then
  docker build -q -f "$ROOT/docker/server/Dockerfile" -t "$IMAGE" "$ROOT/build/server" >/dev/null
  docker network create "$NETWORK" >/dev/null
  docker run --rm --init --name voxelrush-drive-server --network "$NETWORK" "$IMAGE" -port "$PORT" -smokeTestTicks 1200 >"$LOGS/server.log" 2>&1 &
else
  perl -e 'alarm shift; exec @ARGV' "$PROCESS_TIMEOUT" "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - -port "$PORT" -smokeTestTicks 1200 >"$LOGS/server.log" 2>&1 &
fi
SERVER_PID=$!
pids+=("$SERVER_PID")
for _ in $(seq 1 120); do grep -q "event=server_listening" "$LOGS/server.log" 2>/dev/null && break; sleep 0.5; done
grep -q "event=server_listening" "$LOGS/server.log" || fail "server never listened"

start_bot() {
  local log="$1"; shift
  local args=(-batchmode -nographics -logFile - -smokeTestSeconds "$DRIVE_SECONDS" -botDrive -simRttMs "$RTT_MS" "$@")
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --network "$NETWORK" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" "$BOT_IMAGE" \
      timeout --kill-after=10 "$PROCESS_TIMEOUT" /bot/VoxelRushBot.x86_64 -connect "voxelrush-drive-server:$PORT" "${args[@]}" >"$log" 2>&1 &
  else
    perl -e 'alarm shift; exec @ARGV' "$PROCESS_TIMEOUT" "$ROOT/build/bot-mac/VoxelRush" -connect "127.0.0.1:$PORT" "${args[@]}" >"$log" 2>&1 &
  fi
  BOT_PID=$!
  pids+=("$BOT_PID")
}

start_bot "$LOGS/bot-honest.log"; HONEST=$BOT_PID
start_bot "$LOGS/bot-cheat.log" -botCheat; CHEAT=$BOT_PID
code=0; wait "$HONEST" || code=$?; [[ $code -eq 0 ]] || fail "honest bot exit $code (see $LOGS/bot-honest.log)"
code=0; wait "$CHEAT" || code=$?; [[ $code -eq 0 ]] || fail "cheat bot exit $code (see $LOGS/bot-cheat.log)"
code=0; wait "$SERVER_PID" || code=$?; [[ $code -eq 0 ]] || fail "server exit $code (see $LOGS/server.log)"

[[ $(grep -c "event=vehicle_spawned " "$LOGS/server.log") -eq 2 ]] || fail "expected 2 vehicles spawned"

# Server-side: cars spawn at separate points (a car teleported to the origin would make these collide), driven by
# their owners' inputs, within the speed limit.
[[ $(grep "event=vehicle_spawned " "$LOGS/server.log" | sed -nE 's/.*position="([^"]+)".*/\1/p' | sort -u | wc -l) -eq 2 ]] || fail "cars spawned at the same point"
for owner in $(grep "event=vehicle_spawned " "$LOGS/server.log" | sed -nE 's/.*ownerClientId=([0-9]+).*/\1/p'); do
  line=$(grep "event=vehicle_server_stats ownerClientId=$owner " "$LOGS/server.log" | tail -1)
  [[ -n "$line" ]] || fail "no server stats for client $owner"
  dist=$(sed -nE 's/.* distance=([^ ]+).*/\1/p' <<<"$line"); speed=$(sed -nE 's/.* maxSpeed=([^ ]+).*/\1/p' <<<"$line")
  check "client $owner car did not move under its input" "$dist" "v > 50"
  check "client $owner exceeded top speed" "$speed" "v <= 29.5"
done
malformed=$(grep "event=vehicle_server_stats " "$LOGS/server.log" | sed -nE 's/.*rejectedMalformed=([0-9]+).*/\1/p' | sort -n | tail -1)
check "server did not reject the speed-hack inputs" "$malformed" "v >= 5"

# Client-side: prediction stays close to the server and remote cars stream in order.
for bot in honest cheat; do
  log="$LOGS/bot-$bot.log"
  grep -q "event=network_conditions rttMs=$RTT_MS" "$log" || fail "$bot bot did not apply ${RTT_MS} ms RTT"
  check "$bot bot ack latency outside 100..250 ms" "$(last "$log" vehicle_prediction_stats rttMs)" "v >= 100 && v <= 250"
  check "$bot bot received too few snapshots" "$(last "$log" vehicle_prediction_stats snapshots)" "v >= 200"
  check "$bot bot mean prediction error too large" "$(last "$log" vehicle_prediction_stats meanErrorM)" "v >= 0 && v < 0.25"
  # Error-driven snaps only; pre-ack re-syncs are logged separately as sync.
  check "$bot bot hard-corrected too often" "$(last "$log" vehicle_prediction_stats snap)" "v <= 3"
  check "$bot bot saw too few remote snapshots" "$(last "$log" vehicle_remote_stats snapshots)" "v >= 200"
  check "$bot bot received remote snapshots out of order" "$(last "$log" vehicle_remote_stats outOfOrder)" "v == 0"
done

grep -h "event=vehicle_server_stats " "$LOGS/server.log" | tail -2
grep -h "event=vehicle_prediction_stats " "$LOGS"/bot-*.log | sed 's/^/bot: /' | tail -2
echo "DRIVE SMOKE PASS ($MODE, ${RTT_MS} ms RTT)"
