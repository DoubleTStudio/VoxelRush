# Task: AUTH-002 — Refresh token in platform secure storage

## Goal
The API refresh token (the long-lived guest account credential from MVP-004) is kept in the iOS Keychain and the Android Keystore instead of plaintext PlayerPrefs, without losing existing accounts.

## Changes
- **SecureTokenStore** (Networking/Online): an `ITokenStore` over a small `ISecretBackend` (read/write/delete), with PlayerPrefs as the legacy location and fallback. `SecureTokenStore.Create(apiUrl)` picks Keychain on iOS, Keystore on Android and the existing `PrefsTokenStore` in the editor and on desktop. `ClientBootstrap.Api` uses it.
- **Migration:** a token left in PlayerPrefs by an older build is copied into secure storage on first read and then deleted from PlayerPrefs. If both copies exist (migration interrupted), the secure copy wins and the PlayerPrefs one is removed.
- **Fallback:** if secure storage refuses a write, the token is kept in PlayerPrefs (and any older secure copy deleted, so a rotated-out token is never resumed) rather than losing the account. If the platform backend can't be created, the PlayerPrefs store is used.
- **Key loss:** an unreadable entry (Android key gone after a backup restore to another device, keystore reset, damaged data) reads as "no token" and is removed; the app then signs in as a new guest, as on a first launch.
- **iOS plugin** `Plugins/iOS/VoxelRushKeychain.mm`: `kSecClassGenericPassword`, service `com.doubletstudio.voxelrush`, account = the token key, `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`; bound with `[DllImport("__Internal")]`. Plain CoreFoundation, so it builds with or without ARC; links Security.framework.
- **Android plugin** `Plugins/Android/SecureTokenStorage.java`: AES-256-GCM key generated in `AndroidKeyStore` (alias `com.doubletstudio.voxelrush.refresh`, no user authentication), ciphertext and IV in private SharedPreferences `voxelrush.secure`. No new Gradle dependencies; minSdk 26 already covers `KeyGenParameterSpec`.
- The token is read from storage once per store and then served from memory (`PlayerApi` reads it on every request; Keychain/Keystore reads are IPC).
- `PrefsTokenStore.KeyFor(apiUrl)` is shared so every store uses the same per-API key. Logs carry event names and exception types only, never token values.

## Not done
- Not run on a physical phone. The game's Keychain path hasn't run in a full iOS build (unsigned), and token rotation against a live API hasn't run on Android (only the emulator migration, without an API).
- Account linking and recovery UI (`/v1/auth/link-code`, `/link`) is still the only way back to an account after key loss.

## Verification
- `tools/test.sh editmode`: 554/554 (new `SecureTokenStoreTests`, 11: secure save, PlayerPrefs migration through the real `PrefsTokenStore`, leftover cleanup, refused migration, write fallback dropping the stale secure copy, unreadable storage, clear, read caching, no token in logs, factory choice). `tools/test.sh playmode`: 36/36.
- `tools/build.sh android --development` succeeds; `SecureTokenStorage` is in the APK's dex.
- `tools/build.sh ios` succeeds: the Xcode project compiles `Libraries/VoxelRush/Plugins/iOS/VoxelRushKeychain.mm`, links Security.framework, and the IL2CPP output references the Keychain entry points. An unsigned `xcodebuild` (Release, iphoneos, `CODE_SIGNING_ALLOWED=NO`) of that project succeeds, with the Keychain functions linked into UnityFramework.
- Android emulator (API 37, read-only AVD): a standalone harness around `SecureTokenStorage.java` checked round trip, ciphertext-only storage, tampered data and a deleted Keystore key (both read as no token and are removed), and key re-creation. In the development APK, a token seeded into PlayerPrefs moved into `voxelrush.secure.xml` as ciphertext on sign-in, the PlayerPrefs entry was removed, and the token did not appear in logcat.
- iOS simulator: a harness around `VoxelRushKeychain.mm` checked add, update, read and delete.

## Risks / rollback
- Client-only; no API or server changes. A token that can't be stored securely stays in PlayerPrefs (documented fallback), and a lost Android key means a new guest account unless the account was linked.
- iOS Keychain items survive app deletion on the same device, so a reinstall resumes the same guest account (it didn't with PlayerPrefs).
- Rollback: revert. A reverted build reads PlayerPrefs only, so players already migrated get a new guest account; ship forward rather than reverting once released.
