using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using VoxelRush.Editor;

namespace VoxelRush.Tests.EditMode
{
    /// <summary>VR-081: release signing/versioning come from the environment and never stick to the project.</summary>
    public sealed class ReleaseSettingsTests
    {
        private static ReleaseSettings From(Dictionary<string, string> env) =>
            ReleaseSettings.FromEnvironment(name => env.TryGetValue(name, out string value) ? value : null);

        [Test]
        public void NoEnvironment_IsValid_ForDevelopmentBuilds_ButNotForRelease()
        {
            ReleaseSettings settings = From(new Dictionary<string, string>());
            Assert.That(settings.Validate(), Is.Empty);

            settings.RequireSigning = true;
            Assert.That(settings.Validate(), Has.Exactly(1).Contains("upload keystore"));
        }

        [Test]
        public void Keystore_NeedsAnExistingFileAndAllPasswords()
        {
            ReleaseSettings settings = From(new Dictionary<string, string> { [ReleaseSettings.KeystoreVariable] = "/no/such/file.keystore" });

            IReadOnlyList<string> issues = settings.Validate();

            Assert.That(issues, Has.Some.Contains("missing file"));
            Assert.That(issues, Has.Some.Contains("required with a keystore"));
        }

        [TestCase("1.2", "7", true)]
        [TestCase("1.2.3", "2100000000", true)]
        [TestCase("v1.2", "7", false)]
        [TestCase("1.2", "0", false)]
        [TestCase("1.2", "abc", false)]
        public void VersionAndBuildNumber_AreValidated(string version, string build, bool valid)
        {
            ReleaseSettings settings = From(new Dictionary<string, string> { [ReleaseSettings.VersionVariable] = version, [ReleaseSettings.BuildNumberVariable] = build });

            Assert.That(settings.Validate().Count == 0, Is.EqualTo(valid));
        }

        [Test]
        public void Apply_IsUndoneOnDispose_SoNothingLeaksIntoProjectSettings()
        {
            string keystore = Path.GetTempFileName();
            try
            {
                ReleaseSettings settings = From(new Dictionary<string, string>
                {
                    [ReleaseSettings.KeystoreVariable] = keystore,
                    [ReleaseSettings.KeystorePassVariable] = "store-secret",
                    [ReleaseSettings.KeyAliasVariable] = "upload",
                    [ReleaseSettings.KeyPassVariable] = "key-secret",
                    [ReleaseSettings.VersionVariable] = "9.8.7",
                    [ReleaseSettings.BuildNumberVariable] = "4242",
                });
                Assert.That(settings.Validate(), Is.Empty);
                (bool custom, string name, string version, int code, string ios) before = (PlayerSettings.Android.useCustomKeystore, PlayerSettings.Android.keystoreName,
                    PlayerSettings.bundleVersion, PlayerSettings.Android.bundleVersionCode, PlayerSettings.iOS.buildNumber);

                using (settings.Apply())
                {
                    Assert.That(PlayerSettings.Android.useCustomKeystore, Is.True);
                    Assert.That(PlayerSettings.Android.keystoreName, Is.EqualTo(keystore));
                    Assert.That(PlayerSettings.Android.keyaliasName, Is.EqualTo("upload"));
                    Assert.That(PlayerSettings.bundleVersion, Is.EqualTo("9.8.7"));
                    Assert.That(PlayerSettings.Android.bundleVersionCode, Is.EqualTo(4242));
                    Assert.That(PlayerSettings.iOS.buildNumber, Is.EqualTo("4242"));
                }

                Assert.That((PlayerSettings.Android.useCustomKeystore, PlayerSettings.Android.keystoreName, PlayerSettings.bundleVersion,
                    PlayerSettings.Android.bundleVersionCode, PlayerSettings.iOS.buildNumber), Is.EqualTo(before));
                Assert.That(PlayerSettings.Android.keystorePass, Is.Empty);
                Assert.That(PlayerSettings.Android.keyaliasPass, Is.Empty);
            }
            finally
            {
                File.Delete(keystore);
            }
        }
    }
}
