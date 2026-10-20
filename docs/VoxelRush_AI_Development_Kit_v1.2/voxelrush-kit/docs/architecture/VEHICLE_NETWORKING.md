# Vehicle physics and multiplayer
## Vehicle
Rigidbody arcade controller, four suspension raycasts, spring-damper, steering curve by speed, lateral friction and controlled drift, boost cooldown/energy, airborne orientation assist. Separate physics inputs and visuals. Server fixed tick 30Hz.
## Prediction
Client sends sequence-numbered steer/throttle/brake/boost inputs with tick; predicts locally. Server validates bounds, applies to authoritative Rigidbody, publishes state(position, rotation, velocity, angular velocity, last processed input). Client replays unacknowledged inputs after reconciliation. PhysX not deterministic across platforms: blend small errors, hard-correct severe errors; special collision handling.
## Remote interpolation
Buffer server snapshots (~100ms adjustable) and interpolate remote vehicles. Handle packet loss and teleport/resets. Do not trust client positions.
## Interest management
Replicate nearby vehicles and chunk revisions based on location and velocity. Baseline 8 players, evaluate bandwidth under 100ms RTT, 2% packet loss and 150ms jitter.
## Server join
HTTPS obtains short-lived signed room ticket; dedicated server verifies signature, expiry, audience, nonce and room membership. Never embed backend secret in client.
## Security
Validate movement input magnitude, tick windows, rate limits; reject stale/replayed messages; authoritative checkpoint crossing and lap times.
## Tests
2/4/8 players, 100/200ms RTT, loss, jitter, collision reconciliation, boost cheat, out-of-order inputs, reconnection, late join, simultaneous wall collision.
