#!/usr/bin/env bash
# VR-082: 8-player destruction stress with a server crash. Four bots stack towers and cut them (server-side collapse),
# four build and tear down rows, all at once; the server is SIGKILLed mid-activity, restarted on the same world, and
# a late joiner checks the result. Passes when no acknowledged edit is lost (every chunk's recovered revision >=
# the highest revision any bot was acknowledged for), the late joiner's chunks equal the restarted server's, the tick
# held 30 Hz without overruns under load, and the restarted server raises no alerts.
# Usage: tools/stress-destruction.sh native|docker
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
LOGS="$ROOT/artifacts/stress-destruction"
WORLD="$LOGS/world"
PORT=7777
NET="voxelrush-stress"
IMAGE="voxelrush-server:stress"
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
pids=()
rm -rf "$LOGS" && mkdir -p "$WORLD" && chmod 777 "$WORLD"
cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  if [[ "$MODE" == "docker" ]]; then docker rm -f voxelrush-stress-server >/dev/null 2>&1 || true; docker network rm "$NET" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT
fail() { echo "DESTRUCTION STRESS FAIL: $*" >&2; exit 1; }

if [[ "$MODE" == "docker" ]]; then
  docker build -q -f "$ROOT/docker/server/Dockerfile" -t "$IMAGE" "$ROOT/build/server" >/dev/null
  docker network create "$NET" >/dev/null
  ENDPOINT="voxelrush-stress-server:$PORT"
else
  ENDPOINT="127.0.0.1:$PORT"
fi

start_server() { # <log> <ticks>
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --name voxelrush-stress-server --network "$NET" -v "$WORLD:/data" "$IMAGE" -port "$PORT" -worldDir /data -smokeTestTicks "$2" >"$1" 2>&1 &
  else
    perl -e 'alarm shift; exec @ARGV' 200 "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - -port "$PORT" -worldDir "$WORLD" -smokeTestTicks "$2" >"$1" 2>&1 &
  fi
  SERVER_PID=$!; pids+=("$SERVER_PID")
  for _ in $(seq 1 120); do grep -q "event=server_listening" "$1" 2>/dev/null && return 0; sleep 0.5; done
  fail "server never listened ($1)"
}
crash_server() {
  if [[ "$MODE" == "docker" ]]; then docker kill -s KILL voxelrush-stress-server >/dev/null; else kill -9 "$SERVER_PID"; fi
  wait "$SERVER_PID" 2>/dev/null || true
}
bot() { # <log> <args...>
  local log="$1"; shift
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --network "$NET" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" "$BOT_IMAGE" \
      timeout --kill-after=10 120 /bot/VoxelRushBot.x86_64 -batchmode -nographics -logFile - -connect "$ENDPOINT" "$@" >"$log" 2>&1 &
  else
    perl -e 'alarm shift; exec @ARGV' 120 "$ROOT/build/bot-mac/VoxelRush" -batchmode -nographics -logFile - -connect "$ENDPOINT" "$@" >"$log" 2>&1 &
  fi
  pids+=($!)
}

echo "== 8 players: 4 tower cutters + 4 row builders, then SIGKILL"
start_server "$LOGS/server1.log" 6000
for i in 1 2 3 4; do bot "$LOGS/tower$i.log" -botTower 5 -smokeTestSeconds 60; bot "$LOGS/rows$i.log" -botBuild 300 -smokeTestSeconds 60; sleep 0.3; done
# Crash once every tower is cut and the row builders are well into their work.
for _ in $(seq 1 120); do
  towers=$( (grep -l "event=bot_tower_done" "$LOGS"/tower*.log 2>/dev/null || true) | wc -l | tr -d ' ')
  rows=$(cat "$LOGS"/rows*.log 2>/dev/null | grep -c "event=bot_edit_ack" || true)
  [[ "$towers" -eq 4 && "$rows" -ge 80 ]] && break
  sleep 0.5
done
[[ "$towers" -eq 4 ]] || fail "only $towers/4 towers were cut before the crash"
crash_server
echo "server killed with $rows row acks and 4 towers cut"
sleep 2

echo "== restart on the same world; late joiner"
start_server "$LOGS/server2.log" 450
bot "$LOGS/joiner.log" -smokeTestSeconds 6
joiner=${pids[${#pids[@]}-1]}
code=0; wait "$joiner" || code=$?; [[ $code -eq 0 ]] || fail "late joiner exited $code"
code=0; wait "$SERVER_PID" || code=$?; [[ $code -eq 0 ]] || fail "restarted server exited $code"

hashes() { grep "event=$2 " "$1" | sed -nE 's/.*chunk=([^ ]+) revision=([0-9]+) hash=([0-9A-F]+).*/\1 \2 \3/p' | sort; }
hashes "$LOGS/server2.log" world_chunk_hash >"$LOGS/server2.hashes"
hashes "$LOGS/joiner.log" world_replica_hash >"$LOGS/joiner.hashes"
diff -u "$LOGS/server2.hashes" "$LOGS/joiner.hashes" || fail "late joiner differs from the recovered server"

# No acknowledged edit lost: per chunk, recovered revision >= highest acknowledged revision.
cat "$LOGS"/tower*.log "$LOGS"/rows*.log | sed -nE 's/.*event=bot_edit_ack chunk=([^ ]+) revision=([0-9]+).*/\1 \2/p' | sort -k1,1 -k2,2n >"$LOGS/acks.txt"
acks=$(wc -l <"$LOGS/acks.txt" | tr -d ' ')
[[ "$acks" -gt 80 ]] || fail "too few acknowledged edits ($acks)"
lost=$(awk 'NR==FNR { rec[$1]=$2; next } { if (!($1 in rec) || $2 > rec[$1]) print }' "$LOGS/server2.hashes" "$LOGS/acks.txt" | head -5)
[[ -z "$lost" ]] || { echo "$lost"; fail "acknowledged edits missing after recovery"; }
records=$(sed -nE 's/.*event=world_recovered .*records=([0-9]+).*/\1/p' "$LOGS/server2.log")
[[ "${records:-0}" -ge "$acks" ]] || fail "recovered $records records for $acks acknowledged edits"
collapsed=$(grep -c "event=world_collapse " "$LOGS/server1.log" || true)
[[ "$collapsed" -ge 1 ]] || fail "no structural collapse ran under load"

# The tick held under 8-player destruction (server1's metric windows before the crash).
overruns=$(sed -nE 's/.*event=server_tick_metrics .*overruns=([0-9]+).*/\1/p' "$LOGS/server1.log" | awk '{ s += $1 } END { print s + 0 }')
p95=$(sed -nE 's/.*event=server_tick_metrics .*p95Ms=([0-9.]+).*/\1/p' "$LOGS/server1.log" | sort -n | tail -1)
[[ "$overruns" -eq 0 ]] || fail "$overruns tick overruns under load"
"$ROOT/tools/check-server-log.sh" "$LOGS/server2.log" >/dev/null || fail "alerts on the restarted server"

echo "acked edits: $acks; recovered records: $records; collapse ticks: $collapsed; worst p95 tick: ${p95:-?} ms; overruns: $overruns"
echo "DESTRUCTION STRESS PASS ($MODE): 8 players, server SIGKILL, no acknowledged edit lost, late join consistent"
