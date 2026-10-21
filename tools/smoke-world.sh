#!/usr/bin/env bash
# VR-033 DoD: late-join chunk hashes match, and persist across a server restart (VR-032 journal recovery).
# Phase 1: server + two concurrent builder bots A1/A2 (30 durable edits each, adjacent rows crossing a chunk border)
#          + tower bot T (stacks 5 blocks, cuts the base; DES-011: the server collapses the other 4)
#          + late-joining bot B. Phase 2: restart server on the same world dir, bot C joins.
# Usage: [SMOKE_PORT=7777] tools/smoke-world.sh native|docker
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
LOGS="$ROOT/artifacts/smoke-world"
WORLD="$LOGS/world"
PORT="${SMOKE_PORT:-7777}"
EDITS=30
IMAGE="voxelrush-server:smoke"
NETWORK="voxelrush-world"
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
pids=()
# Extra bot flags from the environment, e.g. BOT_ARGS="-simTransportLossPercent 2" (tools/smoke-loss.sh).
read -r -a BOT_EXTRA <<<"${BOT_ARGS:-}"

rm -rf "$LOGS" && mkdir -p "$WORLD" && chmod 777 "$WORLD"
cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  if [[ "$MODE" == "docker" ]]; then
    docker rm -f voxelrush-world-server >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT
fail() { echo "WORLD SMOKE FAIL: $*" >&2; exit 1; }

start_server() { # <log> <ticks>
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --name voxelrush-world-server --network "$NETWORK" -v "$WORLD:/data" "$IMAGE" \
      -port "$PORT" -worldDir /data -smokeTestTicks "$2" >"$1" 2>&1 &
  else
    perl -e 'alarm shift; exec @ARGV' 180 "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - \
      -port "$PORT" -worldDir "$WORLD" -smokeTestTicks "$2" >"$1" 2>&1 &
  fi
  SERVER_PID=$!; pids+=("$SERVER_PID")
  for _ in $(seq 1 120); do grep -q "event=server_listening" "$1" 2>/dev/null && return 0; sleep 0.5; done
  fail "server never listened ($1)"
}

start_bot() { # <log> <args...>
  local log="$1"; shift
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --network "$NETWORK" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" "$BOT_IMAGE" \
      timeout --kill-after=10 120 /bot/VoxelRushBot.x86_64 -batchmode -nographics -logFile - -connect "voxelrush-world-server:$PORT" "$@" "${BOT_EXTRA[@]+"${BOT_EXTRA[@]}"}" >"$log" 2>&1 &
  else
    perl -e 'alarm shift; exec @ARGV' 120 "$ROOT/build/bot-mac/VoxelRush" -batchmode -nographics -logFile - -connect "127.0.0.1:$PORT" "$@" "${BOT_EXTRA[@]+"${BOT_EXTRA[@]}"}" >"$log" 2>&1 &
  fi
  BOT_PID=$!; pids+=("$BOT_PID")
}

expect_exit() { local code=0; wait "$1" || code=$?; [[ $code -eq 0 ]] || fail "$2 exited $code"; }

# Sorted "chunk revision hash" lines from an event.
hashes() { grep "event=$2 " "$1" | sed -nE 's/.*chunk=([^ ]+) revision=([0-9]+) hash=([0-9A-F]+).*/\1 \2 \3/p' | sort; }

if [[ "$MODE" == "docker" ]]; then
  docker build -q -f "$ROOT/docker/server/Dockerfile" -t "$IMAGE" "$ROOT/build/server" >/dev/null
  docker network create "$NETWORK" >/dev/null
fi

echo "== phase 1: builder edits, late joiner syncs"
start_server "$LOGS/server1.log" 1500
start_bot "$LOGS/botA.log" -botBuild "$EDITS" -smokeTestSeconds 25; A=$BOT_PID
# A2 builds ramps (BUILD-002: id 13 = low ramp climbing +Z), so ramp ids cross validation, journal, deltas and restart.
start_bot "$LOGS/botA2.log" -botBuild "$EDITS" -botBlock 13 -smokeTestSeconds 25; A2=$BOT_PID
start_bot "$LOGS/botT.log" -botTower 5 -smokeTestSeconds 25; T=$BOT_PID
for log in botA botA2; do
  for _ in $(seq 1 120); do grep -q "event=bot_build_done" "$LOGS/$log.log" 2>/dev/null && break; sleep 0.5; done
  grep -q "event=bot_build_done edits=$EDITS" "$LOGS/$log.log" || fail "$log did not finish $EDITS edits"
done
for _ in $(seq 1 60); do grep -q "event=bot_tower_done" "$LOGS/botT.log" 2>/dev/null && break; sleep 0.5; done
grep -q "event=bot_tower_done edits=6 accepted=6" "$LOGS/botT.log" || fail "tower bot did not build and cut its tower"
start_bot "$LOGS/botB.log" -smokeTestSeconds 8; B=$BOT_PID
expect_exit "$A" "bot A"; expect_exit "$A2" "bot A2"; expect_exit "$T" "bot T"; expect_exit "$B" "bot B"; expect_exit "$SERVER_PID" "server (phase 1)"
grep -q "event=world_closed .*collapsedBlocks=4 " "$LOGS/server1.log" || fail "server did not collapse the 4 unsupported tower blocks"

hashes "$LOGS/server1.log" world_chunk_hash >"$LOGS/server1.hashes"
hashes "$LOGS/botA.log" world_replica_hash >"$LOGS/botA.hashes"
hashes "$LOGS/botA2.log" world_replica_hash >"$LOGS/botA2.hashes"
hashes "$LOGS/botT.log" world_replica_hash >"$LOGS/botT.hashes"
hashes "$LOGS/botB.log" world_replica_hash >"$LOGS/botB.hashes"
[[ $(wc -l <"$LOGS/server1.hashes") -ge 2 ]] || fail "expected edits in at least 2 chunks"
accepted=0
for log in botA botA2; do
  n=$(sed -nE 's/.*event=bot_build_done edits=[0-9]+ accepted=([0-9]+).*/\1/p' "$LOGS/$log.log")
  [[ "$n" -ge $((EDITS - 2)) ]] || fail "$log: only $n/$EDITS edits accepted"
  accepted=$((accepted + n))
done
diff -u "$LOGS/server1.hashes" "$LOGS/botB.hashes" || fail "late joiner's chunk hashes differ from the server's"
diff -u "$LOGS/server1.hashes" "$LOGS/botA.hashes" || fail "builder A's replica (deltas) differs from the server's"
diff -u "$LOGS/server1.hashes" "$LOGS/botA2.hashes" || fail "builder A2's replica (deltas) differs from the server's"
diff -u "$LOGS/server1.hashes" "$LOGS/botT.hashes" || fail "tower bot's replica (incl. collapse deltas) differs from the server's"
durable=$(sed -nE 's/.*event=world_closed .*durable=([0-9]+).*/\1/p' "$LOGS/server1.log")
echo "edited chunks: $(wc -l <"$LOGS/server1.hashes" | tr -d ' '); accepted row edits: $accepted; durable records: $durable; tower collapsed; late joiner hashes match"

echo "== phase 2: restart on the same world dir, new client joins"
start_server "$LOGS/server2.log" 450
grep -q "event=world_recovered .*records=$durable " "$LOGS/server2.log" || fail "restart did not recover $durable journal records"
start_bot "$LOGS/botC.log" -smokeTestSeconds 6; C=$BOT_PID
expect_exit "$C" "bot C"; expect_exit "$SERVER_PID" "server (phase 2)"
hashes "$LOGS/botC.log" world_replica_hash >"$LOGS/botC.hashes"
diff -u "$LOGS/server1.hashes" "$LOGS/botC.hashes" || fail "hashes after restart differ"
grep -h "event=world_replica_stats" "$LOGS/botB.log" "$LOGS/botC.log" | sed 's/^/stats: /'
echo "WORLD SMOKE PASS ($MODE): late join + restart hashes match ($(wc -l <"$LOGS/server1.hashes" | tr -d ' ') chunks)"
