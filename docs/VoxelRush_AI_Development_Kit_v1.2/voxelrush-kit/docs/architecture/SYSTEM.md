# System architecture
## Runtime
Android/iOS Unity client ↔ Unity Transport ↔ Linux Unity Dedicated Server (30 Hz authority); client ↔ HTTPS ↔ ASP.NET Core API for authentication/session/world metadata; API ↔ PostgreSQL; snapshots ↔ object storage. For first local vertical slice use in-memory room/session and file persistence.
## Ownership boundaries
Client: input, camera, HUD, prediction, interpolation, mesh presentation, cosmetic effects, cache.
Game server: authenticated sessions, tick, vehicle physics, race state, zone policies, voxel mutation, damage, structural support, chunk revision, interest management, persistence enqueue.
API: account/profile, room discovery, signed short-lived join tickets, cosmetics, world metadata, operations/admin.
Storage: durable mutation journal and snapshots; DB indexes revisions and permissions.
## Execution
30Hz fixed simulation, bounded queues for edits/collapse, snapshot publisher 10–20Hz, reliable ordered chunk deltas, background chunk generation and persistence. No synchronous DB/object-storage on tick.
## Core assemblies
VoxelRush.Core (IDs, math, contracts), VoxelRush.World (chunks/generator), VoxelRush.Gameplay (vehicles/races), VoxelRush.Destruction, VoxelRush.Networking, VoxelRush.Presentation, VoxelRush.Server, VoxelRush.Tests. Use asmdefs, avoid cycles.
## Environments
local: Unity editor + Docker Linux server; staging: one always-on VM; beta: managed server allocation for ephemeral race rooms, durable sandbox on VM or long-running host. Avoid coupling persistent world lifetime to ephemeral server lifetime.
## Observability
server tick p50/p95/p99, RTT, packets/sec, active chunks, dirty mesh backlog, pending writes, snapshots age, crash recovery replay duration, joins, errors, memory and CPU. Correlation IDs world/session/operation.
