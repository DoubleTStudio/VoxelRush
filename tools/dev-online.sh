#!/usr/bin/env bash
# DEV-001 local online stack on a macOS development machine: PostgreSQL and S3-compatible snapshot storage (compose
# project voxelrush-dev), the API on 0.0.0.0, and the host agent with the process runtime, which starts a native
# build/server-mac server per world. Phones on the LAN or Tailscale sign in, create worlds and join them through the
# API URL printed by `up`.
# Usage: tools/dev-online.sh up|down|status|check   (check: a snapshot round-trips through the API and storage)
# Env: PUBLIC_HOST (default: Tailscale IPv4, else LAN IP), API_PORT (18090), AGENT_PORT (18091), DB_PORT (15434),
#      S3_PORT (18092), PORT_RANGE_START/PORT_RANGE_END (7800/7899, world servers' UDP ports),
#      SERVER_EXE (default build/server-mac/VoxelRush, built if missing), MAX_WORLDS (4), IDLE_SHUTDOWN_SECONDS (300),
#      DEV_PROJECT (voxelrush-dev; another name runs a second, separate stack from artifacts/dev-online-<name>).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="${DEV_PROJECT:-voxelrush-dev}"
[[ "$PROJECT" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || { echo "dev-online: DEV_PROJECT must be lowercase letters, digits, - or _" >&2; exit 2; }
# DEV-002 another project keeps its own secrets, pids, worlds and volumes, so it never touches the default stack.
if [[ "$PROJECT" == voxelrush-dev ]]; then DIR="$ROOT/artifacts/dev-online"; else DIR="$ROOT/artifacts/dev-online-$PROJECT"; fi
SECRETS="$DIR/secrets.env"
STACK="$DIR/stack.env"
WORLDS="$DIR/worlds"
BUCKET=voxelrush-snapshots
COMPOSE=(docker compose -p "$PROJECT" -f "$ROOT/docker/compose.backend.yaml" -f "$ROOT/docker/compose.dev.yaml")

log() { echo "dev-online: $*"; }
die() { echo "dev-online: $*" >&2; exit 1; }

# down/status read the last `up`'s settings, so they talk to the stack that is actually running.
load_stack() {
  if [[ "${1:-}" == saved && -f "$STACK" ]]; then
    # shellcheck disable=SC1090
    source "$STACK"
  fi
  API_PORT="${API_PORT:-18090}"
  AGENT_PORT="${AGENT_PORT:-18091}"
  DB_PORT="${DB_PORT:-15434}"
  S3_PORT="${S3_PORT:-18092}"
  PORT_RANGE_START="${PORT_RANGE_START:-7800}"
  PORT_RANGE_END="${PORT_RANGE_END:-7899}"
}

# <name> <value>: appended only when missing, so a secrets file from before DEV-002 gains the storage keys.
add_secret() { grep -q "^$1=" "$SECRETS" 2>/dev/null || (umask 077 && echo "$1=$2" >>"$SECRETS"); }

load_secrets() {
  mkdir -p "$DIR"
  # Generated once and kept, so the database volume's password stays valid across up/down.
  add_secret DB_PASSWORD "$(openssl rand -hex 16)"
  add_secret JWT_KEY "$(openssl rand -base64 32)"
  add_secret TICKET_KEY "$(openssl rand -base64 32)"
  add_secret SERVICE_TOKEN "$(openssl rand -hex 24)"
  add_secret S3_ACCESS_KEY "$(openssl rand -hex 10)"
  add_secret S3_SECRET_KEY "$(openssl rand -hex 20)"
  chmod 600 "$SECRETS"
  # shellcheck disable=SC1090
  source "$SECRETS"
}

# Compose interpolates the whole file, so every required variable is set even though only db and s3 start.
compose() {
  VOXELRUSH_DB_PASSWORD="$DB_PASSWORD" VOXELRUSH_JWT_KEY="$JWT_KEY" VOXELRUSH_TICKET_KEY="$TICKET_KEY" \
    VOXELRUSH_SERVICE_TOKEN="$SERVICE_TOKEN" VOXELRUSH_DEV_DB_PORT="$DB_PORT" VOXELRUSH_DEV_S3_PORT="$S3_PORT" \
    VOXELRUSH_DEV_S3_ACCESS_KEY="$S3_ACCESS_KEY" VOXELRUSH_DEV_S3_SECRET_KEY="$S3_SECRET_KEY" VOXELRUSH_DEV_NETWORK="$PROJECT" \
    "${COMPOSE[@]}" "$@"
}

detect_public_host() {
  local ip=""
  if command -v tailscale >/dev/null 2>&1; then
    ip=$(perl -e 'alarm 5; exec @ARGV' tailscale ip -4 2>/dev/null | head -n 1 || true)
  fi
  [[ -n "$ip" ]] || ip=$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || true)
  echo "${ip:-127.0.0.1}"
}

# <name> <command marker>: the pid file's process, only if it is still the one we started.
running_pid() {
  local file="$DIR/$1.pid" pid
  [[ -f "$file" ]] || return 1
  pid=$(cat "$file")
  ps -p "$pid" -o command= 2>/dev/null | grep -qF -- "$2" || return 1
  echo "$pid"
}

stop_pid() { # <name> <marker> <grace seconds>
  local pid
  if pid=$(running_pid "$1" "$2"); then
    kill -TERM "$pid" 2>/dev/null || true
    for _ in $(seq 1 "$3"); do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
    kill -0 "$pid" 2>/dev/null && { log "$1 did not stop in $3 s, killing"; kill -KILL "$pid" 2>/dev/null || true; }
    log "stopped $1 (pid $pid)"
  fi
  rm -f "$DIR/$1.pid"
}

# World servers are identified by our world directory on their command line, so nothing else is matched.
world_pids() { pgrep -f -- "$WORLDS/voxelrush-world-" || true; }

service_status() { # <compose service>
  docker ps --filter "label=com.docker.compose.project=$PROJECT" --filter "label=com.docker.compose.service=$1" \
    --format '{{.Names}} {{.Status}}' | head -n 1 | grep . || echo down
}

port_busy() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }

wait_http() { # <url> <seconds>
  for _ in $(seq 1 "$2"); do curl -fsS -o /dev/null "$1" 2>/dev/null && return 0; sleep 1; done
  return 1
}

# An unsigned request is refused (403) once the S3 gateway and its filer are up.
wait_s3() { # <seconds>
  local code
  for _ in $(seq 1 "$1"); do
    code=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$S3_PORT/" 2>/dev/null || true)
    [[ "$code" == 403 || "$code" == 200 ]] && return 0
    sleep 1
  done
  return 1
}

cmd_up() {
  load_stack
  running_pid api VoxelRush.Api.dll >/dev/null && die "already up; run: $0 status"
  for port in "$API_PORT" "$AGENT_PORT" "$DB_PORT" "$S3_PORT"; do
    port_busy "$port" && die "TCP port $port is in use; set API_PORT / AGENT_PORT / DB_PORT / S3_PORT"
  done

  local public_host="${PUBLIC_HOST:-$(detect_public_host)}"
  local server_exe="${SERVER_EXE:-$ROOT/build/server-mac/VoxelRush}"
  if [[ ! -x "$server_exe" ]]; then
    [[ -z "${SERVER_EXE:-}" ]] || die "SERVER_EXE $server_exe is not an executable"
    log "building the native server (Unity, slow)"
    "$ROOT/tools/build.sh" server-mac
  fi

  load_secrets
  mkdir -p "$WORLDS"
  printf 'API_PORT=%s\nAGENT_PORT=%s\nDB_PORT=%s\nS3_PORT=%s\nPORT_RANGE_START=%s\nPORT_RANGE_END=%s\nPUBLIC_HOST=%s\n' \
    "$API_PORT" "$AGENT_PORT" "$DB_PORT" "$S3_PORT" "$PORT_RANGE_START" "$PORT_RANGE_END" "$public_host" >"$STACK"

  trap 'echo "dev-online: up failed; logs in $DIR" >&2; cmd_down >/dev/null 2>&1 || true' ERR

  log "starting PostgreSQL and S3 storage (compose project $PROJECT, 127.0.0.1:$DB_PORT and 127.0.0.1:$S3_PORT)"
  compose up -d db s3 >"$DIR/compose.log" 2>&1
  for _ in $(seq 1 60); do compose exec -T db pg_isready -h 127.0.0.1 -U postgres -q 2>/dev/null && break; sleep 1; done
  compose exec -T db pg_isready -h 127.0.0.1 -U postgres -q
  wait_s3 60 || { echo "dev-online: S3 storage did not come up on 127.0.0.1:$S3_PORT" >&2; false; }

  log "publishing API and host agent"
  dotnet publish "$ROOT/backend/VoxelRush.Api/VoxelRush.Api.csproj" -c Release -o "$DIR/bin/api" --nologo -v quiet >"$DIR/publish.log" 2>&1
  dotnet publish "$ROOT/backend/VoxelRush.HostAgent/VoxelRush.HostAgent.csproj" -c Release -o "$DIR/bin/host-agent" --nologo -v quiet >>"$DIR/publish.log" 2>&1

  # Keeps app events (world_server_*, host_agent_*) readable without per-request framework lines.
  export Logging__LogLevel__Microsoft=Warning
  # Secrets go through exported variables in a subshell, never through command-line arguments.
  (
    export ASPNETCORE_URLS="http://0.0.0.0:$API_PORT"
    export VoxelRush__ConnectionString="Host=127.0.0.1;Port=$DB_PORT;Username=postgres;Password=$DB_PASSWORD;Database=voxelrush"
    export VoxelRush__JwtSigningKey="$JWT_KEY" VoxelRush__TicketKey="$TICKET_KEY" VoxelRush__ServiceToken="$SERVICE_TOKEN"
    export VoxelRush__HostAgentUrl="http://127.0.0.1:$AGENT_PORT/"
    # DEV-002 presigned URLs carry this host, which native world servers on this machine reach too. The API creates
    # the bucket at startup.
    export VoxelRush__Snapshots__ServiceUrl="http://127.0.0.1:$S3_PORT" VoxelRush__Snapshots__Bucket="$BUCKET"
    export VoxelRush__Snapshots__AccessKey="$S3_ACCESS_KEY" VoxelRush__Snapshots__SecretKey="$S3_SECRET_KEY"
    exec nohup dotnet "$DIR/bin/api/VoxelRush.Api.dll" >"$DIR/api.log" 2>&1
  ) &
  echo $! >"$DIR/api.pid"
  wait_http "http://127.0.0.1:$API_PORT/healthz" 60 || { tail -n 20 "$DIR/api.log" >&2; false; }
  log "API up on 0.0.0.0:$API_PORT"

  # Internal only: loopback, called by the API with the service token.
  (
    export ASPNETCORE_URLS="http://127.0.0.1:$AGENT_PORT"
    export HostAgent__Runtime=process HostAgent__ServerExecutable="$server_exe" HostAgent__WorldDataRoot="$WORLDS"
    export HostAgent__ServiceToken="$SERVICE_TOKEN" HostAgent__TicketKey="$TICKET_KEY" HostAgent__ApiUrl="http://127.0.0.1:$API_PORT"
    export HostAgent__PublicHost="$public_host" HostAgent__MaxWorlds="${MAX_WORLDS:-4}" HostAgent__IdleShutdownSeconds="${IDLE_SHUTDOWN_SECONDS:-300}"
    export HostAgent__PortRangeStart="$PORT_RANGE_START" HostAgent__PortRangeEnd="$PORT_RANGE_END"
    exec nohup dotnet "$DIR/bin/host-agent/VoxelRush.HostAgent.dll" >"$DIR/host-agent.log" 2>&1
  ) &
  echo $! >"$DIR/host-agent.pid"
  wait_http "http://127.0.0.1:$AGENT_PORT/healthz" 60 || { tail -n 20 "$DIR/host-agent.log" >&2; false; }
  log "host agent up on 127.0.0.1:$AGENT_PORT (server $server_exe, UDP $PORT_RANGE_START-$PORT_RANGE_END)"
  trap - ERR

  echo
  echo "API URL for the phone's join screen: http://$public_host:$API_PORT"
  local again="$0"
  [[ "$PROJECT" == voxelrush-dev ]] || again="DEV_PROJECT=$PROJECT $0"
  echo "Snapshots: bucket $BUCKET on http://127.0.0.1:$S3_PORT (round-trip check: $again check)"
  echo "Logs: $DIR/{api,host-agent}.log, world servers: $WORLDS/voxelrush-world-*.log"
}

cmd_down() {
  load_stack saved
  # The agent stops its world servers (SIGTERM: journal flushed, snapshots uploaded, lease released) before it exits.
  stop_pid host-agent VoxelRush.HostAgent.dll 30
  # Servers left by an agent that died; stopped while the API is still up, so they can release their leases.
  local pids
  pids=$(world_pids)
  if [[ -n "$pids" ]]; then
    log "stopping leftover world servers: $(echo "$pids" | tr '\n' ' ')"
    # shellcheck disable=SC2086
    kill -TERM $pids 2>/dev/null || true
    for _ in $(seq 1 15); do [[ -z "$(world_pids)" ]] && break; sleep 1; done
    pids=$(world_pids)
    # shellcheck disable=SC2086
    [[ -z "$pids" ]] || kill -KILL $pids 2>/dev/null || true
  fi
  stop_pid api VoxelRush.Api.dll 15
  if [[ -f "$SECRETS" ]]; then
    load_secrets
    # Removes only this project's containers and network; the database and storage volumes are kept.
    compose down >>"$DIR/compose.log" 2>&1 && log "stopped PostgreSQL and S3 storage (project $PROJECT)"
  fi
}

cmd_status() {
  load_stack saved
  local pid
  if pid=$(running_pid api VoxelRush.Api.dll); then
    echo "api         pid $pid  $(curl -fsS "http://127.0.0.1:$API_PORT/healthz" 2>/dev/null || echo unhealthy)"
  else
    echo "api         down"
  fi
  if pid=$(running_pid host-agent VoxelRush.HostAgent.dll); then
    echo "host-agent  pid $pid  $(curl -fsS "http://127.0.0.1:$AGENT_PORT/healthz" 2>/dev/null || echo unhealthy)"
  else
    echo "host-agent  down"
  fi
  echo "postgres    $(service_status db)"
  echo "s3          $(service_status s3)"
  local worlds
  worlds=$(world_pids)
  echo "worlds      $(echo "$worlds" | grep -c . || true) running"
  for pid in $worlds; do
    echo "  pid $pid  $(ps -p "$pid" -o command= | sed -nE 's/.*-port ([0-9]+).*-worldId ([0-9a-f-]+).*/port \1 world \2/p')"
  done
  [[ -n "${PUBLIC_HOST:-}" ]] && running_pid api VoxelRush.Api.dll >/dev/null && echo "API URL     http://$PUBLIC_HOST:$API_PORT"
  return 0
}

json() { python3 -I -c "import json,sys; print(json.load(sys.stdin)$1)"; }

# DEV-002 what a world server does at shutdown and start, without one: lease, upload URL, PUT, commit, latest, GET.
# Tokens and presigned URLs reach curl through a config on a pipe, never its command line.
cmd_check() {
  load_stack saved
  running_pid api VoxelRush.Api.dll >/dev/null || die "not up; run: $0 up"
  load_secrets
  api="http://127.0.0.1:$API_PORT" holder="dev-online-check-$$" fencing=0 token=""
  local world sha size url latest
  tmp=$(mktemp -d "$DIR/check.XXXXXX")
  trap 'rm -rf "$tmp"' EXIT

  token=$(curl -fsS -X POST "$api/v1/auth/guest" | json "['accessToken']") || check_fail "guest sign-in"
  world=$(player POST /v1/worlds '{"name":"Storage check"}' | json "['id']") || check_fail "world creation"
  fencing=$(svc POST "/internal/v1/worlds/$world/lease/acquire" "{\"holder\":\"$holder\",\"ttlSeconds\":60}" | json "['fencing']") || check_fail "lease"

  head -c 2048 /dev/urandom >"$tmp/up.snap"
  sha=$(shasum -a 256 "$tmp/up.snap" | cut -d ' ' -f 1)
  size=$(wc -c <"$tmp/up.snap" | tr -d ' ')
  url=$(svc POST "/internal/v1/worlds/$world/snapshots/upload" "{\"x\":0,\"y\":0,\"z\":0,\"revision\":1,\"sha256\":\"$sha\",\"sizeBytes\":$size}" | json "['url']") \
    || check_fail "upload URL (501 means the API runs without storage: down, then up)"
  presigned "$url" -X PUT --data-binary "@$tmp/up.snap" -o /dev/null || check_fail "PUT to the presigned URL"
  svc POST "/internal/v1/worlds/$world/snapshots/commit" '{"x":0,"y":0,"z":0,"revision":1}' >/dev/null || check_fail "commit"

  latest=$(svc GET "/internal/v1/worlds/$world/snapshots/latest?x=0&y=0&z=0") || check_fail "latest"
  [[ "$(json "['sha256']" <<<"$latest")" == "$sha" && "$(json "['revision']" <<<"$latest")" == 1 ]] || check_fail "index points at another object"
  presigned "$(json "['url']" <<<"$latest")" -o "$tmp/down.snap" || check_fail "GET from the presigned URL"
  [[ "$(shasum -a 256 "$tmp/down.snap" | cut -d ' ' -f 1)" == "$sha" ]] || check_fail "downloaded bytes differ"

  svc POST "/internal/v1/worlds/$world/lease/release" "{\"holder\":\"$holder\",\"fencing\":$fencing}" >/dev/null || check_fail "lease release"
  player DELETE "/v1/worlds/$world" >/dev/null || check_fail "world deletion"
  log "storage check passed: $size-byte snapshot uploaded, committed, indexed and downloaded intact (bucket $BUCKET, 127.0.0.1:$S3_PORT)"
}

check_fail() { die "storage check failed at: $*; see $DIR/api.log"; }

svc() { # <method> <path> [json body], as a game server holding the lease
  curl -fsS -X "$1" -K <(printf 'header = "X-Service-Token: %s"\nheader = "X-Lease-Holder: %s"\nheader = "X-Lease-Fencing: %s"\n' \
    "$SERVICE_TOKEN" "$holder" "$fencing") -H 'Content-Type: application/json' ${3:+-d "$3"} "$api$2"
}

player() { # <method> <path> [json body]
  curl -fsS -X "$1" -K <(printf 'header = "Authorization: Bearer %s"\n' "$token") -H 'Content-Type: application/json' ${3:+-d "$3"} "$api$2"
}

presigned() { curl -fsS -K <(printf 'url = "%s"\n' "$1") "${@:2}"; } # <url> [curl args]

case "${1:-}" in
  up) cmd_up ;;
  down) cmd_down ;;
  status) cmd_status ;;
  check) cmd_check ;;
  *) echo "Usage: $0 up|down|status|check" >&2; exit 2 ;;
esac
