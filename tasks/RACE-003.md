# Task: RACE-003 — Checkpoint visuals, direction aid, speed HUD and haptics

## Goal
Phone players can see the checkpoints and where to go. The client already receives the track (`RaceClient.Track`), but nothing drew it. The HUD lacked speed. CONTROLS_ASSETS (Accessibility) asks for colour-blind-safe checkpoint markers and a vibration toggle.

## Changes
- **`Core/Race/RaceGuide`** (engine-free):
  - gate markers (`Next` / `Passed` / `Upcoming`, finish = last gate) from an `IRaceView`, lap-aware, and neutral outside Countdown/Racing/Finishing;
  - the aim point inside the next gate (nearest lane, 20% inside the posts), its distance, and its bearing relative to a heading;
  - cues from progress changes (`CheckpointPassed`, `Finished`, `NotCounted`) and a client-side `WrongGate` check (forwards through another gate, or backwards through the next one);
  - km/h and distance formatting.
- **`Presentation/World/CheckpointView`** (on `NetworkWorld`): two posts and a banner per gate, sized from the gate's width, height and normal, with no colliders. It rebuilds when the track changes and re-marks gates on `RaceClient.Changed`. States differ in shape, pattern and brightness, not only hue:
  - next: bright, wider posts, a scrolling and pulsing chevron banner, and a bobbing V marker above it;
  - upcoming: mid-brightness posts with a plain banner;
  - passed: dark posts lowered to 30%, no banner;
  - finish: a checkered banner.
- **`Presentation/UI/RaceDashboard`** (on `NetworkWorld`): a top-left IMGUI panel inside the safe area. It shows:
  - speed in km/h (`LocalPlayer.Vehicle.ForwardSpeed`);
  - an arrow to the next checkpoint, relative to the camera's forward, with its distance in metres;
  - the race time, a local clock from GO that stops at the own finish (the official time stays in the results);
  - a "Wrong checkpoint" hint for 2 s. Moves over 20 m in a frame (grid placement, reset) are ignored.

  The panel stays clear of BUILD/HORN (top centre), the build status box, the RaceHud title, the pause button (top right) and the touch clusters (bottom). It also triggers the haptic cues.
- **`Presentation/Settings/Haptics`**: short vibrations per cue (40/90/120/300 ms), rate limited to one per 0.15 s.
  - Android: the system vibrator, falling back to `Handheld.Vibrate`.
  - iOS: `Handheld.Vibrate`.
  - Editor and batch mode: no-op, still counted for tests.
- **`LocalSettings.Vibration`** (default on), stored as `accessibility.vibration=on|off`. `FormatVersion` stays 1: older builds ignore the unknown key and keep reading the file. Files without the key load as on, and garbage values keep the default.
- **`GameMenu`**: a "Vibration: On/Off" button, saved with the other settings on close. The panel is 60 px taller to fit it.
- **`RaceClient`**: at a countdown, the own progress resets to 0 crossings. The server only resends progress on the next crossing, so the previous race's count used to highlight the wrong gate, and bots coasted as "finished" in later rounds.
- **`Client.unity`**: `CheckpointView` (fileID 1700000100, `_material` = `Art/Debris.mat`) and `RaceDashboard` (1700000101) on `NetworkWorld`.

## Not done
- Race position: the server state has no standings until Results, and adding them changes the protocol (left for a follow-up).
- Reduced-particle setting: not in this task's scope.
- No device run with people: the visuals, haptic feel and panel placement on notched phones are untested on hardware.

## Verification (2026-10-21, macOS arm64, after merging main with COSM-002)
| Command | Result |
|---|---|
| `tools/test.sh domain` | Passed 556/556 (new `RaceGuideTests`, vibration cases in `LocalSettingsTests`) |
| `tools/test.sh editmode` | Passed 665/665 (incl. `Client.unity` has exactly one `CheckpointView` and one `RaceDashboard`) |
| `tools/test.sh playmode` | Passed 54/54. New `RaceGuidanceTests`: gate markers, sizes, brightness order, no colliders; dashboard speed, arrow, distance, clock, cues, wrong gate, teleport ignored; haptics toggle and rate limit; scene wiring with the scene material |
| `tools/build.sh android --development` | Succeeded, development APK 78.5 MiB (COSM-002 assets included); `aapt dump permissions` lists `android.permission.VIBRATE` |

## Risks / rollback
- Client-only, apart from the `RaceClient` countdown reset (display state only, no messages).
- Android gains the `VIBRATE` permission, which Unity adds for `Handheld.Vibrate`.
- About 3 to 4 extra renderers per gate, with property blocks (no SRP batching for those).
- Rollback: revert. Settings files written with the new key stay readable by older builds.
