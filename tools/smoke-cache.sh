#!/usr/bin/env bash
# SAVE-008 end to end: a player's device chunk cache. A builder edits; joiner J1 (cold cache) syncs and saves its cache;
# more edits change some chunks; J2 rejoins with the cache: unchanged chunks are confirmed (not re-sent), changed ones
# arrive as snapshots. J3 rejoins with a tampered cache entry and falls back to the server. Every joiner's hashes
# must equal the server's.
# Usage: tools/smoke-cache.sh native|docker
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
LOGS="$ROOT/artifacts/smoke-cache"
PORT=7777
IMAGE="voxelrush-server:smoke"
NETWORK="voxelrush-cache"
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
pids=()
rm -rf "$LOGS" && mkdir -p "$LOGS/world" "$LOGS/cache" && chmod 777 "$LOGS/world" "$LOGS/cache"
cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  if [[ "$MODE" == "docker" ]]; then
    docker rm -f voxelrush-cache-server >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT
fail() { echo "CACHE SMOKE FAIL: $*" >&2; exit 1; }

bot() { # <log> <args...>, foreground; the cache directory is /cache in Docker
  local log="$1"; shift
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --network "$NETWORK" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" -v "$LOGS/cache:/cache" "$BOT_IMAGE" \
      timeout --kill-after=10 60 /bot/VoxelRushBot.x86_64 -batchmode -nographics -logFile - -connect "voxelrush-cache-server:$PORT" "$@" >"$log" 2>&1
  else
    perl -e 'alarm shift; exec @ARGV' 60 "$ROOT/build/bot-mac/VoxelRush" -batchmode -nographics -logFile - -connect "127.0.0.1:$PORT" "${@/#\/cache/$LOGS/cache}" >"$log" 2>&1
  fi
}
hashes() { grep "event=$2 " "$1" | sed -nE 's/.*chunk=([^ ]+) revision=([0-9]+) hash=([0-9A-F]+).*/\1 \2 \3/p' | sort; }
stat_of() { sed -nE "s/.*event=world_replica_stats .*$2=([0-9]+).*/\1/p" "$1" | tail -1; }

if [[ "$MODE" == "docker" ]]; then
  docker build -q -f "$ROOT/docker/server/Dockerfile" -t "$IMAGE" "$ROOT/build/server" >/dev/null
  docker network create "$NETWORK" >/dev/null
  docker run --rm --init --name voxelrush-cache-server --network "$NETWORK" -v "$LOGS/world:/data" "$IMAGE" \
    -port "$PORT" -worldDir /data -smokeTestTicks 1800 >"$LOGS/server.log" 2>&1 &
else
  perl -e 'alarm shift; exec @ARGV' 120 "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - \
    -port "$PORT" -worldDir "$LOGS/world" -smokeTestTicks 1800 >"$LOGS/server.log" 2>&1 &
fi
SERVER_PID=$!; pids+=("$SERVER_PID")
for _ in $(seq 1 120); do grep -q "event=server_listening" "$LOGS/server.log" 2>/dev/null && break; sleep 0.5; done

bot "$LOGS/builder1.log" -botBuild 12 -smokeTestSeconds 10 || fail "builder 1 failed"
bot "$LOGS/j1.log" -chunkCache /cache -smokeTestSeconds 5 || fail "J1 failed"
REGION=$(sed -nE 's/.*event=world_info .*chunks=([0-9]+).*/\1/p' "$LOGS/j1.log")
[[ "$(stat_of "$LOGS/j1.log" cacheHits)" -eq 0 ]] || fail "J1 had a cold cache"
grep -q "event=world_cache_saved chunks=$REGION" "$LOGS/j1.log" || fail "J1 did not save its cache"

bot "$LOGS/builder2.log" -botBuild 5 -smokeTestSeconds 8 || fail "builder 2 failed"
bot "$LOGS/j2.log" -chunkCache /cache -smokeTestSeconds 5 || fail "J2 failed"
hits=$(stat_of "$LOGS/j2.log" cacheHits); snaps=$(stat_of "$LOGS/j2.log" snapshots)
[[ "$hits" -ge $((REGION - 4)) && "$hits" -lt "$REGION" ]] || fail "J2 confirmed $hits/$REGION chunks (expected all but the changed ones)"
[[ $((hits + snaps)) -ge "$REGION" && "$snaps" -le 4 ]] || fail "J2 got $snaps full snapshots"
[[ "$(stat_of "$LOGS/j2.log" cacheMisses)" -eq 0 ]] || fail "J2 had cache misses"

# Tamper with the body of an edited chunk's entry (header keeps its revision and hash).
entry=$(find "$LOGS/cache" -name "c_*.chunk" -size +40c | head -1)
[[ -n "$entry" ]] || fail "no edited chunk in the cache"
python3 -I -c "import sys; p=sys.argv[1]; b=bytearray(open(p,'rb').read()); b[-1]^=7; open(p,'wb').write(b)" "$entry"
bot "$LOGS/j3.log" -chunkCache /cache -smokeTestSeconds 5 || fail "J3 failed"
[[ "$(stat_of "$LOGS/j3.log" cacheMisses)" -ge 1 && "$(stat_of "$LOGS/j3.log" hashMismatches)" -ge 1 ]] || fail "J3 trusted a tampered entry"

wait "$SERVER_PID" || fail "server exited non-zero"
hashes "$LOGS/server.log" world_chunk_hash >"$LOGS/server.hashes"
for j in j2 j3; do
  hashes "$LOGS/$j.log" world_replica_hash >"$LOGS/$j.hashes"
  diff -u "$LOGS/server.hashes" "$LOGS/$j.hashes" || fail "$j's world differs from the server's"
done
grep -q "event=world_closed .*cacheConfirmed=[1-9]" "$LOGS/server.log" || fail "server confirmed no cached chunks"
echo "CACHE SMOKE PASS ($MODE): rejoin confirmed $hits/$REGION chunks from the device cache, $snaps re-sent, tampered entry fell back to the server"
