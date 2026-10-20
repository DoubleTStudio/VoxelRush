---
name: voxelrush-multiplayer
description: Implement authoritative multiplayer for the VoxelRush Unity/C# mobile multiplayer project.
---
# Multiplayer skill
## When to use
Use for tasks involving: implement authoritative multiplayer.
## Required reading
`README.md`, `AGENTS.md`, `docs/architecture/SYSTEM.md`, `docs/architecture/DECISIONS.md`, matching domain spec, `tasks/BACKLOG.md`, `docs/testing/QUALITY_GATES.md`.
## Implementation sequence
1. Identify ticket, dependencies, existing source interfaces and acceptance tests.
2. Define contract and tests before implementation.
3. Implement bounded incremental changes: Session join, sequence-numbered input, client prediction/reconciliation, interest-filtered snapshots, reliable ordered chunk deltas.
4. Add failure handling, logs, telemetry and explicit configuration.
5. Verify: Packet loss, RTT, spoofed input, late join, reconnection, 8-player tick benchmark.
6. Report exact commands/results, remaining risks, PR review notes.
## Forbidden shortcuts
Do not trust client state, bypass protected-zone policy, skip revisions or durability, block game tick with IO, allocate per voxel, or claim unexecuted tests.
## Deliverable format
Changed files; contract changes; tests and outputs; performance impact; security/persistence impact; limitations; next task.
