# Backups, restore and storage quotas (SAVE-009)

## What is stored where
| Data | Where | Backup |
|---|---|---|
| Accounts, worlds, members, progress, entitlements, snapshot index, leases, restore audit | PostgreSQL | Nightly `pg_dump -Fc` kept 30 days, plus the provider's point-in-time recovery when available |
| Chunk snapshots (compressed, SHA-256 indexed) | S3-compatible object storage (R2) | Every committed snapshot inside the retention window is a restore point; enable bucket versioning or replication for disaster recovery of the bucket itself |
| Journal and latest local snapshots | Game server volume | Disposable: rebuilt from object storage on a fresh disk (SAVE-003) |

## Retention
- Per chunk, the newest `Snapshots:RetainRevisions` (3) revisions of the current epoch are always kept, plus everything committed in the last `Snapshots:RetentionDays` (7).
- Pruning runs on every commit for that chunk. An object is deleted only when no index row (in any epoch) refers to it.
- Restores are allowed only inside the retention window.

## Point-in-time world restore
1. The owner lists restore points: `GET /v1/worlds/{id}/backups` (newest first; commits less than 10 s apart are one save).
2. The world must be stopped (no live lease). Otherwise the restore returns `409 world_running`.
3. `POST /v1/worlds/{id}/restore {"asOf": "<savedAt>"}` starts a new **epoch**. Its index rows point at the objects that were current at `asOf`. Nothing is overwritten, so a later restore to a time before the restore undoes it.
4. Each restore is recorded in `world_restores` (actor, as-of time, epoch, chunks) and listed by `GET /v1/worlds/{id}/restores`.
5. The next server to take the world's lease learns the epoch. If its disk was written under another epoch, it deletes the journal and snapshots (the reward outbox stays), restores from storage, and logs `world_local_reset`.

## Database disaster recovery
1. Provision a new PostgreSQL instance and restore the last dump: `pg_restore -d voxelrush --no-owner voxelrush.dump`.
2. Start the API (migrations are idempotent and run at start-up), then check `/healthz`.
3. Worlds saved after the dump have objects in storage but no index rows. Operators can recover them with a restore to the last indexed save, or by re-indexing from bucket listings (not automated yet).

The drill `tools/recovery-drill.sh native|docker` (CI runs Docker mode) covers steps 1–5 and the database recovery end to end. Two sessions save a world; the database is rebuilt from a dump; the owner restores to the first save; a server on the stale disk rebuilds, and a new player sees exactly the first session's chunks.

## Quotas
- Worlds per owner: `world_quotas.max_worlds`, else `MaxWorldsPerOwner` (3).
- Stored bytes per owner: `world_quotas.max_stored_bytes`, else `Snapshots:MaxStoredBytesPerOwner` (64 MiB). This counts each committed object once across all of the owner's worlds, epochs and retained revisions.
- Over quota, snapshot uploads get `403 quota_exceeded`. The game server keeps the world on its local disk and logs `world_snapshots_published … failed>0 error=…`, so alert on that.

## Storage metrics
`GET /internal/v1/metrics/storage` (service token) returns worlds with snapshots, objects, stored bytes, restores, the top 10 worlds by bytes, and an estimated monthly cost (`Snapshots:CostPerGbMonthUsd`, R2 standard storage $0.015/GB-month). Scrape it into the dashboard alongside MONITORING.md.
