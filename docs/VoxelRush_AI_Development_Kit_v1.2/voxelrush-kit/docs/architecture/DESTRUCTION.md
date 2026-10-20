# Hybrid destruction specification
## Policy
Sandbox editable; race preparation authorized editing; active race only explicitly destructible match overlay; spawn/checkpoints/protected infrastructure immutable. Damage checks authenticated actor, world zone, block material, range, collision provenance, operation id, rate limit and expected revision.
## Sources
Collision: authoritative relative normal velocity and effective mass -> energy = .5*m*v^2; threshold, scale and clamp -> quantized integer HP damage. Mining: server validates tool/range. Explosion: server computes bounded radius falloff and max block budget. Structural collapse: generated only by authoritative support service.
## HP
Sparse per-chunk damage map; initial HP from material catalog. Destroy at HP <=0, replace with Air; update chunk and neighbor dirty flags; enqueue support checks; replicate revision. Repeated physics contacts deduplicated by actor/target/contact window.
## Structure
Natural terrain exempt from mass collapse. Player-built components connected to anchors; bounded BFS on deletion, incremental work queue. Initial caps: 512 checks/tick, 256 removed blocks/collapse, 4 active jobs/world (tunable). Until resolution, mark affected region blocked/non-interactive; avoid unbounded cascades. Union-find alone insufficient for deletions.
## Cosmetic debris
Client local pooled particles/meshes; no per-block synchronized Rigidbody. Explosion effects event distinct from authoritative voxel batch.
## Race isolation
Active race loads locked track revision, writes destructible edits to match overlay, resets on match end; never persist overlay to sandbox base. Race result only after server checkpoint verification.
## Failure handling
If persistence unavailable, throttle/reject new persistent sandbox edits rather than acknowledging unsaved changes. Duplicate operation ID idempotent. Chunk version mismatch triggers snapshot request.
## Tests
Protected region, repeated contact, concurrent hits, collision across chunk border, explosion bounds, unsupported bridge, late join, race overlay reset, server crash mid-commit, client malicious damage amount.
