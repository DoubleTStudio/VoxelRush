# ADR-012: Bootstrap pins, assembly layout and test tiers

Status: Accepted (VR-001). Refines ADR-001; does not replace it.

## Context
ADR-001 requires Unity 6 LTS with the exact editor pinned at bootstrap. SYSTEM.md lists the core assemblies and a single `VoxelRush.Tests`. QUALITY_GATES requires C# domain tests on Linux in addition to Unity EditMode/PlayMode tests.

## Decision
- **Editor:** Unity `6000.3.26f1` (changeset `de8167e7440c`), the current 6.3 LTS patch at bootstrap. Modules: Android (SDK/NDK/OpenJDK), iOS, Linux Dedicated Server, Linux IL2CPP, Mac Dedicated Server (local server runs on Apple Silicon).
- **Packages:** URP 17.3 / Test Framework 1.6 / uGUI 2.0 are core packages locked to the editor. Input System pinned to `1.20.1`. NGO and Unity Transport are deferred to VR-002/VR-012 per DEPLOYMENT.md (verify the compatibility matrix then).
- **Assemblies:** `VoxelRush.Core`, `.World` and `.Destruction` set `noEngineReferences` so domain logic stays pure C#. `VoxelRush.Presentation` is constrained to `!UNITY_SERVER`; `VoxelRush.Server` to `UNITY_SERVER || UNITY_EDITOR`, keeping server code out of client binaries and rendering/input out of the server.
- **Tests:** `VoxelRush.Tests` is split into three tiers:
  - `VoxelRush.Tests.Domain`: engine-free, runs in Unity *and* on plain .NET via `tests/VoxelRush.Domain.Tests` (C# 9, NUnit 3.14 to match Unity's bundled NUnit 3 fork).
  - `VoxelRush.Tests.EditMode`: settings drift, assembly boundaries, scene wiring.
  - `VoxelRush.Tests.PlayMode`: runtime smoke tests.
- **Platform settings** are code (`VoxelRush.Editor.ProjectConfiguration`), applied idempotently and validated by an EditMode test, so ProjectSettings drift fails CI.
- **Server scripting backend:** Mono for now. Linux IL2CPP needs a host-specific toolchain package (`com.unity.toolchain.macos-arm64-linux-x86_64` on macOS, `com.unity.toolchain.linux-x86_64` on Linux CI), an unapproved dependency. Revisit in VR-070 with profiling.
- **Editor-injected packages:** with the Linux IL2CPP module installed, the editor re-adds `com.unity.sdk.linux-arm64`, `com.unity.sdk.linux-x86_64` and `com.unity.toolchain.macos-arm64-linux` (1.1.0) to the manifest on every open. They are kept to avoid churn; the Mono server build does not use them.
- **Local server runs:** amd64 Mono aborts at startup under Docker Desktop's Rosetta emulation (`x86-codegen.h: offset == (gint32)offset`). On Apple Silicon, validate `ServerHost` with the native macOS server build (`tools/build.sh server-mac`); the Linux build is smoke-tested on x86-64 CI.

## Alternatives
- Unity 6000.0 LTS: older, shorter remaining support. Unity 6000.6 mainline: not LTS.
- Single test assembly: cannot run engine-free tests outside Unity.
- Hand-maintained ProjectSettings only: silent drift, nothing enforces orientation/IDs.

## Consequences
- Domain code can't use `UnityEngine` (e.g. `Vector3`); math types must live in Core.
- Upgrading the editor means updating `ProjectVersion.txt`, this ADR and CI images together.

## Migration / rollback
Editor upgrades happen on a dedicated branch: rerun all test tiers and the three builds, then commit the regenerated `Packages/packages-lock.json`. Rollback is reverting that branch; no data migration involved.
