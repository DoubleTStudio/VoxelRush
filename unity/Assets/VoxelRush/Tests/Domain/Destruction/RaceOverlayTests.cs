using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Core.Race;
using VoxelRush.Destruction;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Persistence;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.Domain.Destruction
{
    /// <summary>VR-061: track revision lock and match-scoped destruction overlay.</summary>
    public sealed class RaceOverlayTests
    {
        private static readonly FlatPlotGenerator Generator = new FlatPlotGenerator();
        private static readonly ChunkCoord RegionMin = new ChunkCoord(-2, -1, -2), RegionMax = new ChunkCoord(1, 1, 1);

        // Track: the strip x -4..4, z -30..30; a destructible brick wall across it at z = 10; a builder's tower beside it.
        private static readonly WorldBox[] TrackRegion = { new WorldBox(-4, -2, -30, 5, 16, 31) };
        private static readonly WorldBox WallZone = new WorldBox(-4, 0, 10, 5, 4, 11);

        private string _dir = null!;
        private WorldStore _store = null!;
        private JournalWriter _journal = null!;
        private WorldAuthority _sandbox = null!;
        private ClientWorldReplica _client = null!;
        private readonly List<ChunkMutationBatch> _sandboxBatches = new List<ChunkMutationBatch>();

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "voxelrush-race-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _store = new WorldStore(_dir, Generator.GeneratorVersion, Generator.GenerateBlocks);
            _store.RecoverJournal();
            _journal = new JournalWriter(_store.JournalPath);
            _sandbox = new WorldAuthority(new EditValidator(ZoneMap.Empty), maxEditsPerSecond: 1000, sink: _journal);
            foreach (ChunkCoord c in Region())
            {
                _sandbox.AddChunk(_store.LoadChunk(c));
            }

            _client = new ClientWorldReplica(new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, RegionMin, RegionMax));
            foreach (ChunkCoord c in Region())
            {
                _client.ApplySnapshot(ChunkSnapshotMessage.From(_sandbox.GetChunk(c)!));
            }
        }

        [TearDown]
        public void TearDown()
        {
            _journal.Dispose();
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
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

        private ServerCommitResult Commit(WorldAuthority world, IEnumerable<BlockPos> blocks, byte type, string actor = "builder")
        {
            List<ChunkChanges> changes = blocks.GroupBy(ChunkMath.ToChunk)
                .Select(g => new ChunkChanges(g.Key, g.Select(b => new BlockChange((ushort)ChunkMath.ToLocal(b).Index, type)).ToList()))
                .ToList();
            ServerCommitResult r = world.CommitServerChanges(Guid.NewGuid(), actor, changes, 1);
            if (world == _sandbox && r.Accepted)
            {
                _sandboxBatches.AddRange(r.Batches);
                foreach (ChunkMutationBatch b in r.Batches)
                {
                    _client.ApplyDelta(b);
                }
            }

            return r;
        }

        private static IEnumerable<BlockPos> Wall() =>
            from x in Enumerable.Range(-4, 9) from y in Enumerable.Range(0, 4) select new BlockPos(x, y, 10);

        private static IEnumerable<BlockPos> Tower() => Enumerable.Range(0, 6).Select(y => new BlockPos(3, y, -5));

        private Dictionary<ChunkCoord, ulong> SandboxHashes() => Region().ToDictionary(c => c, c => ChunkHash.Compute(_sandbox.GetChunk(c)!));

        private static ZoneMap RaceZones() => new ZoneMap(new[]
        {
            new Zone(TrackRegion[0], ZoneKind.RacePreparation),          // overlay builds allowed for the server only (no client edits in races)
            new Zone(WallZone, ZoneKind.RaceDestructible),
        });

        private static TrackDefinition Course() => new TrackDefinition(1, new Vec3(0, 1, -28), new[]
        {
            new Gate(1, 0, 1, 0, 0, 1, 4, 3),
            new Gate(2, 0, 1, 25, 0, 1, 4, 3),
        });

        [Test]
        public void DoD_SandboxUnchangedAfterRace_EvenOnDiskAfterRestart()
        {
            Assert.That(Commit(_sandbox, Wall(), BlockIds.Brick).Accepted, Is.True);
            Assert.That(Commit(_sandbox, Tower(), BlockIds.Wood).Accepted, Is.True);
            Dictionary<ChunkCoord, ulong> beforeRace = SandboxHashes();

            // Countdown: lock the track revision and start the overlay; racers' clients fork their replicas.
            var race = new RaceAuthority(Course());
            race.AddRacer("p1");
            Assert.That(race.StartCountdown(100), Is.True);
            var published = new PublishSink();
            MatchWorld match = MatchWorld.Begin(race.RaceId, _sandbox, TrackRegion, RaceZones(), published);
            ClientWorldReplica matchReplica = _client.Fork();

            // Race destruction, all in the overlay: a blast through the wall, a car smashing a block, the tower collapsing.
            var ledger = new DamageLedger(MaterialCatalog.Default);
            ExplosionOutcome blast = new ExplosionResolver(new ExplosionModel(), RaceZones(), ledger)
                .Explode(match.Overlay, Guid.NewGuid(), "race:p1", 0.5f, 1.5f, 10.5f, 3f, 400, 200);
            Assert.That(blast.Destroyed, Is.GreaterThan(10));
            Assert.That(blast.Commit.Batches.All(b => b.BaseRevision >= match.LockedChunks[b.Chunk].Revision), Is.True, "overlay continues from the locked revision");
            var impact = new ImpactResolver(new CollisionDamageModel(), new ContactDeduplicator(), RaceZones());
            BlockPos smashed = new BlockPos(-4, 0, 10);
            Assert.That(impact.Resolve(match.Overlay, ledger, "race:car-p1", smashed, 25f, 1200f, 5, 250).DamageResult.Outcome, Is.EqualTo(DamageOutcome.Destroyed));
            Commit(match.Overlay, new[] { new BlockPos(3, 0, -5) }, BlockIds.Air, "race:collapse-trigger");
            var collapse = new CollapseScheduler(match.Overlay, new AuthorityStructureView(match.Overlay));
            collapse.Enqueue(new BlockPos(3, 0, -5));
            for (uint t = 0; !collapse.IsIdle; t++)
            {
                collapse.Tick(300 + t);
            }

            Assert.That(collapse.BlocksRemoved, Is.EqualTo(5), "tower fell in the overlay");
            foreach (ChunkMutationBatch b in published.Batches)
            {
                matchReplica.ApplyDelta(b);
            }

            Assert.That(matchReplica.Gaps + matchReplica.HashMismatches, Is.Zero);
            Assert.That(match.TouchedChunks(), Is.Not.Empty);
            foreach (ChunkCoord c in match.LockedChunks.Keys)
            {
                Assert.That(ChunkHash.Compute(matchReplica.Get(c)!), Is.EqualTo(ChunkHash.Compute(match.Overlay.GetChunk(c)!)), $"racers see the overlay exactly ({c})");
            }

            // Meanwhile the sandbox: the locked track refuses everything; the rest of the world keeps building and saving.
            EditResult locked = _sandbox.Submit("visitor", new EditIntent(Guid.NewGuid(), new ChunkCoord(0, 0, 0), new LocalBlock(1, 4, 1).Index,
                _sandbox.GetChunk(new ChunkCoord(0, 0, 0))!.Revision, EditAction.Place, BlockIds.Stone), 1.5f, 6f, 2.5f, Array.Empty<WorldBox>(), 210, 7);
            Assert.That(locked.Rejection, Is.EqualTo(EditRejection.RaceLocked));
            Assert.That(Commit(_sandbox, new[] { new BlockPos(0, 0, 10) }, BlockIds.Air).Rejection, Is.EqualTo(EditRejection.RaceLocked), "server changes too");
            Assert.That(Commit(_sandbox, new[] { new BlockPos(20, 0, 20) }, BlockIds.Stone).Accepted, Is.True, "outside the track");
            Dictionary<ChunkCoord, ulong> expected = SandboxHashes();

            // Results and reset: discard the overlay, unlock; clients drop their match copy.
            match.End();
            Assert.That(match.Overlay.GetChunk(new ChunkCoord(0, 0, 0)), Is.Null, "overlay discarded");
            foreach (KeyValuePair<ChunkCoord, (ulong Revision, ulong Hash)> locked2 in match.LockedChunks)
            {
                Assert.That(ChunkHash.Compute(_sandbox.GetChunk(locked2.Key)!), Is.EqualTo(beforeRace[locked2.Key]), $"track chunk {locked2.Key}");
            }

            Assert.That(_sandbox.GetBlock(new BlockPos(0, 1, 10)), Is.EqualTo(BlockIds.Brick), "wall intact in the sandbox");
            Assert.That(_sandbox.GetBlock(new BlockPos(3, 5, -5)), Is.EqualTo(BlockIds.Wood), "tower intact in the sandbox");
            foreach (ChunkCoord c in Region())
            {
                Assert.That(ChunkHash.Compute(_client.Get(c)!), Is.EqualTo(expected[c]), $"client sandbox replica {c} never saw race deltas");
            }

            // On disk: no race record, and a restart reproduces the sandbox exactly.
            _journal.Dispose();
            var reopened = new WorldStore(_dir, Generator.GeneratorVersion, Generator.GenerateBlocks);
            reopened.RecoverJournal();
            Assert.That(reopened.RecoveredRecords.Count, Is.EqualTo(_sandboxBatches.Count));
            Assert.That(reopened.RecoveredRecords.Any(r => r.ActorId.StartsWith("race:") || r.ActorId == CollapseScheduler.ActorId), Is.False);
            foreach (ChunkCoord c in Region())
            {
                Assert.That(ChunkHash.Compute(reopened.LoadChunk(c)), Is.EqualTo(expected[c]), $"restored {c}");
            }

            // Edits in the track work again after the race.
            _journal = new JournalWriter(reopened.JournalPath);
        }

        [Test]
        public void SecondRace_StartsFromTheSameLockedTrack()
        {
            Commit(_sandbox, Wall(), BlockIds.Brick);
            MatchWorld first = MatchWorld.Begin(Guid.NewGuid(), _sandbox, TrackRegion, RaceZones());
            new ExplosionResolver(new ExplosionModel(), RaceZones(), new DamageLedger(MaterialCatalog.Default))
                .Explode(first.Overlay, Guid.NewGuid(), "race", 0.5f, 1.5f, 10.5f, 3f, 400, 1);
            Assert.That(first.Overlay.GetBlock(new BlockPos(0, 1, 10)), Is.EqualTo(BlockIds.Air));
            first.End();

            MatchWorld second = MatchWorld.Begin(Guid.NewGuid(), _sandbox, TrackRegion, RaceZones());

            Assert.That(second.Overlay.GetBlock(new BlockPos(0, 1, 10)), Is.EqualTo(BlockIds.Brick), "wall reset for the next race");
            Assert.That(second.LockedChunks.Values.Select(v => v.Hash), Is.EqualTo(first.LockedChunks.Values.Select(v => v.Hash)));
            second.End();
        }

        [Test]
        public void OverlayZones_OnlyTaggedBlocksAreDestructible()
        {
            Commit(_sandbox, Wall(), BlockIds.Brick);
            Commit(_sandbox, new[] { new BlockPos(0, 0, 0), new BlockPos(0, 1, 0) }, BlockIds.Brick);   // gate post, not tagged
            MatchWorld match = MatchWorld.Begin(Guid.NewGuid(), _sandbox, TrackRegion,
                new ZoneMap(new[] { new Zone(TrackRegion[0], ZoneKind.ActiveRace), new Zone(WallZone, ZoneKind.RaceDestructible) }));

            new ExplosionResolver(new ExplosionModel(), new ZoneMap(new[] { new Zone(TrackRegion[0], ZoneKind.ActiveRace), new Zone(WallZone, ZoneKind.RaceDestructible) }),
                new DamageLedger(MaterialCatalog.Default)).Explode(match.Overlay, Guid.NewGuid(), "race", 0.5f, 1f, 4f, 4f, 400, 1);

            Assert.That(match.Overlay.GetBlock(new BlockPos(0, 1, 0)), Is.EqualTo(BlockIds.Brick), "untagged track object survives");
            match.End();
        }

        [Test]
        public void Begin_RequiresTheWholeTrackLoaded_AndUnlocksOnEnd()
        {
            Assert.Throws<InvalidOperationException>(() => MatchWorld.Begin(Guid.NewGuid(), _sandbox, new[] { new WorldBox(0, 0, 0, 100, 4, 4) }, ZoneMap.Empty));
            Assert.That(_sandbox.Locked, Is.Empty, "a failed begin locks nothing");

            MatchWorld match = MatchWorld.Begin(Guid.NewGuid(), _sandbox, TrackRegion, ZoneMap.Empty);
            Assert.That(_sandbox.IsLocked(new BlockPos(0, 0, 0)), Is.True);
            match.End();
            match.End();
            Assert.That(_sandbox.IsLocked(new BlockPos(0, 0, 0)), Is.False);
        }

        [Test]
        public void TamperedSandbox_DuringRace_IsDetectedAtEnd()
        {
            MatchWorld match = MatchWorld.Begin(Guid.NewGuid(), _sandbox, TrackRegion, ZoneMap.Empty);
            // Bypass the lock (a bug elsewhere would look like this).
            _sandbox.GetChunk(new ChunkCoord(0, 0, 0))!.Set(new LocalBlock(1, 1, 1), BlockIds.Stone);

            Assert.Throws<InvalidOperationException>(() => match.End());
        }

        private sealed class PublishSink : IMutationSink
        {
            public readonly List<ChunkMutationBatch> Batches = new List<ChunkMutationBatch>();

            public bool CanAccept => true;

            public bool CanAcceptMany(int count) => true;

            public void Accept(ChunkMutationBatch batch) => Batches.Add(batch);
        }
    }
}
