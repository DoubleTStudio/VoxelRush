# Task: SAVE-003 — Compressed snapshots, storage index, journal compaction, cold restore

## Goal
Worlds survive losing the game server's disk; snapshots are small and verified; the journal doesn't grow forever. Acceptance (BACKLOG): corrupted snapshot fallback, restore from storage.

## Prerequisites / relevant docs
VR-032 (journal + local snapshots), VR-080 (presigned snapshot upload/commit/latest index), SAVE-005 (lease; snapshot writes need the live lease). SAVE_GAME_CLOUD_PERSISTENCE (snapshot + journal, checksums, keep the previous good snapshot, restore validates), PERSISTENCE_SECURITY (no path traversal, bounded decode).

## In scope / out of scope
In: snapshot schema 2 (varint index gaps + block ids, deflated; schema 1 still readable); bounded inflate (≤ 3 bytes per declared edit, edit count ≤ 4096); two local generations (`.snap` + `.snap.prev`, a corrupt old file isn't kept as fallback); `CompactJournal` drops only records both generations cover, run at start-up before the journal opens; verified `ImportSnapshot` (checksum, decode, content hash, chunk, generator, newer only); `SnapshotSync` (restore newer remote snapshots at start, fail closed if storage is unreachable; on clean shutdown after a clean journal flush, snapshot changed chunks and upload anything not yet uploaded, tracked in `snapshots/uploaded.idx` so failures retry); `ApiSnapshotRemote` (upload → presigned PUT → commit with lease headers; latest → presigned GET checked against the indexed SHA-256); API answers 501 `storage_disabled` when no object storage is configured and the server then keeps worlds on local disk only.
Out: periodic snapshots while running (shutdown only for now), uploading the journal itself (a crash keeps un-snapshotted edits only on the local volume), old-object garbage collection and backups (SAVE-009).

## Tests
- Domain `SnapshotGenerationTests`: compression (2000 edits ≪ 3 bytes/edit), schema 1 still loads, **corrupt latest snapshot → previous generation + kept journal after compaction gives the identical chunk**, single generation is never compacted and compaction is idempotent and state-preserving, both generations corrupt → base + remaining journal with the gap reported (no silent rebuild), import rejects tampered/other-chunk/other-generator/not-newer bytes, hostile edit counts rejected.
- Domain `SnapshotSyncTests`: **shutdown then cold restore on another machine rebuilds the world**, upload index survives restarts and failed uploads retry, newer local state kept and corrupt remote objects rejected, restore fails closed when storage is unreachable.
- API: snapshot routes return 501 `storage_disabled` without storage (after auth).
- End to end `tools/smoke-lease.sh native` (Docker mode in CI): A uploads its snapshots at idle shutdown (`world_snapshots_published … failed=0`); B on an **empty disk** restores from SeaweedFS (`world_snapshots_restored imported≥1`) and a new player's chunk hashes equal A's.

## Verification commands and actual results (2026-10-21, macOS arm64)
| Command | Result |
|---|---|
| `tools/test.sh domain` | Passed 449/449 |
| `tools/test.sh editmode` | Passed 521/521 |
| `dotnet test backend/VoxelRush.Api.Tests` | Passed 80/80 |
| `tools/smoke-lease.sh native` | **LEASE SMOKE PASS** (cold restore on an empty disk matched) |
| `tools/smoke-world.sh native`, `smoke-auth.sh native`, `smoke-progression.sh native` | PASS |
| `tools/crash-test.sh` | PASS: 20 SIGKILLs, 264406 acknowledged edits, none lost |
| `tools/smoke-lease.sh docker` | Not runnable locally: the linux/amd64 server aborts in Mono's JIT under arm64 emulation; runs in CI (x86) |

## Definition of done
- [x] Corrupted snapshot fallback; restore from storage.

## PR summary / risks / rollback
- Risks: a crash (no clean shutdown) on a machine whose disk is then lost loses edits since the last clean shutdown; periodic snapshots/journal shipping would close that. A stale local journal from a previous holder could replay onto a newer restored snapshot if the same disk returns after another server ran the world; deployments should pin a world's volume or wipe it on reassignment.
- Rollback: revert; schema 2 snapshots would then be rejected by older servers and rebuilt from the journal (no compaction happened on their records unless two generations existed).
