# Backend and game server deployment (ALLOC-001)

Implements DEPLOYMENT (staging: single always-on Linux VM, journal on a durable volume) and SAVE_GAME_CLOUD_PERSISTENCE ("on join, acquire per-world lease, allocate instance, restore snapshot+journal").

## Components
| Component | Runs on | Image | Reaches |
|---|---|---|---|
| Metadata API (`backend/VoxelRush.Api`) | backend host, public HTTPS | `docker/api` | PostgreSQL, object storage, host agent |
| Host agent (`backend/VoxelRush.HostAgent`) | game VM, **internal only** | `docker/host-agent` | Docker socket of the game VM |
| Game server, one container per world | game VM, public UDP | `docker/server` | API (`/internal/v1/...`) |

## Join flow
`POST /v1/worlds/{id}/join` (player token):
1. The API checks the player may see the world (strangers get `404` and never start a server).
2. It asks the allocator for the world's server:
   - **No host agent configured** (`VoxelRush:HostAgentUrl` empty, the default): every world uses `VoxelRush:GameServerEndpoint`. This is the single-server development setup.
   - **Host agent configured**: the API calls `POST {HostAgentUrl}/internal/v1/hosts/{worldId}/ensure` with `X-Service-Token`.
3. Responses to the client:

| Status | Body | Client action |
|---|---|---|
| `200` | `{ticket, endpoint, expiresAt, role}` (unchanged) | connect to `endpoint` with `ticket` within 60 s |
| `202` + `Retry-After` | `{"status":"starting","retryAfterSeconds":N}` | call join again after N seconds (no ticket yet) |
| `503` + `Retry-After` | problem `no_capacity` (host full) or `host_unavailable` (agent down, launch failed) | show "servers busy", retry later |

Joins count against the per-player rate limit (`PlayerRequestsPerMinute`, 60), so retry no faster than `retryAfterSeconds`.

## Host agent
For each ensure call (serialized on the host, idempotent):
- Container `voxelrush-world-{worldId:N}` running and its log has `event=server_listening` → `ready` with `{PublicHost}:{port}`.
- Running but not listening yet → `starting`; still not listening after `StartTimeoutSeconds` → container removed, `unavailable start_failed` (the next join relaunches).
- Not running → if `MaxWorlds` containers are active, `unavailable no_capacity`; otherwise the lowest free UDP port in `PortRangeStart..PortRangeEnd` is published to the server's 7777 and the server starts with
  `-port 7777 -maxPlayers {MaxPlayers} -worldDir /data -worldId {id} -idleShutdownSeconds {IdleShutdownSeconds}` → `starting`.
- A name conflict (another agent process started the same world) is reported as `starting`; a port taken by another process is skipped.

Each server takes the world lease from the API, restores from object storage, and exits after `IdleShutdownSeconds` with no players (`--rm` removes the container, freeing its slot and port). World state stays in the per-world named volume `voxelrush-world-{worldId:N}` mounted on `/data`.

Secrets reach game servers as environment variables (`VOXELRUSH_TICKET_KEY`, `VOXELRUSH_API_URL`, `VOXELRUSH_SERVICE_TOKEN`); the agent passes them by name to `docker run`, so they appear in neither its command line nor its logs. They are visible to anyone who can run `docker inspect` on the VM, like any container environment.

### Configuration (`HostAgent__*` environment variables)
| Setting | Default | Notes |
|---|---|---|
| `ServiceToken` | required | Same value as the API's `VoxelRush__ServiceToken` (≥ 32 chars) |
| `TicketKey` | required | Same value as the API's `VoxelRush__TicketKey` (base64, ≥ 32 bytes) |
| `ApiUrl` | required | API URL as game servers reach it, e.g. `http://api:8080` |
| `ServerImage` | required | e.g. `ghcr.io/doubletstudio/voxelrush-server:staging` (must contain ALLOC-001's `/data` mount point) |
| `PublicHost` | required | Address clients connect to (VM public IP or DNS name) |
| `Network` | empty | Docker network for game servers (compose: `voxelrush-backend`) |
| `PortRangeStart` / `PortRangeEnd` | 7800 / 7899 | Open these UDP ports in the firewall; nothing else |
| `MaxWorlds` | 4 | Size to the VM: see `docs/perf` for per-server CPU/memory |
| `MaxPlayers` | 8 | 1..8 |
| `IdleShutdownSeconds` | 300 | 5..86400 |
| `StartTimeoutSeconds` | 90 | |
| `RetryAfterSeconds` | 2 | Hint for clients while a server boots |
| `VolumePrefix` | `voxelrush-world-` | |

API side: `VoxelRush__HostAgentUrl` (e.g. `http://host-agent:8080/`) and optionally `VoxelRush__HostAgentTimeoutSeconds` (10).

### Running it (single VM, compose)
```bash
tools/build-api.sh && tools/build-host-agent.sh
export VOXELRUSH_DB_PASSWORD=... VOXELRUSH_JWT_KEY=... VOXELRUSH_TICKET_KEY=... VOXELRUSH_SERVICE_TOKEN=...   # openssl rand -base64 32
export VOXELRUSH_HOST_AGENT_URL=http://host-agent:8080/
export VOXELRUSH_SERVER_IMAGE=ghcr.io/doubletstudio/voxelrush-server:staging VOXELRUSH_PUBLIC_HOST=<vm public address>
export DOCKER_GID=$(stat -c %g /var/run/docker.sock)    # 0 on Docker Desktop
docker compose -f docker/compose.backend.yaml --profile hosting up --build -d
```
Without `--profile hosting` and `VOXELRUSH_HOST_AGENT_URL`, the stack behaves as before (one server at `GameServerEndpoint`).

### Security
- The Docker socket is root-equivalent on the VM. Only the host agent mounts it; the API never does.
- The agent has no published port in compose. If it runs on a different VM than the API, expose it only on a private network or behind a firewall rule allowing the API host; the service token is the only application-level check.
- The agent runs as a non-root user with a read-only filesystem; socket access comes from `group_add`.
- Game server containers run read-only, with all capabilities dropped and `no-new-privileges`.

### Operations
- Health: `GET /healthz` on the agent (`503 docker_unavailable` when the socket is unreachable).
- Logs: `world_server_launched`, `world_host_full`, `world_port_in_use`, `world_server_start_timeout`, `world_server_launch_failed`, `world_ensure_failed`; API: `host_agent_unreachable`, `host_agent_bad_response`.
- Alert on `world_host_full` (capacity) and repeated `world_server_start_timeout` for a world (crash loop, or the world's lease held by a server elsewhere).
- Running worlds: `docker ps --filter label=voxelrush.world`.
- Stop a world: `docker stop voxelrush-world-<id>` (SIGTERM flushes the journal and releases the lease).
- World volumes outlive their servers. They are disposable caches (BACKUP_RESTORE: rebuilt from object storage), so remove volumes of worlds idle for a long time when disk runs low: `docker volume rm voxelrush-world-<id>` while the world is stopped.
- Upgrading the server image: change `ServerImage` and restart the agent. Running worlds keep the old image until they idle out; stop them to switch immediately.
