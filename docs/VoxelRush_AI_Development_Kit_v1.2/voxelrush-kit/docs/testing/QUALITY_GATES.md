# Quality gates and acceptance
## Every PR
Scope one ticket, compile, unit tests, no new unapproved package, tests recorded, no leaked secrets, code review, profiler evidence for hot path, changelog/ADR if contract changed.
## M1 network driving
2 clients + Linux dedicated server, steering responsive at 100ms RTT, server authoritative under injected speed hack, remote interpolation, disconnect/rejoin. 8-player load target before graduation.
## Voxel
Same seed/version gives matching canonical chunk hashes, correct negative coords, edit at chunk border rebuilds neighbor, chunk unload/reload matches snapshot, no per-block objects.
## Destruction
Simultaneous hit single outcome, protected zones untouched, race overlay resets, explosion capped, structural queue bounded, no acknowledged edit lost on crash, late join identical chunk hash.
## Performance
Device-tier benchmark on real Android and iOS: low tier 30fps target, high tier 60fps target when sustainable, server 30Hz at 8 players. Measure p95/p99 frames, tick, memory, GC, mesh backlog, bandwidth, thermal behavior. Record device models and duration; targets are provisional until tested.
## CI
C# domain unit tests on Linux, Unity EditMode tests, Unity PlayMode tests on available runner, headless Linux server build, Android build, iOS build/sign on macOS runner. Gate release on smoke tests and asset license audit.
## Security
Fuzz malformed payloads, rate limits, replayed operation ID, unauthorized zones, expired tickets, spoofed actor, client-provided damage, storage fault injection.
## Release
Internal prototype → closed alpha → staged Android/iOS beta → production. Verify platform policies and app store requirements at release time.
