# Quality gate report (VR-082)

Evidence for each gate in QUALITY_GATES.md, as of 2026-10-21. "Pass" means the evidence was produced by running the named test/script; "Blocked" means it needs something not available here (devices, store/signing accounts); "Owner" means it needs a human decision. **Sign-off is not given in this report**: it is for the project owners after reviewing it.

## Every PR
| Item | Status | Evidence |
|---|---|---|
| One ticket per PR | Pass, with notes | Stacked PRs per task; cross-cutting fixes were committed to the task that introduced the bug and merged forward (e.g. VR-012 spawn, DES-004 collision commit, VR-062 abuse cap), recorded in the task files. |
| Compiles, unit tests | Pass | CI domain/EditMode/PlayMode per PR; local totals at VR-082: domain 430, EditMode 502, PlayMode 26, API 27. |
| No new unapproved package | Owner | New: NGO/Transport (ADR-014), Burst/Collections/Mathematics (VR-070), backend packages (ADR-018). Inventory: `docs/ASSET_LICENSES.md`. |
| Tests recorded | Pass | `tasks/*.md` "Verification commands and actual results". |
| No leaked secrets | Pass | gitleaks over full history (CI step, `.gitleaks.toml`); secrets only via CI secrets/environment. **Action for owners:** a local clone's `.git/config` remote URL holds a personal access token (not in the repository) — rotate it and use `gh auth setup-git`. |
| Code review | Owner | PRs open for review. |
| Profiler evidence for hot paths | Pass | `docs/perf/VR-013-*` (tick), `VR-070-meshing-profile.json`, `VR-072-bandwidth-loss.json`; VR-082 stress tick p95. |
| ADR when contracts change | Pass | ADR-012…018. |

## M1 network driving
| Item | Status | Evidence |
|---|---|---|
| 2 clients + Linux dedicated server | Pass | `tools/smoke-join.sh`, `smoke-drive.sh` (Docker in CI). |
| Responsive steering at 100 ms RTT | Pass (bots) | `smoke-drive.sh`: mean prediction error ≈ 0.02–0.04 m, 0 snaps. Feel on device: not tested. |
| Authoritative under speed hack | Pass | `smoke-drive.sh` cheat bot: malformed inputs rejected, top speed respected. |
| Remote interpolation | Pass | Remote snapshot stats in `smoke-drive.sh`; adaptive delay (VR-072). |
| Disconnect/rejoin | Pass | VR-062 race reconnect; VR-080 reconnect with a fresh ticket. |
| 8-player load | Pass | `tools/load-test.sh` (0 overruns, p95 < 2 ms on the dev machine). |

## Voxel
| Item | Status | Evidence |
|---|---|---|
| Same seed/version → same canonical hashes | Pass | VR-021 golden hashes (arm64, x86-64, Unity Mono, CI). |
| Negative coordinates | Pass | VR-020 boundary tests. |
| Edit at a chunk border rebuilds the neighbour | Pass | DES-012 `MarkChanged` border tests; mesher border culling (VR-022). |
| Chunk unload/reload matches snapshot | Pass | VR-032 snapshot/journal recovery, VR-033 restart hashes. |
| No per-block objects | Pass | VR-022 PlayMode; DES-005 debris pool. |

## Destruction
| Item | Status | Evidence |
|---|---|---|
| Simultaneous hit → single outcome | Pass | VR-031 serialized revision chain (4 actors × 4,000 intents); DES-003 concurrent hits. |
| Protected zones untouched | Pass | DES-002, DES-004 blast tests. |
| Race overlay resets | Pass | VR-061 (incl. restart). |
| Explosion capped | Pass | DES-004 256-block fixture. |
| Structural queue bounded | Pass | DES-011 stress. |
| No acknowledged edit lost on crash | Pass | VR-032 SIGKILL test (12 kills, ~196k acks); **VR-082 networked**: 8 players, server SIGKILL mid-destruction, every acknowledged revision recovered (3 runs). |
| Late join identical chunk hash | Pass | VR-033, VR-082 stress. |

## Performance
| Item | Status | Evidence |
|---|---|---|
| Device-tier benchmark, Android & iOS (30/60 fps, p95/p99, memory, GC, thermal) | **Blocked** | No devices. Harness and procedure ready: VR-071, `docs/perf/VR-071-device-benchmark.md`. |
| Server 30 Hz at 8 players | Pass | VR-013 load test; VR-082 destruction stress (p95 ≈ 1.2–1.3 ms, 0 overruns). |
| Bandwidth | Pass | VR-072: ≤ 8.4 KB/s per client in a race pack; interest management −41% snapshot payload. |
| Mesh backlog | Partial | VR-070 meshing cost; client render streaming not built yet. |

## CI
| Item | Status | Evidence |
|---|---|---|
| Domain tests on Linux | Pass | `ci.yml` domain job. |
| EditMode, PlayMode | Pass | `ci.yml` Unity job. |
| Headless Linux server build | Pass | `ci.yml`. |
| Android build | Pass | `ci.yml`; signed release path verified locally (VR-081). |
| iOS build and **sign** on macOS | Build: Pass; Sign: **Blocked** | Unsigned compile on main; signing needs Apple certificates/profile (VR-081). |
| Release gated on smoke tests and asset license audit | Pass / Owner | Smokes in CI; license inventory `docs/ASSET_LICENSES.md` needs owner review. |

## Security
| Item | Status | Evidence |
|---|---|---|
| Fuzz malformed payloads | Pass | VR-082 `DecoderFuzzTests` (9 decoders) and `EditWireFuzzTests`; **two bugs found and fixed** (edit intent decoder threw on short input; world-info/snapshot decoders threw unhandled exception types). |
| Rate limits | Pass | VR-031 per-actor edit rate; API auth/player limits (VR-080). |
| Replayed operation ID | Pass | VR-031 idempotency; VR-032 re-seeding after recovery. |
| Unauthorized zones | Pass | DES-002; VR-061 locked track. |
| Expired tickets | Pass | VR-080 (domain, API, end-to-end). |
| Spoofed actor | Pass | VR-012 sender checks; VR-031 server-side pose; tickets bind player id. |
| Client-provided damage | Pass (no path) | No message carries damage; DES-003 uses server physics only. |
| Storage fault injection | Pass | VR-082 `StorageFaultTests` (disk full, fsync failure, torn write); VR-032 crash test. |

## Release
| Item | Status | Evidence |
|---|---|---|
| Internal prototype → closed alpha → staged beta → production | **Blocked** | Needs Firebase project, signing keys, Apple membership, devices (VR-081 runbook). |
| Platform policies / store requirements | Owner | To verify at release time. |

## Summary
All gates that can be evidenced in this environment pass. Blocked: device-tier benchmark, iOS signing, distribution/staged release. Owner decisions: package approval, code review, license review, sign-off.
