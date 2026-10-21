# ADR-020: Ramp blocks carry their orientation in the block id

Status: Accepted (BUILD-002). Refines VOXEL_ENGINE (byte block ids, "special ramps use mesh + collider metadata, not simple cubes") and CONTRACTS (`EditIntent.orientation`).

## Context
The MVP needs 3 ramp variants that players place in 4 orientations, rendered, collided identically on the server and on predicting clients, persisted, synced and destructible. Chunks store one byte per block. Edits, deltas, snapshots, the journal and content hashes all carry that byte and nothing else.

## Decision
- **Ids 13..24 are ramps:** `id = 13 + variant * 4 + yaw`. Variants: low ramp (0 → 0.5 m), high ramp (0.5 → 1 m) and kicker (0 → 0.625 m), each one block long. Yaw is the uphill direction in quarter turns about +Y (0 = +Z, 1 = +X, 2 = -Z, 3 = -X). Ids 0..12 are unchanged. `BlockShapes` (World, engine-free) maps id ↔ shape, yaw and rotation.
- Low ramp followed by high ramp makes a two-block 1:2 ramp up onto a block. The kicker is a jump lip. A 1:1 one-block slope was rejected because the ArcadeCar's front overhang hits it, and 3:4 bleeds about half the car's speed into the suspension (`RampDriveTests`).
- **Geometry lives in one table** (`BlockGeometry`). Both the managed mesher (client render) and the Burst mesher (server and client colliders) use it, so their output stays bit-identical (tested). Each face of a block has a profile: none, full, or a partial shape. Full faces still go through the greedy merge. A neighbour face is hidden only by a full face, or by an identical partial face across the border (for example, the matching triangle of the same ramp at the same yaw, or the half-height ends where a low ramp meets a high ramp). Slopes and partial faces are emitted per cell after the greedy pass.
- **Validation:** placing an unknown id (≥ 25) or a protected block is now refused (`invalid_block_type`). Before, only air was refused. The `EditIntent.orientation` byte stays on the wire, reserved and ignored. `Rotate` stays unsupported. Turning a ramp means placing a different id.
- **Wire protocol 5 → 6:** no message layout changes. Older clients would mesh ids 13..24 as cubes, so their predicted collisions would disagree with the server, and they are refused with `protocol_mismatch`.
- No persisted format or generator version changes: journal record v1, snapshot schema 1 and the generators are untouched. Ramp ids are ordinary bytes in them.

## Alternatives
- Separate orientation storage per block: a second per-block array or sparse map in every chunk, snapshot, delta and hash, which means a schema migration for a 2-bit value.
- Server-side `Rotate` of an existing ramp: needs per-block orientation state, gives little benefit over remove + place, and adds a second way to change a block.
- Authored ramp meshes and separate colliders: two sources of truth that could drift between render, server and client prediction.

## Consequences
- 12 of the 243 remaining byte ids are used. Further shaped blocks follow the same pattern.
- Unknown ids still mesh as cubes, so a newer world opened by an older build degrades to cubes instead of failing.
- Rollback: reverting the code keeps worlds loadable. Placed ramps become cube-shaped blocks without catalog HP (indestructible by damage, still removable by players), until ramps return.
