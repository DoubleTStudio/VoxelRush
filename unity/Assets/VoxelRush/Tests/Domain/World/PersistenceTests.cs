using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Persistence;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>VR-032: journal framing, torn-tail recovery, snapshots, corruption handling and replay.</summary>
    public sealed class PersistenceTests
    {
        private static readonly TerrainGenerator Generator = new TerrainGenerator(12345);
        private static readonly ChunkCoord C = new ChunkCoord(0, 0, 0);
        private string _dir = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "voxelrush-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }

        private WorldStore Store() => new WorldStore(_dir, TerrainGenerator.Version, Generator.GenerateBlocks);

        private static ChunkMutationBatch Batch(ulong baseRevision, ushort index, byte block, ChunkCoord? chunk = null) =>
            new ChunkMutationBatch(Guid.NewGuid(), "alice", chunk ?? C, baseRevision, baseRevision + 1, 7, new[] { new BlockChange(index, block) });

        private void WriteJournal(params ChunkMutationBatch[] batches)
        {
            using var writer = new JournalWriter(Path.Combine(_dir, WorldStore.JournalFileName));
            foreach (ChunkMutationBatch batch in batches)
            {
                Assert.That(writer.TryEnqueue(batch), Is.True);
            }
        }

        [Test]
        public void Codec_RoundTripsBatches()
        {
            ChunkMutationBatch original = new ChunkMutationBatch(Guid.NewGuid(), "bob", new ChunkCoord(-3, 1, 9), 41, 42, 99,
                new[] { new BlockChange(4095, BlockIds.Air), new BlockChange(0, BlockIds.Stone) });
            var records = new List<ChunkMutationBatch>();

            JournalCodec.ReadAll(new MemoryStream(JournalCodec.Encode(original)), records);

            ChunkMutationBatch decoded = records.Single();
            Assert.That((decoded.OperationId, decoded.ActorId, decoded.Chunk, decoded.BaseRevision, decoded.NewRevision, decoded.ServerTick),
                Is.EqualTo((original.OperationId, original.ActorId, original.Chunk, original.BaseRevision, original.NewRevision, original.ServerTick)));
            Assert.That(decoded.Changes.Select(c => (c.Index, c.BlockType)), Is.EqualTo(original.Changes.Select(c => (c.Index, c.BlockType))));
        }

        [Test]
        public void Writer_ReportsDurableBatchesInOrder()
        {
            var batches = Enumerable.Range(0, 50).Select(i => Batch((ulong)i, (ushort)i, BlockIds.Dirt)).ToArray();
            using (var writer = new JournalWriter(Path.Combine(_dir, WorldStore.JournalFileName)))
            {
                foreach (ChunkMutationBatch b in batches)
                {
                    writer.TryEnqueue(b);
                }

                var durable = new List<ChunkMutationBatch>();
                var all = new List<ChunkMutationBatch>();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (all.Count < batches.Length && DateTime.UtcNow < deadline)
                {
                    writer.DrainDurable(durable);
                    all.AddRange(durable);
                }

                Assert.That(all.Select(b => b.OperationId), Is.EqualTo(batches.Select(b => b.OperationId)));
                Assert.That(writer.Pending, Is.Zero);
            }
        }

        [Test]
        public void Recovery_ReplaysJournalOntoGeneratedBase()
        {
            WriteJournal(Batch(0, 300, BlockIds.Air), Batch(1, 301, BlockIds.Stone));
            WorldStore store = Store();
            store.RecoverJournal();

            ChunkData chunk = store.LoadChunk(C);

            Assert.That(chunk.Revision, Is.EqualTo(2));
            Assert.That(chunk.Get(LocalBlock.FromIndex(300)), Is.EqualTo(BlockIds.Air));
            Assert.That(chunk.Get(LocalBlock.FromIndex(301)), Is.EqualTo(BlockIds.Stone));
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(11)]
        public void TornTail_FromCrashMidWrite_IsTruncated_AndEarlierRecordsSurvive(int partialBytes)
        {
            WriteJournal(Batch(0, 300, BlockIds.Air), Batch(1, 301, BlockIds.Stone));
            byte[] torn = JournalCodec.Encode(Batch(2, 302, BlockIds.Dirt)).Take(partialBytes).ToArray();
            using (var f = new FileStream(Path.Combine(_dir, WorldStore.JournalFileName), FileMode.Append))
            {
                f.Write(torn, 0, torn.Length);
            }

            WorldStore store = Store();
            store.RecoverJournal();

            Assert.That(store.TruncatedBytes, Is.EqualTo(partialBytes));
            Assert.That(store.LoadChunk(C).Revision, Is.EqualTo(2), "the un-acknowledged partial record is dropped");
            Store().RecoverJournal();
            Assert.That(new FileInfo(Path.Combine(_dir, WorldStore.JournalFileName)).Length, Is.EqualTo(JournalCodec.Encode(Batch(0, 300, 0)).Length * 2), "file truncated to the valid prefix");
        }

        [Test]
        public void CorruptRecordInTheMiddle_StopsReplayThere()
        {
            WriteJournal(Batch(0, 300, BlockIds.Air), Batch(1, 301, BlockIds.Stone), Batch(2, 302, BlockIds.Dirt));
            string path = Path.Combine(_dir, WorldStore.JournalFileName);
            byte[] bytes = File.ReadAllBytes(path);
            int recordLength = bytes.Length / 3;
            bytes[recordLength + 20] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            WorldStore store = Store();
            store.RecoverJournal();

            Assert.That(store.RecoveredRecords.Count, Is.EqualTo(1));
            Assert.That(store.LoadChunk(C).Revision, Is.EqualTo(1));
        }

        [Test]
        public void Snapshot_PlusLaterJournal_RestoresExactState()
        {
            WriteJournal(Batch(0, 300, BlockIds.Air), Batch(1, 301, BlockIds.Stone));
            WorldStore store = Store();
            store.RecoverJournal();
            ChunkData atTwo = store.LoadChunk(C);
            store.WriteSnapshot(atTwo);
            WriteJournal(Batch(2, 302, BlockIds.Dirt));

            WorldStore reopened = Store();
            reopened.RecoverJournal();
            ChunkData restored = reopened.LoadChunk(C);

            Assert.That(restored.Revision, Is.EqualTo(3));
            Assert.That(restored.Get(LocalBlock.FromIndex(300)), Is.EqualTo(BlockIds.Air));
            Assert.That(restored.Get(LocalBlock.FromIndex(302)), Is.EqualTo(BlockIds.Dirt));
        }

        [Test]
        public void CorruptSnapshot_IsRejected_AndChunkRebuiltFromJournal()
        {
            WriteJournal(Batch(0, 300, BlockIds.Air), Batch(1, 301, BlockIds.Stone));
            WorldStore store = Store();
            store.RecoverJournal();
            store.WriteSnapshot(store.LoadChunk(C));
            string snapshot = Directory.GetFiles(Path.Combine(_dir, "snapshots")).Single();
            byte[] bytes = File.ReadAllBytes(snapshot);
            bytes[10] ^= 0x55;
            File.WriteAllBytes(snapshot, bytes);

            WorldStore reopened = Store();
            reopened.RecoverJournal();
            ChunkData chunk = reopened.LoadChunk(C);

            Assert.That(reopened.RejectedSnapshots, Is.EqualTo(1));
            Assert.That(reopened.Problems.Single(), Does.Contain("snapshot rejected"));
            Assert.That(chunk.Revision, Is.EqualTo(2), "journal still holds the full history");
        }

        [Test]
        public void JournalGap_IsReported_NotPapered()
        {
            WriteJournal(Batch(0, 300, BlockIds.Air), Batch(5, 301, BlockIds.Stone));
            WorldStore store = Store();
            store.RecoverJournal();

            ChunkData chunk = store.LoadChunk(C);

            Assert.That(chunk.Revision, Is.EqualTo(1));
            Assert.That(store.Problems.Single(), Does.Contain("journal gap"));
        }

        [Test]
        public void RecoveredOperations_MakeRetriesIdempotent()
        {
            // Crash after the record was durable but before the ACK reached the client: the retry must not re-apply.
            var authority = new WorldAuthority(new EditValidator(Array.Empty<WorldBox>()));
            ChunkData chunk = Generator.Generate(C);
            authority.AddChunk(chunk);
            int surfaceIndex = Enumerable.Range(0, ChunkMath.Volume).First(i => chunk.Get(LocalBlock.FromIndex(i)) != BlockIds.Air && LocalBlock.FromIndex(i).Y > 0);
            LocalBlock local = LocalBlock.FromIndex(surfaceIndex);
            var intent = new EditIntent(Guid.NewGuid(), C, surfaceIndex, 0, EditAction.Remove, 0);
            EditResult original = authority.Submit("alice", intent, local.X + 0.5f, local.Y + 2f, local.Z + 0.5f, Array.Empty<WorldBox>(), 1, 1);
            Assert.That(original.Accepted, Is.True);
            WriteJournal(original.Batch!);

            WorldStore store = Store();
            store.RecoverJournal();
            var restarted = new WorldAuthority(new EditValidator(Array.Empty<WorldBox>()));
            restarted.AddChunk(store.LoadChunk(C));
            restarted.RememberRecovered(store.RecoveredRecords);

            EditResult retry = restarted.Submit("alice", intent, local.X + 0.5f, local.Y + 2f, local.Z + 0.5f, Array.Empty<WorldBox>(), 2, 2);
            Assert.That(retry.Accepted && retry.IsDuplicate, Is.True);
            Assert.That(restarted.GetChunk(C)!.Revision, Is.EqualTo(1), "never applied twice");
        }

        [Test]
        public void Backpressure_FullQueueRefusesInsteadOfAcknowledging()
        {
            using var writer = new JournalWriter(Path.Combine(_dir, WorldStore.JournalFileName), capacity: 2, maxGroup: 1);
            int refused = 0;
            for (int i = 0; i < 2000; i++)
            {
                if (!writer.TryEnqueue(Batch((ulong)i, 1, BlockIds.Dirt)))
                {
                    refused++;
                }
            }

            Assert.That(refused, Is.GreaterThan(0), "a bounded queue must push back under a burst");
        }

        [Test]
        public void Authority_RefusesBeforeApplying_WhenTheJournalCannotAccept()
        {
            var sink = new FullSink();
            var authority = new WorldAuthority(new EditValidator(Array.Empty<WorldBox>()), sink: sink);
            ChunkData chunk = Generator.Generate(C);
            authority.AddChunk(chunk);
            int air = Enumerable.Range(0, ChunkMath.Volume).First(i => chunk.Get(LocalBlock.FromIndex(i)) == BlockIds.Air);
            LocalBlock l = LocalBlock.FromIndex(air);
            var intent = new EditIntent(Guid.NewGuid(), C, air, 0, EditAction.Place, BlockIds.Stone);

            EditResult refused = authority.Submit("alice", intent, l.X + 0.5f, l.Y + 2f, l.Z + 0.5f, Array.Empty<WorldBox>(), 1, 1);
            Assert.That(refused.Rejection, Is.EqualTo(EditRejection.PersistenceBackpressure));
            Assert.That(chunk.Revision, Is.Zero, "nothing applied, so no revision without a journal record");

            sink.Full = false;
            EditResult retried = authority.Submit("alice", intent, l.X + 0.5f, l.Y + 2f, l.Z + 0.5f, Array.Empty<WorldBox>(), 2, 2);
            Assert.That(retried.Accepted, Is.True, "transient: same operation id retries");
            Assert.That(sink.Accepted.Single().NewRevision, Is.EqualTo(1));
        }

        private sealed class FullSink : IMutationSink
        {
            public bool Full = true;
            public readonly List<ChunkMutationBatch> Accepted = new List<ChunkMutationBatch>();

            public bool CanAccept => !Full;

            public bool CanAcceptMany(int count) => !Full;

            public void Accept(ChunkMutationBatch batch) => Accepted.Add(batch);
        }

        [Test]
        public void SnapshotNames_AreIntegerOnly()
        {
            WorldStore store = Store();
            store.WriteSnapshot(Generator.Generate(new ChunkCoord(-12, 3, 7)));

            Assert.That(Path.GetFileName(Directory.GetFiles(Path.Combine(_dir, "snapshots")).Single()), Is.EqualTo("c_-12_3_7.snap"));
            Assert.That(store.KnownChunks(), Does.Contain(new ChunkCoord(-12, 3, 7)));
        }
    }
}
