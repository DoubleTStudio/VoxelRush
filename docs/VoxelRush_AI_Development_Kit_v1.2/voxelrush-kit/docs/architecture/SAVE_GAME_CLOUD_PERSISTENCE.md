# SAVE-001: Save Games, Cloud Sync and Persistent Worlds (v1.2)
Status: Approved implementation baseline. Owners: backend, networking, voxel engine.

## Product contract
- A player can resume progression, cosmetics, owned vehicles, currency, inventory and achievements across Android/iOS after linking an account.
- A player has at least one free persistent world subject to documented quotas. Additional slots/hosting are optional and must not gate core racing/building.
- Player worlds can be private, friends-only or public; owner controls invitations and edit permissions.
- Saved worlds retain generated seed/version, constructed blocks, removed/damaged blocks, custom tracks and access controls when nobody is online.
- Race-session destruction is ephemeral; do not merge it into persistent world state. Competitive results are stored only after server validation.
- Device-local data contains control/graphics settings and disposable chunk cache, not authoritative purchases/currency/world edits.
- Guest login is supported; account linking is required for reliable cross-device recovery. Explain loss risk before guest logout or device reset.

## Authority and consistency
- Dedicated game server is authoritative for block edits, health, vehicle state, checkpoint results and inventory consumed in building.
- Backend is authoritative for identity, currency, entitlements, unlocks and ownership. Never accept client-submitted balances.
- Use operation_id (UUID), actor_id, world_id, server_tick, chunk_coord, base_revision, new_revision, schema_version and generator_version.
- All mutations must be idempotent; stale chunk revisions trigger snapshot/delta recovery.
- ACK durable edits only after append-only journal commit; periodically compact to compressed snapshots in object storage.
- For cross-chunk explosions use atomic commit manifest or a recoverable transaction protocol; no half-committed persistent event.
- Protect world snapshot/journal with checksums, retention and access controls. Support tested recovery from earlier snapshot plus journal replay.

## Storage split
PostgreSQL: accounts, world metadata, memberships/ACL, player progression, inventory, cosmetics/entitlements, track metadata, chunk snapshot indexes, journal pointers and operation ledger.
Object storage (R2/S3): compressed binary chunk snapshots and backup exports.
Durable journal: ordered block mutations and structural collapses; use transactional PostgreSQL outbox or dedicated durable log initially, then benchmark throughput.
Local device: settings, content-addressed chunk cache and last-known view state; no authoritative writes while offline.

## Autosave behavior
- Purchases/currency: transactional, immediate, verified server-side.
- Confirmed building/destruction: durable journal before success response.
- Progression: transactional on reward completion.
- Vehicle customizations: commit on user confirmation.
- Snapshots: background compaction at ~5–10 minutes or mutation threshold; never depend on snapshots alone for durability.
- Disconnect: persist noncritical session metadata; do not rely on disconnect as sole save trigger.
- Shutdown: drain, flush, close journal; if interrupted, replay committed operations.

## World lifecycle and cost control
World metadata exists independent of any running Unity process. When last player leaves, wait a short configurable idle period, commit pending work, snapshot opportunistically, then stop server. On join, acquire per-world lease, allocate instance, restore snapshot+journal, validate revisions and spawn only after collider-critical chunks are ready. Avoid two authoritative servers writing the same world: lease/fencing token required. Race instances can be short-lived; sandbox world persistence is separate.

## API sketch (versioned, authenticated)
POST /v1/worlds (name, seed, visibility) -> worldId
GET /v1/worlds/mine -> owned/joinable worlds and lastSavedAt
POST /v1/worlds/{id}/join -> short-lived signed server ticket / allocation status
PATCH /v1/worlds/{id} -> name, visibility, ACL (owner only)
POST /v1/worlds/{id}/invites -> invitation with expiration
POST /v1/worlds/{id}/restore -> privileged snapshot restore with audit trail
GET /v1/players/me/progress -> server-authoritative progression
PUT /v1/players/me/settings -> noncompetitive preferences with version/ETag
Do not expose object-store write credentials or untrusted snapshot upload paths to game clients.

## Data models
World: id, ownerId, seed, generatorVersion, schemaVersion, visibility, status, createdAt, lastSavedAt, lastPlayedAt, revision, activeLeaseToken.
WorldMember: worldId, playerId, role, invitedBy, expiry.
ChunkSnapshot: worldId, chunkCoord, revision, storageKey, checksum, sizeBytes, createdAt.
WorldMutation: operationId, worldId, chunkCoord, baseRevision, newRevision, tick, actorId, payload, committedAt.
PlayerProgress: playerId, xp, level, version, updatedAt.
PlayerEntitlement: playerId, sku, sourceTransactionId, status, updatedAt.
WorldQuota: ownerId, maxWorlds, maxStoredBytes, idleShutdownSeconds.

## Failure handling and QA acceptance
- Kill server after ACK: committed changes survive restart.
- Kill server before ACK: operation either committed once or safely retried; never duplicated.
- Two clients edit same block: serialized result, one chunk revision chain.
- Disconnect/reconnect and join from another device: progression and world changes match authoritative server.
- Two concurrent world start requests: only one server holds lease.
- Delete/restore world: permissions, retention, restore audit and backups tested.
- Guest account link merges only allowed non-entitlement local settings; never duplicates purchases.
- Race-scoped block edits never contaminate saved sandbox.
- Offline cached world cannot upload authoritative edits without a dedicated future conflict-resolution design.

## Metrics
Journal commit latency p95/p99, save error rate, replay duration, snapshot bytes per world, storage cost/world, world boot time, active server minutes, lease conflicts, chunk desync count, restoration success.
