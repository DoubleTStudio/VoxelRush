# Release runbook (VR-081)

Implements DEPLOYMENT (CI stages, secrets, rollback) and ADR-013 (Firebase App Distribution).

## Channels
| What | Where | How |
|---|---|---|
| PR builds | CI artifacts (`android-apk`, `server-linux64`) | every PR (`ci.yml`) |
| Server + API images | `ghcr.io/doubletstudio/voxelrush-server`, `voxelrush-api`: `:sha-<commit>`, `:main` | every push to `main`, after a rollback drill against the previous `:main` |
| Internal testers (Android/iOS) | Firebase App Distribution, group `internal` | `release.yml`: tag `vX.Y.Z` or run manually with a version |
| Staged server release | image tags `:staging` → `:beta` → `:production` | `tools/promote-image.sh <sha> <channel>` |

## One-time setup (repository secrets)
Unity (already used by CI): `UNITY_EMAIL`, `UNITY_PASSWORD`.

Firebase: create a Firebase project; register the Android app and the iOS app as `com.doubletstudio.voxelrush`; create the tester group `internal`. Create a Google service account with only the *Firebase App Distribution Admin* role. Secrets: `FIREBASE_SERVICE_ACCOUNT_JSON` (key JSON; prefer moving to keyless OIDC later), `FIREBASE_ANDROID_APP_ID`, `FIREBASE_IOS_APP_ID` (the `1:…:android:…` / `1:…:ios:…` ids).

Android upload key (once, kept offline as well; losing it means testers must reinstall):
```
keytool -genkeypair -keystore upload.keystore -alias upload -keyalg RSA -keysize 4096 -validity 10000
base64 -i upload.keystore | pbcopy     # -> ANDROID_KEYSTORE_BASE64
```
Secrets: `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASS`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASS`.

iOS (Apple Developer Program): an *Apple Distribution* certificate exported as `.p12`, an **Ad Hoc** profile for `com.doubletstudio.voxelrush` listing tester devices. Secrets: `IOS_DIST_CERT_P12_BASE64`, `IOS_DIST_CERT_PASSWORD`, `IOS_ADHOC_PROFILE_BASE64`, `APPLE_TEAM_ID`. New tester devices: add the UDID to the profile, re-export, update the secret.

Until a platform's secrets exist, its jobs are skipped with a warning (never a fake green).

## Cutting an internal build
1. `git tag v0.9.0 && git push origin v0.9.0` (or *Actions → Release → Run workflow* with a version).
2. The build job signs the APK with the upload key (`tools/build.sh android --release` refuses a debug key), versions it from the tag and the run number, and verifies the signature with `apksigner`; iOS is archived and exported Ad Hoc on macOS.
3. Testers get the Firebase invite/update; artifacts stay on the run for 30 days.

Local equivalent: export `VOXELRUSH_ANDROID_KEYSTORE`, `VOXELRUSH_ANDROID_KEYSTORE_PASS`, `VOXELRUSH_ANDROID_KEY_ALIAS`, `VOXELRUSH_ANDROID_KEY_PASS`, `VOXELRUSH_VERSION`, `VOXELRUSH_BUILD_NUMBER`, then `tools/build.sh android --release`. Nothing is written into ProjectSettings.

## Server staged release and rollback
- Every image carries labels: protocol, chunk schema, journal record version, generator versions (`tools/image-labels.sh`).
- Promote: `tools/promote-image.sh <sha> staging` — refuses if the image can't read what the channel's current image wrote (`tools/rollback-check.sh`).
- **Rollback** = promote the previous sha. Allowed only when journal/schema/generator labels match (DEPLOYMENT: "roll back server only after verifying generator/schema compatibility"). A protocol change is allowed but clients must match. If blocked, the release is "no rollback": fix forward.
- Drill: `tools/rollback-drill.sh <candidate> <previous>` (CI on every `main` push): the candidate builds a world, the previous image takes over the same volume, recovers every record and serves identical chunk hashes.
- Before rolling back a live server: stop it gracefully (SIGTERM drains and flushes the journal), keep the world volume, start the previous image on it, run `tools/check-server-log.sh` on its log.

## Not yet
Play internal track/AAB and TestFlight (beta/production per QUALITY_GATES), deploying images to a staging host (no host yet), key rotation for join tickets.
