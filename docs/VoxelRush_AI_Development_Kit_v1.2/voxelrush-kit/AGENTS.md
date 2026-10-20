# Codex instructions — VoxelRush
Read README, GDD, SYSTEM, DECISIONS, relevant skill, and backlog task before editing.

## Workflow
1. Select one ticket and state scope, dependencies, assumptions and acceptance tests.
2. Inspect repository and existing APIs; do not fabricate interfaces that conflict with code.
3. Implement minimal vertical functionality; separate pure domain C# from Unity MonoBehaviours.
4. Write edit-mode unit tests and play-mode/integration tests where required.
5. Run available tests/builds, report exact commands and outcomes. Never claim tests ran if they did not.
6. Submit focused PR with changed files, tradeoffs, risks, test evidence and rollback notes.

## Hard rules
- Dedicated server authoritative for vehicle state, block mutations, inventory and race results.
- Clients send input/intents, never trusted damage amounts or world state.
- World generation version, chunk revisions, schema versions and stable IDs are mandatory.
- Never instantiate one GameObject/Rigidbody per voxel or network cosmetic debris.
- No blocking storage calls on simulation tick; use bounded queues and backpressure.
- Do not add paid services, dependencies or license-unclear assets without approval.
- Avoid changing unrelated systems; no giant PRs. Keep code compileable after each task.
- Security: validate authenticated actor, permissions, rate limits, replay/idempotency, bounds.
- Use clear names, nullable annotations, deterministic tests and structured logging.

## Before implementing
Check `tasks/BACKLOG.md` and `docs/testing/QUALITY_GATES.md`.

## v1.2 addendum: persistent save and economy
Read `docs/architecture/SAVE_GAME_CLOUD_PERSISTENCE.md` and `docs/product/MONETIZATION_ECONOMY.md` before changing accounts, worlds, networking, persistence, rewards or inventory. Apply `skills/save-game/SKILL.md` and `skills/economy-monetization/SKILL.md` as relevant. Implement SAVE-001 through SAVE-009 before claiming cloud save complete.
