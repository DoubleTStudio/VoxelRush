#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Presentation.World;
using VoxelRush.World.Chunks;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>VR-022 DoD: terrain renders as one GameObject + mesh per non-empty chunk, never per voxel.</summary>
    public sealed class VoxelTerrainViewTests
    {
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return TestScenes.UnloadAll();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/VoxelRush/Scenes/VoxelPreview.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => TestScenes.UnloadAll();

        [Test]
        public void Terrain_HasOneObjectPerNonEmptyChunk_NotPerVoxel()
        {
            var view = Object.FindAnyObjectByType<VoxelTerrainView>();
            Assert.That(view, Is.Not.Null);
            Assert.That(view!.ChunksGenerated, Is.EqualTo(64));

            int chunkObjects = view.ChunkObjects.Count;
            Assert.That(chunkObjects, Is.InRange(1, view.ChunksGenerated), "only non-empty chunks get an object");
            Assert.That(view.transform.childCount, Is.EqualTo(chunkObjects));

            int renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).Length;
            Assert.That(renderers, Is.EqualTo(chunkObjects), $"{renderers} renderers for {chunkObjects} chunks: something is spawning per-voxel objects");

            int solidVoxels = 0;
            long triangles = 0;
            foreach (GameObject chunk in view.ChunkObjects.Values)
            {
                Mesh mesh = chunk.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(mesh.vertexCount, Is.GreaterThan(0));
                Assert.That(mesh.bounds.size.x, Is.LessThanOrEqualTo(ChunkMath.Size + 0.01f));
                triangles += mesh.triangles.Length / 3;
                solidVoxels += ChunkMath.Volume;
            }

            Assert.That(triangles, Is.LessThan(solidVoxels), "greedy meshing keeps triangles far below the voxel count");
        }

        [UnityTest]
        public IEnumerator Rebuild_ReplacesChunkObjects_WithoutLeaking()
        {
            var view = Object.FindAnyObjectByType<VoxelTerrainView>()!;
            int before = view.ChunkObjects.Count;

            view.Build();
            // Destroy() completes at the end of the frame.
            yield return null;

            Assert.That(view.ChunkObjects.Count, Is.EqualTo(before));
            Assert.That(Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).Length, Is.EqualTo(before), "old chunk objects leaked");
        }
    }
}
#endif
