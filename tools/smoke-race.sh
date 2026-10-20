#!/usr/bin/env bash
# VR-062 DoD: an 8-player race flow on the dedicated server. Eight headless racing bots join the lobby, the server
# auto-starts the countdown, every bot drives the arena course through the server-validated gates, one bot drops its
# connection after gate 1 and resumes with the same session, and all eight receive results identical to the server's.
# Usage: tools/smoke-race.sh native|docker
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
LOGS="$ROOT/artifacts/smoke-race"
WORLD="$LOGS/world"
PORT=7777
BOTS=8
IMAGE="voxelrush-server:smoke"
NETWORK="voxelrush-race"
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
pids=()

rm -rf "$LOGS" && mkdir -p "$WORLD" && chmod 777 "$WORLD"
cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  if [[ "$MODE" == "docker" ]]; then
    docker rm -f voxelrush-race-server >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT
fail() { echo "RACE SMOKE FAIL: $*" >&2; exit 1; }

if [[ "$MODE" == "docker" ]]; then
  docker build -q -f "$ROOT/docker/server/Dockerfile" -t "$IMAGE" "$ROOT/build/server" >/dev/null
  docker network create "$NETWORK" >/dev/null
  docker run --rm --init --name voxelrush-race-server --network "$NETWORK" -v "$WORLD:/data" "$IMAGE" \
    -port "$PORT" -worldDir /data -raceAutoStart "$BOTS" -smokeTestTicks 1650 >"$LOGS/server.log" 2>&1 &
else
  perl -e 'alarm shift; exec @ARGV' 180 "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - \
    -port "$PORT" -worldDir "$WORLD" -raceAutoStart "$BOTS" -smokeTestTicks 1650 >"$LOGS/server.log" 2>&1 &
fi
SERVER_PID=$!; pids+=("$SERVER_PID")
for _ in $(seq 1 120); do grep -q "event=server_listening" "$LOGS/server.log" 2>/dev/null && break; sleep 0.5; done
grep -q "event=server_listening" "$LOGS/server.log" || fail "server never listened"

bot_pids=()
for i in $(seq 1 "$BOTS"); do
  extra=()
  [[ "$i" -eq 3 ]] && extra=(-botReconnectAfterGate 1)
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --network "$NETWORK" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" "$BOT_IMAGE" \
      timeout --kill-after=10 150 /bot/VoxelRushBot.x86_64 -batchmode -nographics -logFile - -connect "voxelrush-race-server:$PORT" \
      -botRace "${extra[@]+"${extra[@]}"}" -smokeTestSeconds 48 >"$LOGS/bot$i.log" 2>&1 &
  else
    perl -e 'alarm shift; exec @ARGV' 150 "$ROOT/build/bot-mac/VoxelRush" -batchmode -nographics -logFile - -connect "127.0.0.1:$PORT" \
      -botRace "${extra[@]+"${extra[@]}"}" -smokeTestSeconds 48 >"$LOGS/bot$i.log" 2>&1 &
  fi
  bot_pids+=($!); pids+=($!)
  sleep 0.3
done

for i in $(seq 1 "$BOTS"); do
  code=0; wait "${bot_pids[$((i - 1))]}" || code=$?
  [[ $code -eq 0 ]] || fail "bot $i exited $code"
done
code=0; wait "$SERVER_PID" || code=$?
[[ $code -eq 0 ]] || fail "server exited $code"

S="$LOGS/server.log"
grep -q "event=race_countdown .*racers=$BOTS " "$S" || fail "countdown never started with $BOTS racers"
for phase in Countdown Racing Finishing Results; do
  grep -q "event=race_phase phase=$phase " "$S" || fail "race never reached $phase"
done
grep -q "event=race_reconnected " "$S" || fail "the reconnecting bot did not resume its race"
grep -q "event=client_reconnecting " "$LOGS/bot3.log" || fail "bot 3 never dropped its connection"
# The first race (auto-start begins another one after the reset; it needn't finish before shutdown).
first() { grep "event=$1 " "$2" | head -n "$BOTS" || true; }
results=$(first race_result "$S" | wc -l | tr -d ' ')
[[ "$results" -eq "$BOTS" ]] || fail "expected $BOTS results, got $results"
finished=$(first race_result "$S" | grep -c "finished=true" || true)
[[ "$finished" -eq "$BOTS" ]] || fail "only $finished/$BOTS racers finished"
if grep -E "event=race_crossing .*outcome=(TooFast|Teleported|InvalidPosition)" "$S"; then fail "legitimate bots were flagged"; fi

# Every client got the same standings as the server.
server_results=$(first race_result "$S" | sed -nE 's/.*place=([0-9]+) racer=([^ ]+) finished=([^ ]+) raceTicks=([0-9]+).*/\1 \2 \3 \4/p')
for i in $(seq 1 "$BOTS"); do
  client=$(first race_client_result "$LOGS/bot$i.log" | sed -nE 's/.*place=([0-9]+) racer=([^ ]+) finished=([^ ]+) raceTicks=([0-9]+).*/\1 \2 \3 \4/p')
  [[ "$client" == "$server_results" ]] || { echo "server:"; echo "$server_results"; echo "bot $i:"; echo "$client"; fail "bot $i results differ"; }
  first race_client_result "$LOGS/bot$i.log" | grep -q "me=true" || fail "bot $i did not find itself in the results"
done

echo "$server_results" | sed 's/^/result: /'
grep -q "event=race_phase phase=Countdown tick=" <(grep "event=race_phase" "$S" | sed -n '6,$p') || fail "no second countdown after reset"
echo "RACE SMOKE PASS ($MODE): $BOTS racers finished, reconnect resumed, results identical on all clients"
