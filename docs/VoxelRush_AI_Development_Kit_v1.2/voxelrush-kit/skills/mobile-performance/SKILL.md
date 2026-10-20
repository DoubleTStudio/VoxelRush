---
name: voxelrush-mobile-performance
description: Optimize Android/iOS gameplay for the VoxelRush Unity/C# mobile multiplayer project.
---
# Mobile Performance skill
## When to use
Use for tasks involving: optimize android/ios gameplay.
## Required reading
`README.md`, `AGENTS.md`, `docs/architecture/SYSTEM.md`, `docs/architecture/DECISIONS.md`, matching domain spec, `tasks/BACKLOG.md`, `docs/testing/QUALITY_GATES.md`.
## Implementation sequence
1. Identify ticket, dependencies, existing source interfaces and acceptance tests.
2. Define contract and tests before implementation.
3. Implement bounded incremental changes: URP presets, Burst/Jobs, pooling, mesh upload budget, profiler instrumentation, thermal tests.
4. Add failure handling, logs, telemetry and explicit configuration.
5. Verify: 30fps low tier, 60fps capable tier, p95/p99 frames, GC and memory reports.
6. Report exact commands/results, remaining risks, PR review notes.
## Forbidden shortcuts
Do not trust client state, bypass protected-zone policy, skip revisions or durability, block game tick with IO, allocate per voxel, or claim unexecuted tests.
## Deliverable format
Changed files; contract changes; tests and outputs; performance impact; security/persistence impact; limitations; next task.
