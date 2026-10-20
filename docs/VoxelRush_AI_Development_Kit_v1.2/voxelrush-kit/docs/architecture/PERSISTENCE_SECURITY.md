# Persistence and security
## Records
World(worldId, seed, generatorVersion, schemaVersion, owner, bounds, status); ChunkManifest(worldId, coord, revision, snapshotKey, snapshotHash, journalOffset); Mutation(operationId, actorId, worldId, chunk, baseRevision, newRevision, tick, kind, payload, timestamp); ZonePolicy; Room; Player.
## Commit protocol
Validate action → compute candidate changes → assign world-scoped operation ID + ordered per-chunk revisions → durably append journal / transactional metadata → publish committed mutations and acknowledge. Never acknowledge durability before durable journal commit. Multi-chunk explosion uses operation manifest and atomic visibility/recovery semantics; partial commits invisible to clients.
## Snapshotting
Background compress snapshots (e.g. LZ4/Zstd after benchmarking), hash content, upload to object store, atomically publish manifest reference, prune journal only after verified snapshot. Restore snapshot then replay later committed journal entries idempotently.
## Concurrency
Single authoritative simulation writer per world instance; queue external requests, deterministic order. Async persistence with backpressure and durable acknowledgement; reject/slow edits on write backlog. Race overlays ephemeral and excluded from persistent journal.
## Threat model
Forged damage, replayed edit, unauthorized region changes, malformed chunk index, path traversal in storage keys, join-ticket replay, denial-of-service by explosion, tampered generator version, client speed hacks, race finish spoofing.
## Controls
Auth and authorization on every intent; bounded payloads and quotas; signed short-lived tickets, per-session nonce, TLS HTTPS, rate limits, schema validation, storage credentials only server side, server tick replay windows, secure logs without secrets.
## Privacy
Minimize player data; define retention and deletion; parental/child safety and regional privacy compliance review before public release.
