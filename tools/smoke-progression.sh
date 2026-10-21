#!/usr/bin/env bash
# SAVE-006 end to end: two API players race on a ticket-only server; the server grants XP for the validated results
# through the API (idempotent keys); a second device linked to the winner sees the same progression; re-sending a
# grant changes nothing.
# Usage: tools/smoke-progression.sh native|docker   (API and PostgreSQL always run in Docker)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
LOGS="$ROOT/artifacts/smoke-progression"
NET="voxelrush-prog"
API="http://127.0.0.1:18081"
PORT=7777
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
pids=()
rm -rf "$LOGS" && mkdir -p "$LOGS/world" && chmod 777 "$LOGS/world"
cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  docker rm -f -v voxelrush-prog-api voxelrush-prog-db voxelrush-prog-server >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT
fail() { echo "PROGRESSION SMOKE FAIL: $*" >&2; exit 1; }

TICKET_KEY=$(openssl rand -base64 32); JWT_KEY=$(openssl rand -base64 32); SERVICE_TOKEN=$(openssl rand -hex 24); DB_PASSWORD=$(openssl rand -hex 16)
docker network create "$NET" >/dev/null
docker run -d --name voxelrush-prog-db --network "$NET" -e POSTGRES_PASSWORD="$DB_PASSWORD" -e POSTGRES_DB=voxelrush postgres:17.6-alpine >/dev/null
for _ in $(seq 1 60); do docker exec voxelrush-prog-db pg_isready -U postgres -q 2>/dev/null && break; sleep 1; done
docker build -q -f "$ROOT/docker/api/Dockerfile" -t voxelrush-api:smoke "$ROOT/build/api" >/dev/null
docker run -d --name voxelrush-prog-api --network "$NET" -p 18081:8080 --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges:true \
  -e "VoxelRush__ConnectionString=Host=voxelrush-prog-db;Username=postgres;Password=$DB_PASSWORD;Database=voxelrush" \
  -e "VoxelRush__JwtSigningKey=$JWT_KEY" -e "VoxelRush__TicketKey=$TICKET_KEY" -e "VoxelRush__ServiceToken=$SERVICE_TOKEN" voxelrush-api:smoke >/dev/null
for _ in $(seq 1 60); do curl -fsS "$API/healthz" >/dev/null 2>&1 && break; sleep 1; done
curl -fsS "$API/healthz" >/dev/null || fail "API never became healthy"

json() { python3 -I -c "import json,sys; print(json.load(sys.stdin)$1)"; }
a=$(curl -fsS -X POST "$API/v1/auth/guest"); a_token=$(json "['accessToken']" <<<"$a"); a_id=$(json "['playerId']" <<<"$a")
b=$(curl -fsS -X POST "$API/v1/auth/guest"); b_token=$(json "['accessToken']" <<<"$b")
WORLD_ID=$(curl -fsS -X POST "$API/v1/worlds" -H "Authorization: Bearer $a_token" -H "Content-Type: application/json" -d '{"name":"Race Day","visibility":"public"}' | json "['id']")
ticket() { curl -fsS -X POST "$API/v1/worlds/$WORLD_ID/join" -H "Authorization: Bearer $1" | json "['ticket']"; }

if [[ "$MODE" == "docker" ]]; then
  docker build -q -f "$ROOT/docker/server/Dockerfile" -t voxelrush-server:smoke "$ROOT/build/server" >/dev/null
  docker run --rm --init --name voxelrush-prog-server --network "$NET" -v "$LOGS/world:/data" -e "VOXELRUSH_TICKET_KEY=$TICKET_KEY" \
    -e "VOXELRUSH_API_URL=http://voxelrush-prog-api:8080" -e "VOXELRUSH_SERVICE_TOKEN=$SERVICE_TOKEN" voxelrush-server:smoke \
    -port "$PORT" -worldDir /data -worldId "$WORLD_ID" -raceAutoStart 2 -smokeTestTicks 1500 >"$LOGS/server.log" 2>&1 &
  ENDPOINT="voxelrush-prog-server:$PORT"
else
  VOXELRUSH_TICKET_KEY="$TICKET_KEY" VOXELRUSH_API_URL="$API" VOXELRUSH_SERVICE_TOKEN="$SERVICE_TOKEN" perl -e 'alarm shift; exec @ARGV' 120 \
    "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - -port "$PORT" -worldDir "$LOGS/world" -worldId "$WORLD_ID" -raceAutoStart 2 -smokeTestTicks 1500 >"$LOGS/server.log" 2>&1 &
  ENDPOINT="127.0.0.1:$PORT"
fi
SERVER_PID=$!; pids+=("$SERVER_PID")
for _ in $(seq 1 120); do grep -q "event=server_listening" "$LOGS/server.log" 2>/dev/null && break; sleep 0.5; done
grep -q "event=server_rewards enabled=true" "$LOGS/server.log" || fail "server rewards not enabled"

bot() { local log="$1"; shift
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --network "$NET" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" "$BOT_IMAGE" \
      /bot/VoxelRushBot.x86_64 -batchmode -nographics -logFile - -connect "$ENDPOINT" "$@" >"$log" 2>&1 &
  else
    perl -e 'alarm shift; exec @ARGV' 90 "$ROOT/build/bot-mac/VoxelRush" -batchmode -nographics -logFile - -connect "$ENDPOINT" "$@" >"$log" 2>&1 &
  fi
  pids+=($!)
}
bot "$LOGS/a.log" -joinTicket "$(ticket "$a_token")" -botRace -smokeTestSeconds 40; A=$!
sleep 0.5
bot "$LOGS/b.log" -joinTicket "$(ticket "$b_token")" -botRace -smokeTestSeconds 40; B=$!
wait "$A" || fail "racer A failed"; wait "$B" || fail "racer B failed"
wait "$SERVER_PID" || fail "server exited non-zero"
grep -q "event=race_rewards_queued .*grants=2" "$LOGS/server.log" || fail "server did not queue 2 rewards"
[[ $(grep -c "event=reward_grant_sent .*status=200" "$LOGS/server.log") -eq 2 ]] || fail "rewards not confirmed by the API"

xp() { curl -fsS "$API/v1/players/me/progress" -H "Authorization: Bearer $1" | json "['xp']"; }
xa=$(xp "$a_token"); xb=$(xp "$b_token")
[[ "$xa" -gt 0 && "$xb" -gt 0 ]] || fail "XP not granted (A=$xa B=$xb)"
# Winner 100 + 25 per racer beaten, runner-up 100.
[[ $((xa + xb)) -eq 225 ]] || fail "expected 125 + 100 XP for a 2-player race, got $xa + $xb"

# Cross-device: the winner links a second device and sees the same progression.
code=$(curl -fsS -X POST "$API/v1/auth/link-code" -H "Authorization: Bearer $a_token" | json "['code']")
second=$(curl -fsS -X POST "$API/v1/auth/link" -H "Content-Type: application/json" -d "{\"code\":\"$code\"}")
[[ "$(json "['playerId']" <<<"$second")" == "$a_id" ]] || fail "linked device is a different player"
[[ "$(xp "$(json "['accessToken']" <<<"$second")")" == "$xa" ]] || fail "second device sees different XP"

# No double grant: re-send the exact grant the server sent.
key=$(sed -nE "s/.*event=reward_grant_sent grantKey=(race:[0-9a-f]+:${a_id//-/}) .*/\1/p" "$LOGS/server.log" | head -1)
[[ -n "$key" ]] || fail "could not find A's grant key"
dup=$(curl -fsS -X POST "$API/internal/v1/players/$a_id/grants" -H "X-Service-Token: $SERVICE_TOKEN" -H "Content-Type: application/json" \
  -d "{\"grantKey\":\"$key\",\"kind\":\"xp\",\"amount\":999,\"source\":\"race\"}" | json "['duplicate']")
[[ "$dup" == "True" && "$(xp "$a_token")" == "$xa" ]] || fail "re-sent grant was applied again"
echo "A=$xa XP, B=$xb XP; second device matches; replay ignored"
echo "PROGRESSION SMOKE PASS ($MODE): validated race rewards, cross-device continuity, no double grant"
