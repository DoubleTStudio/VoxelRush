using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Presentation;

namespace VoxelRush.Tests.PlayMode
{
    public sealed class ClientSceneSmokeTests
    {
        private const string ClientSceneName = "Client";
        private const int MaxFramesToWait = 120;

        [UnityTest]
        public IEnumerator ClientScene_BootsWithUrpAndReportsReady()
        {
            yield return SceneManager.LoadSceneAsync(ClientSceneName, LoadSceneMode.Single);

            ClientSmokeProbe? probe = null;
            for (int frame = 0; frame < MaxFramesToWait && (probe == null || !probe.IsReady); frame++)
            {
                probe = Object.FindAnyObjectByType<ClientSmokeProbe>();
                yield return null;
            }

            Assert.That(probe, Is.Not.Null, "ClientSmokeProbe not found in Client scene.");
            Assert.That(probe!.IsReady, Is.True, $"Client not ready after {MaxFramesToWait} frames.");
            Assert.That(probe.RenderPipelineName, Is.EqualTo(nameof(UniversalRenderPipelineAsset)));
            Assert.That(Object.FindAnyObjectByType<Camera>(), Is.Not.Null);
        }
    }
}
