using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoxelRush.Gameplay.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>MVP-003: placed blocks are solid, and removed ones stop colliding.</summary>
    public sealed class WorldCollidersTests
    {
        private GameObject? _host;

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
            }
        }

        [UnityTest]
        public IEnumerator PlacedBlock_GetsACollider_AndLosesItWhenRemoved()
        {
            var info = new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, new ChunkCoord(-1, -1, -1), new ChunkCoord(0, 0, 0));
            var gen = new FlatPlotGenerator();
            var chunks = new Dictionary<ChunkCoord, ChunkData>();
            foreach (ChunkCoord c in info.RegionChunks())
            {
                chunks[c] = new ChunkData(c, gen.GeneratorVersion, gen.GenerateBlocks(c));
            }

            _host = new GameObject("Colliders");
            var colliders = _host.AddComponent<WorldColliders>();
            colliders.Bind(() => info, c => chunks.TryGetValue(c, out ChunkData chunk) ? chunk : null);
            colliders.Refresh(all: true);
            Physics.SyncTransforms();

            Assert.That(colliders.ColliderCount, Is.EqualTo(4), "the four ground chunks");
            Assert.That(Physics.Raycast(new Vector3(3.5f, 10f, 3.5f), Vector3.down, out RaycastHit ground, 20f), Is.True);
            Assert.That(ground.point.y, Is.EqualTo(0f).Within(0.01f), "plot ground top");

            // Stack two blocks at (3, 0..1, 3): a car would now hit a 2 m wall.
            ChunkData air = chunks[new ChunkCoord(0, 0, 0)];
            air.Set(new LocalBlock(3, 0, 3), BlockIds.Brick);
            air.Set(new LocalBlock(3, 1, 3), BlockIds.Brick);
            colliders.Refresh();
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();

            Assert.That(Physics.Raycast(new Vector3(3.5f, 10f, 3.5f), Vector3.down, out RaycastHit top, 20f), Is.True);
            Assert.That(top.point.y, Is.EqualTo(2f).Within(0.01f), "placed blocks collide");
            Assert.That(Physics.Raycast(new Vector3(-2f, 0.5f, 3.5f), Vector3.right, 20f), Is.True, "blocks block a car driving into them");

            air.Set(new LocalBlock(3, 0, 3), BlockIds.Air);
            air.Set(new LocalBlock(3, 1, 3), BlockIds.Air);
            colliders.Refresh();
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();

            Assert.That(Physics.Raycast(new Vector3(3.5f, 10f, 3.5f), Vector3.down, out RaycastHit cleared, 20f), Is.True);
            Assert.That(cleared.point.y, Is.EqualTo(0f).Within(0.01f), "removed blocks stop colliding");
            Assert.That(colliders.ColliderCount, Is.EqualTo(4));
        }
    }
}
