---
name: voxelrush-destruction
description: Implement zone-aware voxel damage and collapse for the VoxelRush Unity/C# mobile multiplayer project.
---
# Destruction skill
## When to use
Use for tasks involving: implement zone-aware voxel damage and collapse.
## Required reading
`README.md`, `AGENTS.md`, `docs/architecture/SYSTEM.md`, `docs/architecture/DECISIONS.md`, matching domain spec, `tasks/BACKLOG.md`, `docs/testing/QUALITY_GATES.md`.
## Implementation sequence
1. Identify ticket, dependencies, existing source interfaces and acceptance tests.
2. Define contract and tests before implementation.
3. Implement bounded incremental changes: Server damage calculation, sparse HP, race overlay, bounded explosion, incremental anchor BFS, cosmetic debris.
4. Add failure handling, logs, telemetry and explicit configuration.
5. Verify: Duplicate contacts, simultaneous hits, protected zones, collapse cap, crash recovery.
6. Report exact commands/results, remaining risks, PR review notes.
## Forbidden shortcuts
Do not trust client state, bypass protected-zone policy, skip revisions or durability, block game tick with IO, allocate per voxel, or claim unexecuted tests.
## Deliverable format
Changed files; contract changes; tests and outputs; performance impact; security/persistence impact; limitations; next task.
