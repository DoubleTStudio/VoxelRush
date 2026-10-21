# Task: AUDIO-001 — MVP sound effects

## Goal
Engine, collision, build and destruction sounds (CONTROLS_ASSETS minimum MVP assets) on the client, within the mobile voice and GC budget, with no audio assets to license.

## Changes
- **No audio assets.** `SfxSynth` (Presentation/Audio) synthesises every clip once at startup from fixed seeds: engine loop, boost loop, impact, place, remove, "nope" and crunch. Mono 22.05 kHz, about 3.2 s of samples in total (≈280 KB as float PCM). Loops crossfade their tail into their start so they wrap without a click. Nothing to record in an asset manifest: no file is imported, no third-party sound is used.
- **`GameAudio`** (Presentation/Audio, on `NetworkWorld` in `Client.unity`, fileID 1700000300):
  - **Engines:** one voice for the own car (mostly 2D, `spatialBlend` 0.2) and 4 for the nearest remote cars (3D, linear rolloff to 70 m). Voices stay with their car while it is still among the nearest, so the pitch doesn't jump between cars. `EngineSound` maps speed and throttle to pitch and volume. Pitch rises monotonically with speed and has no simulated gear changes. A boost layer fades in while boosting.
    - Own car: `ArcadeVehicle.ForwardSpeed`, applied throttle, predicted boost.
    - Remote cars: velocity derived from the interpolated pose, throttle estimated from forward acceleration, boost from the snapshot's flag.
  - **Impacts:** `ImpactDetector` compares velocity with a 0.1 s lagging reference. Braking, boost and drifting stay below the 4.5 m/s threshold; crashes and hard landings go above it. It works the same for predicted and interpolated cars.
    - Volume scales with the size of the change. Each car has a 0.3 s cooldown, and all cars together are limited to 4 hits/s with a burst of 2.
    - Jumps over 60 m/s (respawn, flip reset) don't count.
    - Remote snapshot holds (no movement for less than 0.15 s) and catch-up frames (over 50 m/s) are not treated as crashes.
  - **Build:** `PhoneBuilder.EditResolved` reports each verdict: a local pre-check refusal at once, otherwise the server's answer. An accepted place or remove plays its confirm sound; any refusal plays "nope".
  - **Destruction:** blocks a replica delta turns to air are queued (up to 32 per frame). At most 2 crunches per frame play, the nearest first, limited to 10/s, louder for bigger blasts, with pitch by material (glass and metal higher).
  - **Pooling:** 8 one-shot sources created once. A new sound takes a free source or steals the one closest to finishing, and at most 3 start per frame. With engines, at most 18 real voices (the project allows 32).
  - **Volume and pause:** loudness follows the effects volume, which GameMenu already sets as `AudioListener.volume`. At zero, nothing plays or is tracked. App pause, or focus loss on phones, sets `AudioListener.pause` and stops all voices. Velocity history restarts on resume, so coming back isn't heard as a crash.
- **COSM-002 horn:** the horn keeps its own spatial source per car. Like these effects, it follows the effects volume and pause through `AudioListener`. Remote boost for engines reads the same snapshot flag as the spark trails (`CarCosmetics.IsRemoteBoosting`).
- **Multicast-safe replica hook:** `DebrisEffects` now subscribes to `ClientWorldReplica.BlockChanged` with `+=`/`-=` instead of assigning it, so debris and audio both receive deltas, and detaching one leaves the other subscribed.
- **`ClientCars`** (Networking/Vehicles): an NGO-free registry of the cars this client shows. `VehicleNetworkDriver` adds the own (owner role) and remote cars at spawn and removes them at despawn. Nothing changes on the wire, and the server never fills it.
- `Presentation/AssemblyInfo.cs`: internals visible to PlayMode tests.

## Not done
- UI click sounds and music (`MusicVolume` stays unused).
- Tyre skid/drift and surface sounds.
- Engine sounds have no gear simulation, by design: the pitch only rises with speed.
- Remote throttle is estimated from acceleration, because inputs aren't replicated.
- Impacts come from velocity changes, not `Rigidbody` collision callbacks. Owner replay re-simulates physics, which would fire callbacks again for the same contact.
- Not listened to on a phone.

## Verification
Run on 2026-10-21, macOS arm64, after merging main (COSM-002):

| Command | Result |
|---|---|
| `tools/test.sh editmode` | Passed 628/628 |
| `tools/test.sh playmode` | Passed 68/68. New `GameAudioTests` (18): every clip non-silent, bounded, deterministic and of the expected length; loops wrap without a click; engine pitch monotonic in speed and following the own car; voice limit keeps the 4 nearest remote engines and sticks; impact detector ignores braking and boost, with cooldown and rate limit; remote crash and snapshot hold; build result → sound; blast crunches capped alongside debris (multicast hook); effects volume and pause mute; 60 steady-state frames allocate no GC memory (`Is.Not.AllocatingGCMemory`). The Client scene test checks that `GameAudio` sits next to `PhoneBuilder`. |
| `tools/build.sh android --development` | Built `build/android/VoxelRush.apk` |

Before the merge: EditMode 587/587, PlayMode 57/57, Android development build succeeded.
Not run: network smokes (no protocol change), and listening on a device.

## Risks / rollback
- **Remote impacts:** a snapshot gap longer than about 0.15 s, followed by a smooth catch-up, can be heard as a light impact. The global limit bounds how often that can happen.
- **Own-car impacts:** a rare server snap correction can also be heard as an impact.
- **Startup:** synthesis runs once when the scene loads (a few ms, ≈280 KB of clip memory).
- **Volume:** the effects volume is applied as the listener volume, so a future music track would need its own mixer group.
- **Scope:** client presentation only. Presentation stays compiled out of server builds, and there are no protocol or persistence changes.
- **Rollback:** revert. To silence the game without a revert, disable `GameAudio` on `NetworkWorld`.
