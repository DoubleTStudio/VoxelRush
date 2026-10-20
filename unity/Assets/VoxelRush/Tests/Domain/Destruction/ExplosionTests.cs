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
    /// <summary>DES-004: bounded explosions committed as multi-chunk mutation batches.</summary>
    public sealed class ExplosionTests
    {
        private static readonly FlatPlotGenerator Generator = new FlatPlotGenerator();

        // 2x1x2 chunks around the origin corner, so a blast at (0, 4, 0) spans four chunks.
        private static readonly ChunkCoord[] Region =
        {
            new ChunkCoord(-1, 0, -1), new ChunkCoord(-1, 0, 0), new ChunkCoord(0, 0, -1), new ChunkCoord(0, 0, 0),
        };

        private static WorldAuthority World(IMutationSink? sink = null)
        {
            var authority = new WorldAuthority(new EditValidator(ZoneMap.Empty), sink: sink);
            foreach (ChunkCoord coord in Region)
            {
                authority.AddChunk(new ChunkData(coord, Generator.GeneratorVersion, Generator.GenerateBlocks(coord)));
            }

            return authority;
        }

        /// <summary>Server-built 12x8x12 brick block centred on the chunk corner (also exercises CommitServerChanges).</summary>
        private static List<ChunkMutationBatch> BuildCube(WorldAuthority world, byte block = BlockIds.Brick)
        {
            var perChunk = new Dictionary<ChunkCoord, List<BlockChange>>();
            for (int x = -6; x < 6; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (int z = -6; z < 6; z++)
                    {
                        var pos = new BlockPos(x, y, z);
                        ChunkCoord c = ChunkMath.ToChunk(pos);
                        if (!perChunk.TryGetValue(c, out List<BlockChange>? list))
                        {
                            perChunk[c] = list = new List<BlockChange>();
                        }

                        list.Add(new BlockChange((ushort)ChunkMath.ToLocal(pos).Index, block));
                    }
                }
            }

            ServerCommitResult built = world.CommitServerChanges(Guid.NewGuid(), "builder", perChunk.Select(p => new ChunkChanges(p.Key, p.Value)).ToList(), 1);
            Assert.That(built.Accepted, Is.True);
            return built.Batches.ToList();
        }

        private static ExplosionResolver Resolver(ZoneMap? zones = null, DamageLedger? ledger = null) =>
            new ExplosionResolver(new ExplosionModel(), zones ?? ZoneMap.Empty, ledger ?? new DamageLedger(MaterialCatalog.Default));

        [Test]
        public void DoD_256BlockFixture_ConsistentAcrossServerReplicaAndRevisions()
        {
            WorldAuthority server = World();
            var replica = new ClientWorldReplica(new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, Region[0], Region[3]));
            foreach (ChunkCoord coord in Region)
            {
                replica.ApplySnapshot(ChunkSnapshotMessage.From(new ChunkData(coord, Generator.GeneratorVersion, Generator.GenerateBlocks(coord))));
            }

            List<ChunkMutationBatch> all = BuildCube(server);
            Dictionary<ChunkCoord, ulong> before = Region.ToDictionary(c => c, c => server.GetChunk(c)!.Revision);

            // Radius 4 covers ~268 brick blocks; the budget cuts at the 256 nearest. 400 HP at the centre falls off to
            // >= 1 HP at the rim, so near blocks break and far ones only crack.
            ExplosionOutcome blast = Resolver().Explode(server, Guid.NewGuid(), "car-7", 0f, 4f, 0f, 4f, 400, 2);
            all.AddRange(blast.Commit.Batches);

            Assert.That(blast.Rejection, Is.EqualTo(EditRejection.None));
            Assert.That(blast.Damaged, Is.EqualTo(256), "budget");
            Assert.That(blast.BudgetHit, Is.True);
            Assert.That(blast.Destroyed, Is.GreaterThan(0).And.LessThan(256));
            Assert.That(blast.Commit.Batches.Select(b => b.Chunk).Distinct().Count(), Is.EqualTo(blast.Commit.Batches.Count), "one batch per chunk");
            Assert.That(blast.Commit.Batches.Count, Is.EqualTo(4), "the blast spans the four corner chunks");
            Assert.That(blast.Commit.Batches.Sum(b => b.Changes.Count), Is.EqualTo(blast.Destroyed));

            foreach (ChunkMutationBatch b in blast.Commit.Batches)
            {
                Assert.That(b.BaseRevision, Is.EqualTo(before[b.Chunk]), "contiguous with the previous revision");
                Assert.That(b.NewRevision - b.BaseRevision, Is.EqualTo((ulong)b.Changes.Count));
                Assert.That(b.Changes.All(c => c.BlockType == BlockIds.Air));
                foreach (BlockChange c in b.Changes)
                {
                    BlockPos p = ChunkMath.ToWorld(b.Chunk, LocalBlock.FromIndex(c.Index));
                    float dx = p.X + 0.5f, dy = p.Y + 0.5f - 4f, dz = p.Z + 0.5f;
                    Assert.That(dx * dx + dy * dy + dz * dz, Is.LessThanOrEqualTo(16f), "never outside the radius");
                }
            }

            foreach (ChunkMutationBatch b in all)
            {
                replica.ApplyDelta(b);
            }

            foreach (ChunkCoord coord in Region)
            {
                Assert.That(ChunkHash.Compute(replica.Get(coord)!), Is.EqualTo(ChunkHash.Compute(server.GetChunk(coord)!)), $"replica {coord}");
                Assert.That(replica.Get(coord)!.Revision, Is.EqualTo(server.GetChunk(coord)!.Revision));
            }

            Assert.That(replica.Gaps + replica.HashMismatches, Is.Zero);
        }

        [Test]
        public void SameBlast_IsDeterministic()
        {
            ulong[] Run()
            {
                WorldAuthority w = World();
                BuildCube(w);
                Resolver().Explode(w, Guid.NewGuid(), "x", 0.3f, 3.7f, -0.2f, 4f, 250, 1);
                return Region.Select(c => ChunkHash.Compute(w.GetChunk(c)!)).ToArray();
            }

            Assert.That(Run(), Is.EqualTo(Run()));
        }

        [Test]
        public void Radius_Damage_AreClamped_InvalidInputRejected()
        {
            WorldAuthority w = World();
            BuildCube(w, BlockIds.Sand);

            ExplosionOutcome huge = Resolver().Explode(w, Guid.NewGuid(), "x", 0, 4, 0, 1000f, int.MaxValue, 1);

            Assert.That(huge.Damaged, Is.LessThanOrEqualTo(256));
            Assert.That(w.GetBlock(new BlockPos(5, 0, 5)), Is.EqualTo(BlockIds.Sand), "radius clamped to 4: a corner 7.5 m away survives");
            Assert.That(Resolver().Explode(w, Guid.NewGuid(), "x", float.NaN, 4, 0, 3, 100, 1).Rejection, Is.Not.EqualTo(EditRejection.None));
            Assert.That(Resolver().Explode(w, Guid.NewGuid(), "x", 0, 4, 0, -1, 100, 1).Rejection, Is.Not.EqualTo(EditRejection.None));
            Assert.That(Resolver().Explode(w, Guid.NewGuid(), "x", 0, 4, 0, 3, 0, 1).Rejection, Is.Not.EqualTo(EditRejection.None));
        }

        [Test]
        public void Falloff_IsIntegerAndPositiveInsideRadius()
        {
            Assert.That(ExplosionModel.Falloff(100, 0f, 4f), Is.EqualTo(100));
            Assert.That(ExplosionModel.Falloff(100, 2f, 4f), Is.EqualTo(50));
            Assert.That(ExplosionModel.Falloff(100, 3.99f, 4f), Is.EqualTo(1));
            Assert.That(ExplosionModel.Falloff(100, 4.01f, 4f), Is.Zero);
        }

        [Test]
        public void ProtectedBlocks_AndProtectedZones_Survive()
        {
            WorldAuthority w = World();
            BuildCube(w);
            // A protected checkpoint post inside the blast, plus an indestructible block.
            var zones = new ZoneMap(new[] { new Zone(new WorldBox(0, 3, 0, 1, 5, 1), ZoneKind.Protected) });
            w.CommitServerChanges(Guid.NewGuid(), "builder", new[] { new ChunkChanges(new ChunkCoord(-1, 0, -1), new[] { new BlockChange((ushort)ChunkMath.ToLocal(new BlockPos(-1, 4, -1)).Index, BlockIds.Protected) }) }, 1);

            Resolver(zones).Explode(w, Guid.NewGuid(), "x", 0, 4, 0, 4, 400, 2);

            Assert.That(w.GetBlock(new BlockPos(0, 4, 0)), Is.EqualTo(BlockIds.Brick), "protected zone");
            Assert.That(w.GetBlock(new BlockPos(-1, 4, -1)), Is.EqualTo(BlockIds.Protected), "indestructible material");
            Assert.That(w.GetBlock(new BlockPos(1, 4, 1)), Is.EqualTo(BlockIds.Air), "neighbour outside the zone breaks");
        }

        [Test]
        public void CrackedBlocks_KeepDamage_ForTheNextBlast()
        {
            WorldAuthority w = World();
            BuildCube(w, BlockIds.Metal);   // 200 HP
            var ledger = new DamageLedger(MaterialCatalog.Default);
            var target = new BlockPos(0, 4, 0);

            Resolver(ledger: ledger).Explode(w, Guid.NewGuid(), "x", 0.5f, 4.5f, 0.5f, 2f, 120, 1);
            Assert.That(w.GetBlock(target), Is.EqualTo(BlockIds.Metal));
            Assert.That(ledger.For(ChunkMath.ToChunk(target)).GetHp(w.GetChunk(ChunkMath.ToChunk(target))!, ChunkMath.ToLocal(target)), Is.EqualTo(80));

            Resolver(ledger: ledger).Explode(w, Guid.NewGuid(), "x", 0.5f, 4.5f, 0.5f, 2f, 120, 2);
            Assert.That(w.GetBlock(target), Is.EqualTo(BlockIds.Air));
        }

        [Test]
        public void Backpressure_ChangesNothing()
        {
            var sink = new CountingSink { Capacity = 100 };
            WorldAuthority w = World(sink);
            BuildCube(w);
            sink.Capacity = sink.Accepted.Count + 2;   // room for 2 batches; the blast needs 4
            var ledger = new DamageLedger(MaterialCatalog.Default);
            ulong[] before = Region.Select(c => ChunkHash.Compute(w.GetChunk(c)!)).ToArray();

            ExplosionOutcome blast = Resolver(ledger: ledger).Explode(w, Guid.NewGuid(), "x", 0, 4, 0, 4, 400, 2);

            Assert.That(blast.Rejection, Is.EqualTo(EditRejection.PersistenceBackpressure));
            Assert.That(Region.Select(c => ChunkHash.Compute(w.GetChunk(c)!)).ToArray(), Is.EqualTo(before));
            Assert.That(Region.Sum(c => ledger.For(c).DamagedCount), Is.Zero, "no HP change either");
        }

        [Test]
        public void ServerCommit_DuplicateOperation_AppliesOnce_AndUnloadedChunkRefusesAll()
        {
            WorldAuthority w = World();
            var id = Guid.NewGuid();
            var changes = new[]
            {
                new ChunkChanges(new ChunkCoord(0, 0, 0), new[] { new BlockChange(5, BlockIds.Wood) }),
                new ChunkChanges(new ChunkCoord(-1, 0, 0), new[] { new BlockChange(5, BlockIds.Wood) }),
            };

            ServerCommitResult first = w.CommitServerChanges(id, "s", changes, 1);
            ServerCommitResult again = w.CommitServerChanges(id, "s", changes, 2);

            Assert.That(first.Batches.Count, Is.EqualTo(2));
            Assert.That(again.IsDuplicate, Is.True);
            Assert.That(w.GetChunk(new ChunkCoord(0, 0, 0))!.Revision, Is.EqualTo(1));

            ServerCommitResult partial = w.CommitServerChanges(Guid.NewGuid(), "s", new[]
            {
                new ChunkChanges(new ChunkCoord(0, 0, 0), new[] { new BlockChange(6, BlockIds.Wood) }),
                new ChunkChanges(new ChunkCoord(9, 9, 9), new[] { new BlockChange(6, BlockIds.Wood) }),
            }, 3);
            Assert.That(partial.Rejection, Is.EqualTo(EditRejection.ChunkNotLoaded));
            Assert.That(w.GetChunk(new ChunkCoord(0, 0, 0))!.Revision, Is.EqualTo(1), "all or nothing");
        }

        [Test]
        public void ExplosionBreaks_StaleClientEdits()
        {
            WorldAuthority w = World();
            BuildCube(w);
            var c = new ChunkCoord(0, 0, 0);
            ulong seen = w.GetChunk(c)!.Revision;
            Resolver().Explode(w, Guid.NewGuid(), "x", 0.5f, 4.5f, 0.5f, 1.5f, 400, 2);

            // A client aimed at the (now destroyed) block before the blast: stale.
            EditResult r = w.Submit("alice", new EditIntent(Guid.NewGuid(), c, new LocalBlock(0, 4, 0).Index, seen, EditAction.Remove, 0), 0.5f, 6f, 0.5f, Array.Empty<WorldBox>(), 3, 0);
            Assert.That(r.Rejection, Is.EqualTo(EditRejection.StaleRevision));
        }

        private sealed class CountingSink : IMutationSink
        {
            public int Capacity;
            public readonly List<ChunkMutationBatch> Accepted = new List<ChunkMutationBatch>();

            public bool CanAccept => CanAcceptMany(1);

            public bool CanAcceptMany(int count) => Accepted.Count + count <= Capacity;

            public void Accept(ChunkMutationBatch batch) => Accepted.Add(batch);
        }
    }
}
