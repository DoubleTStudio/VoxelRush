# ADR-019: Per-world game servers started by a host agent

Status: Accepted (ALLOC-001). Implements SAVE_GAME_CLOUD_PERSISTENCE ("on join, acquire per-world lease, allocate instance"; join returns "ticket / allocation status") and ADR-009 (persistent sandbox on a long-lived instance). Builds on ADR-018 (join tickets) and SAVE-005 (leases, idle shutdown).

## Context
Join returned one configured endpoint, so a deployment served one world. Game servers already take the world lease, restore from storage and exit when idle, so all that was missing was something to start one per world on demand.

## Decision
- The API asks an `IWorldHostAllocator` for the world's endpoint after the ACL check. The default `StaticAllocator` keeps the old single endpoint; `HostAgentAllocator` is used when `VoxelRush:HostAgentUrl` is set.
- Join answers `200` with a ticket when the server is listening, `202 {"status":"starting","retryAfterSeconds"}` (no ticket) while it boots, and `503 no_capacity|host_unavailable` otherwise.
- A small host agent on the game VM (`backend/VoxelRush.HostAgent`) owns the Docker socket and starts `voxelrush-world-{id}` containers. It picks a port from a range, enforces a world cap, and reports ready once the server logs `server_listening`. It is internal-only and authenticated with the service token.
- Container labels are the agent's state (world, port, start time). It keeps no database and recovers by listing containers.

## Alternatives
- API talks to Docker directly: puts a root-equivalent socket on the internet-facing service.
- Managed allocation (Edgegap, Agones, cloud fleets): DEPLOYMENT reserves it for ephemeral races, after validating availability and pricing. The allocator interface leaves room for it.
- Docker Engine HTTP API instead of the CLI: fewer moving parts in the image, but more code to get right. The CLI sits behind an interface, so swapping later is local.
- Return a ticket together with "starting": the 60 s ticket could expire before a cold start finishes.

## Consequences / risks
- One agent per VM, one VM per API for now; multi-host placement needs a registry of agents.
- Containers carry secrets in their environment (visible via `docker inspect` on the VM).
- Ensure calls are serialized per agent: fine at the expected rate, a bottleneck only at hundreds of joins per second.

## Migration / rollback
No schema change. Rollback: unset `HostAgentUrl` (static endpoint again) and stop the agent.
