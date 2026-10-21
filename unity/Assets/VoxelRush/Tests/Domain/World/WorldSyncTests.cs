using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>VR-033: late-join snapshots, in-order deltas, duplicates, gaps and hash-verified recovery.</summary>
    public sealed class WorldSyncTests
    {
        private static readonly WorldInfo Info = new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, new ChunkCoord(-1, -1, -1), new ChunkCoord(0, 0, 0));
        private static readonly ChunkCoord C = new ChunkCoord(0, -1, 0);

        private static WorldAuthority Server(out IBaseGenerator generator)
        {
            generator = Info.CreateGenerator();
            var authority = new WorldAuthority(new EditValidator(Array.Empty<WorldBox>()), maxEditsPerSecond: 1_000_000, maxQueuedPerTick: 1_000_000);
            foreach (ChunkCoord c in Info.RegionChunks())
            {
                authority.AddChunk(new ChunkData(c, generator.GeneratorVersion, generator.GenerateBlocks(c)));
            }

            return authority;
        }

        // Removes the surface block above (x, z) in chunk C from an eye right above it.
        private static EditResult Edit(WorldAuthority server, int x, int z, ulong expected, uint tick)
        {
            var local = new LocalBlock(x, 15, z);
            byte current = server.GetChunk(C)!.Get(local);
            var intent = new EditIntent(Guid.NewGuid(), C, local.Index, expected, current == BlockIds.Air ? EditAction.Place : EditAction.Remove, BlockIds.Stone);
            return server.Submit("alice", intent, x + 0.5f, 1.5f, z + 0.5f, Array.Empty<WorldBox>(), tick, tick);
        }

        [Test]
        public void WorldInfo_RoundTrips_AndRefusesUnknownGeneratorVersions()
        {
            WorldInfo decoded = WorldInfo.Decode(Info.Encode());
            Assert.That((decoded.WorldId, decoded.Kind, decoded.GeneratorVersion, decoded.RegionMin, decoded.RegionMax), Is.EqualTo((Info.WorldId, Info.Kind, Info.GeneratorVersion, Info.RegionMin, Info.RegionMax)));

            var future = new WorldInfo(Guid.NewGuid(), GeneratorKind.Terrain, 1, TerrainGenerator.Version + 1, 1, C, C);
            Assert.Throws<NotSupportedException>(() => future.CreateGenerator());
        }

        [Test]
        public void LateJoin_SnapshotsReproduceServerHashes()
        {
            WorldAuthority server = Server(out _);
            ulong revision = 0;
            for (int i = 0; i < 20; i++)
            {
                revision = Edit(server, i % 16, i / 16, revision, (uint)i).Batch!.NewRevision;
            }

            var replica = new ClientWorldReplica(WorldInfo.Decode(Info.Encode()));
            foreach (ChunkCoord c in Info.RegionChunks())
            {
                ChunkSnapshotMessage wire = ChunkSnapshotMessage.Decode(ChunkSnapshotMessage.From(server.GetChunk(c)!).Encode());
                Assert.That(replica.ApplySnapshot(wire), Is.True);
            }

            foreach (ChunkCoord c in Info.RegionChunks())
            {
                Assert.That(ChunkHash.Compute(replica.Get(c)!), Is.EqualTo(ChunkHash.Compute(server.GetChunk(c)!)), $"{c}");
                Assert.That(replica.Get(c)!.Revision, Is.EqualTo(server.GetChunk(c)!.Revision));
            }
        }

        [Test]
        public void InOrderDeltas_Apply_DuplicatesIgnored()
        {
            WorldAuthority server = Server(out _);
            var replica = new ClientWorldReplica(Info);
            replica.ApplySnapshot(ChunkSnapshotMessage.From(server.GetChunk(C)!));
            ChunkMutationBatch a = Edit(server, 1, 1, 0, 1).Batch!;
            ChunkMutationBatch b = Edit(server, 2, 2, 1, 2).Batch!;

            replica.ApplyDelta(a);
            replica.ApplyDelta(b);
            replica.ApplyDelta(a);

            Assert.That(replica.AppliedDeltas, Is.EqualTo(2));
            Assert.That(replica.DuplicateDeltas, Is.EqualTo(1));
            Assert.That(ChunkHash.Compute(replica.Get(C)!), Is.EqualTo(ChunkHash.Compute(server.GetChunk(C)!)));
        }

        [Test]
        public void Gap_BuffersAndRequestsSnapshot_ThenCatchesUp()
        {
            WorldAuthority server = Server(out _);
            var replica = new ClientWorldReplica(Info);
            replica.ApplySnapshot(ChunkSnapshotMessage.From(server.GetChunk(C)!));
            Edit(server, 1, 1, 0, 1);
            ChunkMutationBatch second = Edit(server, 2, 2, 1, 2).Batch!;
            ChunkMutationBatch third = Edit(server, 3, 3, 2, 3).Batch!;

            replica.ApplyDelta(second);
            var requests = new List<ChunkCoord>();
            Assert.That(replica.DrainSnapshotRequests(requests), Is.EqualTo(1));
            Assert.That(requests.Single(), Is.EqualTo(C));
            Assert.That(replica.Gaps, Is.EqualTo(1));

            replica.ApplyDelta(third);
            Assert.That(replica.DrainSnapshotRequests(requests), Is.Zero, "one request per chunk until it's served");

            // The server answers with a snapshot at revision 2; the buffered third delta then applies.
            ChunkSnapshotMessage atTwo = new ChunkSnapshotMessage(C, 2, FlatPlotGenerator.Version, 1,
                ChunkHash.Compute(SnapshotAt(server, 2)), new List<KeyValuePair<ushort, byte>>(SnapshotAt(server, 2).Edits));
            Assert.That(replica.ApplySnapshot(atTwo), Is.True);
            Assert.That(replica.Get(C)!.Revision, Is.EqualTo(3));
            Assert.That(ChunkHash.Compute(replica.Get(C)!), Is.EqualTo(ChunkHash.Compute(server.GetChunk(C)!)));
        }

        [Test]
        public void CorruptSnapshot_IsRejected_AndRequestedAgain()
        {
            WorldAuthority server = Server(out _);
            Edit(server, 1, 1, 0, 1);
            ChunkSnapshotMessage good = ChunkSnapshotMessage.From(server.GetChunk(C)!);
            var tampered = new ChunkSnapshotMessage(C, good.Revision, good.GeneratorVersion, good.SchemaVersion, good.ContentHash ^ 1, good.Edits);
            var replica = new ClientWorldReplica(Info);

            Assert.That(replica.ApplySnapshot(tampered), Is.False);
            Assert.That(replica.HashMismatches, Is.EqualTo(1));
            var requests = new List<ChunkCoord>();
            Assert.That(replica.DrainSnapshotRequests(requests), Is.EqualTo(1));
            Assert.That(replica.ApplySnapshot(good), Is.True);
        }

        [Test]
        public void SnapshotDecode_RejectsOutOfRangeIndices()
        {
            var bad = new ChunkSnapshotMessage(C, 1, 1, 1, 0, new[] { new KeyValuePair<ushort, byte>(ushort.MaxValue, 1) });

            Assert.Throws<System.IO.InvalidDataException>(() => ChunkSnapshotMessage.Decode(bad.Encode()));
        }

        [Test]
        public void LossyReorderingChannel_ConvergesToServerHashes()
        {
            WorldAuthority server = Server(out _);
            var replica = new ClientWorldReplica(Info);
            foreach (ChunkCoord c in Info.RegionChunks())
            {
                replica.ApplySnapshot(ChunkSnapshotMessage.From(server.GetChunk(c)!));
            }

            var random = new Random(33);
            var inFlight = new List<ChunkMutationBatch>();
            var requests = new List<ChunkCoord>();
            ulong lastSeen = 0;
            for (uint tick = 1; tick <= 3000; tick++)
            {
                EditResult result = Edit(server, random.Next(16), random.Next(16), lastSeen, tick);
                if (result.Accepted)
                {
                    lastSeen = result.Batch!.NewRevision;
                    // 5% loss; the rest arrives up to a few ticks late, so reordering happens.
                    if (random.NextDouble() >= 0.05)
                    {
                        inFlight.Insert(Math.Max(0, inFlight.Count - random.Next(3)), result.Batch);
                    }
                }

                if (inFlight.Count > 0 && random.Next(2) == 0)
                {
                    replica.ApplyDelta(inFlight[0]);
                    inFlight.RemoveAt(0);
                }

                // Snapshot requests are served with the server's current state.
                if (replica.DrainSnapshotRequests(requests) > 0)
                {
                    foreach (ChunkCoord c in requests)
                    {
                        replica.ApplySnapshot(ChunkSnapshotMessage.From(server.GetChunk(c)!));
                    }
                }
            }

            foreach (ChunkMutationBatch late in inFlight)
            {
                replica.ApplyDelta(late);
            }

            if (replica.DrainSnapshotRequests(requests) > 0)
            {
                foreach (ChunkCoord c in requests)
                {
                    replica.ApplySnapshot(ChunkSnapshotMessage.From(server.GetChunk(c)!));
                }
            }

            Assert.That(replica.Gaps, Is.GreaterThan(0), "the channel must actually have caused gaps");
            Assert.That(ChunkHash.Compute(replica.Get(C)!), Is.EqualTo(ChunkHash.Compute(server.GetChunk(C)!)));
            Assert.That(replica.Get(C)!.Revision, Is.EqualTo(server.GetChunk(C)!.Revision));
        }

        // Rebuilds the server's chunk as of a revision by replaying from the base (test helper).
        private static ChunkData SnapshotAt(WorldAuthority server, ulong revision)
        {
            IBaseGenerator generator = Info.CreateGenerator();
            var fresh = new WorldAuthority(new EditValidator(Array.Empty<WorldBox>()), maxEditsPerSecond: 1_000_000, maxQueuedPerTick: 1_000_000);
            fresh.AddChunk(new ChunkData(C, generator.GeneratorVersion, generator.GenerateBlocks(C)));
            for (int i = 1; i <= (int)revision; i++)
            {
                Edit(fresh, i, i, (ulong)(i - 1), (uint)i);
            }

            return fresh.GetChunk(C)!;
        }
    }
}
