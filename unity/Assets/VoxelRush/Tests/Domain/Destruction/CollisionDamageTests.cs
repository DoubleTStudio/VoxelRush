using System;
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

        private static ChunkData Chunk(ChunkCoord coord, byte fill)
        {
            var blocks = new byte[ChunkMath.Volume];
            for (int i = 0; i < 256; i++)
            {
                blocks[i] = fill;
            }

            return new ChunkData(coord, 1, blocks);
        }

        [Test]
        public void DoD_OneImpact_OneDamageWindow()
        {
            // A car pressed against a wall reports a contact every physics step; only the first one damages.
            ChunkData chunk = Chunk(new ChunkCoord(0, 0, 0), BlockIds.Reinforced);
            var damage = new ChunkDamage(MaterialCatalog.Default);
            var resolver = new ImpactResolver(Model, new ContactDeduplicator(0.3), ZoneMap.Empty);
            var target = new BlockPos(5, 0, 5);

            int applied = 0;
            for (int step = 0; step < 9; step++)     // 9 steps at 30 Hz = 0.3 s of continuous contact
            {
                if (resolver.Resolve("car-1", target, 10f, CarMass, step / 30.0, chunk, damage).Applied)
                {
                    applied++;
                }
            }

            Assert.That(applied, Is.EqualTo(1));
            Assert.That(resolver.Duplicates, Is.EqualTo(8));
            Assert.That(damage.GetHp(chunk, ChunkMath.ToLocal(target)), Is.EqualTo(300 - 104));

            // Bouncing back for a second hit after the window is a second impact.
            Assert.That(resolver.Resolve("car-1", target, 10f, CarMass, 0.5, chunk, damage).Applied, Is.True);
            Assert.That(damage.GetHp(chunk, ChunkMath.ToLocal(target)), Is.EqualTo(300 - 208));
        }

        [Test]
        public void ConcurrentHits_FromTwoCars_BothCount()
        {
            ChunkData chunk = Chunk(new ChunkCoord(0, 0, 0), BlockIds.Stone);
            var damage = new ChunkDamage(MaterialCatalog.Default);
            var resolver = new ImpactResolver(Model, new ContactDeduplicator(), ZoneMap.Empty);
            var target = new BlockPos(5, 0, 5);

            resolver.Resolve("car-1", target, 10f, CarMass, 1.0, chunk, damage);
            ImpactResult second = resolver.Resolve("car-2", target, 10f, CarMass, 1.0, chunk, damage);

            Assert.That(second.DamageResult.Outcome, Is.EqualTo(DamageOutcome.Destroyed), "104 + 104 >= stone 120");
            Assert.That(chunk.Get(ChunkMath.ToLocal(target)), Is.EqualTo(BlockIds.Air));
        }

        [Test]
        public void CrossChunkBorder_EachBlockDamagedInItsOwnChunk()
        {
            ChunkData left = Chunk(new ChunkCoord(-1, 0, 0), BlockIds.Wood), right = Chunk(new ChunkCoord(0, 0, 0), BlockIds.Wood);
            var damageLeft = new ChunkDamage(MaterialCatalog.Default);
            var damageRight = new ChunkDamage(MaterialCatalog.Default);
            var resolver = new ImpactResolver(Model, new ContactDeduplicator(), ZoneMap.Empty);
            BlockPos a = new BlockPos(-1, 0, 4), b = new BlockPos(0, 0, 4);

            Assert.That(ChunkMath.ToChunk(a), Is.EqualTo(left.Coord));
            Assert.That(resolver.Resolve("car", a, 6f, CarMass, 0, left, damageLeft).Applied, Is.True);
            Assert.That(resolver.Resolve("car", b, 6f, CarMass, 0, right, damageRight).Applied, Is.True);
            Assert.That(damageLeft.GetHp(left, ChunkMath.ToLocal(a)), Is.EqualTo(50 - 28));
            Assert.That(damageRight.GetHp(right, ChunkMath.ToLocal(b)), Is.EqualTo(50 - 28));
        }

        [Test]
        public void TargetOutsideGivenChunk_Throws()
        {
            ChunkData chunk = Chunk(new ChunkCoord(0, 0, 0), BlockIds.Stone);
            var resolver = new ImpactResolver(Model, new ContactDeduplicator(), ZoneMap.Empty);

            Assert.Throws<ArgumentException>(() => resolver.Resolve("car", new BlockPos(16, 0, 0), 10f, CarMass, 0, chunk, new ChunkDamage(MaterialCatalog.Default)));
        }

        [Test]
        public void ProtectedAndLiveTrack_NotDamaged_AndDontOpenWindows()
        {
            ChunkData chunk = Chunk(new ChunkCoord(0, 0, 0), BlockIds.Stone);
            var damage = new ChunkDamage(MaterialCatalog.Default);
            var dedup = new ContactDeduplicator();
            var zones = new ZoneMap(new[]
            {
                new Zone(new WorldBox(0, 0, 0, 4, 4, 4), ZoneKind.Protected),
                new Zone(new WorldBox(8, 0, 8, 12, 4, 12), ZoneKind.ActiveRace),
            });
            var resolver = new ImpactResolver(Model, dedup, zones);

            Assert.That(resolver.Resolve("car", new BlockPos(1, 0, 1), 20f, CarMass, 0, chunk, damage).Zone, Is.EqualTo(EditRejection.ProtectedZone));
            Assert.That(resolver.Resolve("car", new BlockPos(9, 0, 9), 20f, CarMass, 0, chunk, damage).Zone, Is.EqualTo(EditRejection.RaceLocked));
            Assert.That(damage.DamagedCount, Is.Zero);
            Assert.That(dedup.OpenWindows, Is.Zero);
        }

        [Test]
        public void Graze_DoesNotOpenWindow_SoTheRealHitStillCounts()
        {
            ChunkData chunk = Chunk(new ChunkCoord(0, 0, 0), BlockIds.Stone);
            var damage = new ChunkDamage(MaterialCatalog.Default);
            var resolver = new ImpactResolver(Model, new ContactDeduplicator(), ZoneMap.Empty);
            var target = new BlockPos(2, 0, 2);

            Assert.That(resolver.Resolve("car", target, 1f, CarMass, 0.00, chunk, damage).Applied, Is.False);
            Assert.That(resolver.Resolve("car", target, 10f, CarMass, 0.05, chunk, damage).Applied, Is.True);
        }
    }
}
