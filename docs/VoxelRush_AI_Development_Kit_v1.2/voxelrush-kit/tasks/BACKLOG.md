# Executable milestone backlog
Tasks are ordered; each must ship code + tests + demo evidence. Estimates are planning ranges, not promises.

## M0 Foundation (week 1–2)
VR-001 Unity URP project, platform settings, asmdefs, pinned editor/packages; DoD editor opens + Android smoke scene.
VR-002 Linux dedicated server build, Docker image and local client connection; DoD two clients join.
VR-003 Domain CI + Unity test runners + GitHub Actions; DoD green test pipeline.

## M1 Networked vehicle (weeks 3–6)
VR-010 Touch Input System action maps; DoD steer+gas simultaneous, landscape safe area.
VR-011 Arcade Rigidbody vehicle with suspension, drift and boost; DoD stable over static track.
VR-012 NGO connection, input sequencing, server authority, prediction/reconciliation; DoD two cars under 100ms RTT.
VR-013 8-player headless load fixture, telemetry; DoD recorded 30Hz tick metrics.

## M2 Voxel world (weeks 7–11)
VR-020 Chunk coordinates/data, negative coordinate tests; DoD boundary tests pass.
VR-021 Seeded terrain generator + golden hashes; DoD reproducible.
VR-022 Cull/greedy meshing + atlas; DoD no per-voxel GameObjects.
VR-023 Streaming, collider revision and budget; DoD driving never enters unready chunk.

## M3 Building/persistence (weeks 12–16)
VR-030 Grid targeting and ghost UI; DoD invalid placement feedback.
VR-031 Authoritative edit validation and per-chunk revision; DoD concurrent edits serialized.
VR-032 Durable journal and snapshot/recovery; DoD crash recovery no lost acknowledged edits.
VR-033 Join sync and delta gap recovery; DoD late join chunk hash matches.

## M4 Destruction (weeks 17–19)
DES-001 Material catalog + HP data + sparse damage; DoD material tests.
DES-002 Zone policy and protected structures; DoD unauthorized edits rejected.
DES-003 Vehicle collision damage and contact dedup; DoD one impact one damage window.
DES-004 Bounded explosion and chunk mutation batching; DoD 256-block fixture consistent.
DES-005 Debris VFX pooling; DoD no networked debris rigidbodies.

## M5 Structural (weeks 20–23)
DES-010 Player-built anchor graph; DoD disconnected component detection.
DES-011 Incremental bounded collapse scheduler; DoD queue caps under stress.
DES-012 Mesh/collider rebuild priority under destruction; DoD no stale collision.

## M6 Race (weeks 24–27)
VR-060 Server race state and ordered checkpoint validation; DoD spoofed finish rejected.
VR-061 Track revision lock and match-scoped destruction overlay; DoD sandbox unchanged after race.
VR-062 Lobby, race HUD, results and reconnect; DoD 8-player race flow.

## M7 Optimization (weeks 28–31)
VR-070 Burst/job meshing and main-thread budgets; DoD profile captured.
VR-071 Android/iOS device quality presets, thermal soak; DoD target tier benchmark.
VR-072 Network bandwidth/interest optimization and loss testing; DoD stable at 2% loss fixture.

## M8 Beta (weeks 32–36)
VR-080 API accounts, join tickets, PostgreSQL metadata, R2-compatible snapshots; DoD auth/security tests.
VR-081 CI signing, staged release, monitoring and rollback drill; DoD internal beta installs.
VR-082 8-player destruction stress, recovery/security audit; DoD all quality gates signed off.

## Agent task template
Task ID; objective; prerequisites; files allowed; interfaces; edge cases; unit/integration tests; performance/security tests; acceptance criteria; commands actually run; PR risk/rollback. Never implement unrelated tasks as a side effect.


## Persistent saves, worlds and economy (v1.2)
- SAVE-001 [P0] Define schema/contract for player progression, world metadata, chunk revisions and operation IDs. Acceptance: migrations and contract tests.
- SAVE-002 [P0] Implement append-only durable mutation journal and idempotent write path. Acceptance: crash-before/after-ACK tests.
- SAVE-003 [P0] Implement compressed chunk snapshot creation, checksum, index and journal replay. Acceptance: corrupted snapshot fallback.
- SAVE-004 [P0] Implement world create/list/join/visibility/invite permissions. Acceptance: ACL and authorization tests.
- SAVE-005 [P0] Implement world lease/fencing, idle shutdown and cold restore. Acceptance: concurrent allocation test, saved world survives shutdown.
- SAVE-006 [P0] Implement cross-device player progression/inventory/entitlements sync. Acceptance: Android/iOS session continuity and no double grant.
- SAVE-007 [P0] Separate ephemeral race destruction from persistent sandbox edits. Acceptance: match reset never changes saved world.
- SAVE-008 [P1] Implement local settings and disposable chunk cache with version checks. Acceptance: stale cache invalidation.
- SAVE-009 [P1] Implement backups, restore audit, quotas and storage cost metrics. Acceptance: recovery drill and quota enforcement.
- ECON-001 [P1] Implement cosmetic-only catalog and backend entitlement ledger. Acceptance: free core gameplay, cross-device entitlement.
- ECON-002 [P2] Implement verified IAP and optional rewarded ads after privacy review. Acceptance: duplicate/revoked purchase and reward replay tests.
