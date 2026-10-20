# Architecture decision records (accepted baseline)
ADR-001 Unity 6 LTS C# URP Input System, exact editor pinned at bootstrap.
ADR-002 Dedicated server authority for physics, race, damage, edits; client prediction only.
ADR-003 Chunk-based procedural voxel terrain with pinned generator version and revisions.
ADR-004 Hybrid destruction: editable sandbox, protected infrastructure, isolated race overlay.
ADR-005 Incremental bounded structural checks for player-built structures, not global natural terrain.
ADR-006 Debris cosmetic client-only; no per-voxel GameObjects or networked physics.
ADR-007 Durable ordered mutation journal plus verified snapshots.
ADR-008 Start with NGO/Unity Transport and benchmark 8 players before scaling.
ADR-009 Persistent sandbox hosted on long-lived instance; ephemeral races can use managed allocation.
ADR-010 First vertical slice uses no separate account API; introduce backend when persistence/session needs require it.

Each change requires an ADR with context, alternatives, decision, consequences, migration and rollback.

- ADR-009: Cloud progression and persistent worlds are authoritative server-side; local saves are cache/settings only.
- ADR-010: World lifecycle independent of game server, with single-writer lease/fencing and cold restore.
- ADR-011: Free core gameplay; paid cosmetic-only entitlements with verified transactions.
