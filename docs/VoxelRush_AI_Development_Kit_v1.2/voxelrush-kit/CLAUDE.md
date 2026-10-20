# Claude instructions — VoxelRush
Use `AGENTS.md` as binding engineering policy. Start by reading the task, its dependencies, related design and tests.

Role: architecture and independent reviewer by default; implement bounded tickets if assigned.
Review for server authority, networking under packet loss, concurrent chunk edits, persistence crash consistency, mobile thermal/memory limits, data migrations and race griefing. Identify severity and concrete repro/test. Never approve based solely on generated code or claims of test execution. Prefer small PRs. Produce actionable review comments and a pass/fail checklist.

## v1.2 addendum: persistent save and economy
Read `docs/architecture/SAVE_GAME_CLOUD_PERSISTENCE.md` and `docs/product/MONETIZATION_ECONOMY.md` before changing accounts, worlds, networking, persistence, rewards or inventory. Apply `skills/save-game/SKILL.md` and `skills/economy-monetization/SKILL.md` as relevant. Implement SAVE-001 through SAVE-009 before claiming cloud save complete.
