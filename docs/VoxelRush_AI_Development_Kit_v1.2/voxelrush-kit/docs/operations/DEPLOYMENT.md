# Development and deployment runbook
## Bootstrap
1. Install Unity Hub + Unity 6 LTS editor (pin exact version), Android Build Support, iOS Build Support, Linux Dedicated Server module; Git LFS, Docker, .NET 10 SDK.
2. Create Unity URP 3D project in `unity/`; commit ProjectSettings and Packages/manifest; set landscape, Input System, asmdefs.
3. Build Linux dedicated server headless and run in Docker with UDP game port exposed. Client points to server endpoint via environment-specific config.
4. Add Unity Transport + NGO compatible versions after verifying current official compatibility matrix. Avoid unverified pinned version numbers.
5. For first slice, local sessions and file-backed snapshot/journal; add auth/API/DB after core networked gameplay.
## Staging
Use single always-on Linux VM for persistent sandbox. Store journal on durable volume; back up snapshots externally. HTTPS for API, UDP for game traffic, firewall, monitoring, graceful shutdown flush, crash restart policy.
## Production direction
Long-running sandbox worlds on dedicated VM/cluster; on-demand race servers from managed hosting after validating availability/limits. Do not depend on deprecated hosting products. Re-evaluate Edgegap, managed allocation and pricing when provisioning.
## CI pipeline stages
PR: format → domain tests → Unity EditMode → server build → integration smoke. Main: publish Docker image → deploy staging → 2-client smoke. Release: Android signed AAB via Play internal track, iOS archive/TestFlight via macOS runner and signing secrets.
## Secrets
Use CI secret store, never Unity project assets. Separate stage/prod keys, short-lived join tickets, least privilege DB and object storage credentials.
## Rollback
Keep prior Docker image and schema-compatible snapshot; roll back server only after verifying generator/schema compatibility. No destructive migration without tested restore path.
## Monitoring
Alert on tick overruns, write backlog, journal errors, join failures, server OOM and mismatch rate. Log operation IDs and chunk revisions.
