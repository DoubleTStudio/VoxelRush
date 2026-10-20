using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using VoxelRush.Destruction;
using VoxelRush.Presentation.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>DES-005 DoD: debris is pooled, local and never networked or physics-simulated.</summary>
    public sealed class DebrisEffectsTests
    {
        private GameObject? _host;

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
            }
        }

        private DebrisEffects Create(int capacity)
        {
            _host = new GameObject("DebrisTest");
            var effects = _host.AddComponent<DebrisEffects>();
            effects.Configure(capacity, new DebrisPlanner(piecesPerBlock: 3, maxPiecesPerFrame: 96));
            return effects;
        }

        [Test]
        public void DoD_BlastDebris_IsPooledAndHasNoRigidbodiesCollidersOrNetworking()
        {
            DebrisEffects effects = Create(64);

            // A real 256-block blast on the server, replicated: removals reach the effects through the replica hook.
            var gen = new FlatPlotGenerator();
            var coords = new[] { new ChunkCoord(-1, -1, -1), new ChunkCoord(-1, -1, 0), new ChunkCoord(0, -1, -1), new ChunkCoord(0, -1, 0) };
            var server = new WorldAuthority(new EditValidator(ZoneMap.Empty));
            var replica = new ClientWorldReplica(new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, coords[0], coords[3]));
            foreach (ChunkCoord c in coords)
            {
                server.AddChunk(new ChunkData(c, gen.GeneratorVersion, gen.GenerateBlocks(c)));
                replica.ApplySnapshot(ChunkSnapshotMessage.From(server.GetChunk(c)!));
            }

            effects.Attach(replica);
            ExplosionOutcome blast = new ExplosionResolver(new ExplosionModel(), ZoneMap.Empty, new DamageLedger(MaterialCatalog.Default))
                .Explode(server, Guid.NewGuid(), "x", 0f, -1f, 0f, 4f, 400, 1);
            foreach (ChunkMutationBatch b in blast.Commit.Batches)
            {
                replica.ApplyDelta(b);
            }

            Assert.That(blast.Destroyed, Is.GreaterThan(20));
            Assert.That(effects.Spawned, Is.EqualTo(96), "per-frame cap");
            Assert.That(effects.ActiveCount, Is.LessThanOrEqualTo(64), "pool capacity");
            Assert.That(effects.Recycled, Is.EqualTo(32), "oldest pieces reused, nothing new allocated");

            Component[] components = _host!.GetComponentsInChildren<Component>(includeInactive: true);
            Assert.That(components.OfType<Rigidbody>(), Is.Empty);
            Assert.That(components.OfType<Collider>(), Is.Empty);
            string[] allowed = { "Transform", "MeshFilter", "MeshRenderer", nameof(DebrisEffects) };
            Assert.That(components.Select(c => c.GetType().Name).Distinct().Except(allowed), Is.Empty, "no NetworkObject or other components");
            Assert.That(_host.transform.childCount, Is.EqualTo(64));
        }

        [Test]
        public void Pieces_FallAndExpire()
        {
            DebrisEffects effects = Create(16);
            effects.SpawnFor(new BlockPos(0, 10, 0), BlockIds.Stone);
            Transform piece = effects.PooledTransforms.First(t => t.gameObject.activeSelf);
            float startY = piece.position.y;

            for (int i = 0; i < 30; i++)
            {
                effects.Tick(1f / 30f);
            }

            Assert.That(piece.position.y, Is.LessThan(startY), "gravity");
            for (int i = 0; i < 20; i++)
            {
                effects.Tick(1f / 30f);
            }

            Assert.That(effects.ActiveCount, Is.Zero, "expired after the lifetime");
            Assert.That(effects.PooledTransforms.Count(t => t.gameObject.activeSelf), Is.Zero);
        }

        [Test]
        public void SteadyState_SpawnAndTick_DoNotAllocate()
        {
            DebrisEffects effects = Create(64);
            effects.SpawnFor(new BlockPos(0, 0, 0), BlockIds.Stone);   // warm the pool
            effects.Tick(0.016f);
            var block = new BlockPos(1, 2, 3);

            Assert.That(() =>
            {
                effects.SpawnFor(block, BlockIds.Wood);
                effects.Tick(0.016f);
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Detach_StopsListening()
        {
            DebrisEffects effects = Create(8);
            var coord = new ChunkCoord(0, -1, 0);
            var gen = new FlatPlotGenerator();
            var replica = new ClientWorldReplica(new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, coord, coord));
            replica.ApplySnapshot(ChunkSnapshotMessage.From(new ChunkData(coord, gen.GeneratorVersion, gen.GenerateBlocks(coord))));
            effects.Attach(replica);
            effects.Detach();

            replica.ApplyDelta(new ChunkMutationBatch(Guid.NewGuid(), "s", coord, 0, 1, 1, new List<BlockChange> { new BlockChange((ushort)new LocalBlock(0, 15, 0).Index, BlockIds.Air) }));

            Assert.That(effects.Spawned, Is.Zero);
            Assert.That(replica.BlockChanged, Is.Null);
        }
    }
}
