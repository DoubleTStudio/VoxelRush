using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoxelRush.Editor;

namespace VoxelRush.Tests.EditMode
{
    public sealed class BuildSceneTests
    {
        // Matched by name: Presentation is compiled out while the editor targets the dedicated server.
        [TestCase(BuildEntryPoints.ClientScene, "VoxelRush.Presentation.ClientSmokeProbe")]
        [TestCase(BuildEntryPoints.ClientScene, "VoxelRush.Networking.ClientBootstrap")]
        [TestCase(BuildEntryPoints.BotScene, "VoxelRush.Networking.ClientBootstrap")]
        [TestCase(BuildEntryPoints.ServerScene, "VoxelRush.Server.ServerHost")]
        public void Scene_ContainsExactlyOneEntryComponent(string scenePath, string componentType)
        {
            if (!IsTypeLoaded(componentType))
            {
                Assert.Fail($"{componentType} is not compiled for the active build target; run tests with a client target (tools/test.sh does).");
            }

            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                int count = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                    .Count(component => component != null && component.GetType().FullName == componentType);

                Assert.That(count, Is.EqualTo(1));
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static bool IsTypeLoaded(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetType(fullName, false) != null);

        [TestCase(BuildEntryPoints.ServerScene)]
        [TestCase(BuildEntryPoints.BotScene)]
        public void HeadlessScene_HasNoCameraOrAudioListener(string scenePath)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                var roots = scene.GetRootGameObjects();
                Assert.That(roots.SelectMany(r => r.GetComponentsInChildren<Camera>(true)), Is.Empty);
                Assert.That(roots.SelectMany(r => r.GetComponentsInChildren<AudioListener>(true)), Is.Empty);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
