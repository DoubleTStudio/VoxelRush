#!/usr/bin/env bash
# SAVE-005 end to end (API and PostgreSQL always in Docker): server A takes a world's lease; server B for the
# same world is refused (exit 4); a player builds on A and leaves; A shuts down when idle, flushing and releasing the
# lease; B then starts on the saved world (higher fencing number) and a new player sees exactly A's chunks.
# Usage: tools/smoke-lease.sh native|docker
set -euo pipefail

MODE="${1:-native}"
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOGS="$ROOT/artifacts/smoke-lease"
NET="voxelrush-lease"
API="http://127.0.0.1:18082"
pids=()
rm -rf "$LOGS" && mkdir -p "$LOGS/world"
cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  docker rm -f -v voxelrush-lease-api voxelrush-lease-db voxelrush-lease-7777 voxelrush-lease-7778 >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT
fail() { echo "LEASE SMOKE FAIL: $*" >&2; exit 1; }

TICKET_KEY=$(openssl rand -base64 32); JWT_KEY=$(openssl rand -base64 32); SERVICE_TOKEN=$(openssl rand -hex 24); DB_PASSWORD=$(openssl rand -hex 16)
docker network create "$NET" >/dev/null
docker run -d --name voxelrush-lease-db --network "$NET" -e POSTGRES_PASSWORD="$DB_PASSWORD" -e POSTGRES_DB=voxelrush postgres:17.6-alpine >/dev/null
for _ in $(seq 1 60); do docker exec voxelrush-lease-db pg_isready -U postgres -q 2>/dev/null && break; sleep 1; done
docker build -q -f "$ROOT/docker/api/Dockerfile" -t voxelrush-api:smoke "$ROOT/build/api" >/dev/null
docker run -d --name voxelrush-lease-api --network "$NET" -p 18082:8080 --read-only --tmpfs /tmp --cap-drop ALL \
  -e "VoxelRush__ConnectionString=Host=voxelrush-lease-db;Username=postgres;Password=$DB_PASSWORD;Database=voxelrush" \
  -e "VoxelRush__JwtSigningKey=$JWT_KEY" -e "VoxelRush__TicketKey=$TICKET_KEY" -e "VoxelRush__ServiceToken=$SERVICE_TOKEN" voxelrush-api:smoke >/dev/null
for _ in $(seq 1 60); do curl -fsS "$API/healthz" >/dev/null 2>&1 && break; sleep 1; done

json() { python3 -I -c "import json,sys; print(json.load(sys.stdin)$1)"; }
owner=$(curl -fsS -X POST "$API/v1/auth/guest" | json "['accessToken']")
WORLD=$(curl -fsS -X POST "$API/v1/worlds" -H "Authorization: Bearer $owner" -H "Content-Type: application/json" -d '{"name":"Leased Plot"}' | json "['id']")
ticket() { curl -fsS -X POST "$API/v1/worlds/$WORLD/join" -H "Authorization: Bearer $owner" | json "['ticket']"; }
[[ "$MODE" == "docker" ]] && docker build -q -f "$ROOT/docker/server/Dockerfile" -t voxelrush-server:smoke "$ROOT/build/server" >/dev/null && chmod 777 "$LOGS/world"
server() { # <log> <port> <args...>
  local log="$1" port="$2"; shift 2
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --name "voxelrush-lease-$port" --network "$NET" -v "$LOGS/world:/data" -e "VOXELRUSH_TICKET_KEY=$TICKET_KEY" \
      -e "VOXELRUSH_API_URL=http://voxelrush-lease-api:8080" -e "VOXELRUSH_SERVICE_TOKEN=$SERVICE_TOKEN" voxelrush-server:smoke \
      -port "$port" -worldDir /data -worldId "$WORLD" "$@" >"$log" 2>&1 &
    SERVER_PID=$!; pids+=("$SERVER_PID")
    return
  fi
  VOXELRUSH_TICKET_KEY="$TICKET_KEY" VOXELRUSH_API_URL="$API" VOXELRUSH_SERVICE_TOKEN="$SERVICE_TOKEN" perl -e 'alarm shift; exec @ARGV' 120 \
    "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - -port "$port" -worldDir "$LOGS/world" -worldId "$WORLD" "$@" >"$log" 2>&1 &
  SERVER_PID=$!; pids+=("$SERVER_PID")
}
bot() { local log="$1" port="$2"; shift 2
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --network "$NET" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" "$BOT_IMAGE" \
      timeout --kill-after=10 60 /bot/VoxelRushBot.x86_64 -batchmode -nographics -logFile - -connect "voxelrush-lease-$port:$port" "$@" >"$log" 2>&1
    return
  fi
  perl -e 'alarm shift; exec @ARGV' 60 "$ROOT/build/bot-mac/VoxelRush" -batchmode -nographics -logFile - -connect "127.0.0.1:$port" "$@" >"$log" 2>&1; }
hashes() { grep "event=$2 " "$1" | sed -nE 's/.*chunk=([^ ]+) revision=([0-9]+) hash=([0-9A-F]+).*/\1 \2 \3/p' | sort; }

server "$LOGS/a.log" 7777 -idleShutdownSeconds 6; A=$SERVER_PID
for _ in $(seq 1 120); do grep -q "event=server_listening" "$LOGS/a.log" 2>/dev/null && break; sleep 0.5; done
grep -q "event=server_lease_acquired .*fencing=1" "$LOGS/a.log" || fail "A did not take the lease"

server "$LOGS/b-refused.log" 7778 -smokeTestTicks 300; B=$SERVER_PID
code=0; wait "$B" || code=$?
[[ $code -eq 4 ]] || fail "second server for the same world exited $code (expected 4)"
grep -q "event=server_world_leased result=HeldByOther" "$LOGS/b-refused.log" || fail "B did not report the held lease"

bot "$LOGS/builder.log" 7777 -joinTicket "$(ticket)" -botBuild 12 -smokeTestSeconds 12 || fail "builder failed"
code=0; wait "$A" || code=$?
[[ $code -eq 0 ]] || fail "A exited $code after going idle"
grep -q "event=server_idle_shutdown" "$LOGS/a.log" || fail "A did not shut down for idleness"
grep -q "event=server_lease_released fencing=1 status=204" "$LOGS/a.log" || fail "A did not release its lease"
hashes "$LOGS/a.log" world_chunk_hash >"$LOGS/a.hashes"
[[ -s "$LOGS/a.hashes" ]] || fail "A saved no edits"

server "$LOGS/b.log" 7778 -smokeTestTicks 450; B=$SERVER_PID
for _ in $(seq 1 120); do grep -q "event=server_listening" "$LOGS/b.log" 2>/dev/null && break; sleep 0.5; done
grep -q "event=server_lease_acquired .*fencing=2" "$LOGS/b.log" || fail "B did not take over with fencing 2"
bot "$LOGS/joiner.log" 7778 -joinTicket "$(ticket)" -smokeTestSeconds 6 || fail "joiner failed"
wait "$B" || fail "B exited non-zero"
hashes "$LOGS/joiner.log" world_replica_hash >"$LOGS/joiner.hashes"
diff -u "$LOGS/a.hashes" "$LOGS/joiner.hashes" || fail "the saved world changed across the handover"
echo "LEASE SMOKE PASS ($MODE): second server refused, idle shutdown released the lease, saved world survived the handover ($(wc -l <"$LOGS/a.hashes" | tr -d ' ') chunks)"
