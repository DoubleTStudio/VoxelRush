using System;
using System.Runtime.InteropServices;
using UnityEngine;
using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Networking.Online
{
    /// <summary>Key/value secret storage (Keychain, Android Keystore, PlayerPrefs); faked in tests.</summary>
    public interface ISecretBackend
    {
        /// <summary>The stored value, or null when missing or no longer readable.</summary>
        string? Read(string key);

        /// <summary>False when the value could not be stored.</summary>
        bool Write(string key, string value);

        void Delete(string key);
    }

    /// <summary>
    /// AUTH-002: the refresh token in platform secure storage. A token left in PlayerPrefs by older builds moves
    /// over on first read; if secure storage refuses a write the token stays in PlayerPrefs rather than being lost.
    /// </summary>
    public sealed class SecureTokenStore : ITokenStore
    {
        private readonly ISecretBackend _secure;
        private readonly ISecretBackend _prefs;
        private readonly string _key;
        private bool _loaded;
        private string? _current;

        public SecureTokenStore(ISecretBackend secure, ISecretBackend prefs, string key)
        {
            _secure = secure;
            _prefs = prefs;
            _key = key;
        }

        /// <summary>Keychain on iOS, Keystore on Android, PlayerPrefs elsewhere (editor, desktop).</summary>
        public static ITokenStore Create(string apiUrl) => Create(apiUrl, PlatformBackend());

        public static ITokenStore Create(string apiUrl, ISecretBackend? secure) =>
            secure == null ? new PrefsTokenStore(apiUrl) : new SecureTokenStore(secure, new PlayerPrefsBackend(), PrefsTokenStore.KeyFor(apiUrl));

        // Cached: PlayerApi reads the token on every call, and Keystore/Keychain reads are IPC.
        public string? Load()
        {
            if (!_loaded)
            {
                _current = LoadStored();
                _loaded = true;
            }

            return _current;
        }

        public void Save(string? refreshToken)
        {
            _current = refreshToken;
            _loaded = true;
            Store(refreshToken);
        }

        private string? LoadStored()
        {
            string? value = SecureRead();
            string? legacy = NonEmpty(_prefs.Read(_key));
            if (value != null)
            {
                if (legacy != null)
                {
                    // Left behind by a migration interrupted after the secure write.
                    _prefs.Delete(_key);
                }

                return value;
            }

            if (legacy != null && SecureWrite(legacy))
            {
                _prefs.Delete(_key);
                Debug.Log(LogEvent.Format("token_migrated", ("store", "secure")));
            }

            return legacy;
        }

        private void Store(string? refreshToken)
        {
            if (refreshToken == null)
            {
                SecureDelete();
                _prefs.Delete(_key);
                return;
            }

            if (SecureWrite(refreshToken))
            {
                _prefs.Delete(_key);
                return;
            }

            // Drop the stale secure copy so the next Load can't resume a rotated-out token.
            SecureDelete();
            _prefs.Write(_key, refreshToken);
            Debug.LogWarning(LogEvent.Format("token_store_fallback", ("store", "prefs")));
        }

        private string? SecureRead()
        {
            try
            {
                return NonEmpty(_secure.Read(_key));
            }
            catch (Exception e)
            {
                Debug.LogWarning(LogEvent.Format("token_read_failed", ("error", e.GetType().Name)));
                return null;
            }
        }

        private bool SecureWrite(string value)
        {
            try
            {
                return _secure.Write(_key, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning(LogEvent.Format("token_write_failed", ("error", e.GetType().Name)));
                return false;
            }
        }

        private void SecureDelete()
        {
            try
            {
                _secure.Delete(_key);
            }
            catch (Exception e)
            {
                Debug.LogWarning(LogEvent.Format("token_delete_failed", ("error", e.GetType().Name)));
            }
        }

        private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

        private static ISecretBackend? PlatformBackend()
        {
            try
            {
#if UNITY_IOS && !UNITY_EDITOR
                return new KeychainBackend();
#elif UNITY_ANDROID && !UNITY_EDITOR
                return new AndroidKeystoreBackend();
#else
                return null;
#endif
            }
            catch (Exception e)
            {
                // A device without working secure storage still plays, with the PlayerPrefs store.
                Debug.LogWarning(LogEvent.Format("token_store_unavailable", ("error", e.GetType().Name)));
                return null;
            }
        }
    }

    /// <summary>PlayerPrefs as a backend: the legacy location and the fallback.</summary>
    public sealed class PlayerPrefsBackend : ISecretBackend
    {
        public string? Read(string key)
        {
            string value = PlayerPrefs.GetString(key, string.Empty);
            return value.Length > 0 ? value : null;
        }

        public bool Write(string key, string value)
        {
            PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();
            return true;
        }

        public void Delete(string key)
        {
            if (PlayerPrefs.HasKey(key))
            {
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
            }
        }
    }

#if UNITY_IOS && !UNITY_EDITOR
    /// <summary>iOS Keychain generic passwords (Plugins/iOS/VoxelRushKeychain.mm).</summary>
    internal sealed class KeychainBackend : ISecretBackend
    {
        public string? Read(string key) => VoxelRushKeychainRead(key);

        public bool Write(string key, string value) => VoxelRushKeychainWrite(key, value);

        public void Delete(string key) => VoxelRushKeychainDelete(key);

        [DllImport("__Internal")]
        private static extern string? VoxelRushKeychainRead(string key);

        [DllImport("__Internal")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool VoxelRushKeychainWrite(string key, string value);

        [DllImport("__Internal")]
        private static extern void VoxelRushKeychainDelete(string key);
    }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>AES-GCM key in AndroidKeyStore, ciphertext in private SharedPreferences (Plugins/Android/SecureTokenStorage.java).</summary>
    internal sealed class AndroidKeystoreBackend : ISecretBackend
    {
        private readonly AndroidJavaObject _storage;

        public AndroidKeystoreBackend()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            _storage = new AndroidJavaObject("com.doubletstudio.voxelrush.security.SecureTokenStorage", activity);
        }

        public string? Read(string key) => _storage.Call<string>("read", key);

        public bool Write(string key, string value) => _storage.Call<bool>("write", key, value);

        public void Delete(string key) => _storage.Call("delete", key);
    }
#endif
}
