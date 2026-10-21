# Asset and dependency license inventory (VR-082)

QUALITY_GATES: "gate release on … asset license audit". This is the inventory; licenses are as published by each project and must be re-verified when versions change and before a store release.

## Art and content in the repository
| Asset | Origin | Notes |
|---|---|---|
| `Art/VoxelAtlas.png` | Generated procedurally in this repo (VR-022) | No third-party art (GDD: distinct original art, no Minecraft IP). |
| `Art/VoxelAtlas.mat`, `Art/Debris.mat` | Created in this repo | Debris uses the URP Lit shader. |
| UI font | Unity built-in `LegacyRuntime.ttf` | Ships with the engine. |
| Shaders `Shaders/VoxelAtlas.shader` | Written in this repo | |

## Unity packages (`unity/Packages/manifest.json`)
Unity-published packages (Input System, Netcode for GameObjects, URP, Test Framework, uGUI, Burst/Collections/Mathematics via dependencies, IDE integrations, Linux toolchains, built-in modules) under the Unity Companion License / Unity Terms of Service. Engine: Unity Personal (check revenue eligibility before commercial release).

## .NET packages
| Package | Use | License (as published) |
|---|---|---|
| Npgsql | API → PostgreSQL | PostgreSQL License |
| Microsoft.AspNetCore.Authentication.JwtBearer | API access tokens | MIT |
| AWSSDK.S3 | API → R2/S3 snapshots | Apache-2.0 |
| NUnit, NUnit3TestAdapter, Microsoft.NET.Test.Sdk | tests | MIT |
| Microsoft.AspNetCore.Mvc.Testing | API tests | MIT |
| Testcontainers.PostgreSql | API tests | MIT |

## Container images
| Image | Use | License |
|---|---|---|
| `ubuntu:24.04` (digest-pinned) | server image base, CI bot containers | Ubuntu/Canonical image terms; component licenses vary |
| `mcr.microsoft.com/dotnet/aspnet:10.0` (digest-pinned) | API image base | .NET runtime: MIT |
| `postgres:17.6-alpine` | API database (compose, tests) | PostgreSQL License |
| `chrislusf/seaweedfs:3.97` | S3 stand-in, tests only | Apache-2.0 |
| linters (`actionlint`, `hadolint`, `shellcheck`, `gitleaks`) | CI/dev tooling only | MIT / GPL-3.0 (shellcheck, not shipped) |

## Not shipped / development only
CI actions (`actions/*`, `RageAgainstThePixel/*`), `firebase-tools` (release upload, Apache-2.0).

## Open items
- Legal review of the Unity Personal eligibility and store requirements before closed alpha.
- Any new art, audio or font needs an entry here with provenance before it is merged.
