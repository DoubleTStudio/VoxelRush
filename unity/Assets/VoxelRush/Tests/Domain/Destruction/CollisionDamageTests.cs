using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Destruction;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Tests.Domain.Destruction
{
    /// <summary>DES-003: collision energy model, contact dedup and the server-side resolver.</summary>
    public sealed class CollisionDamageTests
    {
        private const float CarMass = 1200f;

        private static readonly CollisionDamageModel Model = new CollisionDamageModel();

        [Test]
        public void BelowThreshold_NoDamage()
        {
            // 1200 kg at 3 m/s = 5.4 kJ < 8 kJ: parking bumps don't break blocks.
            Assert.That(Model.Damage(3f, CarMass), Is.Zero);
            Assert.That(Model.Damage(0f, CarMass), Is.Zero);
        }

        [Test]
        public void Damage_GrowsWithSpeedSquared_IsIntegerAndClamped()
        {
            int at10 = Model.Damage(10f, CarMass);  // 60 kJ -> (60-8)*2 = 104
            int at20 = Model.Damage(20f, CarMass);  // 240 kJ -> clamped

            Assert.That(at10, Is.EqualTo(104));
            Assert.That(at20, Is.EqualTo(Model.MaxDamagePerImpact));
            Assert.That(Model.Damage(-10f, CarMass), Is.EqualTo(at10), "direction of the normal doesn't matter");
        }

        [Test]
        public void InvalidPhysics_NoDamage()
        {
            Assert.That(Model.Damage(float.NaN, CarMass), Is.Zero);
            Assert.That(Model.Damage(10f, float.NaN), Is.Zero);
            Assert.That(Model.Damage(10f, 0f), Is.Zero);
            Assert.That(Model.Damage(10f, -5f), Is.Zero);
            Assert.That(Model.Damage(float.PositiveInfinity, CarMass), Is.EqualTo(Model.MaxDamagePerImpact), "clamped, no overflow");
        }

        [Test]
        public void ModelValidation()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CollisionDamageModel(thresholdJoules: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CollisionDamageModel(hpPerKiloJoule: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CollisionDamageModel(maxDamagePerImpact: 0));
        }

        [Test]
        public void Dedup_WindowPerActorAndTarget()
        {
            var dedup = new ContactDeduplicator(0.3);
            var block = new BlockPos(1, 2, 3);

            Assert.That(dedup.TryBeginImpact("a", block, 10.0), Is.True);
            Assert.That(dedup.TryBeginImpact("a", block, 10.1), Is.False, "same contact, inside the window");
            Assert.That(dedup.TryBeginImpact("b", block, 10.1), Is.True, "another car is its own impact");
            Assert.That(dedup.TryBeginImpact("a", new BlockPos(1, 2, 4), 10.1), Is.True, "another block is its own impact");
            Assert.That(dedup.TryBeginImpact("a", block, 10.3), Is.True, "window expired");
        }

        [Test]
        public void Dedup_PruneBoundsMemory()
        {
            var dedup = new ContactDeduplicator(0.3);
            for (int i = 0; i < 1000; i++)
            {
                dedup.TryBeginImpact("a", new BlockPos(i, 0, 0), i * 0.001);
            }

            dedup.Prune(5.0);

            Assert.That(dedup.OpenWindows, Is.Zero);
        }

        private static WorldAuthority World(byte fill, IMutationSink? sink = null, params ChunkCoord[] coords)
        {
            var world = new WorldAuthority(new EditValidator(ZoneMap.Empty), sink: sink);
            foreach (ChunkCoord coord in coords.Length == 0 ? new[] { new ChunkCoord(0, 0, 0) } : coords)
            {
                var blocks = new byte[ChunkMath.Volume];
                for (int i = 0; i < 256; i++)
                {
                    blocks[i] = fill;
                }

                world.AddChunk(new ChunkData(coord, 1, blocks));
            }

            return world;
        }

        private static ushort Hp(WorldAuthority world, DamageLedger ledger, BlockPos b) =>
            ledger.For(ChunkMath.ToChunk(b)).GetHp(world.GetChunk(ChunkMath.ToChunk(b))!, ChunkMath.ToLocal(b));

        [Test]
        public void DoD_OneImpact_OneDamageWindow()
        {
            // A car pressed against a wall reports a contact every physics step; only the first one damages.
            WorldAuthority world = World(BlockIds.Reinforced);
            var ledger = new DamageLedger(MaterialCatalog.Default);
            var resolver = new ImpactResolver(Model, new ContactDeduplicator(0.3), ZoneMap.Empty);
            var target = new BlockPos(5, 0, 5);

            int applied = 0;
            for (int step = 0; step < 9; step++)     // 9 steps at 30 Hz = 0.3 s of continuous contact
            {
                if (resolver.Resolve(world, ledger, "car-1", target, 10f, CarMass, step / 30.0, (uint)step).Applied)
                {
                    applied++;
                }
            }

            Assert.That(applied, Is.EqualTo(1));
            Assert.That(resolver.Duplicates, Is.EqualTo(8));
            Assert.That(Hp(world, ledger, target), Is.EqualTo(300 - 104));

            // Bouncing back for a second hit after the window is a second impact.
            Assert.That(resolver.Resolve(world, ledger, "car-1", target, 10f, CarMass, 0.5, 15).Applied, Is.True);
            Assert.That(Hp(world, ledger, target), Is.EqualTo(300 - 208));
            Assert.That(world.GetChunk(new ChunkCoord(0, 0, 0))!.Revision, Is.Zero, "damage alone isn't a block change");
        }

        [Test]
        public void ConcurrentHits_FromTwoCars_BothCount_AndTheKillIsJournaled()
        {
            var sink = new RecordingSink();
            WorldAuthority world = World(BlockIds.Stone, sink);
            var ledger = new DamageLedger(MaterialCatalog.Default);
            var resolver = new ImpactResolver(Model, new ContactDeduplicator(), ZoneMap.Empty);
            var target = new BlockPos(5, 0, 5);

            resolver.Resolve(world, ledger, "car-1", target, 10f, CarMass, 1.0, 30);
            ImpactResult second = resolver.Resolve(world, ledger, "car-2", target, 10f, CarMass, 1.0, 30);

            Assert.That(second.DamageResult.Outcome, Is.EqualTo(DamageOutcome.Destroyed), "104 + 104 >= stone 120");
            Assert.That(world.GetBlock(target), Is.EqualTo(BlockIds.Air));
            Assert.That(sink.Batches.Single(), Is.SameAs(second.Batch), "removal goes through the authority (journal, replication)");
            Assert.That(second.Batch!.ActorId, Is.EqualTo("car-2"));
        }

        [Test]
        public void JournalBackpressure_LeavesBlockAndHpUntouched()
        {
            var sink = new RecordingSink { Full = true };
            WorldAuthority world = World(BlockIds.Wood, sink);
            var ledger = new DamageLedger(MaterialCatalog.Default);
            var target = new BlockPos(2, 0, 2);

            ImpactResult r = new ImpactResolver(Model, new ContactDeduplicator(), ZoneMap.Empty).Resolve(world, ledger, "car", target, 20f, CarMass, 0, 1);

            Assert.That(r.Zone, Is.EqualTo(EditRejection.PersistenceBackpressure));
            Assert.That(world.GetBlock(target), Is.EqualTo(BlockIds.Wood));
            Assert.That(Hp(world, ledger, target), Is.EqualTo(50));
        }

        [Test]
        public void CrossChunkBorder_EachBlockDamagedInItsOwnChunk()
        {
            WorldAuthority world = World(BlockIds.Wood, null, new ChunkCoord(-1, 0, 0), new ChunkCoord(0, 0, 0));
            var ledger = new DamageLedger(MaterialCatalog.Default);
            var resolver = new ImpactResolver(Model, new ContactDeduplicator(), ZoneMap.Empty);
            BlockPos a = new BlockPos(-1, 0, 4), b = new BlockPos(0, 0, 4);

            Assert.That(resolver.Resolve(world, ledger, "car", a, 6f, CarMass, 0, 1).Applied, Is.True);
            Assert.That(resolver.Resolve(world, ledger, "car", b, 6f, CarMass, 0, 1).Applied, Is.True);
            Assert.That(Hp(world, ledger, a), Is.EqualTo(50 - 28));
            Assert.That(Hp(world, ledger, b), Is.EqualTo(50 - 28));
        }

        [Test]
        public void UnloadedChunk_AndIndestructible_AreNotDamaged()
        {
            WorldAuthority world = World(BlockIds.Protected);
            var ledger = new DamageLedger(MaterialCatalog.Default);
            var resolver = new ImpactResolver(Model, new ContactDeduplicator(), ZoneMap.Empty);

            Assert.That(resolver.Resolve(world, ledger, "car", new BlockPos(40, 0, 0), 10f, CarMass, 0, 1).Zone, Is.EqualTo(EditRejection.ChunkNotLoaded));
            Assert.That(resolver.Resolve(world, ledger, "car", new BlockPos(1, 0, 1), 30f, CarMass, 0, 1).Applied, Is.False);
            Assert.That(world.GetBlock(new BlockPos(1, 0, 1)), Is.EqualTo(BlockIds.Protected));
        }

        [Test]
        public void ProtectedAndLiveTrack_NotDamaged_AndDontOpenWindows()
        {
            WorldAuthority world = World(BlockIds.Stone);
            var ledger = new DamageLedger(MaterialCatalog.Default);
            var dedup = new ContactDeduplicator();
            var zones = new ZoneMap(new[]
            {
                new Zone(new WorldBox(0, 0, 0, 4, 4, 4), ZoneKind.Protected),
                new Zone(new WorldBox(8, 0, 8, 12, 4, 12), ZoneKind.ActiveRace),
            });
            var resolver = new ImpactResolver(Model, dedup, zones);

            Assert.That(resolver.Resolve(world, ledger, "car", new BlockPos(1, 0, 1), 20f, CarMass, 0, 1).Zone, Is.EqualTo(EditRejection.ProtectedZone));
            Assert.That(resolver.Resolve(world, ledger, "car", new BlockPos(9, 0, 9), 20f, CarMass, 0, 1).Zone, Is.EqualTo(EditRejection.RaceLocked));
            Assert.That(ledger.For(new ChunkCoord(0, 0, 0)).DamagedCount, Is.Zero);
            Assert.That(dedup.OpenWindows, Is.Zero);
        }

        [Test]
        public void Graze_DoesNotOpenWindow_SoTheRealHitStillCounts()
        {
            WorldAuthority world = World(BlockIds.Stone);
            var ledger = new DamageLedger(MaterialCatalog.Default);
            var resolver = new ImpactResolver(Model, new ContactDeduplicator(), ZoneMap.Empty);
            var target = new BlockPos(2, 0, 2);

            Assert.That(resolver.Resolve(world, ledger, "car", target, 1f, CarMass, 0.00, 1).Applied, Is.False);
            Assert.That(resolver.Resolve(world, ledger, "car", target, 10f, CarMass, 0.05, 2).Applied, Is.True);
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
