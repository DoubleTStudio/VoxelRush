#!/usr/bin/env bash
# VR-080 end to end: PostgreSQL + API containers issue join tickets; a game server started with -worldId admits only
# valid tickets for that world. Checks: the owner joins and builds; a visitor joins but can't build; a replayed,
# forged or missing ticket is refused at the handshake.
# Usage: tools/smoke-auth.sh native|docker   (API and PostgreSQL always run in Docker)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
LOGS="$ROOT/artifacts/smoke-auth"
NET="voxelrush-auth"
API="http://127.0.0.1:18080"
PORT=7777
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
pids=()

rm -rf "$LOGS" && mkdir -p "$LOGS/world" && chmod 777 "$LOGS/world"
cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  docker rm -f voxelrush-auth-api voxelrush-auth-db voxelrush-auth-server >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT
fail() { echo "AUTH SMOKE FAIL: $*" >&2; exit 1; }

# Fresh secrets for this run only.
TICKET_KEY=$(openssl rand -base64 32)
JWT_KEY=$(openssl rand -base64 32)
SERVICE_TOKEN=$(openssl rand -hex 24)
DB_PASSWORD=$(openssl rand -hex 16)

docker network create "$NET" >/dev/null
docker run -d --name voxelrush-auth-db --network "$NET" -e POSTGRES_PASSWORD="$DB_PASSWORD" -e POSTGRES_DB=voxelrush postgres:17.6-alpine >/dev/null
for _ in $(seq 1 60); do docker exec voxelrush-auth-db pg_isready -U postgres -q 2>/dev/null && break; sleep 1; done
docker build -q -f "$ROOT/docker/api/Dockerfile" -t voxelrush-api:smoke "$ROOT/build/api" >/dev/null
docker run -d --name voxelrush-auth-api --network "$NET" -p 18080:8080 --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges:true \
  -e "VoxelRush__ConnectionString=Host=voxelrush-auth-db;Username=postgres;Password=$DB_PASSWORD;Database=voxelrush" \
  -e "VoxelRush__JwtSigningKey=$JWT_KEY" -e "VoxelRush__TicketKey=$TICKET_KEY" -e "VoxelRush__ServiceToken=$SERVICE_TOKEN" \
  -e "VoxelRush__GameServerEndpoint=127.0.0.1:$PORT" voxelrush-api:smoke >/dev/null
for _ in $(seq 1 60); do curl -fsS "$API/healthz" >/dev/null 2>&1 && break; sleep 1; done
curl -fsS "$API/healthz" >/dev/null || { docker logs voxelrush-auth-api >"$LOGS/api.log" 2>&1; fail "API never became healthy"; }

json() { python3 -I -c "import json,sys; print(json.load(sys.stdin)$1)"; }
owner=$(curl -fsS -X POST "$API/v1/auth/guest"); owner_token=$(json "['accessToken']" <<<"$owner")
guest=$(curl -fsS -X POST "$API/v1/auth/guest"); guest_token=$(json "['accessToken']" <<<"$guest")
world=$(curl -fsS -X POST "$API/v1/worlds" -H "Authorization: Bearer $owner_token" -H "Content-Type: application/json" -d '{"name":"Smoke Plot","visibility":"public"}')
WORLD_ID=$(json "['id']" <<<"$world")
ticket() { curl -fsS -X POST "$API/v1/worlds/$WORLD_ID/join" -H "Authorization: Bearer $1" | json "['ticket']"; }
OWNER_TICKET=$(ticket "$owner_token")
VISITOR_TICKET=$(ticket "$guest_token")
# One flipped character in the signature part.
FORGED=$(python3 -I -c "import sys; t=sys.argv[1]; i=len(t)-5; print(t[:i]+('A' if t[i]!='A' else 'B')+t[i+1:])" "$(ticket "$owner_token")")
echo "world $WORLD_ID; tickets issued"

if [[ "$MODE" == "docker" ]]; then
  docker build -q -f "$ROOT/docker/server/Dockerfile" -t voxelrush-server:smoke "$ROOT/build/server" >/dev/null
  docker run --rm --init --name voxelrush-auth-server --network "$NET" -v "$LOGS/world:/data" -e "VOXELRUSH_TICKET_KEY=$TICKET_KEY" voxelrush-server:smoke \
    -port "$PORT" -worldDir /data -worldId "$WORLD_ID" -smokeTestTicks 1200 >"$LOGS/server.log" 2>&1 &
  ENDPOINT="voxelrush-auth-server:$PORT"
else
  VOXELRUSH_TICKET_KEY="$TICKET_KEY" perl -e 'alarm shift; exec @ARGV' 120 "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - \
    -port "$PORT" -worldDir "$LOGS/world" -worldId "$WORLD_ID" -smokeTestTicks 1200 >"$LOGS/server.log" 2>&1 &
  ENDPOINT="127.0.0.1:$PORT"
fi
SERVER_PID=$!; pids+=("$SERVER_PID")
for _ in $(seq 1 120); do grep -q "event=server_listening" "$LOGS/server.log" 2>/dev/null && break; sleep 0.5; done
grep -q "event=server_tickets_required worldId=$WORLD_ID" "$LOGS/server.log" || fail "server did not enable ticket mode"

bot() { # <log> <args...>; the ticket key is never given to bots
  local log="$1"; shift
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --network "$NET" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" "$BOT_IMAGE" \
      /bot/VoxelRushBot.x86_64 -batchmode -nographics -logFile - -connect "$ENDPOINT" "$@" >"$log" 2>&1
  else
    perl -e 'alarm shift; exec @ARGV' 90 "$ROOT/build/bot-mac/VoxelRush" -batchmode -nographics -logFile - -connect "$ENDPOINT" "$@" >"$log" 2>&1
  fi
}

bot "$LOGS/owner.log" -joinTicket "$OWNER_TICKET" -botBuild 6 -smokeTestSeconds 15 & OWNER=$!
sleep 2
bot "$LOGS/visitor.log" -joinTicket "$VISITOR_TICKET" -botBuild 4 -smokeTestSeconds 12 & VISITOR=$!
sleep 1
code=0; bot "$LOGS/replay.log" -joinTicket "$OWNER_TICKET" -smokeTestSeconds 5 || code=$?; [[ $code -eq 3 ]] || fail "replayed ticket bot exited $code (expected 3)"
code=0; bot "$LOGS/forged.log" -joinTicket "$FORGED" -smokeTestSeconds 5 || code=$?; [[ $code -eq 3 ]] || fail "forged ticket bot exited $code (expected 3)"
code=0; bot "$LOGS/none.log" -smokeTestSeconds 5 || code=$?; [[ $code -eq 3 ]] || fail "ticketless bot exited $code (expected 3)"
code=0; wait "$OWNER" || code=$?; [[ $code -eq 0 ]] || fail "owner bot exited $code"
code=0; wait "$VISITOR" || code=$?; [[ $code -eq 0 ]] || fail "visitor bot exited $code"
code=0; wait "$SERVER_PID" || code=$?; [[ $code -eq 0 ]] || fail "server exited $code"

S="$LOGS/server.log"
for reason in ticket_replayed ticket_invalid ticket_required; do
  grep -q "event=client_rejected .*reason=$reason" "$S" || fail "server did not reject with $reason"
done
[[ $(grep -c "event=client_connected " "$S") -eq 2 ]] || fail "expected exactly the owner and the visitor to connect"
grep -q "event=bot_build_done edits=6 accepted=6" "$LOGS/owner.log" || fail "owner could not build"
grep -q "event=bot_build_done edits=4 accepted=0" "$LOGS/visitor.log" || fail "visitor was allowed to build"
grep -q "event=bot_edit_rejected reason=not_permitted" "$LOGS/visitor.log" || fail "visitor edits not refused as not_permitted"
docker logs voxelrush-auth-api >"$LOGS/api.log" 2>&1
if grep -qF "$TICKET_KEY" "$LOGS"/*.log; then fail "the ticket key appears in a log"; fi
echo "AUTH SMOKE PASS ($MODE): owner builds, visitor can't, replayed/forged/missing tickets refused"
