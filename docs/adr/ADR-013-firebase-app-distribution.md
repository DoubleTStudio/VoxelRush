# ADR-013: Firebase App Distribution for pre-release Android/iOS builds

Status: Accepted. Implementation in VR-081; refines DEPLOYMENT.md "Release" for internal/alpha builds.

## Context
DEPLOYMENT.md plans internal builds via the Play internal track and TestFlight. Pre-release testers need one channel for both platforms, fast turnaround from CI, and no store review for every build.

## Decision
- Internal and closed-alpha builds go to testers through **Firebase App Distribution** for both Android and iOS.
- CI uploads with the official `firebase-tools` CLI at a pinned version (no third-party upload action).
- Authentication: a Google service account limited to the *Firebase App Distribution Admin* role, preferably keyless via GitHub OIDC Workload Identity Federation; otherwise a JSON key held only in CI secrets.
- **Distribution only:** no Firebase SDK (Analytics, Crashlytics, etc.) is added to the app by this decision. Any SDK needs its own ADR plus the privacy/child-safety review from PERSISTENCE_SECURITY.md.
- Store tracks (Play internal/closed testing, TestFlight) remain the path for staged beta and production (QUALITY_GATES "Release").

## Requirements this creates
- Android: a stable upload keystore in CI secrets. Debug keys differ per CI machine, so testers couldn't upgrade in place. APK for testers; AAB stays the Play format.
- iOS: Apple Developer Program membership, an Apple Distribution certificate (.p12) and an **Ad Hoc** provisioning profile listing tester device UDIDs; archive + export on a macOS runner.
- Firebase project with Android and iOS apps registered under `com.doubletstudio.voxelrush`; tester groups defined in Firebase.

## Alternatives
- Play internal track + TestFlight only: two channels and TestFlight review latency; still used for beta/production.
- Third-party GitHub Actions for upload: extra supply-chain surface for a one-line CLI call.

## Consequences
- iOS testers must register devices before they can install (Ad Hoc limit of 100 devices per device type per membership year).
- Release notes and tester groups are managed in Firebase; builds are retained per Firebase's retention policy.

## Migration / rollback
Disable the distribution job; builds remain available as CI artifacts. Revoke the service account (or its key) and remove the CI secrets. No app code depends on Firebase.
