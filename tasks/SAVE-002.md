# Task: SAVE-002 — Append-only durable mutation journal and idempotent write path

## Goal
Every acknowledged edit survives a crash, and retries never apply twice. Acceptance (BACKLOG): crash-before/after-ACK tests.

## Delivered by
- **VR-032**: CRC-framed append-only journal, background group commit with fsync before ACK, bounded queue (backpressure), torn-tail recovery, verified snapshots, idempotency re-seeded from recovered records. `tools/crash-test.sh` (SIGKILL loop, ~196k acks, none lost).
- **VR-031**: operation ids are idempotent (durable results returned again; transient refusals retryable).
- **VR-082**: networked 8-player destruction stress with server SIGKILL; storage fault injection.

## Added here (`Tests/Domain/Security/CrashAckTests.cs`)
- **Crash after ACK**: the edit is durable, the process dies, the client retries the same operation after restart → original result (`IsDuplicate`), revision unchanged, one journal record.
- **Crash before ACK**: fsync fails (nothing durable, no ACK), restart → the change is absent; the client's retry applies once; a second retry is a duplicate; the journal holds the operation exactly once.

## Verification (2026-10-21)
| Command | Result |
|---|---|
| `tools/test.sh domain` | Passed 432/432 |

## Definition of done
- [x] Crash-before/after-ACK tests.
