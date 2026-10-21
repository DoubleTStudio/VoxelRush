using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoxelRush.Networking.Online;

namespace VoxelRush.Tests.EditMode
{
    /// <summary>AUTH-002: refresh token in secure storage, migration out of PlayerPrefs, and fallbacks.</summary>
    public sealed class SecureTokenStoreTests
    {
        private const string Key = "voxelrush.refresh.https://api.example";

        private sealed class FakeBackend : ISecretBackend
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public bool RefuseWrites;
            public bool Throw;
            public int Reads;

            public string? Read(string key)
            {
                Reads++;
                Fail();
                return Values.TryGetValue(key, out string value) ? value : null;
            }

            public bool Write(string key, string value)
            {
                Fail();
                if (RefuseWrites)
                {
                    return false;
                }

                Values[key] = value;
                return true;
            }

            public void Delete(string key)
            {
                Fail();
                Values.Remove(key);
            }

            private void Fail()
            {
                if (Throw)
                {
                    throw new InvalidOperationException("backend down");
                }
            }
        }

        [Test]
        public void SavedToken_GoesToSecureStorage_NotPrefs()
        {
            var secure = new FakeBackend();
            var prefs = new FakeBackend();

            new SecureTokenStore(secure, prefs, Key).Save("r1");

            Assert.That(secure.Values[Key], Is.EqualTo("r1"));
            Assert.That(prefs.Values, Is.Empty);
            Assert.That(new SecureTokenStore(secure, prefs, Key).Load(), Is.EqualTo("r1"));
        }

        [Test]
        public void PrefsToken_FromAnOlderBuild_MovesToSecureStorageOnFirstRead()
        {
            var secure = new FakeBackend();
            var prefs = new FakeBackend();
            prefs.Values[Key] = "r-old";

            string? loaded = new SecureTokenStore(secure, prefs, Key).Load();

            Assert.That(loaded, Is.EqualTo("r-old"));
            Assert.That(secure.Values[Key], Is.EqualTo("r-old"));
            Assert.That(prefs.Values.ContainsKey(Key), Is.False);
        }

        [Test]
        public void SecureCopy_Wins_AndALeftoverPrefsCopyIsRemoved()
        {
            var secure = new FakeBackend();
            var prefs = new FakeBackend();
            secure.Values[Key] = "r2";
            prefs.Values[Key] = "r2";

            Assert.That(new SecureTokenStore(secure, prefs, Key).Load(), Is.EqualTo("r2"));
            Assert.That(prefs.Values.ContainsKey(Key), Is.False);
        }

        [Test]
        public void MigrationRefused_KeepsTheTokenInPrefs_SoTheAccountIsNotLost()
        {
            var secure = new FakeBackend { RefuseWrites = true };
            var prefs = new FakeBackend();
            prefs.Values[Key] = "r-old";

            Assert.That(new SecureTokenStore(secure, prefs, Key).Load(), Is.EqualTo("r-old"));
            Assert.That(prefs.Values[Key], Is.EqualTo("r-old"));
        }

        [Test]
        public void RefusedSecureWrite_FallsBackToPrefs_AndDropsTheStaleSecureCopy()
        {
            var secure = new FakeBackend();
            var prefs = new FakeBackend();
            secure.Values[Key] = "r1";
            secure.RefuseWrites = true;
            LogAssert.Expect(LogType.Warning, new Regex("token_store_fallback"));

            new SecureTokenStore(secure, prefs, Key).Save("r2");

            Assert.That(secure.Values.ContainsKey(Key), Is.False);
            Assert.That(prefs.Values[Key], Is.EqualTo("r2"));
            secure.RefuseWrites = false;
            Assert.That(new SecureTokenStore(secure, prefs, Key).Load(), Is.EqualTo("r2"), "next launch resumes the newest token");
        }

        [Test]
        public void UnreadableSecureStorage_MeansNoToken_WithoutThrowing()
        {
            // Android Keystore key lost after a restore to a new device: the backend reports nothing stored.
            var secure = new FakeBackend { Throw = true };
            var prefs = new FakeBackend();
            LogAssert.Expect(LogType.Warning, new Regex("token_read_failed"));

            Assert.That(new SecureTokenStore(secure, prefs, Key).Load(), Is.Null);
        }

        [Test]
        public void ClearingTheToken_RemovesItEverywhere()
        {
            var secure = new FakeBackend();
            var prefs = new FakeBackend();
            secure.Values[Key] = "r1";
            prefs.Values[Key] = "r1";

            var store = new SecureTokenStore(secure, prefs, Key);
            store.Save(null);

            Assert.That(secure.Values, Is.Empty);
            Assert.That(prefs.Values, Is.Empty);
            Assert.That(store.Load(), Is.Null);
        }

        [Test]
        public void Load_ReadsSecureStorageOnce_ThenServesTheCachedToken()
        {
            var secure = new FakeBackend();
            secure.Values[Key] = "r1";
            var store = new SecureTokenStore(secure, new FakeBackend(), Key);

            store.Load();
            store.Load();
            store.Save("r2");

            Assert.That(store.Load(), Is.EqualTo("r2"));
            Assert.That(secure.Reads, Is.EqualTo(1));
        }

        [Test]
        public void Logs_NeverContainTheToken()
        {
            const string token = "r-secret-value";
            var secure = new FakeBackend { RefuseWrites = true };
            var prefs = new FakeBackend();
            prefs.Values[Key] = token;
            var logged = new List<string>();
            void Capture(string message, string stack, LogType type) => logged.Add(message);
            Application.logMessageReceived += Capture;
            try
            {
                var store = new SecureTokenStore(secure, prefs, Key);
                store.Load();
                store.Save(token);
                secure.RefuseWrites = false;
                new SecureTokenStore(secure, prefs, Key).Load();
            }
            finally
            {
                Application.logMessageReceived -= Capture;
            }

            Assert.That(logged, Is.Not.Empty);
            Assert.That(logged, Has.None.Contains(token));
        }

        [Test]
        public void Factory_UsesPrefsStore_WithoutASecureBackend()
        {
            Assert.That(SecureTokenStore.Create("https://api.example", null), Is.TypeOf<PrefsTokenStore>());
            Assert.That(SecureTokenStore.Create("https://api.example"), Is.TypeOf<PrefsTokenStore>(), "editor has no platform secure storage");
            Assert.That(SecureTokenStore.Create("https://api.example", new FakeBackend()), Is.TypeOf<SecureTokenStore>());
        }

        [Test]
        public void Factory_MigratesATokenThatPrefsTokenStoreSaved()
        {
            string url = "https://auth002-test-" + Guid.NewGuid().ToString("N") + ".example/";
            string key = PrefsTokenStore.KeyFor(url);
            var secure = new FakeBackend();
            try
            {
                new PrefsTokenStore(url).Save("r-legacy");

                string? loaded = SecureTokenStore.Create(url, secure).Load();

                Assert.That(loaded, Is.EqualTo("r-legacy"));
                Assert.That(secure.Values[key], Is.EqualTo("r-legacy"));
                Assert.That(PlayerPrefs.HasKey(key), Is.False);
            }
            finally
            {
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
            }
        }
    }
}
