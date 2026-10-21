# Task: SAVE-001 — Schema/contract for progression, world metadata, chunk revisions and operation IDs

## Goal
One reviewed data contract for the backend. Acceptance (BACKLOG): migrations and contract tests.

## Prerequisites / relevant docs
VR-080 (API, migration 001), VR-031/032 (operation ids, chunk revisions in the journal). SAVE_GAME_CLOUD_PERSISTENCE (data models), PERSISTENCE_SECURITY (records). Contract: `docs/contracts/DATABASE_SCHEMA.md`.

## In scope / out of scope
In: migration `002_progression_quotas.sql` (`player_progress`, `player_settings`, `reward_grants`, `world_quotas`), the schema contract document, schema contract tests.
Out: endpoints using the new tables (SAVE-006, SAVE-009), invites (SAVE-004), leases (SAVE-005), entitlements (ECON-001).

## Tests (`backend/VoxelRush.Api.Tests/SchemaContractTests.cs`, real PostgreSQL 17)
Migrations recorded and re-applying is a no-op; every contract table has exactly the documented columns in order (8 tables); constraints reject negative XP, level 0, unknown reward kinds, non-positive amounts, negative quotas, unknown visibility, oversized settings; duplicate reward grant keys are a unique violation (no double grant); deleting a player removes their refresh tokens and progress.

## Issue found during validation
The local Docker VM disk was 99% full, so the test S3 server (SeaweedFS) went read-only and the snapshot test failed with HTTP 500. The fixture now uses small volumes and a 50 MiB free-space floor; `smoke-auth.sh` removes its containers' anonymous volumes. (The disk is mostly other projects' images; worth pruning on the dev machine.)

## Verification commands and actual results (2026-10-21)
| Command | Result |
|---|---|
| `dotnet test backend/VoxelRush.Api.Tests` | Passed 45/45 |

## Definition of done
- [x] Migrations and contract tests.

## PR summary / risks / rollback
- Risks: none at runtime (new tables only).
- Rollback: revert; `DROP TABLE` the four new tables and delete the `002_progression_quotas.sql` row from `schema_migrations`.
