# Protocol contracts v1 (design, not generated code)
All wire messages: protocolVersion:uint16, sessionId, worldId, messageId, serverTick (where applicable). Use stable versioned serialization; recommend protobuf or vetted NGO serialization, not JSON in high-frequency transport.

ClientInput {sequence:uint32, clientTick:uint32, steering:int16[-1000,1000], throttle:uint16[0,1000], brake:uint16[0,1000], flags:uint8}; sent 30Hz unreliable sequenced.
VehicleSnapshot {serverTick, entityId, position, rotation, linearVelocity, angularVelocity, lastProcessedInput}; 10–20Hz interest-filtered.
EditIntent {operationId:uuid, chunkCoord, localIndex:uint16[0,4095], expectedRevision:uint64, action:Place|Remove|Rotate, blockType, orientation}; reliable.
MineIntent {operationId, target, toolId}; no trusted damage amount.
ChunkMutationBatch {worldId, chunkCoord, baseRevision:uint64, newRevision:uint64, operationId, changes:[{index, blockType, hp}]}; reliable ordered per chunk.
ChunkSnapshot {worldId, chunkCoord, revision, generatorVersion, schemaVersion, contentHash, compressedPayload}; reliable segmented.
ExplosionEffect {eventId, position, radiusVisual, effectSeed}; unreliable cosmetic, never authoritative block changes.
RaceState {raceId, trackRevision, state, serverStartTick, orderedCheckpointIds}; reliable.

Rules: client applies delta only if baseRevision==localRevision; otherwise buffer/request missing deltas or snapshot. Operation IDs idempotent; chunk indices bounds checked. Multi-chunk changes share operation ID and atomic visibility barrier. Network payload limits enforced. Negative world coordinate division must use floor semantics.
