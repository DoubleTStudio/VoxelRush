using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VoxelRush.Editor
{
    /// <summary>
    /// Batch-mode build entry points, e.g.
    /// <c>Unity -batchmode -quit -projectPath unity -executeMethod VoxelRush.Editor.BuildEntryPoints.BuildLinuxServer -buildOutput Builds/Server/VoxelRushServer.x86_64</c>.
    /// </summary>
    public static class BuildEntryPoints
    {
        public const string ClientScene = "Assets/VoxelRush/Scenes/Client.unity";
        public const string ServerScene = "Assets/VoxelRush/Scenes/Server.unity";

        private const string OutputArgument = "-buildOutput";
        private const string DevelopmentArgument = "-developmentBuild";

        public static void ConfigureProject()
        {
            ProjectConfiguration.Apply();
            IReadOnlyList<string> issues = ProjectConfiguration.Validate();
            foreach (string issue in issues)
            {
                Debug.LogError(issue);
            }

            ExitIfBatchMode(issues.Count == 0 ? 0 : 1);
        }

        public static void BuildAndroid() =>
            Build(BuildTarget.Android, StandaloneBuildSubtarget.Player, ClientScene, "Builds/Android/VoxelRush.apk");

        public static void BuildIos() =>
            Build(BuildTarget.iOS, StandaloneBuildSubtarget.Player, ClientScene, "Builds/iOS/Xcode");

        public static void BuildLinuxServer() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, ServerScene, "Builds/Server/VoxelRushServer.x86_64");

        // Native server for local runs on Apple Silicon, where amd64 Mono crashes under Rosetta.
        public static void BuildMacServer() =>
            Build(BuildTarget.StandaloneOSX, StandaloneBuildSubtarget.Server, ServerScene, "Builds/ServerMac");

        // Desktop client players run headless (-batchmode -nographics -connect) as join-test bots; not shipped.
        public static void BuildLinuxClient() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Player, ClientScene, "Builds/ClientLinux/VoxelRushClient.x86_64");

        public static void BuildMacClient() =>
            Build(BuildTarget.StandaloneOSX, StandaloneBuildSubtarget.Player, ClientScene, "Builds/ClientMac/VoxelRushClient.app");

        [MenuItem("VoxelRush/Build/Android (APK)")]
        private static void BuildAndroidMenu() => BuildAndroid();

        [MenuItem("VoxelRush/Build/iOS (Xcode project)")]
        private static void BuildIosMenu() => BuildIos();

        [MenuItem("VoxelRush/Build/Linux Dedicated Server")]
        private static void BuildLinuxServerMenu() => BuildLinuxServer();

        private static void Build(BuildTarget target, StandaloneBuildSubtarget subtarget, string scene, string defaultOutput)
        {
            string[] args = Environment.GetCommandLineArgs();
            string output = GetArgumentValue(args, OutputArgument) ?? defaultOutput;
            bool development = Array.IndexOf(args, DevelopmentArgument) >= 0;

            var issues = ProjectConfiguration.Validate();
            if (issues.Count > 0)
            {
                Debug.LogError($"Project configuration drift; run VoxelRush/Apply Project Configuration.\n{string.Join("\n", issues)}");
                ExitIfBatchMode(1);
                return;
            }

            string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Scenes serialize against the editor's compiled scripts, whose defines (UNITY_SERVER) follow the
            // active target/subtarget; a mismatch ships players with missing scripts, so refuse to build.
            if (!EditorMatches(target, subtarget))
            {
                EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(target), target);
                Debug.LogError($"Editor was not on {target}/{subtarget}; switched it. Batch mode: start Unity with " +
                    "-buildTarget and -standaloneBuildSubtarget (tools/build.sh does). Interactive: rerun the build after recompilation.");
                ExitIfBatchMode(1);
                return;
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = output,
                target = target,
                targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                subtarget = (int)subtarget,
                options = development ? BuildOptions.Development : BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log($"Build {target}/{subtarget} {summary.result}: {summary.totalErrors} errors, {summary.totalSize} bytes, {summary.totalTime} -> {output}");

            ExitIfBatchMode(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        private static bool EditorMatches(BuildTarget target, StandaloneBuildSubtarget subtarget) =>
            EditorUserBuildSettings.activeBuildTarget == target
            && (BuildPipeline.GetBuildTargetGroup(target) != BuildTargetGroup.Standalone
                || EditorUserBuildSettings.standaloneBuildSubtarget == subtarget);

        private static string? GetArgumentValue(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static void ExitIfBatchMode(int exitCode)
        {
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(exitCode);
            }
        }
    }
}
