# Task: ECON-001 — Cosmetic-only catalog and backend entitlement ledger

## Goal
Optional cosmetics without pay-to-win, owned server-side and visible on every device. Acceptance (BACKLOG): free core gameplay, cross-device entitlement.

## Prerequisites / relevant docs
SAVE-006 (progression, device linking). MONETIZATION_ECONOMY (core free; cosmetic-only; inventory and entitlements backend-authoritative; refunds/revocations; idempotent grants), GDD (no pay-to-win), skills/economy-monetization.

## In scope / out of scope
In: migration `005_entitlements.sql`; `catalog.json` (8 placeholder items: paints, decal, trail, horn, flag; level or store unlock), validated at startup (cosmetic kinds only, exactly one unlock rule); `GET /v1/catalog`; `GET /v1/players/me/entitlements`; `POST /v1/players/me/entitlements/claim` (level unlocks, checked against backend progression); internal grant (`grant`/`purchase` source, idempotent per transaction id) and revoke.
Out: purchases, receipt verification and rewarded ads (ECON-002, after privacy review), equipping cosmetics in the client and rendering them, soft currency (not planned for MVP).

## Tests (`backend/VoxelRush.Api.Tests/EconomyTests.cs`)
Catalog cosmetic-only (allowed kinds; no gameplay fields such as speed/boost/damage/blocks/checkpoints; a level-1 item is free); **core gameplay needs no entitlements** (a player with none creates a world and gets a join ticket); level unlocks require server progression and are idempotent; store items can't be self-claimed, unknown SKUs rejected, players can't use the server grant path; server grants idempotent per transaction, a receipt belongs to one player, unknown sources rejected, revoke on refund, a revoked receipt can't be replayed; **entitlements follow the player to a linked device**; catalog validation.

## Verification commands and actual results (2026-10-21)
| Command | Result |
|---|---|
| `dotnet test backend/VoxelRush.Api.Tests` | Passed 74/74 |

## Definition of done
- [x] Free core gameplay; cross-device entitlement.

## PR summary / risks / rollback
- Risks: catalog items and unlock levels are placeholders for design.
- Rollback: revert; drop `player_entitlements`.
