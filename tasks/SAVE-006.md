# Task: SAVE-006 — Cross-device player progression/inventory/entitlements sync

## Goal
A player's progression follows them to any device and is granted exactly once. Acceptance (BACKLOG): Android/iOS session continuity and no double grant.

## Prerequisites / relevant docs
SAVE-001 (tables), VR-080 (auth, tickets), VR-062 (race results). SAVE_GAME_CLOUD_PERSISTENCE (backend authoritative for progression; never accept client balances; idempotent mutations; `GET /v1/players/me/progress`, `PUT /v1/players/me/settings` with version/ETag; guest account linking), MONETIZATION_ECONOMY (progression is cosmetic only).

## In scope / out of scope
In: migration `004_device_links.sql`; device linking (`POST /v1/auth/link-code` → 10-character single-use code, 10 min; `POST /v1/auth/link` → a new login for the same player); `GET /v1/players/me/progress`; settings with `If-Match` optimistic concurrency (428 without it, 412 on a stale version); internal idempotent grants (`POST /internal/v1/players/{id}/grants`, service token; ledger row and XP change in one transaction); `RaceRewards` (finisher 100 + 25 per racer beaten, non-finisher 10 per checkpoint); `RewardOutbox` (durable, re-sends after a crash); `RewardClient` (background sender with backoff); ticketed servers queue grants for validated results.
Out: platform sign-in (Apple/Google) for account recovery without the first device, inventory/entitlements (ECON-001), offline play.

## Tests
- API (`ProgressionTests`, real PostgreSQL): new player level 1; grants apply once per key (retry is `duplicate`, XP unchanged); 12 concurrent retries of one grant apply once; only game servers grant (player tokens refused, no client write path, invalid/unknown grants rejected); a second device links to the same player and sees the same progress, first device stays signed in, codes single use; codes expire, unknown codes fail, creating one needs a signed-in device; settings concurrency across two devices (stale write refused with 412, latest readable, oversized/non-object refused); level curve.
- Domain: `RaceRewardsTests` (places, checkpoints, unknown racers skipped, stable keys), `RewardOutboxTests` (pending grants survive restart until confirmed, duplicate keys once, torn/garbage lines skipped, empty after completion).
- **End to end** `tools/smoke-progression.sh native`: PostgreSQL + API containers, ticket-only server with rewards enabled, two API players race; the server queues 2 grants and the API confirms both; XP 125 (winner) + 100; the winner links a second device and sees the same XP; re-sending the server's exact grant is a duplicate and changes nothing.

## Issues found during validation (fixed)
- Under Unity's Mono, a journal file whose fsync failed stayed open after `JournalWriter.Dispose` (the close itself threw), blocking a restart from reopening it: `Dispose` now tolerates the close error once a failure is recorded. The SAVE-002 crash test also needed `FileShare.ReadWrite` (Mono enforces share modes, .NET on macOS doesn't).

## Verification commands and actual results (2026-10-21, macOS arm64)
| Command | Result |
|---|---|
| `dotnet test backend/VoxelRush.Api.Tests` | Passed 67/67 |
| `tools/test.sh domain` | Passed 437/437 |
| `tools/test.sh editmode` | Passed 509/509 |
| `tools/smoke-progression.sh native` | **PROGRESSION SMOKE PASS** |

## Definition of done
- [x] Session continuity across devices (link code; same player and progress) and no double grant (ledger + concurrency + end to end).
- [ ] On real Android and iOS devices: not run (no devices).

## PR summary / risks / rollback
- Risks: device linking is the only recovery path until platform sign-in; the server image needs CA certificates before calling an HTTPS API (local/CI use HTTP inside the Docker network).
- Rollback: revert; drop `device_link_codes`.
