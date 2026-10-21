# Task: SAVE-009 — Backups, restore audit, quotas and storage cost metrics

## Goal
A world can be recovered after player mistakes or infrastructure loss, every restore is audited, storage per owner is bounded, and storage cost is visible. Acceptance (BACKLOG): recovery drill and quota enforcement.

## Prerequisites / relevant docs
SAVE-003 (snapshots in object storage, cold restore), SAVE-005 (leases), SAVE-001 (`world_quotas`). SAVE_GAME_CLOUD_PERSISTENCE (`POST /v1/worlds/{id}/restore` privileged with audit trail; WorldQuota; retention; metrics: snapshot bytes per world, storage cost/world, restoration success).

## In scope / out of scope
In: migration `007_backups_quotas.sql` (`worlds.restore_epoch`, `chunk_snapshots.epoch`/`committed_at` with epoch in the primary key, `world_restores`); retention on commit (newest 3 revisions of the current epoch + 7-day window; objects deleted only when unreferenced); per-owner stored-byte quota on upload (`403 quota_exceeded`; distinct objects across worlds/epochs; `world_quotas` row or default 64 MiB) and `world_quotas.max_worlds` on world creation; `GET /v1/worlds/{id}/backups` (restore points, gap-grouped), owner-only `POST /v1/worlds/{id}/restore` (stopped world only, inside retention, new epoch pointing at the objects current then, non-destructive and undoable), `GET /v1/worlds/{id}/restores` (audit); lease responses carry the epoch and servers on a disk from another epoch wipe journal + snapshots (not the reward outbox) and rebuild from storage; `GET /internal/v1/metrics/storage` (worlds, objects, bytes, restores, top worlds, estimated monthly cost); storage calls retried (3 attempts) on transient errors; `docs/operations/BACKUP_RESTORE.md`.
Out: re-indexing objects saved after the last database dump (documented as a manual step), bucket-level versioning/replication (provider configuration), restore UI.

## Tests
- API `BackupTests`: **point-in-time restore** to the first of two saves (chunk first saved later returns to its generated state), lease reports the new epoch, **undo** by restoring to before the restore, new epoch reuses revision numbers safely, audit lists both restores with the actor; owner-only/not-found for members, stopped world required (`409 world_running`), future/outside retention/missing `asOf` refused; **quota**: max worlds from the quota row, 1500 + 1500 > 2500 bytes refused, shared objects counted once after a restore; **retention** keeps newest/window rows and deletes only unreferenced objects (restored epoch keeps its object); storage metrics are service-only and priced. Schema contract updated (new columns and tables).
- Domain: `AlignEpoch` wipes only the old timeline and keeps the reward outbox.
- **Recovery drill** `tools/recovery-drill.sh native` (Docker mode in CI): two sessions save; `pg_dump` → database destroyed → `pg_restore` → API restarted; owner restores to the first save (epoch 1); the server on the stale disk logs `world_local_reset epoch=1`, restores from storage, and a new player's chunk hashes equal the first session's; the restore is audited and metrics show stored bytes.

## Verification commands and actual results (2026-10-21, macOS arm64)
| Command | Result |
|---|---|
| `dotnet test backend/VoxelRush.Api.Tests` | Passed 89/89 (3 consecutive runs) |
| `tools/test.sh domain` / `editmode` | Passed 450/450 / 522/522 |
| `tools/recovery-drill.sh native` | **RECOVERY DRILL PASS**, 6/6 consecutive runs |
| `tools/smoke-lease.sh native` | PASS |
| shellcheck, actionlint | clean |

Found during the drill: SeaweedFS makes its volumes read-only when the Docker disk is nearly full (local VM at 99%), which showed up as `snapshot PUT failed (500)`. The throwaway S3 containers in the smoke and drill scripts now keep data on tmpfs. The server logs the upload error in `world_snapshots_published`.

## Definition of done
- [x] Recovery drill; quota enforcement.

## PR summary / risks / rollback
- Risks: retention defaults (3 revisions, 7 days, 64 MiB/owner) are placeholders to tune against real usage and cost. Restores are owner-only, and support-initiated restores would need an admin role. Worlds saved after the last dump need manual re-indexing.
- Rollback: revert the code; the migration is additive except the snapshot primary key (now includes `epoch`). Rows from epochs > 0 would need deleting before going back to the old key.
