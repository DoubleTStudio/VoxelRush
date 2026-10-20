using System;
using System.Collections.Generic;
using NUnit.Framework;
using VoxelRush.Destruction;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.Domain.Destruction
{
    /// <summary>DES-005: cosmetic debris planning and the replica's change notifications.</summary>
    public sealed class DebrisPlannerTests
    {
        [Test]
        public void PiecesPerBlock_AndFrameCap()
        {
            var planner = new DebrisPlanner(piecesPerBlock: 3, maxPiecesPerFrame: 10);
            var into = new List<DebrisSpawn>();

            for (int i = 0; i < 5; i++)
            {
                planner.Plan(new BlockPos(i, 0, 0), BlockIds.Stone, into);
            }

            Assert.That(into.Count, Is.EqualTo(10), "big blasts thin out");
            Assert.That(planner.Dropped, Is.EqualTo(5));

            planner.BeginFrame();
            Assert.That(planner.Plan(new BlockPos(0, 0, 0), BlockIds.Stone, into), Is.EqualTo(3), "next frame has budget again");
        }

        [Test]
        public void Pieces_StartInsideTheBlock_AndFlyUp()
        {
            var planner = new DebrisPlanner(piecesPerBlock: 50, maxPiecesPerFrame: 50);
            var into = new List<DebrisSpawn>();
            planner.Plan(new BlockPos(-3, 7, 12), BlockIds.Brick, into);

            foreach (DebrisSpawn s in into)
            {
                Assert.That(s.X, Is.InRange(-3f, -2f));
                Assert.That(s.Y, Is.InRange(7f, 8f));
                Assert.That(s.Z, Is.InRange(12f, 13f));
                Assert.That(s.Vy, Is.GreaterThan(0f));
                Assert.That(s.BlockType, Is.EqualTo(BlockIds.Brick));
            }
        }

        [Test]
        public void Replica_ReportsChangedBlocksFromDeltas_NotSnapshots()
        {
            var gen = new FlatPlotGenerator();
            var coord = new ChunkCoord(0, -1, 0);
            var replica = new ClientWorldReplica(new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, coord, coord));
            var seen = new List<(ushort Index, byte Old, byte New)>();
            replica.BlockChanged = (c, i, o, n) => seen.Add((i, o, n));

            var server = new ChunkData(coord, gen.GeneratorVersion, gen.GenerateBlocks(coord));
            replica.ApplySnapshot(ChunkSnapshotMessage.From(server));
            Assert.That(seen, Is.Empty, "snapshots are not removals");

            var grass = new LocalBlock(2, 15, 2);
            replica.ApplyDelta(new ChunkMutationBatch(Guid.NewGuid(), "s", coord, 0, 1, 1, new[] { new BlockChange((ushort)grass.Index, BlockIds.Air) }));

            Assert.That(seen, Is.EqualTo(new[] { ((ushort)grass.Index, BlockIds.Grass, BlockIds.Air) }));
        }
    }
}
