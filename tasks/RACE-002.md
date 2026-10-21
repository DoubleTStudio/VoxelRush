# Task: RACE-002 — Ready-up lobby so phone players can start races

## Goal
Races start when the players are ready, not only through the server's `-raceAutoStart N` flag. Players who joined mid-race can enter the next round.

## Changes
- **`Core/Race/RaceLobby`** (engine-free): a ready set; `ShouldStart` is true once every connected racer is ready (at least one). **`RaceLobbyMessage`** carries the ready and racer counts in 2 bytes and rejects malformed payloads.
- **`RaceServer`:**
  - handles `vr/race-ready` (1 byte, Waiting phase only). A connected spectator who readies is added as a racer.
  - starts the countdown on auto-start or ready-up, puts every racer on the grid, and clears the ready set.
  - broadcasts `vr/race-lobby` on every change and sends it to each joiner. A leaver's ready state is dropped.
- **`RaceClient`:** `Lobby`, `WantsReady` (cleared at the countdown) and `SetReady`.
- **`RaceHud`:** a "Ready to race N/M" / "Not ready" button while waiting and not on the join screen.
- **Bots:** `-botReady N` readies once N racers are connected. `RACE_READY=1 tools/smoke-race.sh` runs the race smoke without server auto-start.

## Verification
- `dotnet test tests/VoxelRush.Domain.Tests`: 469/469 (4 new `RaceLobbyTests`). PlayMode 36/36. EditMode 548/548.
- `tools/smoke-race.sh native`: PASS (auto-start). `RACE_READY=1 tools/smoke-race.sh native`: PASS. All 8 bots readied, `race_countdown ... readyUp=true` with 8 racers, all finished, and the reconnect resumed.

## Risks / rollback
A new client → server message, validated (length, value, phase, known sender). Auto-start behaviour is unchanged. Rollback: revert.
