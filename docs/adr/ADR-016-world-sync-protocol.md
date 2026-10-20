# ADR-016: Voxel world sync protocol and the first networked build plot

Status: Accepted (VR-031..VR-033). Implements CONTRACTS (EditIntent, ChunkMutationBatch, ChunkSnapshot) and ADR-007.

## Decision
- **Messages** (NGO named messages, length-prefixed, ≤ 64 KiB, decoded with bounds checks):
  `vr/world-info` (server→client: world id, generator kind, seed, generator/schema version, region), `vr/chunk-snapshot` (edits-only snapshot + revision + content hash; ReliableFragmentedSequenced), `vr/chunk-delta` (ChunkMutationBatch in the journal record encoding; ReliableSequenced), `vr/snapshot-request` (client→server; ≤ 8/s per client), `vr/edit` (EditIntent), `vr/edit-result` (operation id, rejection code, new revision).
- **Commit protocol:** edits are validated and applied on the server tick by `WorldAuthority` (single writer, server-side actor pose from the player's car), appended to the journal by the background writer, and **only after the record is durable** broadcast as a delta and acknowledged to the actor. Retries of durable operations are acknowledged again (idempotent).
- **Client replica:** applies a delta only when `baseRevision == local`; older deltas are duplicates; newer ones are buffered and a snapshot requested; snapshots are verified by content hash (clients regenerate the base from the pinned generator and refuse unknown generator versions).
- **Joins:** world info, then snapshots of the region, ≤ 4 chunks per client per tick.
- **First networked world:** a flat build plot (`FlatPlotGenerator` v1) covering the spawn row, chunks (−3,−2,−5)..(2,0,−2), persisted under `-worldDir` (default `$HOME/voxelrush-world`; in Docker mount a volume). Data-only for now: no server colliders or client rendering of the plot yet (follow-up together with the arena replacement and VR-023 streamer integration).

## Alternatives
- NetworkVariables/NetworkList per chunk: no revision/gap semantics, no hash verification, poor fit for sparse edits.
- Sending full 4096-byte chunks: unnecessary when the base is deterministic.

## Consequences
- Protocol changes need `SimulationConstants.ProtocolVersion` bumps; the journal record encoding is shared with the delta message.

## Migration / rollback
Revert; the world directory is versioned (journal record v1, snapshot schema 1).
