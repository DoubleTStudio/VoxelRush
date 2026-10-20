---
name: voxelrush-unity-gameplay
description: Build vehicle controls, race loop and UI for the VoxelRush Unity/C# mobile multiplayer project.
---
# Unity Gameplay skill
## When to use
Use for tasks involving: build vehicle controls, race loop and ui.
## Required reading
`README.md`, `AGENTS.md`, `docs/architecture/SYSTEM.md`, `docs/architecture/DECISIONS.md`, matching domain spec, `tasks/BACKLOG.md`, `docs/testing/QUALITY_GATES.md`.
## Implementation sequence
1. Identify ticket, dependencies, existing source interfaces and acceptance tests.
2. Define contract and tests before implementation.
3. Implement bounded incremental changes: Input System actions, Rigidbody simulation, camera and touch controls; implement pure tuning/config separated from MonoBehaviour.
4. Add failure handling, logs, telemetry and explicit configuration.
5. Verify: Mobile multitouch, physics fixed timestep, scene smoke, PlayMode tests, server/client authority.
6. Report exact commands/results, remaining risks, PR review notes.
## Forbidden shortcuts
Do not trust client state, bypass protected-zone policy, skip revisions or durability, block game tick with IO, allocate per voxel, or claim unexecuted tests.
## Deliverable format
Changed files; contract changes; tests and outputs; performance impact; security/persistence impact; limitations; next task.
