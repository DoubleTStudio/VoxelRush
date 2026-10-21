#!/usr/bin/env bash
# DEV-001 local online stack on a macOS development machine: PostgreSQL (compose project voxelrush-dev), the API on
# 0.0.0.0, and the host agent with the process runtime, which starts a native build/server-mac server per world.
# Phones on the LAN or Tailscale sign in, create worlds and join them through the API URL printed by `up`.
# Usage: tools/dev-online.sh up|down|status
# Env: PUBLIC_HOST (default: Tailscale IPv4, else LAN IP), API_PORT (18090), AGENT_PORT (18091), DB_PORT (15434),
#      SERVER_EXE (default build/server-mac/VoxelRush, built if missing), MAX_WORLDS (4), IDLE_SHUTDOWN_SECONDS (300).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIR="$ROOT/artifacts/dev-online"
SECRETS="$DIR/secrets.env"
STACK="$DIR/stack.env"
WORLDS="$DIR/worlds"
PROJECT=voxelrush-dev
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
}

load_secrets() {
  mkdir -p "$DIR"
  if [[ ! -f "$SECRETS" ]]; then
    # Generated once and kept, so the database volume's password stays valid across up/down.
    (umask 077 && {
      echo "DB_PASSWORD=$(openssl rand -hex 16)"
      echo "JWT_KEY=$(openssl rand -base64 32)"
      echo "TICKET_KEY=$(openssl rand -base64 32)"
      echo "SERVICE_TOKEN=$(openssl rand -hex 24)"
    } >"$SECRETS")
  fi
  chmod 600 "$SECRETS"
  # shellcheck disable=SC1090
  source "$SECRETS"
}

# Compose interpolates the whole file, so every required variable is set even though only db starts.
compose() {
  VOXELRUSH_DB_PASSWORD="$DB_PASSWORD" VOXELRUSH_JWT_KEY="$JWT_KEY" VOXELRUSH_TICKET_KEY="$TICKET_KEY" \
    VOXELRUSH_SERVICE_TOKEN="$SERVICE_TOKEN" VOXELRUSH_DEV_DB_PORT="$DB_PORT" "${COMPOSE[@]}" "$@"
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

port_busy() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }

wait_http() { # <url> <seconds>
  for _ in $(seq 1 "$2"); do curl -fsS -o /dev/null "$1" 2>/dev/null && return 0; sleep 1; done
  return 1
}

cmd_up() {
  load_stack
  running_pid api VoxelRush.Api.dll >/dev/null && die "already up; run: $0 status"
  for port in "$API_PORT" "$AGENT_PORT" "$DB_PORT"; do
    port_busy "$port" && die "TCP port $port is in use; set API_PORT / AGENT_PORT / DB_PORT"
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
  printf 'API_PORT=%s\nAGENT_PORT=%s\nDB_PORT=%s\nPUBLIC_HOST=%s\n' "$API_PORT" "$AGENT_PORT" "$DB_PORT" "$public_host" >"$STACK"

  trap 'echo "dev-online: up failed; logs in $DIR" >&2; cmd_down >/dev/null 2>&1 || true' ERR

  log "starting PostgreSQL (compose project $PROJECT, 127.0.0.1:$DB_PORT)"
  compose up -d db >"$DIR/compose.log" 2>&1
  for _ in $(seq 1 60); do compose exec -T db pg_isready -h 127.0.0.1 -U postgres -q 2>/dev/null && break; sleep 1; done
  compose exec -T db pg_isready -h 127.0.0.1 -U postgres -q

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
    exec nohup dotnet "$DIR/bin/host-agent/VoxelRush.HostAgent.dll" >"$DIR/host-agent.log" 2>&1
  ) &
  echo $! >"$DIR/host-agent.pid"
  wait_http "http://127.0.0.1:$AGENT_PORT/healthz" 60 || { tail -n 20 "$DIR/host-agent.log" >&2; false; }
  log "host agent up on 127.0.0.1:$AGENT_PORT (server $server_exe, UDP 7800-7899)"
  trap - ERR

  echo
  echo "API URL for the phone's join screen: http://$public_host:$API_PORT"
  echo "Logs: $DIR/{api,host-agent}.log, world servers: $WORLDS/voxelrush-world-*.log"
}

cmd_down() {
  load_stack saved
  # The agent stops its world servers (SIGTERM: journal flushed, lease released) before it exits.
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
    # Removes only this project's container and network; the database volume is kept.
    compose down >>"$DIR/compose.log" 2>&1 && log "stopped PostgreSQL (project $PROJECT)"
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
  echo "postgres    $(docker ps --filter "label=com.docker.compose.project=$PROJECT" --format '{{.Names}} {{.Status}}' | head -n 1 | grep . || echo down)"
  local worlds
  worlds=$(world_pids)
  echo "worlds      $(echo "$worlds" | grep -c . || true) running"
  for pid in $worlds; do
    echo "  pid $pid  $(ps -p "$pid" -o command= | sed -nE 's/.*-port ([0-9]+).*-worldId ([0-9a-f-]+).*/port \1 world \2/p')"
  done
  [[ -n "${PUBLIC_HOST:-}" ]] && running_pid api VoxelRush.Api.dll >/dev/null && echo "API URL     http://$PUBLIC_HOST:$API_PORT"
  return 0
}

case "${1:-}" in
  up) cmd_up ;;
  down) cmd_down ;;
  status) cmd_status ;;
  *) echo "Usage: $0 up|down|status" >&2; exit 2 ;;
esac
