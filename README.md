# VoxelRush

Cross-platform (Android/iOS) voxel racing, building and destruction game with an authoritative Linux dedicated server.
Specifications, ADRs and the backlog live in `docs/VoxelRush_AI_Development_Kit_v1.2/voxelrush-kit/` and are the source of truth; project-local decisions are in `docs/adr/`.

## Prerequisites
- Unity **6000.3.26f1** (pinned in `unity/ProjectSettings/ProjectVersion.txt`) with Android Build Support (SDK/NDK/OpenJDK), iOS Build Support, Linux Dedicated Server Build Support, Linux IL2CPP; Mac Dedicated Server Build Support for local server runs.
  Install via Hub: `unityhub://6000.3.26f1/de8167e7440c`
- .NET SDK 10 (`global.json`), Git LFS, Docker (server smoke test), Xcode (iOS archive).

## Layout
```
unity/                      Unity project
  Assets/VoxelRush/
    Runtime/Core            engine-free IDs, constants, contracts, hosting options, logging
    Runtime/World           engine-free chunks/generator (M2)
    Runtime/Destruction     engine-free damage/structural (M4-M5)
    Runtime/Gameplay        vehicles, races
    Runtime/Networking      transport/netcode (VR-002, VR-012)
    Runtime/Presentation    client-only (excluded from server builds)
    Runtime/Server          server-only (excluded from client builds)
    Editor                  project configuration + build entry points
    Tests/Domain            engine-free tests, also run on plain .NET
    Tests/EditMode          settings drift, assembly boundaries, scene wiring
    Tests/PlayMode          runtime smoke tests
    Scenes/Client.unity     mobile entry scene
    Scenes/Server.unity     dedicated server entry scene
tests/VoxelRush.Domain.Tests  .NET harness compiling Core + Tests/Domain
tools/                      unity, test, build and smoke scripts
tasks/                      per-task records (scope, commands, results)
```

## Commands
```bash
tools/test.sh domain        # dotnet test, no Unity needed
tools/test.sh editmode      # Unity EditMode tests -> artifacts/test-results/EditMode.xml
tools/test.sh playmode      # Unity PlayMode tests -> artifacts/test-results/PlayMode.xml
tools/test.sh               # all tiers

tools/build.sh android      # build/android/VoxelRush.apk
tools/build.sh ios          # build/ios/Xcode (archive/sign in Xcode or CI)
tools/build.sh server       # build/server/VoxelRushServer.x86_64 (headless Linux)
tools/build.sh server-mac   # build/server-mac/VoxelRush (headless macOS, for local runs)
tools/build.sh bot-linux    # build/bot-linux (headless bot: client logic, server-subtarget player)
tools/build.sh bot-mac      # build/bot-mac (headless bot, local runs)
tools/smoke-server.sh       # run the Linux server build in an amd64 Ubuntu container (x86-64 hosts)
SMOKE_NATIVE=1 tools/smoke-server.sh build/server-mac   # same checks against the macOS build
tools/smoke-join.sh docker  # server image + 2 Linux bots on one Docker network; server-full rejection (x86-64 hosts)
tools/smoke-join.sh native  # same with the macOS server + bot builds
tools/build.sh bot-mac --development && tools/smoke-drive.sh native   # 2 predicted cars at 100 ms RTT + speed hack (docker mode on x86-64)
tools/load-test.sh native 8 40   # server + 8 driving bots; tick metrics -> artifacts/load/summary.json (baseline: docs/perf/)
tools/dev-online.sh up|down|status   # macOS: PostgreSQL + API + host agent with native per-world servers, for phones on LAN/Tailscale
```
Unity must not already have the project open when running batch-mode commands.
On Apple Silicon the Linux (amd64 Mono) server aborts under Docker's Rosetta emulation; use the `server-mac` build locally. See ADR-012.

Platform settings are code: edit `unity/Assets/VoxelRush/Editor/ProjectConfiguration.cs`, then run **VoxelRush > Apply Project Configuration** (or `tools/unity.sh -quit -executeMethod VoxelRush.Editor.BuildEntryPoints.ConfigureProject`) and commit `ProjectSettings/`. An EditMode test fails on drift.

## Controls
Landscape touch HUD (`Client.unity` → `TouchHud`): ◀ ▶ steer bottom-left; GAS, BRAKE, BOOST, DRIFT (handbrake) bottom-right; BUILD top centre; all inside the device safe area.
The `Driving` action map is defined in code (`Presentation/Input/DrivingControls.cs`); touch buttons emulate a virtual gamepad, so touch, gamepad (stick/triggers/face buttons) and keyboard (WASD/arrows, Shift boost, Space drift, B build) share one map.

## Dedicated server
```
VoxelRushServer.x86_64 -batchmode -nographics -logFile - [-port 7777] [-maxPlayers 1..8] [-smokeTestTicks N]
```
Logs are logfmt (`event=server_started`, `server_listening`, `client_connected players=N`, `client_rejected reason=server_full`, ...).
Exit codes: `0` success, `2` invalid arguments, `3` could not listen.
Every 5 s the server logs `server_tick_metrics` (tick rate, p50/p95/p99/max tick work in ms against the 33.3 ms budget, overruns, dropped ticks, players, snapshots sent); `server_tick_summary` on shutdown.

Docker (UDP 7777, non-root, read-only install dir):
```bash
tools/build.sh server
docker compose -f docker/compose.yaml up --build
```

## Networked driving
The server spawns one car per player on the shared `Arena`; physics ticks at 30 Hz. Owners predict and reconcile; other cars are interpolated (`docs/adr/ADR-015-authoritative-vehicle-netcode.md`).
Development builds accept `-simRttMs N [-simJitterMs J] [-simLossPercent L]` to simulate network conditions for gameplay messages. Bots accept `-botDrive` (scripted driving), `-botCheat` (sends speed-hack inputs the server must reject) and `-botPaint <sku>` (asks to show a paint; the server checks ownership).

## Test client and bots
Clients connect with `-connect host:port [-smokeTestSeconds N]` (exit `0` stayed connected, `3` connect failed or rejected).
Headless bots (`Bot.unity`) run the same connection component in a server-subtarget player; Linux desktop players crash without a window system, so automated clients are always bots.
In the editor, enable *Connect In Editor* on the `ClientBootstrap` object in `Client.unity`.
Connection handshake and admission rules: `docs/adr/ADR-014-netcode-connection-handshake.md`.

## CI (`.github/workflows/ci.yml`)
| Job | Runs on | Trigger | Needs Unity secrets |
|---|---|---|---|
| Domain tests (.NET) | ubuntu | every PR / push | no |
| Unity tests + builds: EditMode, PlayMode, Linux server + smoke, Android APK, iOS Xcode project (iOS on `main`/manual) | ubuntu | every PR / push | yes |
| iOS compile (unsigned) | macOS | `main` / manual | (uses the Xcode project artifact) |

The Unity job installs the pinned editor on the runner (`RageAgainstThePixel/unity-setup`), activates a free **Unity Personal** license (`RageAgainstThePixel/activate-unity-license`), runs the same `tools/*.sh` scripts as local development, and returns the seat afterwards.
It is one job under a repo-wide concurrency lock, so only one Personal activation is in use at a time.
It is **skipped with a warning** until these repository secrets exist (Settings > Secrets and variables > Actions):
- `UNITY_EMAIL`, `UNITY_PASSWORD`: a Unity ID with a Personal license and **2FA disabled** (a dedicated CI account is recommended).

All third-party actions are pinned to commit SHAs. Lint locally with `docker run --rm -v "$PWD:/repo" -w /repo rhysd/actionlint:1.7.7 -no-color`.
