using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Streaming;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>DES-012: collider rebuild priority under destruction.</summary>
    public sealed class ColliderRebuildPriorityTests
    {
        private sealed class FlatSource : IChunkSource
        {
            public ChunkData Load(ChunkCoord coord)
            {
                var blocks = new byte[ChunkMath.Volume];
                if (coord.Y == 0)
                {
                    for (int i = 0; i < ChunkMath.Size * ChunkMath.Size * 4; i++)
                    {
                        blocks[i] = BlockIds.Stone;
                    }
                }

                return new ChunkData(coord, 1, blocks);
            }
        }

        private sealed class CountingBackend : IChunkBackend
        {
            public readonly List<ChunkCoord> Built = new List<ChunkCoord>();

            public void BuildCollider(ChunkData chunk, ChunkData?[] neighbours) => Built.Add(chunk.Coord);

            public void Remove(ChunkCoord coord)
            {
            }
        }

        private static StreamingSettings Settings(int radius = 3) => new StreamingSettings
        {
            RadiusChunks = radius,
            MinChunkY = 0,
            MaxChunkY = 0,
            GenerateBudget = 100,
            ColliderBudget = 100,
            UnloadHysteresisChunks = 1,
        };

        private static ChunkStreamer Loaded(out CountingBackend backend, float x = 8, float z = 8, int radius = 3)
        {
            backend = new CountingBackend();
            var streamer = new ChunkStreamer(new FlatSource(), backend, Settings(radius));
            for (int i = 0; i < 5; i++)
            {
                streamer.Step(At(x, z));
            }

            streamer.Settings.ColliderBudget = 2;
            backend.Built.Clear();
            return streamer;
        }

        private static StreamingObserver[] At(float x, float z, float vx = 0, float vz = 0) => new[] { new StreamingObserver(x, 1, z, vx, vz) };

        private static void Remove(ChunkStreamer s, BlockPos block)
        {
            ChunkCoord c = ChunkMath.ToChunk(block);
            LocalBlock local = ChunkMath.ToLocal(block);
            if (s.Get(c)!.Set(local, BlockIds.Air))
            {
                s.MarkChanged(c, local);
            }
        }

        [Test]
        public void BorderBlock_MarksTheNeighbourToo_InteriorDoesNot()
        {
            ChunkStreamer s = Loaded(out _);

            Remove(s, new BlockPos(15, 3, 8));                       // +X border of chunk (0,0,0)
            Assert.That(s.IsColliderCurrent(new ChunkCoord(0, 0, 0)), Is.False);
            Assert.That(s.IsColliderCurrent(new ChunkCoord(1, 0, 0)), Is.False, "its border faces depended on that block");
            Assert.That(s.IsColliderCurrent(new ChunkCoord(0, 0, 1)), Is.True);

            Remove(s, new BlockPos(40, 3, 40));                      // interior of chunk (2,0,2)
            Assert.That(s.IsColliderCurrent(new ChunkCoord(2, 0, 2)), Is.False);
            Assert.That(new[] { new ChunkCoord(1, 0, 2), new ChunkCoord(3, 0, 2), new ChunkCoord(2, 0, 1), new ChunkCoord(2, 0, 3) }.All(s.IsColliderCurrent), Is.True);
            Assert.That(s.StaleCount, Is.EqualTo(3));
        }

        [Test]
        public void Critical_RebuildsOnlyNearTheCar_NearestFirst()
        {
            ChunkStreamer s = Loaded(out CountingBackend backend);
            Remove(s, new BlockPos(40, 3, 40));                      // far (chunk 2,0,2)
            Remove(s, new BlockPos(8, 3, 8));                        // under the car
            Remove(s, new BlockPos(20, 3, 8));                       // next chunk, within 8 m

            int rebuilt = s.RefreshCritical(At(12, 8));

            Assert.That(rebuilt, Is.EqualTo(2));
            Assert.That(backend.Built, Is.EqualTo(new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(1, 0, 0) }), "nearest first");
            Assert.That(s.IsColliderCurrent(new ChunkCoord(2, 0, 2)), Is.False, "far changes wait for the background budget");
        }

        [Test]
        public void Critical_LooksAhead_AlongVelocity()
        {
            ChunkStreamer s = Loaded(out CountingBackend backend);
            Remove(s, new BlockPos(40, 3, 8));                       // chunk (2,0,0) starts 24 m ahead; 0.5 s at 50 m/s reaches it

            Assert.That(s.RefreshCritical(At(8, 8)), Is.Zero);
            Assert.That(s.RefreshCritical(At(8, 8, vx: 50)), Is.EqualTo(1));
        }

        [Test]
        public void Critical_CatchesRevisionChanges_WithoutMarkChanged()
        {
            ChunkStreamer s = Loaded(out _);
            s.Get(new ChunkCoord(0, 0, 0))!.Set(new LocalBlock(4, 3, 4), BlockIds.Air);

            Assert.That(s.RefreshCritical(At(8, 8)), Is.EqualTo(1));
            Assert.That(s.IsColliderCurrent(new ChunkCoord(0, 0, 0)), Is.True);
        }

        [Test]
        public void Critical_Budget_DefersTheRest_AndCountsIt()
        {
            ChunkStreamer s = Loaded(out _);
            s.Settings.CriticalColliderBudget = 1;
            s.Settings.CriticalDistance = 20;
            Remove(s, new BlockPos(8, 3, 8));
            Remove(s, new BlockPos(24, 3, 8));

            s.RefreshCritical(At(8, 8));

            Assert.That(s.IsColliderCurrent(new ChunkCoord(0, 0, 0)), Is.True, "nearest got the budget");
            Assert.That(s.CriticalDeferred, Is.EqualTo(1));
            s.RefreshCritical(At(8, 8));
            Assert.That(s.IsColliderCurrent(new ChunkCoord(1, 0, 0)), Is.True);
        }

        [Test]
        public void Background_IsNearestFirst_WithinBudget()
        {
            ChunkStreamer s = Loaded(out CountingBackend backend);
            Remove(s, new BlockPos(56, 3, 56));                      // chunk (3,0,3), far
            Remove(s, new BlockPos(40, 3, 8));                       // chunk (2,0,0)
            Remove(s, new BlockPos(-24, 3, 8));                      // chunk (-2,0,0)

            s.RefreshStaleColliders(At(8, 8));

            Assert.That(backend.Built, Is.EqualTo(new[] { new ChunkCoord(-2, 0, 0), new ChunkCoord(2, 0, 0) }).Or.EqualTo(new[] { new ChunkCoord(2, 0, 0), new ChunkCoord(-2, 0, 0) }));
            Assert.That(s.IsColliderCurrent(new ChunkCoord(3, 0, 3)), Is.False, "budget 2");
        }

        [Test]
        public void DoD_NoStaleCollisionNearCars_UnderContinuousDestruction()
        {
            // A car at 28 m/s for 20 s while blocks are blown out every tick — right in front of it, beside it, and far
            // away — and streaming runs only every 4th tick. Before each physics step, every chunk within the critical
            // distance must have a collider built from its current blocks.
            Assert.That(Run(useCritical: true, out long deferred), Is.Zero);
            Assert.That(deferred, Is.Zero);

            // Control: with only the old background refresh the same scenario does drive next to stale colliders.
            Assert.That(Run(useCritical: false, out _), Is.GreaterThan(0));
        }

        private static int Run(bool useCritical, out long deferred)
        {
            var backend = new CountingBackend();
            ChunkStreamer s = new ChunkStreamer(new FlatSource(), backend, Settings(radius: 3));
            var rng = new Random(7);
            float x = 8, z = 8;
            const float vx = 28f, dt = 1f / 30f;
            for (int i = 0; i < 10; i++)
            {
                s.Step(At(x, z, vx));
            }

            s.Settings.GenerateBudget = 4;
            s.Settings.ColliderBudget = 2;
            int staleNearCar = 0;
            for (int tick = 0; tick < 600; tick++)
            {
                StreamingObserver[] car = At(x, z, vx);
                if (tick % 4 == 0)
                {
                    s.Step(car);
                    s.RefreshStaleColliders(car);
                }

                // Destruction this tick: a few blocks 2..12 m ahead of the car, one beside it, and a blast field far behind.
                for (int k = 0; k < 3; k++)
                {
                    TryRemove(s, new BlockPos((int)x + rng.Next(2, 12), rng.Next(0, 4), (int)z + rng.Next(-6, 6)));
                }

                TryRemove(s, new BlockPos((int)x + rng.Next(-3, 3), rng.Next(0, 4), (int)z + 6));
                for (int k = 0; k < 6; k++)
                {
                    TryRemove(s, new BlockPos((int)x - rng.Next(20, 40), rng.Next(0, 4), (int)z + rng.Next(-30, 30)));
                }

                if (useCritical)
                {
                    s.RefreshCritical(car);
                }

                // "Physics step": every chunk within 8 m of the car (point-to-chunk distance) must be current.
                for (int cx = ChunkMath.FloorDiv((int)(x - 8)); cx <= ChunkMath.FloorDiv((int)(x + 8)); cx++)
                {
                    for (int cz = ChunkMath.FloorDiv((int)(z - 8)); cz <= ChunkMath.FloorDiv((int)(z + 8)); cz++)
                    {
                        var coord = new ChunkCoord(cx, 0, cz);
                        float dx = Math.Max(0, Math.Max(cx * 16 - x, x - (cx * 16 + 16)));
                        float dz = Math.Max(0, Math.Max(cz * 16 - z, z - (cz * 16 + 16)));
                        if (dx * dx + dz * dz <= 64 && s.StateOf(coord) == ChunkState.Ready && !s.IsColliderCurrent(coord))
                        {
                            staleNearCar++;
                        }
                    }
                }

                x += vx * dt;
            }

            deferred = s.CriticalDeferred;
            return staleNearCar;
        }

        private static void TryRemove(ChunkStreamer s, BlockPos block)
        {
            ChunkCoord c = ChunkMath.ToChunk(block);
            if (s.StateOf(c) != ChunkState.Unloaded)
            {
                Remove(s, block);
            }
        }

        [Test]
        public void Unload_ForgetsStaleness()
        {
            ChunkStreamer s = Loaded(out _, radius: 1);
            Remove(s, new BlockPos(20, 3, 8));
            for (int i = 0; i < 20; i++)
            {
                s.Step(At(500, 500));
            }

            Assert.That(s.StateOf(new ChunkCoord(1, 0, 0)), Is.EqualTo(ChunkState.Unloaded));
            Assert.That(s.StaleCount, Is.Zero);
        }
    }
}
