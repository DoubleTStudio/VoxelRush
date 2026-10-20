# VoxelRush — AI Implementation Kit
Version 1.0 | 2026-10-20 | Status: architecture baseline / not implemented

VoxelRush is an original cross-platform 3D voxel racing, building, and destruction game. Android and iOS clients share a procedural world with an authoritative Unity dedicated server.

## Start here
1. Read `docs/product/GDD.md`, `docs/architecture/SYSTEM.md`, `docs/architecture/DECISIONS.md`.
2. Read `AGENTS.md` (Codex) or `CLAUDE.md` (Claude).
3. Follow `tasks/BACKLOG.md` in dependency order. Do not skip the vertical slice.
4. Every PR must satisfy `docs/testing/QUALITY_GATES.md`.

## Product defaults
- 8 players per dedicated server instance; 30 Hz authoritative tick; landscape mobile.
- Persistent editable sandbox, race preparation, locked race-critical geometry during active races.
- Procedural bounded launch map with expansion capability; deterministic generation version.
- Unity 6 LTS (pin exact editor version before initial project commit), URP, Input System, Netcode for GameObjects (evaluate after vertical slice), Linux dedicated server.
- Backend optional for M0/M1: ASP.NET Core .NET 10, PostgreSQL, object storage when accounts/persistence are needed.

## Documents
- Product: GDD, gameplay rules, controls, assets
- Architecture: system, voxel, vehicle, networking, destruction, persistence, deployment, security
- Protocols: event contracts and revision behavior
- Testing: quality gates and scenarios
- Operations: CI/CD and runbooks
- Skills: scoped agent guidance with inputs, implementation sequence, verification
- Tasks: milestone backlog with definitions of done

## Explicit non-goals for initial vertical slice
Infinite persistent public world, 32-player servers, complex vehicle deformation, fully simulated debris, economy, payments, cross-server seamless travel.

## Important
This is a complete *planning and agent instruction package*, not a compiled Unity project or finished game. Unity package versions, hosting prices and service availability must be validated when bootstrapping.

## v1.2 addendum: persistent save and economy
Read `docs/architecture/SAVE_GAME_CLOUD_PERSISTENCE.md` and `docs/product/MONETIZATION_ECONOMY.md` before changing accounts, worlds, networking, persistence, rewards or inventory. Apply `skills/save-game/SKILL.md` and `skills/economy-monetization/SKILL.md` as relevant. Implement SAVE-001 through SAVE-009 before claiming cloud save complete.
