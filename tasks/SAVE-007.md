# Task: SAVE-007 — Separate ephemeral race destruction from persistent sandbox edits

## Goal
Races never change the saved world. Acceptance (BACKLOG): match reset never changes saved world.

## Delivered by VR-061 (`tasks/VR-061.md`, ADR-004)
- `MatchWorld`: at countdown the track region of the sandbox is locked at its current revisions and cloned into an overlay authority whose only sink is a publisher (no journal); race destruction (blasts, collisions, collapse) goes to the overlay; `End()` unlocks and verifies the sandbox track is byte-identical, then discards the overlay.
- Sandbox edits and server commits inside the locked track are refused (`race_locked`); the rest of the world keeps saving.
- Clients apply match deltas to a forked replica; their sandbox replica never sees race deltas.

## Evidence
`Tests/Domain/Destruction/RaceOverlayTests.cs` — **DoD**: after a race with an explosion, a collision kill and a collapse, the sandbox track hashes exactly as before the race, and **after a restart the journal holds no race records and every chunk reloads to the pre-race hash**; the next race starts from the same locked track; tampering is detected at `End()`.

## Definition of done
- [x] Match reset never changes the saved world (in memory, on clients and on disk after restart).
