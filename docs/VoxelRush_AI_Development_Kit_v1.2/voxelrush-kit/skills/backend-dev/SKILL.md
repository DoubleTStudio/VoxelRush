---
name: voxelrush-backend-dev
description: Build game metadata and persistent services for the VoxelRush Unity/C# mobile multiplayer project.
---
# Backend Dev skill
## When to use
Use for tasks involving: build game metadata and persistent services.
## Required reading
`README.md`, `AGENTS.md`, `docs/architecture/SYSTEM.md`, `docs/architecture/DECISIONS.md`, matching domain spec, `tasks/BACKLOG.md`, `docs/testing/QUALITY_GATES.md`.
## Implementation sequence
1. Identify ticket, dependencies, existing source interfaces and acceptance tests.
2. Define contract and tests before implementation.
3. Implement bounded incremental changes: ASP.NET Core .NET10, signed join tickets, PostgreSQL metadata, durable journal, object snapshots, CI Docker.
4. Add failure handling, logs, telemetry and explicit configuration.
5. Verify: Idempotency, least privilege, schema migrations, restore tests, no blocking tick.
6. Report exact commands/results, remaining risks, PR review notes.
## Forbidden shortcuts
Do not trust client state, bypass protected-zone policy, skip revisions or durability, block game tick with IO, allocate per voxel, or claim unexecuted tests.
## Deliverable format
Changed files; contract changes; tests and outputs; performance impact; security/persistence impact; limitations; next task.
