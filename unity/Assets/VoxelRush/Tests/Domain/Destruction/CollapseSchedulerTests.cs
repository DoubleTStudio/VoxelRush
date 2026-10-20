using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Destruction;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.Domain.Destruction
{
    /// <summary>DES-011: incremental bounded collapse.</summary>
    public sealed class CollapseSchedulerTests
    {
        private static readonly FlatPlotGenerator Generator = new FlatPlotGenerator();
        private static readonly ChunkCoord RegionMin = new ChunkCoord(-2, -1, -2), RegionMax = new ChunkCoord(1, 1, 1);

        private WorldAuthority _world = null!;
        private RecordingSink _sink = null!;
        private AuthorityStructureView _view = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _world = new WorldAuthority(new EditValidator(ZoneMap.Empty), maxEditsPerSecond: 1000, sink: _sink);
            foreach (ChunkCoord c in Region())
            {
                _world.AddChunk(new ChunkData(c, Generator.GeneratorVersion, Generator.GenerateBlocks(c)));
            }

            _view = new AuthorityStructureView(_world);
        }

        private static IEnumerable<ChunkCoord> Region()
        {
            for (int x = RegionMin.X; x <= RegionMax.X; x++)
            {
                for (int y = RegionMin.Y; y <= RegionMax.Y; y++)
                {
                    for (int z = RegionMin.Z; z <= RegionMax.Z; z++)
                    {
                        yield return new ChunkCoord(x, y, z);
                    }
                }
            }
        }

        private void Set(IEnumerable<BlockPos> blocks, byte type)
        {
            var changes = blocks.GroupBy(ChunkMath.ToChunk)
                .Select(g => new ChunkChanges(g.Key, g.Select(b => new BlockChange((ushort)ChunkMath.ToLocal(b).Index, type)).ToList()))
                .ToList();
            Assert.That(_world.CommitServerChanges(Guid.NewGuid(), "builder", changes, 1).Accepted, Is.True);
        }

        private static IEnumerable<BlockPos> Column(int x, int z, int height) => Enumerable.Range(0, height).Select(y => new BlockPos(x, y, z));

        private List<CollapseTickStats> RunUntilIdle(CollapseScheduler scheduler, int maxTicks = 500)
        {
            var stats = new List<CollapseTickStats>();
            for (uint tick = 10; !scheduler.IsIdle; tick++)
            {
                Assert.That(stats.Count, Is.LessThan(maxTicks), "scheduler never settled");
                stats.Add(scheduler.Tick(tick));
            }

            return stats;
        }

        private int FloatingPlayerBuiltBlocks()
        {
            var analyzer = new SupportAnalyzer(_view);
            var component = new List<BlockPos>();
            var floating = new HashSet<BlockPos>();
            foreach (ChunkCoord c in Region())
            {
                ChunkData chunk = _world.GetChunk(c)!;
                foreach (KeyValuePair<ushort, byte> edit in chunk.Edits)
                {
                    BlockPos b = ChunkMath.ToWorld(c, LocalBlock.FromIndex(edit.Key));
                    if (edit.Value != BlockIds.Air && !floating.Contains(b) && analyzer.Analyze(b, int.MaxValue, component) == SupportStatus.Disconnected)
                    {
                        floating.UnionWith(component);
                    }
                }
            }

            return floating.Count;
        }

        [Test]
        public void Tower_CutAtTheBase_Collapses_ThroughJournaledBatches()
        {
            Set(Column(3, 3, 6), BlockIds.Wood);
            int before = _sink.Batches.Count;
            Set(new[] { new BlockPos(3, 0, 3) }, BlockIds.Air);
            var scheduler = new CollapseScheduler(_world, _view);

            Assert.That(scheduler.Enqueue(new BlockPos(3, 0, 3)), Is.True);
            RunUntilIdle(scheduler);

            Assert.That(Column(3, 3, 6).All(b => _world.GetBlock(b) == BlockIds.Air));
            Assert.That(scheduler.Collapses, Is.EqualTo(1));
            Assert.That(scheduler.BlocksRemoved, Is.EqualTo(5));
            Assert.That(_sink.Batches.Skip(before + 1).All(b => b.ActorId == CollapseScheduler.ActorId), "removals go through the journal");
        }

        [Test]
        public void SupportedStructure_StaysUp()
        {
            Set(Column(0, 3, 4).Concat(Column(10, 3, 4)).Concat(Enumerable.Range(1, 9).Select(x => new BlockPos(x, 3, 3))), BlockIds.Wood);
            Set(new[] { new BlockPos(5, 3, 3) }, BlockIds.Air);
            var scheduler = new CollapseScheduler(_world, _view);
            scheduler.Enqueue(new BlockPos(5, 3, 3));

            RunUntilIdle(scheduler);

            Assert.That(scheduler.BlocksRemoved, Is.Zero);
            Assert.That(_world.GetBlock(new BlockPos(4, 3, 3)), Is.EqualTo(BlockIds.Wood));
        }

        [Test]
        public void DoD_QueueCapsHoldUnderStress_AndEverythingFloatingFalls()
        {
            // 64 towers (8 high) under one 24x24 deck: 2,048 + 576 player-built blocks. Cut every tower base at once,
            // plus 1,500 extra removal points (a blast field) to overflow the pending queue.
            var towers = new List<BlockPos>();
            for (int x = -12; x < 12; x += 3)
            {
                for (int z = -12; z < 12; z += 3)
                {
                    towers.Add(new BlockPos(x, 0, z));
                }
            }

            Set(towers.SelectMany(t => Column(t.X, t.Z, 8)), BlockIds.Brick);
            Set(Enumerable.Range(-12, 24).SelectMany(x => Enumerable.Range(-12, 24).Select(z => new BlockPos(x, 8, z))), BlockIds.Wood);
            // A separate anchored hut that must survive.
            Set(Column(25, 25, 3), BlockIds.Stone);

            var replica = Replica();
            Set(towers, BlockIds.Air);

            var limits = new CollapseLimits { MaxPending = 1024 };
            var scheduler = new CollapseScheduler(_world, _view, limits);
            int refused = 0;
            foreach (BlockPos t in towers)
            {
                Assert.That(scheduler.Enqueue(t), Is.True);
            }

            for (int i = 0; i < 1500; i++)
            {
                if (!scheduler.Enqueue(new BlockPos(-30 + i % 60, 20 + i / 600, -30 + (i / 60) % 10)))
                {
                    refused++;
                }
            }

            Assert.That(scheduler.Pending, Is.EqualTo(1024), "pending queue capped");
            Assert.That(refused, Is.EqualTo(64 + 1500 - 1024));
            Assert.That(scheduler.IsSaturated, Is.True);

            List<CollapseTickStats> stats = RunUntilIdle(scheduler);

            Assert.That(stats.Max(s => s.Checks), Is.LessThanOrEqualTo(limits.ChecksPerTick), "checks per tick");
            Assert.That(stats.Max(s => s.Removed), Is.LessThanOrEqualTo(limits.MaxRemovedPerTick), "removed blocks per tick");
            Assert.That(stats.Max(s => s.ActiveJobs), Is.LessThanOrEqualTo(limits.MaxActiveJobs), "active jobs");
            Assert.That(stats.Max(s => s.Pending), Is.LessThanOrEqualTo(limits.MaxPending));
            Assert.That(stats.Count(s => s.Removed == limits.MaxRemovedPerTick), Is.GreaterThan(0), "the removal cap actually bound");
            Assert.That(scheduler.BlocksRemoved, Is.EqualTo(64 * 7 + 576), "tower remnants + deck");
            Assert.That(FloatingPlayerBuiltBlocks(), Is.Zero);
            Assert.That(_world.GetBlock(new BlockPos(25, 2, 25)), Is.EqualTo(BlockIds.Stone), "anchored hut untouched");
            Assert.That(scheduler.Abandoned, Is.Zero);

            foreach (ChunkMutationBatch b in _sink.Batches.Skip(_replicaFrom))
            {
                replica.ApplyDelta(b);
            }

            foreach (ChunkCoord c in Region())
            {
                Assert.That(ChunkHash.Compute(replica.Get(c)!), Is.EqualTo(ChunkHash.Compute(_world.GetChunk(c)!)), $"replica {c}");
            }

            TestContext.WriteLine($"ticks={stats.Count} maxChecks={stats.Max(s => s.Checks)} maxRemoved={stats.Max(s => s.Removed)} removed={scheduler.BlocksRemoved}");
        }

        private int _replicaFrom;

        private ClientWorldReplica Replica()
        {
            var replica = new ClientWorldReplica(new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, RegionMin, RegionMax));
            foreach (ChunkCoord c in Region())
            {
                replica.ApplySnapshot(ChunkSnapshotMessage.From(_world.GetChunk(c)!));
            }

            _replicaFrom = _sink.Batches.Count;
            return replica;
        }

        [Test]
        public void RegionUnderAnalysis_RejectsClientEdits_UntilSettled()
        {
            Set(Column(3, 3, 6), BlockIds.Wood);
            Set(new[] { new BlockPos(3, 0, 3) }, BlockIds.Air);
            var scheduler = new CollapseScheduler(_world, _view, new CollapseLimits { ChecksPerTick = 2, MaxRemovedPerTick = 2 });
            _world.IsBlocked = scheduler.IsBlocked;
            scheduler.Enqueue(new BlockPos(3, 0, 3));

            EditResult Place(int x, int y, int z) => _world.Submit("alice",
                new EditIntent(Guid.NewGuid(), ChunkMath.ToChunk(new BlockPos(x, y, z)), ChunkMath.ToLocal(new BlockPos(x, y, z)).Index,
                    _world.GetChunk(ChunkMath.ToChunk(new BlockPos(x, y, z)))!.Revision, EditAction.Place, BlockIds.Stone),
                x + 0.5f, y + 2f, z + 1.5f, Array.Empty<WorldBox>(), 5, 0);

            Assert.That(Place(4, 0, 3).Rejection, Is.EqualTo(EditRejection.StructureSettling), "next to the removal point");
            // 2 checks per tick: the six neighbour probes take three ticks, then the search climbs the tower.
            for (uint tick = 1; tick < 10 && !scheduler.IsBlocked(new BlockPos(4, 3, 3)); tick++)
            {
                scheduler.Tick(tick);
            }

            Assert.That(scheduler.IsIdle, Is.False);
            Assert.That(Place(4, 3, 3).Rejection, Is.EqualTo(EditRejection.StructureSettling), "beside the tower being analysed (would re-anchor it)");
            Assert.That(Place(9, 0, 9).Accepted, Is.True, "elsewhere is fine");

            RunUntilIdle(scheduler);

            Assert.That(Place(4, 0, 3).Accepted, Is.True, "settled");
            Assert.That(EditValidator.Describe(EditRejection.StructureSettling), Does.Contain("settling"));
        }

        [Test]
        public void JournalBackpressure_RetriesRemovals_RegionStaysBlocked()
        {
            Set(Column(3, 3, 4), BlockIds.Wood);
            Set(new[] { new BlockPos(3, 0, 3) }, BlockIds.Air);
            var scheduler = new CollapseScheduler(_world, _view);
            scheduler.Enqueue(new BlockPos(3, 0, 3));
            scheduler.Tick(1);   // finds the component
            _sink.Full = true;

            CollapseTickStats stalled = scheduler.Tick(2);

            Assert.That(stalled.Removed, Is.Zero);
            Assert.That(stalled.QueuedRemovals, Is.EqualTo(3));
            Assert.That(scheduler.IsBlocked(new BlockPos(3, 2, 3)), Is.True);

            _sink.Full = false;
            RunUntilIdle(scheduler);
            Assert.That(_world.GetBlock(new BlockPos(3, 3, 3)), Is.EqualTo(BlockIds.Air));
        }

        [Test]
        public void OversizedSearch_IsAbandoned_AsSupported()
        {
            Set(Column(3, 3, 40 - 10), BlockIds.Wood);
            Set(new[] { new BlockPos(3, 0, 3) }, BlockIds.Air);
            var scheduler = new CollapseScheduler(_world, _view, new CollapseLimits { MaxChecksPerJob = 10, ChecksPerTick = 4 });
            scheduler.Enqueue(new BlockPos(3, 0, 3));

            RunUntilIdle(scheduler);

            Assert.That(scheduler.Abandoned, Is.EqualTo(1));
            Assert.That(scheduler.BlocksRemoved, Is.Zero);
        }

        [Test]
        public void BlockChangedBeforeRemoval_IsLeftAlone()
        {
            Set(Column(3, 3, 4), BlockIds.Wood);
            Set(new[] { new BlockPos(3, 0, 3) }, BlockIds.Air);
            var scheduler = new CollapseScheduler(_world, _view);
            scheduler.Enqueue(new BlockPos(3, 0, 3));
            scheduler.Tick(1);
            Set(new[] { new BlockPos(3, 3, 3) }, BlockIds.Air);   // a blast got there first

            RunUntilIdle(scheduler);

            Assert.That(scheduler.BlocksRemoved, Is.EqualTo(2));
        }

        [Test]
        public void Enqueue_DeduplicatesPoints()
        {
            var scheduler = new CollapseScheduler(_world, _view);
            scheduler.Enqueue(new BlockPos(1, 1, 1));
            scheduler.Enqueue(new BlockPos(1, 1, 1));

            Assert.That(scheduler.Pending, Is.EqualTo(1));
        }

        private sealed class RecordingSink : IMutationSink
        {
            public bool Full;
            public readonly List<ChunkMutationBatch> Batches = new List<ChunkMutationBatch>();

            public bool CanAccept => !Full;

            public bool CanAcceptMany(int count) => !Full;

            public void Accept(ChunkMutationBatch batch) => Batches.Add(batch);
        }
    }
}
