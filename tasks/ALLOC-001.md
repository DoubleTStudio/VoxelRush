# Task: ALLOC-001 — Start a game server per world on join

## Goal
`POST /v1/worlds/{id}/join` returned one fixed `GameServerEndpoint`, so a deployment could serve one world. SAVE_GAME_CLOUD_PERSISTENCE: "on join, acquire per-world lease, allocate instance, restore snapshot+journal"; join returns "signed server ticket / allocation status". ADR-009: sandbox worlds on a long-lived instance.

## Changes
- **API** (`backend/VoxelRush.Api/Hosting.cs`): `IWorldHostAllocator.EnsureAsync(worldId)` → `Ready(endpoint)` / `Starting(retryAfter)` / `Unavailable(reason)`.
  - `StaticAllocator` (default): always `GameServerEndpoint`, so existing behaviour and tests are unchanged.
  - `HostAgentAllocator`: chosen when `VoxelRush:HostAgentUrl` is set. Calls the host agent with `X-Service-Token`. Timeouts, refused connections, non-JSON or malformed answers become `host_unavailable`.
- **Join**: the ACL check runs first (strangers never start servers), then the allocator. `200` returns the ticket and endpoint (same `JoinResponse` shape). `202` + `Retry-After` returns `{"status":"starting","retryAfterSeconds":N}` with no ticket, so a ticket can't expire while the server boots. `503` + `Retry-After` returns problem `no_capacity` or `host_unavailable`.
- **Host agent** (`backend/VoxelRush.HostAgent`, new): `POST /internal/v1/hosts/{worldId}/ensure` (service token) and `GET /healthz`.
  - One container per world, named `voxelrush-world-{id:N}`. Labels carry world, port and start time, so state survives agent restarts.
  - The lowest free UDP port in the range maps to the server's 7777. Ports held by other processes are skipped. `MaxWorlds` cap returns `no_capacity`.
  - Ready once `docker logs` shows `event=server_listening` (cached per container). A boot that exceeds `StartTimeoutSeconds` is removed and reported as `start_failed`.
  - One gate serializes ensure calls, so the same-world race, the cap and the port choice are atomic. A container-name conflict from another agent process counts as `starting`.
  - Server args are the real `ServerLaunchOptions` names: `-port 7777 -maxPlayers -worldDir /data -worldId -idleShutdownSeconds`. Secrets are passed by name (`--env NAME`) and never appear on the command line or in logs.
  - Containers run `--rm --init --read-only --tmpfs /tmp --cap-drop ALL no-new-privileges`, with a per-world named volume on `/data`.
  - Docker sits behind `IContainerRuntime` (`DockerCli` in production, a fake in tests).
- **Shared contract**: `HostContract.cs` is compiled into both projects (like `JoinTicket.cs`).
- **Docker**: `docker/host-agent/Dockerfile` (aspnet base + static docker CLI, non-root, read-only).
  - `docker/server/Dockerfile` creates `/data` owned by the server user, so a fresh named volume is writable.
  - `compose.backend.yaml`: `host-agent` service under profile `hosting`. Only it mounts `/var/run/docker.sock`, with a comment explaining why. Network fixed as `voxelrush-backend`; API `HostAgentUrl` comes from `VOXELRUSH_HOST_AGENT_URL`.
- **Tooling/CI**: `tools/build-host-agent.sh`. The `api-tests` job also runs the host agent tests and publishes the agent. `docker/host-agent/` counts as backend-only in the changed-areas filter.
- **Docs**: `docs/adr/ADR-019-per-world-server-allocation.md` records the decision. `docs/operations/DEPLOYMENT.md` covers the join flow, agent behaviour, configuration, running it, security and operations.

## Not done
- Client handling of `202`/`503` (retry loop, "server starting" UI). The current client doesn't call join yet, and tooling uses the static allocator.
- Publishing a host agent image to GHCR (`tools/publish-images.sh`) and deploying to a staging VM.
- More than one game VM: the API takes a single `HostAgentUrl`; picking among agents (capacity, region) comes later.
- Reaping old per-world volumes (manual, documented).
- Crash-loop backoff per world: a server that exits right away (e.g. lease held elsewhere) is relaunched on the next join.

## Verification (2026-10-21, macOS arm64, Docker Desktop)
| Command | Result |
|---|---|
| `dotnet test backend/VoxelRush.HostAgent.Tests` | Passed 23/23 |
| `dotnet test backend/VoxelRush.Api.Tests --filter FullyQualifiedName~AllocationTests` | Passed 8/8 |
| `dotnet test backend/VoxelRush.Api.Tests` before the change (baseline) | Passed 104/104 |
| `dotnet test backend/VoxelRush.Api.Tests` with the change | see PR (local Docker stopped mid-run; CI runs it) |
| actionlint 1.7.7 | clean |
| `docker compose -f docker/compose.backend.yaml [--profile hosting] config` | valid |
| Host agent image + real Docker, with a stand-in server image that logs `server_listening` | 401 without token; 202 → 202 → 200 `127.0.0.1:7850`; second world on 7851; third world 503 `no_capacity`; after a server stops, the third world gets 7850; volume writable by uid 10001; server saw the right args and env; service token absent from agent log |
| `docker/server/Dockerfile` with a fresh named volume on `/data` | writable by uid 10001 |

## Risks / rollback
- The Docker socket is root-equivalent on the game VM. It is limited to the agent, which has no published port in compose, a non-root user and a read-only filesystem. Keep the agent off public networks.
- Container env (ticket key, service token) is visible to whoever can `docker inspect` on the VM.
- Compose now names its network `voxelrush-backend`; an existing stack gets a new network on the next `up`.
- Rollback: unset `VoxelRush__HostAgentUrl` (back to `GameServerEndpoint`) and stop the agent; or revert. No schema changes.
