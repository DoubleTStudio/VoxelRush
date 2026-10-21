# Metadata database contract (SAVE-001)

PostgreSQL, forward-only migrations in `backend/VoxelRush.Api/Migrations` (applied in order under an advisory lock; recorded in `schema_migrations`). Never edit an applied migration; add a new one. Chunk contents are **not** stored here: blocks live in the game server's journal and in object-storage snapshots (PERSISTENCE_SECURITY, SAVE_GAME storage split).

| Table | Purpose | Key rules |
|---|---|---|
| `players` | Accounts (guest for now) | deleting a player cascades to tokens, memberships, progress, grants |
| `refresh_tokens` | Rotating refresh tokens | SHA-256 only; `family_id` revoked on reuse |
| `worlds` | World metadata | name 1–40 chars; visibility `private`/`friends`/`public`; generator and schema version pinned at creation; `revision` |
| `world_members` | ACL | role `visitor`/`editor`; optional expiry |
| `world_invites` | Owner-issued invitations | code stored as SHA-256; role `visitor`/`editor`; expiry ≤ 7 days; 1–50 uses; revocable |
| `chunk_snapshots` | Snapshot index | (world, chunk, revision) unique; `sha256` 32 bytes; `pending` → `committed` after verification |
| `player_progress` | XP/level | xp ≥ 0, level ≥ 1, `version` for optimistic concurrency |
| `player_settings` | Non-competitive preferences | JSON ≤ 8 KiB, `version` (ETag) |
| `reward_grants` | Idempotent reward ledger | unique `grant_key` (e.g. `race:<raceId>:<playerId>`): a grant can't apply twice |
| `device_link_codes` | Second-device sign-in (SAVE-006) | SHA-256 of a 10-character code, 10 min, single use |
| `player_entitlements` | Cosmetic ownership (ECON-001) | primary key (source, transaction): a receipt grants once; `revoked` on refund; sources `level`/`grant`/`purchase` |
| `world_leases` | One authoritative server per world (SAVE-005) | holder, fencing number (+1 per new holder), expiry, released flag; world-scoped server writes must present the live (holder, fencing) |
| `world_quotas` | Per-owner limits | max worlds, max stored bytes, idle shutdown seconds; absent row = configured defaults |

Identifiers: UUIDs for players, worlds, operations (journal records carry `operationId`), tickets. Chunk revisions are per chunk, ordered, assigned by the single authoritative writer.
