# Voxel engine design
## Data model
16×16×16 chunks; byte block ID for first 255 materials; sparse ushort HP for damaged blocks; chunk coordinate int32 XYZ; local index x + 16*(z+16*y). Separate immutable base procedural blocks from sparse edits. Chunk revision ulong, generatorVersion int, schemaVersion int.
## Deterministic generation
World seed + chunk coordinates + pinned generator version; stable integer hash/PRNG. Heightmap first, biome + caves later. Never assume floating point noise exactly matches across CPU architectures: canonical hashes/tests; server can send snapshots if mismatch.
## Chunk lifecycle
Unloaded → Requested → BaseGenerated → DeltasApplied → MeshPending → Ready → UnloadPending. Client streaming priorities: under vehicle, ahead of travel direction, nearby player edits, visible horizon. Server collision-relevant chunks must be ready before entry.
## Meshing
Cull hidden faces, greedy mesh compatible faces, texture atlas/material batching. Async Jobs/Burst compute buffers, main-thread mesh upload bounded. Dirty neighbor on edge edit. Special ramps/boosts use authored mesh+collider metadata, not simple cubes.
## Collision
Generate colliders only around players and gameplay critical regions. Server collider revision gates simulation through changed region. Client prediction reconciles on collider mismatch. Never leave stale invisible solid colliders.
## Memory
4,096 bytes raw block IDs per chunk before damage/mesh/metadata; chunk cache capped by device tier. No per-block GameObject. Pools for meshes and temporary buffers.
## Tests
Seed reproducibility across server/client, negative coordinates and floor division, boundaries, greedy mesh face count, dirty neighbor, unload/reload, delta replay, collider revision and corrupted snapshot recovery.
