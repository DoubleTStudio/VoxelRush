#!/usr/bin/env bash
# SAVE-006 end to end: two API players race on a ticket-only server; the server grants XP for the validated results
# through the API (idempotent keys); a second device linked to the winner sees the same progression; re-sending a
# grant changes nothing. COSM-001/002: A shows an owned paint and decal to B and honks (rate-limited by the server);
# B's unowned paint and flag are refused, and B can't honk without a horn.
# Usage: tools/smoke-progression.sh native|docker   (API and PostgreSQL always run in Docker)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
LOGS="$ROOT/artifacts/smoke-progression"
NET="voxelrush-prog"
API="http://127.0.0.1:18081"
# SMOKE_PORT: another local server may already hold 7777.
PORT="${SMOKE_PORT:-7777}"
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
# A owns the free level-1 paint; B owns nothing.
curl -fsS -X POST "$API/v1/players/me/entitlements/claim" -H "Authorization: Bearer $a_token" -H "Content-Type: application/json" -d '{"sku":"paint.racing-red"}' >/dev/null
# A is also granted the level-gated decal and horn (as a server grant, so no XP is needed before the race).
for sku in decal.lightning horn.retro; do
  curl -fsS -X POST "$API/internal/v1/players/$a_id/entitlements" -H "X-Service-Token: $SERVICE_TOKEN" -H "Content-Type: application/json" \
    -d "{\"sku\":\"$sku\",\"source\":\"grant\",\"transactionId\":\"smoke-$sku-$(openssl rand -hex 4)\"}" >/dev/null
done

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
bot "$LOGS/a.log" -joinTicket "$(ticket "$a_token")" -botRace -botEquip paint.racing-red,decal.lightning,horn.retro -botHorn -smokeTestSeconds 40; A=$!
sleep 0.5
bot "$LOGS/b.log" -joinTicket "$(ticket "$b_token")" -botRace -botEquip paint.chrome,flag.checkered-gold -botHorn -smokeTestSeconds 40; B=$!
wait "$A" || fail "racer A failed"; wait "$B" || fail "racer B failed"
wait "$SERVER_PID" || fail "server exited non-zero"
grep -q "event=race_rewards_queued .*grants=2" "$LOGS/server.log" || fail "server did not queue 2 rewards"
# Auto-start can run a second race inside the bots' window; the assertions are about the first one.
first_race=$(sed -nE 's/.*event=race_rewards_queued raceId=([0-9a-f-]+) .*/\1/p' "$LOGS/server.log" | head -1)
first_xp=$(grep "event=reward_grant_sent grantKey=race:${first_race//-/}:.*status=200" "$LOGS/server.log" | sed -nE 's/.* xp=([0-9]+) .*/\1/p' | sort -n | tr '\n' ' ')
[[ "$first_xp" == "100 125 " ]] || fail "first race rewards not confirmed by the API (xp: $first_xp)"
confirmed_xp=$(grep "event=reward_grant_sent .*status=200" "$LOGS/server.log" | sed -nE 's/.* xp=([0-9]+) .*/\1/p' | awk '{ s += $1 } END { print s + 0 }')
if grep "event=reward_grant_sent" "$LOGS/server.log" | grep -qv "status=200"; then fail "a reward grant was not confirmed"; fi

grep -q "event=server_cosmetics mode=Verified" "$LOGS/server.log" || fail "server not verifying cosmetic ownership"
grep -q "event=vehicle_cosmetic_applied .*kind=paint sku=paint.racing-red" "$LOGS/server.log" || fail "owned paint not applied"
grep -q "event=vehicle_cosmetic_applied .*kind=decal sku=decal.lightning" "$LOGS/server.log" || fail "owned decal not applied"
grep -q "event=vehicle_cosmetic_applied .*kind=horn sku=horn.retro" "$LOGS/server.log" || fail "owned horn not applied"
grep -q "event=vehicle_cosmetic_rejected .*kind=paint sku=paint.chrome reason=not_owned" "$LOGS/server.log" || fail "unowned paint not refused"
grep -q "event=vehicle_cosmetic_rejected .*kind=flag sku=flag.checkered-gold reason=not_owned" "$LOGS/server.log" || fail "unowned flag not refused"
grep -q "event=vehicle_cosmetic_seen .*own=false kind=paint sku=paint.racing-red" "$LOGS/b.log" || fail "B did not see A's paint"
grep -q "event=vehicle_cosmetic_seen .*own=false kind=decal sku=decal.lightning" "$LOGS/b.log" || fail "B did not see A's decal"
grep -q "event=vehicle_cosmetic_seen .*own=true kind=paint sku=paint.racing-red" "$LOGS/a.log" || fail "A did not see its own paint"
if grep -q "event=vehicle_cosmetic_seen .*sku=\(paint.chrome\|flag.checkered-gold\)" "$LOGS/a.log" "$LOGS/b.log"; then fail "an unowned cosmetic was shown"; fi
echo "cosmetics: A's owned paint and decal shown to both; B's unowned paint and flag refused"

# Both bots press the horn 5 times a second; the server relays A's at most once a second and never B's.
grep -q "event=vehicle_horn_rejected .*reason=rate_limited" "$LOGS/server.log" || fail "horn spam not rate-limited"
grep -q "event=vehicle_horn_rejected .*reason=no_horn" "$LOGS/server.log" || fail "horn without a horn not refused"
relayed=$(grep -c "event=vehicle_horn_relayed" "$LOGS/server.log" || true)
heard=$(grep -c "event=vehicle_horn_heard .*sku=horn.retro" "$LOGS/b.log" || true)
[[ "$relayed" -ge 5 && "$relayed" -le 42 ]] || fail "expected 5..42 relayed horn presses in ~40 s, got $relayed"
[[ "$heard" -ge 5 && "$heard" -le "$relayed" ]] || fail "B heard $heard of $relayed relayed horns"
if grep -q "event=vehicle_horn_heard" "$LOGS/a.log"; then fail "A heard a horn nobody owns"; fi
grep -q "event=server_horn_summary relayed=$relayed " "$LOGS/server.log" || fail "horn summary missing"
echo "horn: $relayed presses relayed (rate-limited), B heard $heard, B's presses refused"

xp() { curl -fsS "$API/v1/players/me/progress" -H "Authorization: Bearer $1" | json "['xp']"; }
xa=$(xp "$a_token"); xb=$(xp "$b_token")
[[ "$xa" -gt 0 && "$xb" -gt 0 ]] || fail "XP not granted (A=$xa B=$xb)"
# Winner 100 + 25 per racer beaten, runner-up 100 (checked above); the API holds exactly what was granted.
[[ $((xa + xb)) -eq $confirmed_xp ]] || fail "expected $confirmed_xp XP from confirmed grants, got $xa + $xb"

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
echo "PROGRESSION SMOKE PASS ($MODE): validated race rewards, cross-device continuity, no double grant, owned cosmetics only, rate-limited horn"
