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
tools/smoke-server.sh       # run the Linux server build in an amd64 Ubuntu container (x86-64 hosts)
SMOKE_NATIVE=1 tools/smoke-server.sh build/server-mac   # same checks against the macOS build
```
Unity must not already have the project open when running batch-mode commands.
On Apple Silicon the Linux (amd64 Mono) server aborts under Docker's Rosetta emulation; use the `server-mac` build locally. See ADR-012.

Platform settings are code: edit `unity/Assets/VoxelRush/Editor/ProjectConfiguration.cs`, then run **VoxelRush > Apply Project Configuration** (or `tools/unity.sh -quit -executeMethod VoxelRush.Editor.BuildEntryPoints.ConfigureProject`) and commit `ProjectSettings/`. An EditMode test fails on drift.

## Dedicated server
```
VoxelRushServer.x86_64 -batchmode -nographics -logFile - [-port 7777] [-maxPlayers 1..8] [-smokeTestTicks N]
```
Logs are logfmt (`event=server_started port=7777 ...`). Exit codes: `0` success, `2` invalid arguments.

## CI (`.github/workflows/ci.yml`)
| Job | Runs on | Trigger | Needs Unity license |
|---|---|---|---|
| Domain tests (.NET) | ubuntu | every PR / push | no |
| Unity EditMode + PlayMode | ubuntu (GameCI image) | every PR / push | yes |
| Linux dedicated server + Docker smoke | ubuntu | every PR / push | yes |
| Android APK | ubuntu | every PR / push | yes |
| iOS Xcode project, then unsigned compile | ubuntu, then macOS | `main` and manual only | yes |

Unity jobs are **skipped with a warning** until license secrets exist (never reported green without running).
Repository secrets (Settings > Secrets and variables > Actions), per GameCI activation docs:
- Personal license: `UNITY_LICENSE` (license file contents), `UNITY_EMAIL`, `UNITY_PASSWORD`
- Pro/Plus seat: `UNITY_SERIAL`, `UNITY_EMAIL`, `UNITY_PASSWORD`

All third-party actions are pinned to commit SHAs. Lint locally with `docker run --rm -v "$PWD:/repo" -w /repo rhysd/actionlint:1.7.7`.
