# Task: BUILD-002 — Ramps: 3 variants × 4 orientations, ghost preview and rotate on the phone

## Goal
Players build ramps from the phone and drive up and jump off them. Ramps render, collide the same on the server and on predicting clients, persist, sync and can be destroyed like other player-built blocks (GDD MVP: 3 ramp variants).

## Changes
- **Wire protocol 4 → 5** (`SimulationConstants.ProtocolVersion`). Message layouts are unchanged. Older clients would mesh ramp ids as cubes and mispredict collisions, so they are refused with `protocol_mismatch`.
- **No persisted format or generator change:** journal record v1, snapshot schema 1, chunk hashes and generator versions are untouched. Ramps are ordinary block bytes (ADR-020).
- **Block ids** (`BlockIds`, `BlockShapes`): ids 13..24 = `13 + variant * 4 + yaw`. Ids 0..12 are unchanged. Yaw is the uphill direction (0 = +Z, 1 = +X, 2 = -Z, 3 = -X).
  - Low ramp 13..16 (0 → 0.5 m).
  - High ramp 17..20 (0.5 → 1 m). Low followed by high makes a two-block 1:2 ramp onto a block.
  - Kicker 21..24 (0 → 0.625 m jump lip).
- **Geometry** (`BlockGeometry`, shared by both meshers):
  - Per-face profiles (none, full, half-height end, kicker end, ramp side). A face is culled only by a full neighbour face or by an identical partial face across the border. So a ramp never hides a neighbour's face it only partly covers, and long-ramp seams, back-to-back kickers and same-yaw ramps side by side drop their hidden inner faces.
  - Full faces stay in the greedy merge. Slopes and partial faces are emitted per cell after it, in the same order in `ChunkMesher` (render) and `BurstChunkMesher` (server and client colliders).
  - Winding is checked when the templates are built.
  - The cube-only mesh is unchanged (VR-070 golden hash still matches).
- **Validation** (`EditValidator`, same code on server and client): placing an unknown id (≥ 25) or `Protected` is now `invalid_block_type`. Before, only air was refused. `EditIntent.orientation` stays reserved and ignored, and `Rotate` stays unsupported (turning means placing another id).
- **Destruction** (`MaterialCatalog`): HP per variant, the same in every yaw. Low ramp 60, high ramp 100, kicker 80 (placeholders, concrete-like). Ramps are player-built blocks, so anchors, collapse, collision damage and explosions treat them like any other block.
- **Phone build UX** (`PhoneBuilder`, new `BuildGhost`, IMGUI):
  - Place mode has two palette rows: six blocks, then Low ramp / High ramp / Kicker. A newly picked ramp climbs away from the camera.
  - A tap aims. The ghost shows the exact shape the meshers will build, green when the server's validator would accept it, red with the validator's reason otherwise. It is re-validated every frame as the car moves.
  - A bottom-centre row offers PLACE (or REMOVE), ROTATE (ramps, 90° per press) and CANCEL. Tapping the ghost again also confirms. Desktop keys: R, Enter, Esc.
  - Remove mode uses the same aim-then-confirm flow, and the ghost takes the aimed block's shape.
  - Drags and pinches still orbit and zoom the camera. No scene changes: the ghost is created at runtime.
- **Bots/tools:** `-botBlock <id>` for builder bots. `tools/smoke-world.sh` takes `SMOKE_PORT` (default 7777), and its second builder bot now builds low ramps, so ramp ids cross the server validator, journal, deltas, late join and restart.
- **Docs:** ADR-020 (ramp ids, culling rules, rejected 1:1 and 3:4 slopes, rollback). README build-mode controls and `-botBlock`.

## Not done
- Ramps use the stone atlas tile; the atlas has only 4 tiles today, and other placed blocks also render with placeholder tiles.
- Damage sources are not wired into the running server yet (car impacts, explosions). This is unchanged. Ramps are covered by the domain damage and explosion code and tests, and at runtime by removal and structural collapse.
- The ghost is opaque and tinted, not translucent, to avoid relying on transparent shader variants in player builds.
- No server-side `Rotate` of an already placed ramp. Remove it and place it again.

## Verification (2026-10-21, macOS arm64)
_Filled in below._

## Risks / rollback
- Protocol bump: clients and servers must roll out together.
- Tightened validation: a client that placed `Protected` or unknown ids now gets `invalid_block_type`. No shipped client offers them.
- Kicker steepness is tuned to the current ArcadeCar (front overhang, suspension). A car with less clearance needs `RampDriveTests` re-run.
- Rollback: revert. Worlds stay loadable, and placed ramps then mesh as cubes with no catalog HP until ramps return (ADR-020).
