# ADR-017: Race flow on the dedicated server and client session ids

Status: Accepted (VR-060..VR-062). Extends ADR-014 (handshake) and ADR-016 (world sync).

## Decision
- **Protocol 2 handshake**: `protocolVersion (uint16 LE) + session id (16 bytes)`, exactly 18 bytes. A protocol-1 payload (2 bytes) decodes as a version mismatch, not malformed. The session id is generated per client process and reused on reconnect; the gate refuses a session that already holds a slot (`session_in_use`).
- **Server owns the race** (`RaceServer` over the engine-free `RaceAuthority`): lobby by session id, optional `-raceAutoStart N`, racers placed on the start grid at countdown, every racer's **server car** sampled each tick (previous → current position; resets and server spawns flagged as respawns), RaceState broadcast on every phase change, progress to the racer after each judged crossing, results broadcast at Results. Clients cannot claim anything.
- **Reconnect**: a racer who disconnects mid-race keeps progress for 30 s; reconnecting with the same session resumes it and the car spawns 4 m past the last validated gate (that spawn never counts as a crossing). Disconnected racers don't hold the race open.
- **Names**: players see display names (`P1`…); session ids never leave the server.
- Messages: `vr/race-track` (gate geometry), `vr/race-state` (CONTRACTS RaceState), `vr/race-progress`, `vr/race-results`; all ReliableSequenced, bounds-checked codecs in `Core/Race`.

## Alternatives
- NGO NetworkVariables for race state: no control over encoding/validation, harder to test engine-free.
- Trusting client-reported finishes or lap times: rejected (GAMEPLAY_RULES, VEHICLE_NETWORKING).

## Consequences / risks
- The session id is self-chosen, so whoever learns it can resume that racer. Acceptable for the test lobby; on servers started with `-worldId`, join tickets (ADR-018, VR-080) replace it with the API-issued player id.
- An unclean disconnect (crash) blocks the same session until the transport times the old connection out.

## Migration / rollback
Protocol bump: old clients are refused with `protocol_mismatch`. Rollback: revert and return to protocol 1.
