---
name: voxelrush-voxel-engine
description: Implement procedural chunk storage and meshing for the VoxelRush Unity/C# mobile multiplayer project.
---
# Voxel Engine skill
## When to use
Use for tasks involving: implement procedural chunk storage and meshing.
## Required reading
`README.md`, `AGENTS.md`, `docs/architecture/SYSTEM.md`, `docs/architecture/DECISIONS.md`, matching domain spec, `tasks/BACKLOG.md`, `docs/testing/QUALITY_GATES.md`.
## Implementation sequence
1. Identify ticket, dependencies, existing source interfaces and acceptance tests.
2. Define contract and tests before implementation.
3. Implement bounded incremental changes: 16^3 chunks, signed coordinates, pinned generator, greedy meshing, neighbor dirty tracking, collider revisions.
4. Add failure handling, logs, telemetry and explicit configuration.
5. Verify: Golden seed hashes, negative coordinates, no GameObject per voxel, frame and memory budget.
6. Report exact commands/results, remaining risks, PR review notes.
## Forbidden shortcuts
Do not trust client state, bypass protected-zone policy, skip revisions or durability, block game tick with IO, allocate per voxel, or claim unexecuted tests.
## Deliverable format
Changed files; contract changes; tests and outputs; performance impact; security/persistence impact; limitations; next task.
