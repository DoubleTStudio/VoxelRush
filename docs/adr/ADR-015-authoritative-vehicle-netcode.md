# ADR-015: Authoritative vehicle netcode: tick loop, input protocol, prediction and reconciliation

Status: Accepted (VR-012). Implements ADR-002 and VEHICLE_NETWORKING for vehicles.

## Context
Cars must be server-authoritative (ADR-002) yet responsive on mobile at ~100 ms RTT. PhysX isn't deterministic across platforms, so the client predicts and the server corrects.

## Decision
- **Tick loop:** one 30 Hz `NetworkSimulation` per process (SimulationConstants.ServerTickRate). Physics runs in `SimulationMode.Script`: each tick, every car applies forces (`ArcadeVehicle.Simulate`), then `Physics.Simulate(1/30)`. Physics step = network tick (stability covered at 30 Hz and 60 Hz by VR-011 tests). Catch-up is capped at 4 ticks per frame.
- **Input (client → server):** each tick the owner records a sequenced `InputCommand` (CONTRACTS ClientInput, quantized) and sends its newest **3** commands in one **unreliable** owner-only RPC (redundancy covers loss). The server re-checks the sender against the owner.
- **Server queue (`ServerInputQueue`):** in-order by sequence; already-processed or queued sequences are dropped as harmless duplicates; rejects out-of-contract values (`Malformed`), sequences > 64 ahead, more than 2× tick rate new inputs per second, and overflow. One input is consumed per tick; when starved it repeats the last input for 6 ticks, then goes neutral. Clients exceeding 600 rejected commands are disconnected (`input_abuse`).
- **Snapshots (server → clients):** every 2 ticks (15 Hz), unreliable: tick, last processed input, pose, velocities, boost state. Stale/out-of-order snapshots are dropped.
- **Owner prediction:** simulates with the *quantized* input (exactly what the server decodes). On a snapshot it compares the authoritative state with its prediction for the acknowledged sequence: error ≤ 5 cm and ≤ 2° → nothing; < 3 m and < 45° → restore + replay unacknowledged inputs, then blend 50% toward the corrected pose; otherwise snap (restore + replay).
- **Remote cars:** kinematic; `SnapshotInterpolator` renders 120 ms behind the server timeline (smoothed offset), extrapolates ≤ 100 ms when starved, snaps on > 10 m jumps.
- **Spawning:** the server spawns one `NetworkedCar` (from `Resources/Network`) per connected player, owned by that player, at a free `Arena/SpawnPoints` slot; despawned on disconnect.
- **Network conditions for testing:** NGO 2.13's transport simulator is a no-op (deprecated in favour of the Multiplayer Tools package, a new dependency). Instead a dev-only `DelayLine` delays/drops our gameplay messages (`-simRttMs`, `-simJitterMs`, `-simLossPercent`; half the RTT each way), active only when `Debug.isDebugBuild`. It doesn't delay NGO's own handshake messages.

## Alternatives
- NGO `NetworkTransform` / client-authoritative movement: violates ADR-002.
- Lockstep/deterministic physics: PhysX is not cross-platform deterministic.
- Multiplayer Tools network simulator: extra dependency; Linux `tc netem`: CI-only, can't run on macOS dev machines.
- Reliable input: head-of-line blocking under loss; redundancy is cheaper.

## Consequences
- Input→ack latency includes up to one server tick and the snapshot interval (measured ~135–142 ms at 100 ms RTT).
- Replay re-simulates the whole physics scene per replayed tick; fine for one predicted car + kinematic remotes, revisit with VR-013 load data.
- `InputBatch.MaxCommands = 4` and the snapshot layout are protocol contracts; changes need `SimulationConstants.ProtocolVersion` bumps.

## Migration / rollback
Revert the VR-012 change set; no persisted data.
