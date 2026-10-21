#!/usr/bin/env bash
# VR-081 rollback drill: the candidate server image builds a world (durable edits), then the previous image takes over
# the same world volume, as a rollback would. Passes when the compatibility check allows the rollback, the previous
# image recovers every journal record, and a client joining it sees exactly the candidate's chunk hashes.
# Exit 2 = rollback blocked by data-version labels (the release must be marked "no rollback").
# Usage: tools/rollback-drill.sh <candidate-image> <previous-image>   (Linux x86-64 Docker; bots from build/bot-linux)
#        tools/rollback-drill.sh <candidate-dir> <previous-dir>        (native macOS server builds; no label check)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CANDIDATE="$1"; PREVIOUS="$2"
LOGS="$ROOT/artifacts/rollback-drill"
NET="voxelrush-drill"
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
rm -rf "$LOGS" && mkdir -p "$LOGS/world" && chmod 777 "$LOGS/world"
cleanup() { docker rm -f voxelrush-drill-server >/dev/null 2>&1 || true; docker network rm "$NET" >/dev/null 2>&1 || true; }
trap cleanup EXIT
fail() { echo "ROLLBACK DRILL FAIL: $*" >&2; exit 1; }

NATIVE=0; [[ -d "$CANDIDATE" && -d "$PREVIOUS" ]] && NATIVE=1
if [[ $NATIVE -eq 0 ]]; then
  "$ROOT/tools/rollback-check.sh" "$CANDIDATE" "$PREVIOUS" | tee "$LOGS/check.txt" || { echo "ROLLBACK DRILL: blocked by data versions"; exit 2; }
  docker network create "$NET" >/dev/null
fi

server() { # <image|dir> <log> <ticks>
  if [[ $NATIVE -eq 1 ]]; then
    perl -e 'alarm shift; exec @ARGV' 120 "$1/VoxelRush" -batchmode -nographics -logFile - -port 7777 -worldDir "$LOGS/world" -smokeTestTicks "$3" >"$2" 2>&1 &
  else
    docker run --rm --init --name voxelrush-drill-server --network "$NET" -v "$LOGS/world:/data" "$1" \
      -port 7777 -worldDir /data -smokeTestTicks "$3" >"$2" 2>&1 &
  fi
  SERVER_PID=$!
  for _ in $(seq 1 120); do grep -q "event=server_listening" "$2" 2>/dev/null && return 0; sleep 0.5; done
  fail "server never listened ($2)"
}
bot() { # <log> <args...>
  local log="$1"; shift
  if [[ $NATIVE -eq 1 ]]; then
    perl -e 'alarm shift; exec @ARGV' 90 "$ROOT/build/bot-mac/VoxelRush" -batchmode -nographics -logFile - -connect "127.0.0.1:7777" "$@" >"$log" 2>&1
    return
  fi
  docker run --rm --init --network "$NET" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" "$BOT_IMAGE" \
    timeout --kill-after=10 90 /bot/VoxelRushBot.x86_64 -batchmode -nographics -logFile - -connect "voxelrush-drill-server:7777" "$@" >"$log" 2>&1
}
hashes() { grep "event=$2 " "$1" | sed -nE 's/.*chunk=([^ ]+) revision=([0-9]+) hash=([0-9A-F]+).*/\1 \2 \3/p' | sort; }

server "$CANDIDATE" "$LOGS/candidate.log" 900
bot "$LOGS/builder.log" -botBuild 20 -smokeTestSeconds 20 || fail "builder bot failed on the candidate"
wait "$SERVER_PID" || fail "candidate server exited non-zero"
hashes "$LOGS/candidate.log" world_chunk_hash >"$LOGS/candidate.hashes"
durable=$(sed -nE 's/.*event=world_closed .*durable=([0-9]+).*/\1/p' "$LOGS/candidate.log")
[[ -s "$LOGS/candidate.hashes" && "${durable:-0}" -gt 0 ]] || fail "candidate wrote no world data"

server "$PREVIOUS" "$LOGS/previous.log" 450
grep -q "event=world_recovered .*records=$durable " "$LOGS/previous.log" || fail "previous image did not recover all $durable records"
bot "$LOGS/joiner.log" -smokeTestSeconds 6 || fail "client could not join the rolled-back server"
wait "$SERVER_PID" || fail "previous server exited non-zero"
hashes "$LOGS/joiner.log" world_replica_hash >"$LOGS/joiner.hashes"
diff -u "$LOGS/candidate.hashes" "$LOGS/joiner.hashes" || fail "world differs after rollback"
"$ROOT/tools/check-server-log.sh" "$LOGS/previous.log" >/dev/null || fail "alerts on the rolled-back server"
echo "ROLLBACK DRILL PASS: $durable records and $(wc -l <"$LOGS/candidate.hashes" | tr -d ' ') chunks intact on $PREVIOUS"
