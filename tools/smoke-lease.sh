#!/usr/bin/env bash
# SAVE-005/SAVE-003 end to end (API, PostgreSQL and S3 storage always in Docker): server A takes a world's lease;
# server B for the same world is refused (exit 4); a player builds on A and leaves; A shuts down when idle, flushing,
# uploading snapshots and releasing the lease; B then starts on another machine (empty disk) with a higher fencing
# number, restores the world from object storage, and a new player sees exactly A's chunks.
# Usage: tools/smoke-lease.sh native|docker
set -euo pipefail

MODE="${1:-native}"
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOGS="$ROOT/artifacts/smoke-lease"
NET="voxelrush-lease"
API="http://127.0.0.1:18082"
pids=()
rm -rf "$LOGS" && mkdir -p "$LOGS/world-a" "$LOGS/world-b"
cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  docker rm -f -v voxelrush-lease-api voxelrush-lease-db voxelrush-lease-s3 voxelrush-lease-7777 voxelrush-lease-7778 >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT
fail() { echo "LEASE SMOKE FAIL: $*" >&2; exit 1; }

TICKET_KEY=$(openssl rand -base64 32); JWT_KEY=$(openssl rand -base64 32); SERVICE_TOKEN=$(openssl rand -hex 24); DB_PASSWORD=$(openssl rand -hex 16)
S3_KEY=$(openssl rand -hex 8); S3_SECRET=$(openssl rand -hex 20)
printf '{"identities":[{"name":"api","credentials":[{"accessKey":"%s","secretKey":"%s"}],"actions":["Admin","Read","Write","List"]}]}' "$S3_KEY" "$S3_SECRET" >"$LOGS/s3.json"
chmod 644 "$LOGS/s3.json"
docker network create "$NET" >/dev/null
docker run -d --name voxelrush-lease-db --network "$NET" -e POSTGRES_PASSWORD="$DB_PASSWORD" -e POSTGRES_DB=voxelrush postgres:17.6-alpine >/dev/null
# Presigned URLs carry the storage host, so the API and the game servers must reach S3 at the same address: native
# servers use 127.0.0.1 (the API shares the S3 container's network namespace), containers use its name.
if [[ "$MODE" == "docker" ]]; then
  S3_URL="http://voxelrush-lease-s3:18333"; S3_PORTS=(); API_NET=(--network "$NET" -p 18082:8080); SERVER_API="http://voxelrush-lease-api:8080"
else
  S3_URL="http://127.0.0.1:18333"; S3_PORTS=(-p 18333:18333 -p 18082:8080); API_NET=(--network container:voxelrush-lease-s3); SERVER_API="$API"
fi
docker run -d --name voxelrush-lease-s3 --network "$NET" "${S3_PORTS[@]+"${S3_PORTS[@]}"}" --tmpfs /data:size=512m -v "$LOGS/s3.json:/etc/seaweedfs/s3.json:ro" chrislusf/seaweedfs:3.97 \
  server -s3 -s3.port=18333 -s3.config=/etc/seaweedfs/s3.json -volume.port=18080 -dir=/data -master.volumeSizeLimitMB=64 -volume.minFreeSpace=50MiB >/dev/null
for _ in $(seq 1 60); do docker exec voxelrush-lease-db pg_isready -U postgres -q 2>/dev/null && break; sleep 1; done
docker build -q -f "$ROOT/docker/api/Dockerfile" -t voxelrush-api:smoke "$ROOT/build/api" >/dev/null
for _ in $(seq 1 60); do docker exec voxelrush-lease-s3 sh -c 'wget -S --spider http://127.0.0.1:18333/ 2>&1 | grep -q "HTTP/"' 2>/dev/null && break; sleep 1; done
docker run -d --name voxelrush-lease-api "${API_NET[@]}" --read-only --tmpfs /tmp --cap-drop ALL \
  -e "VoxelRush__Snapshots__ServiceUrl=$S3_URL" -e "VoxelRush__Snapshots__AccessKey=$S3_KEY" -e "VoxelRush__Snapshots__SecretKey=$S3_SECRET" -e "VoxelRush__Snapshots__Region=us-east-1" \
  -e "VoxelRush__ConnectionString=Host=voxelrush-lease-db;Username=postgres;Password=$DB_PASSWORD;Database=voxelrush" \
  -e "VoxelRush__JwtSigningKey=$JWT_KEY" -e "VoxelRush__TicketKey=$TICKET_KEY" -e "VoxelRush__ServiceToken=$SERVICE_TOKEN" voxelrush-api:smoke >/dev/null
for _ in $(seq 1 90); do curl -fsS "$API/healthz" >/dev/null 2>&1 && break; sleep 1; done
curl -fsS "$API/healthz" >/dev/null || { docker logs voxelrush-lease-api 2>&1 | tail -20; fail "API did not start"; }

json() { python3 -I -c "import json,sys; print(json.load(sys.stdin)$1)"; }
owner=$(curl -fsS -X POST "$API/v1/auth/guest" | json "['accessToken']")
WORLD=$(curl -fsS -X POST "$API/v1/worlds" -H "Authorization: Bearer $owner" -H "Content-Type: application/json" -d '{"name":"Leased Plot"}' | json "['id']")
ticket() { curl -fsS -X POST "$API/v1/worlds/$WORLD/join" -H "Authorization: Bearer $owner" | json "['ticket']"; }
[[ "$MODE" == "docker" ]] && docker build -q -f "$ROOT/docker/server/Dockerfile" -t voxelrush-server:smoke "$ROOT/build/server" >/dev/null
[[ "$MODE" == "docker" ]] && chmod 777 "$LOGS/world-a" "$LOGS/world-b"
server() { # <log> <port> <world dir> <args...>
  local log="$1" port="$2" dir="$3"; shift 3
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --name "voxelrush-lease-$port" --network "$NET" -v "$dir:/data" -e "VOXELRUSH_TICKET_KEY=$TICKET_KEY" \
      -e "VOXELRUSH_API_URL=$SERVER_API" -e "VOXELRUSH_SERVICE_TOKEN=$SERVICE_TOKEN" voxelrush-server:smoke \
      -port "$port" -worldDir /data -worldId "$WORLD" "$@" >"$log" 2>&1 &
    SERVER_PID=$!; pids+=("$SERVER_PID")
    return
  fi
  VOXELRUSH_TICKET_KEY="$TICKET_KEY" VOXELRUSH_API_URL="$API" VOXELRUSH_SERVICE_TOKEN="$SERVICE_TOKEN" perl -e 'alarm shift; exec @ARGV' 120 \
    "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - -port "$port" -worldDir "$dir" -worldId "$WORLD" "$@" >"$log" 2>&1 &
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

server "$LOGS/a.log" 7777 "$LOGS/world-a" -idleShutdownSeconds 6; A=$SERVER_PID
for _ in $(seq 1 120); do grep -q "event=server_listening" "$LOGS/a.log" 2>/dev/null && break; sleep 0.5; done
grep -q "event=server_lease_acquired .*fencing=1" "$LOGS/a.log" || fail "A did not take the lease"

server "$LOGS/b-refused.log" 7778 "$LOGS/world-b" -smokeTestTicks 300; B=$SERVER_PID
code=0; wait "$B" || code=$?
[[ $code -eq 4 ]] || fail "second server for the same world exited $code (expected 4)"
grep -q "event=server_world_leased result=HeldByOther" "$LOGS/b-refused.log" || fail "B did not report the held lease"

bot "$LOGS/builder.log" 7777 -joinTicket "$(ticket)" -botBuild 12 -smokeTestSeconds 12 || fail "builder failed"
code=0; wait "$A" || code=$?
[[ $code -eq 0 ]] || fail "A exited $code after going idle"
grep -q "event=server_idle_shutdown" "$LOGS/a.log" || fail "A did not shut down for idleness"
grep -q "event=server_lease_released fencing=1 status=204" "$LOGS/a.log" || fail "A did not release its lease"
grep -qE "event=world_snapshots_published written=[1-9][0-9]* uploaded=[1-9][0-9]* failed=0" "$LOGS/a.log" || fail "A did not upload its snapshots"
hashes "$LOGS/a.log" world_chunk_hash >"$LOGS/a.hashes"
[[ -s "$LOGS/a.hashes" ]] || fail "A saved no edits"

# B runs on an empty disk: everything comes back from object storage.
rm -rf "$LOGS/world-b" && mkdir -p "$LOGS/world-b" && { [[ "$MODE" != "docker" ]] || chmod 777 "$LOGS/world-b"; }
server "$LOGS/b.log" 7778 "$LOGS/world-b" -smokeTestTicks 450; B=$SERVER_PID
for _ in $(seq 1 120); do grep -q "event=server_listening" "$LOGS/b.log" 2>/dev/null && break; sleep 0.5; done
grep -q "event=server_lease_acquired .*fencing=2" "$LOGS/b.log" || fail "B did not take over with fencing 2"
grep -qE "event=world_snapshots_restored imported=[1-9]" "$LOGS/b.log" || fail "B restored nothing from storage"
bot "$LOGS/joiner.log" 7778 -joinTicket "$(ticket)" -smokeTestSeconds 6 || fail "joiner failed"
wait "$B" || fail "B exited non-zero"
hashes "$LOGS/joiner.log" world_replica_hash >"$LOGS/joiner.hashes"
diff -u "$LOGS/a.hashes" "$LOGS/joiner.hashes" || fail "the saved world changed across the handover"
echo "LEASE SMOKE PASS ($MODE): second server refused, idle shutdown uploaded and released, cold restore on an empty disk matched ($(wc -l <"$LOGS/a.hashes" | tr -d ' ') chunks)"
