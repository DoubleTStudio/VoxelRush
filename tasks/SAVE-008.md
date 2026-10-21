# Task: SAVE-008 — Local settings and disposable chunk cache with version checks

## Goal
Device-local data stays non-authoritative and disposable: preferences survive restarts, and rejoining a world doesn't re-download chunks the device already has. Acceptance (BACKLOG): stale cache invalidation.

## Prerequisites / relevant docs
VR-033 (world sync, replica hash checks), VR-071 (quality tiers). SAVE_GAME_CLOUD_PERSISTENCE ("Local device: settings, content-addressed chunk cache and last-known view state; no authoritative writes while offline"; device-local data is control/graphics settings and disposable chunk cache).

## In scope / out of scope
In:
- `Core/Settings/LocalSettings`: versioned key=value file with controls sensitivity, button scale, quality override (null = adaptive) and volumes. Writes are atomic. A bad value falls back per key; corrupt or newer-format files give defaults and stay untouched.
- `Presentation/Settings/DeviceSettings` loads from `persistentDataPath` (defaults in batch mode) and applies to touch scaling, steering and quality. Precedence: command line > saved choice > device classification; a saved choice turns adaptation off.
- `World/Sync/ChunkCache`: one directory per world version (world id, generator kind/seed/version, chunk schema, cache format). Opening a world deletes its directories for other versions. Entries are encoded snapshot messages, and unreadable ones are deleted. LRU eviction of other worlds stays within a 32 MiB budget.
- `CachedChunkWire` codec (strict length, ≤ 2000 entries). Protocol: client → `vr/chunk-cache-manifest` (always sent, possibly empty); the server holds a joiner's initial stream for up to 1 s for it, then sends `vr/chunk-current` batches (only full snapshots count against the per-tick budget) for chunks whose revision and content hash match. The client loads those from the cache and `ClientWorldReplica` re-verifies the hash; a miss or mismatch drops the entry and requests a snapshot. Players cache under `persistentDataPath/chunk-cache`; headless bots only with `-chunkCache <dir>`. The cache is saved when leaving, on disconnect and on app pause.

Out: a settings UI, syncing allowed settings with the cloud (`/v1/players/me/settings` exists from SAVE-006), last-known view state, offline play (no authoritative writes offline by design).

## Tests
- Domain `LocalSettingsTests`: round trip without leftover temp file; defaults with adaptive quality; per-key fallback for out-of-range/garbage/unknown values; newer-format and corrupt files give defaults and are left untouched.
- Domain `ChunkCacheTests`: manifest lists stored versions and a confirmed load passes the replica's hash check. **Stale entries are dropped when the server has a newer revision**, and the same revision with different content (after a SAVE-009 restore) is a miss. **A generator/schema change invalidates the world's old cache** while other worlds are untouched. Corrupt entries are deleted; a tampered body with a forged header fails the replica hash check and requests a snapshot. The budget evicts LRU other worlds, never the current one. Wire round trip and malformed lengths are covered.
- **End to end** `tools/smoke-cache.sh native` (Docker mode in CI): J1 joins cold (0 hits) and saves 72 chunks; after more edits J2 rejoins — **71/72 chunks confirmed from the cache, 1 re-sent, 0 misses**; J3 with a tampered entry gets a hash mismatch and falls back to the server; J2/J3 hashes equal the server's; the server logs `cacheConfirmed`.

## Verification commands and actual results (2026-10-21, macOS arm64)
| Command | Result |
|---|---|
| `tools/test.sh domain` | Passed 464/464 |
| `tools/test.sh editmode` / `playmode` | Passed 536/536 / 26/26 |
| `tools/smoke-cache.sh native` | **CACHE SMOKE PASS** (71/72 confirmed, 1 re-sent, tamper fallback) |
| `tools/smoke-world.sh native`, `smoke-auth.sh native`, `smoke-lease.sh native` | PASS (protocol change regression) |
| shellcheck, actionlint | clean |

Found while testing: messages over the MTU (a 72-entry manifest is 2 KB) need `ReliableFragmentedSequenced`; both cache messages use it.

## Definition of done
- [x] Stale cache invalidation (revision/hash mismatch, generator/schema/format version change, corrupt and tampered entries).

## PR summary / risks / rollback
- Risks: a joiner's first snapshots wait up to 1 s for its manifest (only when it never sends one); the cache trusts nothing without the server's (revision, hash) and a local hash check.
- Rollback: revert; old clients without a manifest still work (server falls back after 1 s), and deleting the cache directory is always safe.
