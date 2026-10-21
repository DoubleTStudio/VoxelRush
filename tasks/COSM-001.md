# Task: COSM-001 — Paint shown to every player, decided by the server

## Goal
Every car shows its owner's equipped paint to everyone. The server decides: a player only shows a paint they own (ECON-001 ledger), never what the client claims.

## Changes
- **Wire protocol 3 → 4:** the car prefab gains a NetworkBehaviour, so older clients are refused with `protocol_mismatch` instead of failing to deserialize the spawn.
- **Contract (client → server):** after its car spawns, the owner sends its equipped paint SKU with `VehiclePaint.RequestPaintRpc` (reliable, owner-only, sender checked again on the server). The payload is a hand-written `PaintRequest` (byte length, at most 48 ASCII bytes; longer lengths are never read). It is not a `FixedString`, because NGO reads FixedString lengths unchecked in release players. Changing the equipped paint while connected sends it again (`LocalPlayer.EquippedPaint`).
- **Contract (server → all):** `VehiclePaint` has a server-written `NetworkVariable<FixedString64Bytes>` on the car, so late joiners get it with the spawn. NGO refuses client writes before deserializing them. Each change is one small delta per client, metered in `NetworkBandwidth`.
- **Server authority** (`Core/Cosmetics`, engine-free):
  - `PaintCatalog`: paint SKUs the game can show (mirrors `catalog.json`), the SKU shape (1..48 chars of `a-z0-9.-`), and a bounded parser for the API answer.
  - `PaintAuthority` refuses malformed and unknown SKUs at once. Other requests are queued (latest wins) and decided per tick.
  - Ownership lookups run on the thread pool and are cached per player. A miss is refused only on an answer fetched after the request, so a purchase made just before equipping counts. Misses are re-checked at most every 10 s, failed lookups retry after 5 s, and an unconfirmed request is dropped after 30 s.
  - At most one visible change per player per second. Drain allocates nothing when idle.
- **Modes** (`server_paint mode=...`):
  - Ticketed server with `VOXELRUSH_API_URL` and `VOXELRUSH_SERVICE_TOKEN`: `Verified` (owned paints only).
  - Ticketed server without the API: `DefaultOnly` (fail closed).
  - Development server (no tickets, no identities): `AnyCatalogPaint`, i.e. any catalog paint, nothing else.
- **Networking:** `PaintServer` (server: requests → authority → car, logs `vehicle_paint_applied` / `vehicle_paint_rejected reason=malformed|unknown_sku|not_owned|unverified`; raw client text is never logged). `EntitlementClient` (background HTTP, 5 s timeout, 64 KB response cap). `CarPaints` (NGO-free registry of confirmed paints for presentation). `VehicleSpawner.PaintOf`. Bots accept `-botPaint <sku>`.
- **API:** `GET /internal/v1/players/{id}/entitlements` (service token) → `{"skus":[...]}`, active entitlements only; unknown players own nothing.
- **Presentation:** the SKU → colour table moved from `GarageMenu` to `PaintPalette`. `CarPaintView` (on `NetworkWorld` in `Client.unity`) paints every car, own and remote, from the confirmed SKU. `GarageMenu` no longer tints the own car locally; it publishes the equipped paint.
- `NetworkedCar.prefab` gains the `VehiclePaint` component.
- `tools/smoke-auth.sh` and `tools/smoke-progression.sh` take `SMOKE_PORT` (default 7777).
- `smoke-progression` now also checks paint: A claims the free paint and B asks for a store paint it doesn't own.

## Not done
- Decal, trail, horn and flag visuals (still no rendering).
- The garage is only reachable from the join screen, so an in-race equip change is supported by the protocol but has no UI.
- The own car shows its paint only once the server confirms it (one round trip plus a cached lookup). With an unreachable API it stays the default colour.
- A refund while connected takes effect on the next request or reconnect, not immediately.

## Verification (2026-10-21, macOS arm64)
| Command | Result |
|---|---|
| `tools/test.sh domain` | Passed 489/489 (new `PaintAuthorityTests`) |
| `tools/test.sh editmode` | Passed 571/571 (new: paint authority, `PaintWireTests`, prefab has `VehiclePaint`, catalog paints match the backend) |
| `tools/test.sh playmode` | Passed 39/39 (new: remote car painted from the confirmed SKU, wheels untouched, default restores, cars that spawned earlier, palette covers every paint, scene has `CarPaintView`) |
| `dotnet test backend/VoxelRush.Api.Tests` | Passed 105/105 (new: internal owned SKUs, active only, service token only) |
| `tools/build.sh server-mac` | Built |
| `tools/build.sh bot-mac --development` | Built |
| `SMOKE_PORT=7787 tools/smoke-auth.sh native` | AUTH SMOKE PASS (server `mode=DefaultOnly`) |
| `SMOKE_PORT=7787 tools/smoke-progression.sh native` | PROGRESSION SMOKE PASS. Server: `vehicle_paint_applied sku=paint.racing-red` and `vehicle_paint_rejected sku=paint.chrome reason=not_owned`. B logs `vehicle_paint_seen own=false sku=paint.racing-red`. No bot ever shows chrome. |

## Risks / rollback
- Protocol bump: clients and servers must roll out together.
- Every equip triggers at most one API read per player per 10 s.
- Rollback: revert (protocol back to 3). The API endpoint is read-only and has no migration.
