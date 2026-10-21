using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Core.Performance;
using VoxelRush.Presentation.Quality;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>VR-071: tier presets reach URP without touching the project asset; the benchmark runs and writes its result.</summary>
    public sealed class QualityBenchmarkTests
    {
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return TestScenes.UnloadAll();
            yield return SceneManager.LoadSceneAsync("Assets/VoxelRush/Scenes/Client.unity", LoadSceneMode.Additive);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            yield return TestScenes.UnloadAll();
        }

        [UnityTest]
        public IEnumerator Presets_ApplyToARuntimeCopy_AndRestoreTheProjectAsset()
        {
            QualityController quality = QualityController.Instance!;
            Assert.That(quality, Is.Not.Null, "Client.unity has a QualityController");
            RenderPipelineAsset projectAsset = GraphicsSettings.defaultRenderPipeline;
            float projectScale = ((UniversalRenderPipelineAsset)(QualitySettings.GetRenderPipelineAssetAt(QualitySettings.GetQualityLevel()) ?? projectAsset)).renderScale;

            quality.Adaptive = false;
            quality.ForceTier(QualityTier.Low);
            yield return null;

            Assert.That(QualitySettings.renderPipeline, Is.SameAs(quality.RuntimeAsset));
            Assert.That(quality.RuntimeAsset!.renderScale, Is.EqualTo(0.7f).Within(1e-4f));
            Assert.That(quality.RuntimeAsset.shadowDistance, Is.Zero);
            Assert.That(Application.targetFrameRate, Is.EqualTo(30));

            quality.ForceTier(QualityTier.High);
            Assert.That(quality.RuntimeAsset.renderScale, Is.EqualTo(1f));
            Assert.That(Application.targetFrameRate, Is.EqualTo(60));
            float unchanged = ((UniversalRenderPipelineAsset)(QualitySettings.GetRenderPipelineAssetAt(QualitySettings.GetQualityLevel()) ?? projectAsset)).renderScale;
            Assert.That(unchanged, Is.EqualTo(projectScale), "project asset untouched");
        }

        [UnityTest]
        public IEnumerator Benchmark_RendersTerrain_AndWritesResult()
        {
            BenchmarkRunner runner = Object.FindFirstObjectByType<BenchmarkRunner>()!;
            Assert.That(runner, Is.Not.Null, "Client.unity has a BenchmarkRunner");

            yield return runner.Run(seconds: 2f, warmup: 0.5f);

            Assert.That(runner.LastResultPath, Is.Not.Null);
            Assert.That(File.Exists(runner.LastResultPath!), Is.True);
            string json = File.ReadAllText(runner.LastResultPath!);
            StringAssert.Contains("\"p95Ms\"", json);
            StringAssert.Contains("\"tierStart\"", json);
            Assert.That(Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).Length, Is.GreaterThan(20), "terrain chunks rendered");
            File.Delete(runner.LastResultPath!);
            TestContext.WriteLine(json);
        }
    }
}
