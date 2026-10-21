# Task: COSM-002 — Decal, trail, horn and flag shown to every player, decided by the server

## Goal
The non-paint catalog cosmetics (`decal.lightning`, `trail.sparks`, `horn.retro`, `flag.checkered-gold`) can be equipped, one per kind, and are seen or heard by every player. Same server authority as paint (COSM-001): a car only shows items its player owns, and the server decides.

## Changes
- **Wire protocol 4 → 5.** The car's networked component now has one server-written slot per kind, and there are new horn RPCs. Older clients are refused with `protocol_mismatch`.
- **Loadout instead of paint only.** COSM-001's paint code now covers every kind, so nothing is duplicated:
  - `PaintCatalog` → `CosmeticCatalog`. It holds `CosmeticKind` (Paint, Decal, Trail, Horn, Flag), the SKUs of each kind (a mirror of `catalog.json`), `IsWellFormed(kind, sku)` / `IsKnown(kind, sku)` / `TryKindOf`, and the same bounded parser for the API answer.
  - `PaintAuthority` → `LoadoutAuthority`. Requests are keyed by (client, kind). Each slot has its own queue (latest wins) and its own rate limit (1 visible change/s). One ownership lookup and cache per player serves every slot. Malformed, unknown and out-of-range-kind requests are refused at once and never looked up.
  - `PaintServer` → `LoadoutServer`, `VehiclePaint` → `VehicleLoadout` (same script GUID, so the prefab reference is kept), `CarPaints` → `CarCosmetics`, `VehicleSpawner.PaintOf` → `LoadoutOf`.
  - The ownership modes are unchanged: `Verified`, `DefaultOnly` and development `AnyCatalogItem`.
- **Contract (client → server):** `VehicleLoadout.RequestCosmeticRpc(LoadoutRequest)`, reliable and owner-only, with the sender checked again on the server. The payload is still hand-written: 1 kind byte, 1 length byte, then at most 48 ASCII bytes. Longer lengths are never read, and it is never a `FixedString`. `HonkRpc()` (owner-only, no payload) reports a horn press.
- **Contract (server → all):** five server-written `NetworkVariable<FixedString64Bytes>` slots on the car (late joiners get them with the spawn), each change metered in `NetworkBandwidth`. `HornRelayRpc` (`SendTo.NotOwner`, `InvokePermission = Server`, sender checked on receipt) is sent only for a press the server accepted.
- **Horn authority** (`Core/Cosmetics/HornLimiter`, engine-free). The server relays a press only if the car shows a server-approved horn, and at most one press per player per second. Refused presses don't extend the window, and nothing the client sends changes the rate. The server logs `vehicle_horn_relayed`, plus one `vehicle_horn_rejected reason=no_horn|rate_limited` warning per client and reason, and `server_horn_summary relayed= rateLimited=` on shutdown.
- **Logs:** `server_paint` → `server_cosmetics mode=...`, and `vehicle_paint_applied|rejected|seen` → `vehicle_cosmetic_applied|rejected|seen kind=<kind> sku=...`. Raw client text and the raw kind byte are never logged.
- **Presentation (client-only; the server compiles it out):**
  - `CarCosmeticsView` sits on `NetworkWorld` in `Client.unity`. It reads the confirmed loadout from `CarCosmetics`, for own and remote cars:
    - Decal: a procedural lightning-bolt mesh on the hood.
    - Flag: a pole and a cloth with a procedural gold checkered texture at the rear, swaying gently.
    - Trail: spark particles (small voxel cubes) while the car is boosting or above 18 m/s. Remote boost comes from the snapshot's boost flag. Particle systems are pooled, at most 8, with a per-car budget from `QualityPreset.TrailParticles`: Low 0 (off), Medium 48, High 96. A tier change at runtime (adaptive quality) turns trails off or on.
    - Horn: an `AudioSource` at the car (fully 3D, linear roll-off to 80 m) that plays a procedurally generated two-tone square-wave clip.
  - Everything reuses the car's own lit material through property blocks, so no new shader or asset ships. `CosmeticAssets` builds the meshes, the texture and the clip once.
  - `HornButton` sits on `TouchHud`. It clones the BUILD button into a HORN button (`<Gamepad>/leftShoulder`; keyboard H), shown only while the own car has a confirmed horn. The own car plays its own press at once, with a local 1 s cooldown matching the server's. Other players hear it when the server relays it.
  - `GarageMenu` has Equip/Unequip for every owned item of a known kind, one per kind. Choices are stored per kind (`voxelrush.<kind>`; paint keeps `voxelrush.paint`) and sent to the server.
- **Bots:** `-botPaint <sku>` → `-botEquip <sku>[,<sku>...]` (kind inferred from the SKU). Development builds also accept `-botHorn`, which presses the horn 5 times a second and bypasses the client cooldown, like `-botCheat`.
- **`tools/smoke-progression.sh`:**
  - A is also granted `decal.lightning` and `horn.retro` through the internal grant endpoint, and spams the horn.
  - B asks for `paint.chrome` and `flag.checkered-gold`, which it doesn't own, and spams the horn without owning one.
  - The script checks that B sees A's decal and hears A's horn, that the presses are rate-limited, and that B's unowned items and horn presses are refused.
- No backend change: the internal entitlements endpoint from COSM-001 already returns every owned SKU.

## Not done
- The garage is still reachable only from the join screen. Changing equipment during a race works in the protocol but has no UI (as in COSM-001).
- The horn has no volume setting or mute. In-editor audio was not checked by ear, because batch tests run without an audio device; tests check the clip, the source settings and play calls.
- Trail and decal looks are first-pass procedural art. On-device visual and thermal checks are still to do.
- A refund while connected takes effect on the next request or reconnect (as in COSM-001).

## Verification (2026-10-21, macOS arm64)
| Command | Result |
|---|---|
| `tools/test.sh domain` | Passed 521/521. New: `LoadoutAuthorityTests` per kind (owned / not owned / malformed / unknown / out-of-range kind), one shared lookup across slots, a rate limit per slot, `HornLimiterTests`, and trail particles off at the Low tier. |
| `tools/test.sh editmode` | Passed 628/628. New: `LoadoutWireTests` (round trip per kind, over-long, non-ASCII, out-of-range kind, truncated), a server horn relay limited to 1/s and only with a horn, RPC invoke permissions (relay is server-only), the prefab has `VehicleLoadout` and no client-only effects, and the catalog matches the backend for every kind. |
| `tools/test.sh playmode` | Passed 50/50. New `CarCosmeticsViewTests`: decal and flag appear on a remote car and go when unequipped, items confirmed before the view started, trail sparks while boosting and not when parked, trail off at the Low tier and back at Medium, trails pooled and capped, horn spatialised at the car and played on relay, procedural clip and texture, garage equip/unequip per kind. The Client scene has `CarCosmeticsView` and a hidden HORN button bound to the horn path. |
| `tools/build.sh server-mac` | Built |
| `tools/build.sh bot-mac --development` | Built |
| `tools/build.sh android --development` | Built `build/android/VoxelRush.apk` |
| `SMOKE_PORT=7787 tools/smoke-progression.sh native` | PROGRESSION SMOKE PASS. Server logs `server_cosmetics mode=Verified`; `vehicle_cosmetic_applied` for A's paint, decal and horn; `vehicle_cosmetic_rejected ... reason=not_owned` for B's chrome paint and gold flag; `vehicle_horn_rejected reason=no_horn` (B), `vehicle_horn_rejected reason=rate_limited` (A); and `server_horn_summary relayed=35 rateLimited=164`. B logs A's paint and decal (`vehicle_cosmetic_seen own=false`) and heard 35 horns. No bot shows chrome or the flag, and A hears no horn. |

## Risks / rollback
- Protocol bump: clients and servers must roll out together.
- Log names changed (`vehicle_paint_*` → `vehicle_cosmetic_*`, `server_paint` → `server_cosmetics`). Anything that greps the old names needs updating. The in-repo smoke script is updated.
- Horn traffic is bounded: at most 1 relayed press/s per player (about 8 bytes per receiving client). Spammed presses cost the server one dictionary lookup each.
- Rollback: revert (protocol back to 4). No data or API migration.
