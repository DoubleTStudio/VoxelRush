using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;

namespace VoxelRush.Editor
{
    /// <summary>
    /// Release signing and versioning from the environment (VR-081, ADR-013): CI writes the upload keystore to a temp
    /// file and passes its path and passwords as environment variables. Nothing here is saved into the project.
    /// </summary>
    public sealed class ReleaseSettings
    {
        public const string KeystoreVariable = "VOXELRUSH_ANDROID_KEYSTORE";
        public const string KeystorePassVariable = "VOXELRUSH_ANDROID_KEYSTORE_PASS";
        public const string KeyAliasVariable = "VOXELRUSH_ANDROID_KEY_ALIAS";
        public const string KeyPassVariable = "VOXELRUSH_ANDROID_KEY_PASS";
        public const string VersionVariable = "VOXELRUSH_VERSION";
        public const string BuildNumberVariable = "VOXELRUSH_BUILD_NUMBER";
        // Play's limit for versionCode.
        public const int MaxBuildNumber = 2_100_000_000;

        public string? Keystore { get; private set; }

        public string? KeystorePass { get; private set; }

        public string? KeyAlias { get; private set; }

        public string? KeyPass { get; private set; }

        public string? Version { get; private set; }

        public int? BuildNumber { get; private set; }

        /// <summary>Release builds must be signed with the upload key: debug keys differ per machine, so testers couldn't upgrade.</summary>
        public bool RequireSigning { get; set; }

        public bool HasKeystore => !string.IsNullOrEmpty(Keystore);

        public static ReleaseSettings FromEnvironment(Func<string, string?> env)
        {
            var settings = new ReleaseSettings
            {
                Keystore = Empty(env(KeystoreVariable)),
                KeystorePass = Empty(env(KeystorePassVariable)),
                KeyAlias = Empty(env(KeyAliasVariable)),
                KeyPass = Empty(env(KeyPassVariable)),
                Version = Empty(env(VersionVariable)),
            };
            string? build = Empty(env(BuildNumberVariable));
            if (build != null)
            {
                settings.BuildNumber = int.TryParse(build, out int n) ? n : -1;
            }

            return settings;
        }

        /// <summary>Problems that must stop the build; never includes secret values.</summary>
        public IReadOnlyList<string> Validate()
        {
            var issues = new List<string>();
            if (HasKeystore)
            {
                if (!File.Exists(Keystore))
                {
                    issues.Add($"{KeystoreVariable} points to a missing file.");
                }

                if (KeystorePass == null || KeyAlias == null || KeyPass == null)
                {
                    issues.Add($"{KeystorePassVariable}, {KeyAliasVariable} and {KeyPassVariable} are required with a keystore.");
                }
            }
            else if (RequireSigning)
            {
                issues.Add($"Release builds need the upload keystore ({KeystoreVariable}).");
            }

            if (Version != null && !Regex.IsMatch(Version, @"^\d{1,4}\.\d{1,4}(\.\d{1,6})?$"))
            {
                issues.Add($"{VersionVariable} must look like 1.2 or 1.2.3.");
            }

            if (BuildNumber is int b && (b < 1 || b > MaxBuildNumber))
            {
                issues.Add($"{BuildNumberVariable} must be an integer in 1..{MaxBuildNumber}.");
            }

            return issues;
        }

        /// <summary>Applies signing/version to PlayerSettings until disposed, then restores the previous values.</summary>
        public IDisposable Apply()
        {
            var previous = new Snapshot();
            if (HasKeystore)
            {
                // Unity mangles absolute keystore paths outside the project on Linux ("/tmp/k" becomes "tmp/k"), so
                // build from a copy in the project's Temp folder; the copy is deleted on dispose.
                string copy = Path.Combine("Temp", "release-signing", Guid.NewGuid().ToString("N") + ".keystore");
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(Keystore!, copy);
                previous.KeystoreCopy = copy;
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = copy.Replace('\\', '/');
                PlayerSettings.Android.keystorePass = KeystorePass;
                PlayerSettings.Android.keyaliasName = KeyAlias;
                PlayerSettings.Android.keyaliasPass = KeyPass;
            }

            if (Version != null)
            {
                PlayerSettings.bundleVersion = Version;
            }

            if (BuildNumber is int build)
            {
                PlayerSettings.Android.bundleVersionCode = build;
                PlayerSettings.iOS.buildNumber = build.ToString();
            }

            return previous;
        }

        private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private sealed class Snapshot : IDisposable
        {
            private readonly bool _custom = PlayerSettings.Android.useCustomKeystore;
            private readonly string _keystore = PlayerSettings.Android.keystoreName;
            private readonly string _alias = PlayerSettings.Android.keyaliasName;
            private readonly string _version = PlayerSettings.bundleVersion;
            private readonly int _code = PlayerSettings.Android.bundleVersionCode;
            private readonly string _iosBuild = PlayerSettings.iOS.buildNumber;

            public string? KeystoreCopy { get; set; }

            public void Dispose()
            {
                if (KeystoreCopy != null && File.Exists(KeystoreCopy))
                {
                    File.Delete(KeystoreCopy);
                }

                PlayerSettings.Android.useCustomKeystore = _custom;
                PlayerSettings.Android.keystoreName = _keystore;
                PlayerSettings.Android.keyaliasName = _alias;
                // Passwords are never serialized by Unity, but clear them from memory anyway.
                PlayerSettings.Android.keystorePass = string.Empty;
                PlayerSettings.Android.keyaliasPass = string.Empty;
                PlayerSettings.bundleVersion = _version;
                PlayerSettings.Android.bundleVersionCode = _code;
                PlayerSettings.iOS.buildNumber = _iosBuild;
            }
        }
    }
}
