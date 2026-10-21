using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Destruction;
using VoxelRush.World.Chunks;

namespace VoxelRush.Tests.Domain.Destruction
{
    /// <summary>DES-001 DoD: material catalog values and sparse integer damage.</summary>
    public sealed class MaterialTests
    {
        private static readonly MaterialCatalog Catalog = MaterialCatalog.Default;

        [TestCase(BlockIds.Dirt, 30)]
        [TestCase(BlockIds.Grass, 35)]
        [TestCase(BlockIds.Wood, 50)]
        [TestCase(BlockIds.Asphalt, 100)]
        [TestCase(BlockIds.Stone, 120)]
        [TestCase(BlockIds.Reinforced, 300)]
        public void SpecifiedHp_MatchesGameplayRules(byte id, int hp)
        {
            Assert.That(Catalog.Get(id).MaxHp, Is.EqualTo(hp));
        }

        [Test]
        public void Protected_IsIndestructibleAndNotBuildable()
        {
            BlockMaterial m = Catalog.Get(BlockIds.Protected);

            Assert.That(m.IsIndestructible, Is.True);
            Assert.That(m.Buildable, Is.False);
        }

        [Test]
        public void Catalog_HasTwelveBlockTypes_AllNamedWithPositiveHp()
        {
            Assert.That(Catalog.Count, Is.EqualTo(12), "MVP: 12 block types");
            for (byte id = 1; id <= 12; id++)
            {
                BlockMaterial m = Catalog.Get(id);
                Assert.That(m.Name, Is.Not.Empty);
                Assert.That(m.MaxHp, Is.GreaterThan(0));
            }

            Assert.That(Catalog.TryGet(BlockIds.Air, out _), Is.False);
            Assert.That(Catalog.TryGet(200, out _), Is.False);
            Assert.That(Catalog.Get(BlockIds.Glass).Opaque, Is.False, "glass doesn't hide neighbouring faces");
        }

        [Test]
        public void Catalog_RejectsDuplicatesAirAndZeroHp()
        {
            Assert.Throws<ArgumentException>(() => new MaterialCatalog(new[] { new BlockMaterial(1, "A", 10, true, true), new BlockMaterial(1, "B", 10, true, true) }));
            Assert.Throws<ArgumentException>(() => new MaterialCatalog(new[] { new BlockMaterial(BlockIds.Air, "Air", 10, true, true) }));
            Assert.Throws<ArgumentException>(() => new MaterialCatalog(new[] { new BlockMaterial(5, "Zero", 0, true, true) }));
            Assert.Throws<KeyNotFoundException>(() => Catalog.Get(99));
        }
    }

    public sealed class ChunkDamageTests
    {
        private static ChunkData Chunk(byte fill)
        {
            var blocks = new byte[ChunkMath.Volume];
            for (int i = 0; i < 256; i++)
            {
                blocks[i] = fill;
            }

            return new ChunkData(new ChunkCoord(0, 0, 0), 1, blocks);
        }

        private static readonly LocalBlock Target = new LocalBlock(3, 0, 3);

        [Test]
        public void Damage_ReducesHp_SparselyStored()
        {
            ChunkData chunk = Chunk(BlockIds.Stone);
            var damage = new ChunkDamage(MaterialCatalog.Default);

            DamageResult r = damage.Apply(chunk, Target, 50);

            Assert.That(r.Outcome, Is.EqualTo(DamageOutcome.Damaged));
            Assert.That(r.RemainingHp, Is.EqualTo(70));
            Assert.That(damage.GetHp(chunk, Target), Is.EqualTo(70));
            Assert.That(damage.GetHp(chunk, new LocalBlock(4, 0, 4)), Is.EqualTo(120), "undamaged blocks need no entry");
            Assert.That(damage.DamagedCount, Is.EqualTo(1));
            Assert.That(chunk.Revision, Is.Zero, "damage alone doesn't change block content");
        }

        [Test]
        public void Damage_ToZeroOrBelow_DestroysAndClearsEntry()
        {
            ChunkData chunk = Chunk(BlockIds.Dirt);
            var damage = new ChunkDamage(MaterialCatalog.Default);
            damage.Apply(chunk, Target, 20);

            DamageResult r = damage.Apply(chunk, Target, 25);

            Assert.That(r.Outcome, Is.EqualTo(DamageOutcome.Destroyed));
            Assert.That(chunk.Get(Target), Is.EqualTo(BlockIds.Air));
            Assert.That(chunk.Revision, Is.EqualTo(1));
            Assert.That(damage.DamagedCount, Is.Zero);
        }

        [Test]
        public void ExactlyLethalDamage_Destroys()
        {
            ChunkData chunk = Chunk(BlockIds.Wood);

            Assert.That(new ChunkDamage(MaterialCatalog.Default).Apply(chunk, Target, 50).Outcome, Is.EqualTo(DamageOutcome.Destroyed));
        }

        [Test]
        public void Protected_Air_NonPositive_AreIgnored()
        {
            var damage = new ChunkDamage(MaterialCatalog.Default);
            ChunkData protectedChunk = Chunk(BlockIds.Protected);

            Assert.That(damage.Apply(protectedChunk, Target, int.MaxValue).Outcome, Is.EqualTo(DamageOutcome.Ignored));
            Assert.That(protectedChunk.Get(Target), Is.EqualTo(BlockIds.Protected));
            Assert.That(damage.Apply(Chunk(BlockIds.Stone), new LocalBlock(3, 5, 3), 10).Outcome, Is.EqualTo(DamageOutcome.Ignored), "air");
            Assert.That(damage.Apply(Chunk(BlockIds.Stone), Target, 0).Outcome, Is.EqualTo(DamageOutcome.Ignored));
            Assert.That(damage.Apply(Chunk(BlockIds.Stone), Target, -40).Outcome, Is.EqualTo(DamageOutcome.Ignored), "no healing via negative damage");
        }

        [Test]
        public void HugeDamage_DoesNotOverflow()
        {
            ChunkData chunk = Chunk(BlockIds.Reinforced);

            Assert.That(new ChunkDamage(MaterialCatalog.Default).Apply(chunk, Target, int.MaxValue).Outcome, Is.EqualTo(DamageOutcome.Destroyed));
        }

        [Test]
        public void ReplacedBlock_StartsAtFullHp()
        {
            ChunkData chunk = Chunk(BlockIds.Stone);
            var damage = new ChunkDamage(MaterialCatalog.Default);
            damage.Apply(chunk, Target, 100);
            chunk.Set(Target, BlockIds.Brick);
            damage.OnBlockReplaced(Target);

            Assert.That(damage.GetHp(chunk, Target), Is.EqualTo(90));
        }

        [Test]
        public void Restore_DropsInvalidEntries()
        {
            ChunkData chunk = Chunk(BlockIds.Stone);
            var damage = new ChunkDamage(MaterialCatalog.Default);

            damage.Restore(chunk, new[]
            {
                new KeyValuePair<ushort, ushort>((ushort)Target.Index, 40),
                new KeyValuePair<ushort, ushort>((ushort)new LocalBlock(0, 5, 0).Index, 10),   // air
                new KeyValuePair<ushort, ushort>(1, 500),                                        // above max
                new KeyValuePair<ushort, ushort>(4096, 10),                                      // out of range
            });

            Assert.That(damage.Entries.Select(e => (int)e.Key), Is.EqualTo(new[] { Target.Index }));
        }

        [Test]
        public void SameHitSequence_IsDeterministic()
        {
            int[] hits = { 7, 13, 1, 40, 22, 9, 31 };
            ChunkData a = Chunk(BlockIds.Concrete), b = Chunk(BlockIds.Concrete);
            var da = new ChunkDamage(MaterialCatalog.Default);
            var db = new ChunkDamage(MaterialCatalog.Default);
            foreach (int hit in hits)
            {
                Assert.That(da.Apply(a, Target, hit).RemainingHp, Is.EqualTo(db.Apply(b, Target, hit).RemainingHp));
            }
        }
    }
}
