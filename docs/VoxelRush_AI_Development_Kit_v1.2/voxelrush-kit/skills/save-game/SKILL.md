# Skill: Save Game and World Persistence
Use when implementing player cloud saves, autosave, persistent voxel worlds, snapshot/journal, reconnect, idle server lifecycle or cross-device recovery.
1. Read `docs/architecture/SAVE_GAME_CLOUD_PERSISTENCE.md`, `docs/architecture/PERSISTENCE_SECURITY.md`, `docs/protocols/CONTRACTS.md`, and `AGENTS.md`.
2. Identify authoritative writer, operation idempotency key, revision, transaction boundary and failure/retry path before coding.
3. Keep PostgreSQL metadata and object storage snapshots separate; do not save full procedural maps for every edit.
4. Never ACK an edit as saved before journal durability; include crash/replay tests.
5. Test guest-account linking, race-scope isolation, two-server lease fencing, concurrent edits and device switching.
6. Submit a small PR with schema migration, contract tests, telemetry and rollback plan. Avoid claiming production readiness without fault injection.
