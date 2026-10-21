using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VoxelRush.Editor
{
    /// <summary>Single source of truth for platform/player settings; Apply is idempotent, Validate reports drift.</summary>
    public static class ProjectConfiguration
    {
        public const string CompanyName = "DoubleTStudio";
        public const string ProductName = "VoxelRush";
        public const string ApplicationIdentifier = "com.doubletstudio.voxelrush";
        public const AndroidSdkVersions AndroidMinSdk = AndroidSdkVersions.AndroidApiLevel26;
        public const string IosMinimumVersion = "15.0";

        // PlayerSettings.activeInputHandler: 0 = legacy, 1 = Input System package, 2 = both.
        private const int InputSystemOnly = 1;

        // Actions are defined in code (DrivingControls); a project-wide actions asset must not be configured.
        private const string ProjectWideActionsKey = "com.unity.input.settings.actions";

        private static readonly NamedBuildTarget[] MobileTargets = { NamedBuildTarget.Android, NamedBuildTarget.iOS };

        // Client first: index 0 is what mobile players boot into.
        public static readonly string[] BuildScenes = { BuildEntryPoints.ClientScene, BuildEntryPoints.ServerScene };

        [MenuItem("VoxelRush/Apply Project Configuration")]
        public static void Apply()
        {
            PlayerSettings.companyName = CompanyName;
            PlayerSettings.productName = ProductName;
            PlayerSettings.colorSpace = ColorSpace.Linear;

            foreach (NamedBuildTarget target in MobileTargets)
            {
                PlayerSettings.SetApplicationIdentifier(target, ApplicationIdentifier);
                PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
                PlayerSettings.SetApiCompatibilityLevel(target, ApiCompatibilityLevel.NET_Standard);
            }

            // Linux server builds belong to the Standalone group.
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, ApplicationIdentifier);

            // Mono until VR-070: Linux IL2CPP needs host-specific toolchain packages.
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.Mono2x);
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Server, ApiCompatibilityLevel.NET_Standard);

            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidMinSdk;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.iOS.targetOSVersionString = IosMinimumVersion;
            // MVP-004: dev builds may reach a LAN/Tailscale API over plain HTTP; release builds need HTTPS.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;

            var scenes = new EditorBuildSettingsScene[BuildScenes.Length];
            for (int i = 0; i < BuildScenes.Length; i++)
            {
                scenes[i] = new EditorBuildSettingsScene(BuildScenes[i], true);
            }

            EditorBuildSettings.scenes = scenes;

            EditorBuildSettings.RemoveConfigObject(ProjectWideActionsKey);

            SerializedObject playerSettings = GetPlayerSettingsObject();
            playerSettings.FindProperty("activeInputHandler").intValue = InputSystemOnly;
            playerSettings.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.SaveAssets();
            Debug.Log("VoxelRush project configuration applied.");
        }

        public static IReadOnlyList<string> Validate()
        {
            var issues = new List<string>();

            Expect(issues, "companyName", PlayerSettings.companyName, CompanyName);
            Expect(issues, "productName", PlayerSettings.productName, ProductName);
            Expect(issues, "colorSpace", PlayerSettings.colorSpace, ColorSpace.Linear);

            foreach (NamedBuildTarget target in MobileTargets)
            {
                Expect(issues, $"{target.TargetName}.applicationIdentifier", PlayerSettings.GetApplicationIdentifier(target), ApplicationIdentifier);
                Expect(issues, $"{target.TargetName}.scriptingBackend", PlayerSettings.GetScriptingBackend(target), ScriptingImplementation.IL2CPP);
            }

            Expect(issues, "Standalone.applicationIdentifier", PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Standalone), ApplicationIdentifier);
            Expect(issues, "Server.scriptingBackend", PlayerSettings.GetScriptingBackend(NamedBuildTarget.Server), ScriptingImplementation.Mono2x);

            Expect(issues, "insecureHttpOption", PlayerSettings.insecureHttpOption, InsecureHttpOption.DevelopmentOnly);
            Expect(issues, "defaultInterfaceOrientation", PlayerSettings.defaultInterfaceOrientation, UIOrientation.AutoRotation);
            Expect(issues, "autorotateLandscapeLeft", PlayerSettings.allowedAutorotateToLandscapeLeft, true);
            Expect(issues, "autorotateLandscapeRight", PlayerSettings.allowedAutorotateToLandscapeRight, true);
            Expect(issues, "autorotatePortrait", PlayerSettings.allowedAutorotateToPortrait, false);
            Expect(issues, "autorotatePortraitUpsideDown", PlayerSettings.allowedAutorotateToPortraitUpsideDown, false);

            Expect(issues, "Android.targetArchitectures", PlayerSettings.Android.targetArchitectures, AndroidArchitecture.ARM64);
            Expect(issues, "Android.minSdkVersion", PlayerSettings.Android.minSdkVersion, AndroidMinSdk);
            Expect(issues, "iOS.targetOSVersionString", PlayerSettings.iOS.targetOSVersionString, IosMinimumVersion);

            Expect(issues, "activeInputHandler", GetPlayerSettingsObject().FindProperty("activeInputHandler").intValue, InputSystemOnly);

            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;
            Expect(issues, "EditorBuildSettings.scenes.Length", buildScenes.Length, BuildScenes.Length);
            for (int i = 0; i < buildScenes.Length && i < BuildScenes.Length; i++)
            {
                Expect(issues, $"EditorBuildSettings.scenes[{i}]", buildScenes[i].path, BuildScenes[i]);
                Expect(issues, $"EditorBuildSettings.scenes[{i}].enabled", buildScenes[i].enabled, true);
            }

            if (EditorBuildSettings.TryGetConfigObject(ProjectWideActionsKey, out UnityEngine.Object _)
                || ConfigKeyIsSerialized(ProjectWideActionsKey))
            {
                issues.Add($"EditorBuildSettings has a '{ProjectWideActionsKey}' config object; input actions are defined in code.");
            }

            if (!(GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset))
            {
                issues.Add("GraphicsSettings.defaultRenderPipeline is not a UniversalRenderPipelineAsset.");
            }

            for (int level = 0; level < QualitySettings.names.Length; level++)
            {
                RenderPipelineAsset? levelPipeline = QualitySettings.GetRenderPipelineAssetAt(level);
                if (levelPipeline != null && !(levelPipeline is UniversalRenderPipelineAsset))
                {
                    issues.Add($"Quality level '{QualitySettings.names[level]}' overrides a non-URP pipeline.");
                }
            }

            return issues;
        }

        // TryGetConfigObject returns false for a key whose asset was deleted, so also check the serialized map.
        private static bool ConfigKeyIsSerialized(string key)
        {
            string path = System.IO.Path.Combine("ProjectSettings", "EditorBuildSettings.asset");
            return System.IO.File.Exists(path) && System.IO.File.ReadAllText(path).Contains(key + ":");
        }

        private static SerializedObject GetPlayerSettingsObject() =>
            new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));

        private static void Expect<T>(List<string> issues, string name, T actual, T expected)
        {
            if (!EqualityComparer<T>.Default.Equals(actual, expected))
            {
                issues.Add($"{name}: expected '{expected}', found '{actual}'.");
            }
        }
    }
}
