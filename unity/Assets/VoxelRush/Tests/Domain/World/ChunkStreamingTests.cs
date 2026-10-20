using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Streaming;

namespace VoxelRush.Tests.Domain.World
{
    public sealed class ChunkStreamingTests
    {
        private sealed class FlatSource : IChunkSource
        {
            public int Loads;

            public ChunkData Load(ChunkCoord coord)
            {
                Loads++;
                var blocks = new byte[ChunkMath.Volume];
                if (coord.Y == 0)
                {
                    for (int i = 0; i < ChunkMath.Size * ChunkMath.Size; i++)
                    {
                        blocks[i] = BlockIds.Stone;
                    }
                }

                return new ChunkData(coord, 1, blocks);
            }
        }

        private sealed class RecordingBackend : IChunkBackend
        {
            public readonly List<ChunkCoord> Built = new List<ChunkCoord>();
            public readonly List<ChunkCoord> Removed = new List<ChunkCoord>();
            public readonly List<ChunkData?[]> NeighboursAtBuild = new List<ChunkData?[]>();

            public void BuildCollider(ChunkData chunk, ChunkData?[] neighbours)
            {
                Built.Add(chunk.Coord);
                NeighboursAtBuild.Add((ChunkData?[])neighbours.Clone());
            }

            public void Remove(ChunkCoord coord) => Removed.Add(coord);
        }

        private static StreamingSettings Settings(int generate = 4, int collider = 2, int radius = 2) => new StreamingSettings
        {
            RadiusChunks = radius,
            MinChunkY = 0,
            MaxChunkY = 0,
            GenerateBudget = generate,
            ColliderBudget = collider,
            UnloadHysteresisChunks = 1,
        };

        private static StreamingObserver[] At(float x, float z, float vx = 0, float vz = 0) => new[] { new StreamingObserver(x, 1, z, vx, vz) };

        [Test]
        public void Step_RespectsPerTickBudgets()
        {
            var source = new FlatSource();
            var backend = new RecordingBackend();
            var streamer = new ChunkStreamer(source, backend, Settings(generate: 3, collider: 1));

            streamer.Step(At(8, 8));
            Assert.That(source.Loads, Is.EqualTo(3));
            Assert.That(backend.Built.Count, Is.LessThanOrEqualTo(1));

            streamer.Step(At(8, 8));
            Assert.That(source.Loads, Is.EqualTo(6));
            Assert.That(backend.Built.Count, Is.LessThanOrEqualTo(2));
        }

        [Test]
        public void Stationary_EventuallyMakesWholeRadiusReady_UnderVehicleFirst()
        {
            var backend = new RecordingBackend();
            var streamer = new ChunkStreamer(new FlatSource(), backend, Settings());
            for (int i = 0; i < 40; i++)
            {
                streamer.Step(At(-8, 40));
            }

            for (int x = -3; x <= 1; x++)
            {
                for (int z = 0; z <= 4; z++)
                {
                    Assert.That(streamer.IsColumnReady(x, z), Is.True, $"column ({x},{z})");
                }
            }

            Assert.That(backend.Built[0], Is.EqualTo(new ChunkCoord(-1, 0, 2)), "column under the vehicle first (negative x)");
        }

        [Test]
        public void Moving_StreamsAheadBeforeBehind()
        {
            var source = new FlatSource();
            var streamer = new ChunkStreamer(source, new RecordingBackend(), Settings(generate: 6, collider: 2, radius: 3));
            streamer.Step(At(8, 8, vx: 0, vz: 28));

            // First generation batch: under the vehicle, then toward +Z.
            Assert.That(streamer.StateOf(new ChunkCoord(0, 0, 0)), Is.Not.EqualTo(ChunkState.Unloaded));
            Assert.That(streamer.StateOf(new ChunkCoord(0, 0, 1)), Is.Not.EqualTo(ChunkState.Unloaded));
            Assert.That(streamer.StateOf(new ChunkCoord(0, 0, -2)), Is.EqualTo(ChunkState.Unloaded), "behind waits");
        }

        [Test]
        public void Colliders_WaitForDesiredNeighbours_SoBordersHaveNoWalls()
        {
            var backend = new RecordingBackend();
            var streamer = new ChunkStreamer(new FlatSource(), backend, Settings(generate: 1, collider: 4, radius: 1));
            for (int i = 0; i < 30; i++)
            {
                streamer.Step(At(8, 8));
            }

            Assert.That(backend.Built, Is.Not.Empty);
            for (int b = 0; b < backend.Built.Count; b++)
            {
                ChunkCoord c = backend.Built[b];
                ChunkData?[] n = backend.NeighboursAtBuild[b];
                // Horizontal neighbours inside the 3×3 desired area must have been supplied.
                if (Math.Abs(c.X + 1) <= 1 && c.X + 1 <= 1) Assert.That(n[0], Is.Not.Null, $"{c} +X");
                if (Math.Abs(c.X - 1) <= 1) Assert.That(n[1], Is.Not.Null, $"{c} -X");
            }
        }

        [Test]
        public void Unload_UsesHysteresis()
        {
            var backend = new RecordingBackend();
            var streamer = new ChunkStreamer(new FlatSource(), backend, Settings(generate: 50, collider: 50, radius: 1));
            streamer.Step(At(8, 8));
            streamer.Step(At(8, 8));
            Assert.That(streamer.IsColumnReady(-1, 0), Is.True);

            // One chunk east: column -1 leaves the radius (1) but stays within radius + hysteresis (2).
            streamer.Step(At(8 + 16, 8));
            Assert.That(streamer.StateOf(new ChunkCoord(-1, 0, 0)), Is.Not.EqualTo(ChunkState.Unloaded), "within radius + hysteresis");

            streamer.Step(At(8 + 160, 8));
            Assert.That(streamer.StateOf(new ChunkCoord(-1, 0, 0)), Is.EqualTo(ChunkState.Unloaded));
            Assert.That(backend.Removed, Does.Contain(new ChunkCoord(-1, 0, 0)));
        }

        [Test]
        public void ColliderRevision_TracksEdits_AndStaleCollidersRebuild()
        {
            var backend = new RecordingBackend();
            var streamer = new ChunkStreamer(new FlatSource(), backend, Settings(generate: 50, collider: 50, radius: 0));
            streamer.Step(At(8, 8));
            var coord = new ChunkCoord(0, 0, 0);
            Assert.That(streamer.ColliderRevision(coord), Is.EqualTo(0UL));

            streamer.Get(coord)!.Set(new LocalBlock(3, 0, 3), BlockIds.Air);
            int builds = backend.Built.Count;

            Assert.That(streamer.RefreshStaleColliders(), Is.EqualTo(1));
            Assert.That(streamer.ColliderRevision(coord), Is.EqualTo(1UL));
            Assert.That(backend.Built.Count, Is.EqualTo(builds + 1));
            Assert.That(streamer.RefreshStaleColliders(), Is.Zero, "nothing stale twice");
        }

        [Test]
        public void TravelGuard_ClampsOnlyTowardUnreadyColumns()
        {
            Func<float, float, bool> readyBelowX16 = (x, z) => x < 16;
            float vx = 10, vz = 5;
            Assert.That(TravelGuard.Clamp(15.5f, 5, ref vx, ref vz, 0.1f, 1f, readyBelowX16), Is.True);
            Assert.That(vx, Is.Zero);
            Assert.That(vz, Is.EqualTo(5), "sliding along the boundary is fine");

            vx = -10;
            Assert.That(TravelGuard.Clamp(15.5f, 5, ref vx, ref vz, 0.1f, 1f, readyBelowX16), Is.False, "moving away from it is fine");
            Assert.That(vx, Is.EqualTo(-10));
        }

        [Test]
        public void TravelGuard_BlocksDiagonalIntoUnreadyCorner()
        {
            Func<float, float, bool> cornerMissing = (x, z) => !(x >= 16 && z >= 16);
            float vx = 5, vz = 5;

            TravelGuard.Clamp(15.8f, 15.8f, ref vx, ref vz, 0.1f, 0.5f, cornerMissing);

            Assert.That(vx == 0 && vz == 0, Is.True);
        }

        // stepEvery: streaming runs every Nth tick. 8/4 per tick keeps far ahead; 1/1 every 4th tick (7.5 chunks/s)
        // can't keep up with 28 m/s (≈12 chunks/s needed), so the guard must hold the car.
        [TestCase(8, 4, 1, false)]
        [TestCase(1, 1, 4, true)]
        public void DoD_DrivingNeverEntersUnreadyChunk(int generateBudget, int colliderBudget, int stepEvery, bool expectHeld)
        {
            var streamer = new ChunkStreamer(new FlatSource(), new RecordingBackend(), Settings(generateBudget, colliderBudget, radius: 3));
            const float dt = 1f / 30f;
            const float targetSpeed = 28f;
            float x = 8, z = 8, vx = 0, vz = 0;
            int heldTicks = 0;
            bool spawned = false;
            for (int tick = 0; tick < 30 * 30; tick++)
            {
                if (tick % stepEvery == 0)
                {
                    streamer.Step(new[] { new StreamingObserver(x, 1, z, vx, vz) });
                }

                if (!spawned)
                {
                    // Spawn gating: the car is held until its own column is collidable.
                    spawned = streamer.IsBlockColumnReady(x, z);
                    continue;
                }

                // Weave to cross chunk borders in both axes; full throttle.
                vx = (float)Math.Sin(tick * 0.02) * 8f;
                vz = targetSpeed;
                if (TravelGuard.Clamp(x, z, ref vx, ref vz, dt, 2.5f, streamer.IsBlockColumnReady))
                {
                    heldTicks++;
                }

                x += vx * dt;
                z += vz * dt;
                Assert.That(streamer.IsBlockColumnReady(x, z), Is.True, $"tick {tick}: entered unready column at ({x:0.0},{z:0.0})");
            }

            Assert.That(spawned, Is.True);
            if (expectHeld)
            {
                Assert.That(heldTicks, Is.GreaterThan(0), "a starved budget must hold the car at the boundary");
                Assert.That(z, Is.LessThan(8 + targetSpeed * 30 - targetSpeed), "held for at least a second in total");
            }
            else
            {
                Assert.That(heldTicks, Is.Zero, "a generous budget keeps ahead of the car");
                Assert.That(z, Is.GreaterThan(8 + targetSpeed * 29));
            }
        }
    }
}
