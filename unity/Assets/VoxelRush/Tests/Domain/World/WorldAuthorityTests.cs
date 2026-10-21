using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>VR-031: authoritative validation, idempotency, rate limits and serialized revisions.</summary>
    public sealed class WorldAuthorityTests
    {
        private static readonly ChunkCoord C = new ChunkCoord(1, 0, 1);
        private static readonly IReadOnlyList<WorldBox> NoOccupants = Array.Empty<WorldBox>();
        private WorldAuthority _authority = null!;
        private double _now;

        [SetUp]
        public void SetUp()
        {
            _authority = new WorldAuthority(new EditValidator(Array.Empty<WorldBox>()));
            _authority.AddChunk(Flat(C));
            _now = 0;
        }

        private static ChunkData Flat(ChunkCoord coord)
        {
            var blocks = new byte[ChunkMath.Volume];
            for (int i = 0; i < 256; i++)
            {
                blocks[i] = BlockIds.Stone;
            }

            return new ChunkData(coord, 1, blocks);
        }

        private static int Index(int x, int y, int z) => new LocalBlock(x, y, z).Index;

        // Eye 2 m above the chunk's local (x, z) column: within reach of the surface.
        private EditResult Submit(string actor, EditIntent intent, uint tick = 1)
        {
            BlockPos target = ChunkMath.ToWorld(intent.Chunk, LocalBlock.FromIndex(intent.LocalIndex));
            _now += 0.2;
            return _authority.Submit(actor, intent, target.X + 0.5f, 3f, target.Z + 0.5f, NoOccupants, tick, _now);
        }

        private static EditIntent Place(int x, int y, int z, ulong expected, Guid? id = null) =>
            new EditIntent(id ?? Guid.NewGuid(), C, Index(x, y, z), expected, EditAction.Place, BlockIds.Dirt);

        private static EditIntent Remove(int x, int y, int z, ulong expected) =>
            new EditIntent(Guid.NewGuid(), C, Index(x, y, z), expected, EditAction.Remove, 0);

        [Test]
        public void Accept_ProducesContiguousRevisionBatch()
        {
            EditResult result = Submit("alice", Place(3, 1, 3, expected: 0));

            Assert.That(result.Accepted, Is.True);
            Assert.That(result.Batch!.BaseRevision, Is.EqualTo(0));
            Assert.That(result.Batch.NewRevision, Is.EqualTo(1));
            Assert.That(result.Batch.Changes.Single().BlockType, Is.EqualTo(BlockIds.Dirt));
            Assert.That(_authority.GetChunk(C)!.Get(new LocalBlock(3, 1, 3)), Is.EqualTo(BlockIds.Dirt));
        }

        [Test]
        public void SameBlock_ConcurrentEdits_FirstWinsSecondIsStale()
        {
            EditResult first = Submit("alice", Place(3, 1, 3, expected: 0));
            EditResult second = Submit("bob", Place(3, 1, 3, expected: 0));

            Assert.That(first.Accepted, Is.True);
            Assert.That(second.Rejection, Is.EqualTo(EditRejection.StaleRevision));
            Assert.That(_authority.GetChunk(C)!.Revision, Is.EqualTo(1), "one revision for one accepted edit");
        }

        [Test]
        public void DifferentBlocks_SameChunk_BothAcceptedWithOneChain()
        {
            EditResult a = Submit("alice", Place(3, 1, 3, expected: 0));
            EditResult b = Submit("bob", Place(9, 1, 9, expected: 0));

            Assert.That(a.Accepted && b.Accepted, Is.True, "chunk revision moved, but bob's block didn't change");
            Assert.That((a.Batch!.NewRevision, b.Batch!.BaseRevision, b.Batch.NewRevision), Is.EqualTo((1UL, 1UL, 2UL)));
        }

        [Test]
        public void DuplicateOperationId_IsIdempotent()
        {
            var id = Guid.NewGuid();
            EditResult first = Submit("alice", Place(3, 1, 3, 0, id));
            EditResult replay = Submit("alice", Place(3, 1, 3, 0, id));

            Assert.That(replay.IsDuplicate, Is.True);
            Assert.That(replay.Batch, Is.SameAs(first.Batch));
            Assert.That(_authority.GetChunk(C)!.Revision, Is.EqualTo(1), "never applied twice");
            Assert.That(_authority.Accepted, Is.EqualTo(1));
        }

        [Test]
        public void ForgedFutureRevision_IsRejected()
        {
            Assert.That(Submit("mallory", Place(3, 1, 3, expected: 99)).Rejection, Is.EqualTo(EditRejection.InvalidRevision));
        }

        [TestCase(-1)]
        [TestCase(4096)]
        [TestCase(65535)]
        public void OutOfRangeIndex_IsRejected(int index)
        {
            var intent = new EditIntent(Guid.NewGuid(), C, index, 0, EditAction.Place, BlockIds.Dirt);

            Assert.That(_authority.Submit("mallory", intent, 0, 0, 0, NoOccupants, 1, 1).Rejection, Is.EqualTo(EditRejection.InvalidIndex));
        }

        [Test]
        public void UnloadedChunk_And_Rotate_AreRejected()
        {
            Assert.That(Submit("alice", new EditIntent(Guid.NewGuid(), new ChunkCoord(50, 0, 50), 0, 0, EditAction.Place, BlockIds.Dirt)).Rejection, Is.EqualTo(EditRejection.ChunkNotLoaded));
            Assert.That(Submit("alice", new EditIntent(Guid.NewGuid(), C, Index(1, 1, 1), 0, EditAction.Rotate, BlockIds.Dirt)).Rejection, Is.EqualTo(EditRejection.UnsupportedAction));
        }

        [Test]
        public void Reach_UsesServerSidePose_NotClientClaim()
        {
            // The intent carries no position; the server supplies the actor's real eye, here 40 m away.
            EditIntent intent = Place(3, 1, 3, 0);

            Assert.That(_authority.Submit("alice", intent, 60, 3, 60, NoOccupants, 1, 1).Rejection, Is.EqualTo(EditRejection.OutOfReach));
        }

        [Test]
        public void RateLimit_TenPerSecondPerActor_OtherActorsUnaffected()
        {
            int accepted = 0;
            for (int i = 0; i < 15; i++)
            {
                var intent = new EditIntent(Guid.NewGuid(), C, Index(i, 1, 0), (ulong)accepted, EditAction.Place, BlockIds.Dirt);
                // Chunk C starts at world (16, 0, 16); the eye stands over each target.
                if (_authority.Submit("spammer", intent, 16 + i + 0.5f, 3, 16.5f, NoOccupants, 1, nowSeconds: 5.0).Accepted)
                {
                    accepted++;
                }
            }

            Assert.That(accepted, Is.EqualTo(10));
            var other = new EditIntent(Guid.NewGuid(), C, Index(5, 1, 5), (ulong)accepted, EditAction.Place, BlockIds.Dirt);
            Assert.That(_authority.Submit("alice", other, 21.5f, 3, 21.5f, NoOccupants, 1, nowSeconds: 5.0).Accepted, Is.True);
        }

        [Test]
        public void RateLimited_SameOperationId_CanBeRetried()
        {
            for (int i = 0; i < 10; i++)
            {
                Assert.That(_authority.Submit("bob", new EditIntent(Guid.NewGuid(), C, Index(i, 1, 0), (ulong)i, EditAction.Place, BlockIds.Dirt), 16 + i + 0.5f, 3, 16.5f, NoOccupants, 1, 7.0).Accepted, Is.True);
            }

            var id = Guid.NewGuid();
            var intent = new EditIntent(id, C, Index(0, 1, 9), 10, EditAction.Place, BlockIds.Dirt);
            Assert.That(_authority.Submit("bob", intent, 16.5f, 3, 25.5f, NoOccupants, 1, 7.0).Rejection, Is.EqualTo(EditRejection.RateLimited));
            Assert.That(_authority.Submit("bob", intent, 16.5f, 3, 25.5f, NoOccupants, 2, 8.0).Accepted, Is.True, "transient refusal isn't remembered");
        }

        [Test]
        public void PerTickQueue_IsBounded()
        {
            var authority = new WorldAuthority(new EditValidator(Array.Empty<WorldBox>()), maxEditsPerSecond: 1000, maxQueuedPerTick: 3);
            authority.AddChunk(Flat(C));
            var results = Enumerable.Range(0, 5)
                .Select(i => authority.Submit("a" + i, new EditIntent(Guid.NewGuid(), C, Index(i, 1, 0), 0, EditAction.Place, BlockIds.Dirt), i + 16.5f, 3, 16.5f, NoOccupants, 7, 1))
                .ToList();

            Assert.That(results.Count(r => r.Rejection == EditRejection.ServerBusy), Is.EqualTo(2));
        }

        [Test]
        public void DoD_RandomConcurrentEdits_FormOneRevisionChainThatReplays()
        {
            var random = new Random(2026);
            var actors = new[] { "alice", "bob", "carol", "dave" };
            var accepted = new List<ChunkMutationBatch>();
            // Clients aim with the revision they last saw (often stale); targets collide on purpose.
            var lastSeen = actors.ToDictionary(a => a, a => 0UL);
            for (int step = 0; step < 4000; step++)
            {
                string actor = actors[random.Next(actors.Length)];
                int x = random.Next(4), z = random.Next(4);
                bool remove = random.Next(3) == 0;
                var intent = new EditIntent(Guid.NewGuid(), C, Index(x, remove ? 0 : 1, z), lastSeen[actor], remove ? EditAction.Remove : EditAction.Place, BlockIds.Dirt);
                EditResult result = _authority.Submit(actor, intent, 16 + x + 0.5f, 3, 16 + z + 0.5f, NoOccupants, (uint)step, step);
                if (result.Accepted)
                {
                    accepted.Add(result.Batch!);
                }

                if (random.Next(4) == 0)
                {
                    lastSeen[actor] = _authority.GetChunk(C)!.Revision;
                }
            }

            Assert.That(accepted.Count, Is.GreaterThan(20), "some edits must win");
            for (int i = 0; i < accepted.Count; i++)
            {
                Assert.That(accepted[i].BaseRevision, Is.EqualTo((ulong)i), "one gap-free revision chain");
                Assert.That(accepted[i].NewRevision, Is.EqualTo((ulong)i + 1));
            }

            // Replaying the accepted batches in order onto the original chunk reproduces the authority's state.
            ChunkData replay = Flat(C);
            foreach (ChunkMutationBatch batch in accepted)
            {
                foreach (BlockChange change in batch.Changes)
                {
                    replay.Set(LocalBlock.FromIndex(change.Index), change.BlockType);
                }
            }

            Assert.That(ChunkHash.Compute(replay), Is.EqualTo(ChunkHash.Compute(_authority.GetChunk(C)!)));
            Assert.That(replay.Revision, Is.EqualTo(_authority.GetChunk(C)!.Revision));
        }
    }
}
