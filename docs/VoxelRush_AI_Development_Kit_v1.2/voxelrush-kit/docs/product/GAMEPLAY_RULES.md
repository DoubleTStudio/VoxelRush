# Gameplay rules and state machines
## Zones
Sandbox: permitted players build/destroy editable blocks; protected spawn remains immutable.
RacePreparation: owner/editor may build; publish immutable track revision for race.
ActiveRace: no terrain editing except pre-tagged race-scoped destructibles; checkpoint/track-critical objects immutable.
Protected: no building, damage or vehicle collisions causing destructive changes.
## Race states
Waiting → Countdown(3s) → Racing → Finishing → Results → Reset. Server exclusively changes state. On countdown lock a track revision; during race use an isolated match overlay for permitted destruction, discard overlay after results.
## Checkpoints
Server checks ordered crossing of gate planes, minimum progression time, position within gate bounds, respawn history. Cannot finish without all gates in order.
## Building
Raycast grid targeting; place/remove/rotate; server checks player reach (<=6m default), inventory, permission, protected zone, overlap, per-user rate (e.g. 10 edits/s configurable), chunk loaded and expected revision. No edit accepted if it would trap a player in solid voxel; policy configurable.
## Damage
Material HP: dirt30, grass35, wood50, asphalt100, stone120, reinforced300, protected infinite. Quantize damage to integer units. Collision based on normal impact energy with configurable mass/threshold/scaling. Coalesce duplicate contacts per impact window. Mining removes authorized blocks through server action.
## Structural
Natural terrain exempt from global collapse. Player-built supports form bounded connectivity components; disconnected structures collapse in scheduled batches. No individual networked debris rigidbodies.
## Vehicles
Arcade acceleration, speed-based steering, drifting, boost, jump assist, reset when stuck; initial car max speed tunable, no fixed numerical promise. Car HP optional in M1; vehicle damage in destruction milestone.
## Multiplayer abuse
No arbitrary remote edits, no untrusted actor IDs, enforce room membership and protected regions. Server may disconnect repeated malformed/rate-limit violating clients.
