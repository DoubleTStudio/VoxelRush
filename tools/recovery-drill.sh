#!/usr/bin/env bash
# SAVE-009 recovery drill (API, PostgreSQL and S3 storage in Docker): two play sessions save a world; the database is
# then lost and rebuilt from a pg_dump; the owner restores the world to the first save; a server starting on the old
# (now stale) disk sees the new epoch, rebuilds from storage, and a new player sees exactly the first session's chunks.
# Usage: tools/recovery-drill.sh native|docker
set -euo pipefail

MODE="${1:-native}"
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOGS="$ROOT/artifacts/recovery-drill"
NET="voxelrush-drill"
API="http://127.0.0.1:18083"
pids=()
rm -rf "$LOGS" && mkdir -p "$LOGS/world"
cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  docker logs voxelrush-drill-s3 >"$LOGS/s3.log" 2>&1 || true
  docker rm -f -v voxelrush-drill-api voxelrush-drill-db voxelrush-drill-s3 voxelrush-drill-7777 >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT
fail() { echo "RECOVERY DRILL FAIL: $*" >&2; exit 1; }

TICKET_KEY=$(openssl rand -base64 32); JWT_KEY=$(openssl rand -base64 32); SERVICE_TOKEN=$(openssl rand -hex 24); DB_PASSWORD=$(openssl rand -hex 16)
S3_KEY=$(openssl rand -hex 8); S3_SECRET=$(openssl rand -hex 20)
printf '{"identities":[{"name":"api","credentials":[{"accessKey":"%s","secretKey":"%s"}],"actions":["Admin","Read","Write","List"]}]}' "$S3_KEY" "$S3_SECRET" >"$LOGS/s3.json"
chmod 644 "$LOGS/s3.json"
docker network create "$NET" >/dev/null
# Same addressing as smoke-lease.sh: presigned URLs must work for the API and the game server alike.
if [[ "$MODE" == "docker" ]]; then
  S3_URL="http://voxelrush-drill-s3:18334"; S3_PORTS=(); API_NET=(--network "$NET" -p 18083:8080); SERVER_API="http://voxelrush-drill-api:8080"
else
  S3_URL="http://127.0.0.1:18334"; S3_PORTS=(-p 18334:18334 -p 18083:8080); API_NET=(--network container:voxelrush-drill-s3); SERVER_API="$API"
fi
docker run -d --name voxelrush-drill-s3 --network "$NET" "${S3_PORTS[@]+"${S3_PORTS[@]}"}" --tmpfs /data:size=512m -v "$LOGS/s3.json:/etc/seaweedfs/s3.json:ro" chrislusf/seaweedfs:3.97 \
  server -s3 -s3.port=18334 -s3.config=/etc/seaweedfs/s3.json -volume.port=18081 -dir=/data -master.volumeSizeLimitMB=64 -volume.minFreeSpace=50MiB >/dev/null
start_db() {
  docker run -d --name voxelrush-drill-db --network "$NET" -e POSTGRES_PASSWORD="$DB_PASSWORD" -e POSTGRES_DB=voxelrush postgres:17.6-alpine >/dev/null
  for _ in $(seq 1 60); do docker exec voxelrush-drill-db pg_isready -U postgres -q 2>/dev/null && break; sleep 1; done
  # pg_isready passes during the init restart; wait for the real server.
  for _ in $(seq 1 30); do docker exec voxelrush-drill-db psql -U postgres -d voxelrush -qtAc 'SELECT 1' >/dev/null 2>&1 && break; sleep 1; done
}
start_db
docker build -q -f "$ROOT/docker/api/Dockerfile" -t voxelrush-api:smoke "$ROOT/build/api" >/dev/null
for _ in $(seq 1 60); do docker exec voxelrush-drill-s3 sh -c 'wget -S --spider http://127.0.0.1:18334/ 2>&1 | grep -q "HTTP/"' 2>/dev/null && break; sleep 1; done
start_api() {
  docker rm -f voxelrush-drill-api >/dev/null 2>&1 || true
  docker run -d --name voxelrush-drill-api "${API_NET[@]}" --read-only --tmpfs /tmp --cap-drop ALL \
    -e "VoxelRush__Snapshots__ServiceUrl=$S3_URL" -e "VoxelRush__Snapshots__AccessKey=$S3_KEY" -e "VoxelRush__Snapshots__SecretKey=$S3_SECRET" -e "VoxelRush__Snapshots__Region=us-east-1" \
    -e "VoxelRush__ConnectionString=Host=voxelrush-drill-db;Username=postgres;Password=$DB_PASSWORD;Database=voxelrush" \
    -e "VoxelRush__JwtSigningKey=$JWT_KEY" -e "VoxelRush__TicketKey=$TICKET_KEY" -e "VoxelRush__ServiceToken=$SERVICE_TOKEN" voxelrush-api:smoke >/dev/null
  for _ in $(seq 1 90); do curl -fsS "$API/healthz" >/dev/null 2>&1 && return; sleep 1; done
  docker logs voxelrush-drill-api 2>&1 | tail -20; fail "API did not start"
}
start_api

json() { python3 -I -c "import json,sys; d=json.load(sys.stdin); print(eval('d'+sys.argv[1]))" "$1"; }
owner=$(curl -fsS -X POST "$API/v1/auth/guest" | json "['accessToken']")
WORLD=$(curl -fsS -X POST "$API/v1/worlds" -H "Authorization: Bearer $owner" -H "Content-Type: application/json" -d '{"name":"Drill Plot"}' | json "['id']")
ticket() { curl -fsS -X POST "$API/v1/worlds/$WORLD/join" -H "Authorization: Bearer $owner" | json "['ticket']"; }
[[ "$MODE" == "docker" ]] && docker build -q -f "$ROOT/docker/server/Dockerfile" -t voxelrush-server:smoke "$ROOT/build/server" >/dev/null && chmod 777 "$LOGS/world"
server() { # <log> <args...>: always the same disk
  local log="$1"; shift
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --name voxelrush-drill-7777 --network "$NET" -v "$LOGS/world:/data" -e "VOXELRUSH_TICKET_KEY=$TICKET_KEY" \
      -e "VOXELRUSH_API_URL=$SERVER_API" -e "VOXELRUSH_SERVICE_TOKEN=$SERVICE_TOKEN" voxelrush-server:smoke \
      -port 7777 -worldDir /data -worldId "$WORLD" "$@" >"$log" 2>&1 &
  else
    VOXELRUSH_TICKET_KEY="$TICKET_KEY" VOXELRUSH_API_URL="$API" VOXELRUSH_SERVICE_TOKEN="$SERVICE_TOKEN" perl -e 'alarm shift; exec @ARGV' 120 \
      "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - -port 7777 -worldDir "$LOGS/world" -worldId "$WORLD" "$@" >"$log" 2>&1 &
  fi
  SERVER_PID=$!; pids+=("$SERVER_PID")
  for _ in $(seq 1 120); do grep -q "event=server_listening" "$log" 2>/dev/null && return; kill -0 "$SERVER_PID" 2>/dev/null || break; sleep 0.5; done
  tail -5 "$log"; fail "server did not start ($log)"
}
bot() { local log="$1"; shift
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --network "$NET" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" "$BOT_IMAGE" \
      timeout --kill-after=10 60 /bot/VoxelRushBot.x86_64 -batchmode -nographics -logFile - -connect voxelrush-drill-7777:7777 "$@" >"$log" 2>&1
    return
  fi
  perl -e 'alarm shift; exec @ARGV' 60 "$ROOT/build/bot-mac/VoxelRush" -batchmode -nographics -logFile - -connect 127.0.0.1:7777 "$@" >"$log" 2>&1; }
hashes() { grep "event=$2 " "$1" | sed -nE 's/.*chunk=([^ ]+) revision=([0-9]+) hash=([0-9A-F]+).*/\1 \2 \3/p' | sort; }
session() { # <name> <edits>: build, leave, idle shutdown with upload
  server "$LOGS/$1.log" -idleShutdownSeconds 6
  bot "$LOGS/$1-builder.log" -joinTicket "$(ticket)" -botBuild "$2" -smokeTestSeconds 12 || fail "$1 builder failed"
  wait "$SERVER_PID" || fail "$1 server exited non-zero"
  [[ "$MODE" == "docker" ]] || docker logs voxelrush-drill-api >"$LOGS/api-$1.log" 2>&1
  grep -qE "event=world_snapshots_published written=[0-9]+ uploaded=[1-9][0-9]* failed=0" "$LOGS/$1.log" || fail "$1 did not upload"
  hashes "$LOGS/$1.log" world_chunk_hash >"$LOGS/$1.hashes"
}

session first 12
sleep 12   # keep the two saves apart (restore points group commits less than 10 s apart)
session second 7
cmp -s "$LOGS/first.hashes" "$LOGS/second.hashes" && fail "the second session changed nothing"

# Disaster: the database is gone. Rebuild it from last night's dump; object storage survived.
docker exec voxelrush-drill-db pg_dump -U postgres -Fc voxelrush >"$LOGS/voxelrush.dump"
docker rm -f -v voxelrush-drill-db >/dev/null
start_db
docker exec -i voxelrush-drill-db pg_restore -U postgres -d voxelrush --no-owner <"$LOGS/voxelrush.dump"
start_api

points=$(curl -fsS "$API/v1/worlds/$WORLD/backups" -H "Authorization: Bearer $owner")
echo "$points" >"$LOGS/backups.json"
[[ $(echo "$points" | json ".__len__()") -eq 2 ]] || fail "expected two restore points: $points"
AS_OF=$(echo "$points" | json "[1]['savedAt']")
restore=$(curl -fsS -X POST "$API/v1/worlds/$WORLD/restore" -H "Authorization: Bearer $owner" -H "Content-Type: application/json" -d "{\"asOf\":\"$AS_OF\"}")
[[ $(echo "$restore" | json "['epoch']") -eq 1 ]] || fail "restore did not start epoch 1: $restore"

# The server's disk still holds the second session; the lease tells it that timeline is gone.
server "$LOGS/restored.log" -smokeTestTicks 450
grep -q "event=world_local_reset epoch=1" "$LOGS/restored.log" || fail "server kept the stale disk"
grep -qE "event=world_snapshots_restored imported=[1-9]" "$LOGS/restored.log" || fail "server restored nothing from storage"
bot "$LOGS/joiner.log" -joinTicket "$(ticket)" -smokeTestSeconds 6 || fail "joiner failed"
wait "$SERVER_PID" || fail "restored server exited non-zero"
hashes "$LOGS/joiner.log" world_replica_hash >"$LOGS/joiner.hashes"
diff -u "$LOGS/first.hashes" "$LOGS/joiner.hashes" || fail "restored world differs from the first save"

audit=$(curl -fsS "$API/v1/worlds/$WORLD/restores" -H "Authorization: Bearer $owner")
[[ $(echo "$audit" | json "[0]['epoch']") -eq 1 ]] || fail "restore not audited: $audit"
stored=$(curl -fsS "$API/internal/v1/metrics/storage" -H "X-Service-Token: $SERVICE_TOKEN" | json "['storedBytes']")
[[ "$stored" -gt 0 ]] || fail "storage metrics empty"
echo "RECOVERY DRILL PASS ($MODE): database rebuilt from dump, world restored to the first of two saves on a stale disk ($(wc -l <"$LOGS/first.hashes" | tr -d ' ') chunks, ${stored} bytes stored)"
