# Task: SAVE-005 — World lease/fencing, idle shutdown and cold restore

## Goal
Exactly one server writes a world; servers can come and go without losing it. Acceptance (BACKLOG): concurrent allocation test, saved world survives shutdown.

## Prerequisites / relevant docs
VR-080 (API, tickets), VR-032 (journal), SAVE-003 (snapshot store). SAVE_GAME_CLOUD_PERSISTENCE (world lifecycle: idle period, commit, snapshot, stop; on join acquire per-world lease, restore, validate; "avoid two authoritative servers writing the same world: lease/fencing token required"), ADR-010 (single writer).

## In scope / out of scope
In: migration `006_world_leases.sql`; `LeaseStore` (atomic acquire: none/released/expired → new holder with fencing + 1, live holder re-acquire keeps its term; renew/release need holder + fencing; an expired lease is lost even if nobody took it); internal lease endpoints; snapshot writes require the live lease (`X-Lease-Holder`/`X-Lease-Fencing`); `LeaseClient` (acquire at startup, background renewals at TTL/3, `Lost` when refused or expired while the API was unreachable, release on shutdown); `ServerHost` (ticketed servers with API access take the lease before touching the world: exit 4 if held, fail closed (3) if the API is unreachable; exit 5 on a lost lease; `-idleShutdownSeconds`: flush the journal, release, exit 0).
Out: allocating servers on demand (orchestration), restoring a world on a different machine from object storage (SAVE-003 restore path), fencing on reward grants (player-scoped and idempotent already).

## Tests
- API (`LeaseTests`): **20 concurrent acquisitions → exactly one winner** (losers see the holder); takeover after expiry raises fencing and the old holder can neither renew nor write snapshots; renew extends, an expired lease is lost and re-acquiring is a new term; release lets the next server in, idempotent re-acquire keeps the term; bad holder/TTL bounds, unknown worlds, player tokens refused. Snapshot tests now hold a lease.
- Domain: `-idleShutdownSeconds` parsing.
- **End to end** `tools/smoke-lease.sh native` (Docker mode in CI): server A takes the lease (fencing 1); server B for the same world exits 4 (`HeldByOther`); a player builds 12 edits on A and leaves; A idle-shuts down after 6 s, releases (204) and exits 0; B starts on the saved world with fencing 2; a new player's chunk hashes equal A's.

## Verification commands and actual results (2026-10-21, macOS arm64)
| Command | Result |
|---|---|
| `dotnet test backend/VoxelRush.Api.Tests` | Passed 79/79 |
| `tools/test.sh domain` / `editmode` | Passed 438/438 / 509/509 |
| `tools/smoke-lease.sh native` | **LEASE SMOKE PASS** |
| `tools/smoke-progression.sh native`, `tools/smoke-auth.sh native` | PASS |

## Definition of done
- [x] Concurrent allocation test; saved world survives shutdown and handover.

## PR summary / risks / rollback
- Risks: a server that can't reach the API keeps serving until its last confirmed lease expiry (≤ 30 s), then stops; clock skew between API instances matters (single API instance for now).
- Rollback: revert; drop `world_leases` (snapshot writes then no longer need a lease).
