using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Persistence;

namespace VoxelRush.Tests.Domain.Security
{
    /// <summary>VR-082 (QUALITY_GATES Security: storage fault injection): a failing disk never yields an acknowledged-but-lost edit.</summary>
    public sealed class StorageFaultTests
    {
        /// <summary>Accepts writes until <see cref="FailAfterBytes"/>, then fails writes or flushes like a full or broken disk.</summary>
        private sealed class FaultyStream : MemoryStream
        {
            public long FailAfterBytes = long.MaxValue;
            public bool FailFlush;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Length + count > FailAfterBytes)
                {
                    throw new IOException("No space left on device");
                }

                base.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                if (FailFlush)
                {
                    throw new IOException("fsync failed");
                }

                base.Flush();
            }
        }

        private static ChunkMutationBatch Batch(ulong baseRevision) =>
            new ChunkMutationBatch(Guid.NewGuid(), "client:1", new ChunkCoord(0, 0, 0), baseRevision, baseRevision + 1, 1, new[] { new BlockChange((ushort)baseRevision, BlockIds.Stone) });

        private static void WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 200 && !condition(); i++)
            {
                Thread.Sleep(10);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DiskFullOrFsyncFailure_StopsAcknowledgements_AndRefusesNewEdits(bool failFlush)
        {
            var stream = new FaultyStream();
            using var writer = new JournalWriter(stream, "faulty.log");
            var durable = new List<ChunkMutationBatch>();
            Assert.That(writer.TryEnqueue(Batch(0)), Is.True);
            WaitFor(() =>
            {
                writer.DrainDurable(durable);
                return writer.DurableCount > 0;
            });
            Assert.That(writer.DurableCount, Is.EqualTo(1), "healthy write is durable");

            stream.FailAfterBytes = failFlush ? long.MaxValue : stream.Length + 10;
            stream.FailFlush = failFlush;
            writer.TryEnqueue(Batch(1));
            WaitFor(() => writer.Failure != null);

            Assert.That(writer.Failure, Is.InstanceOf<IOException>());
            Assert.That(writer.CanAccept, Is.False);
            Assert.That(writer.TryEnqueue(Batch(2)), Is.False, "no more edits can be queued for acknowledgement");
            writer.DrainDurable(durable);
            Assert.That(writer.DurableCount, Is.EqualTo(1), "the failed batch is never reported durable (never acknowledged)");
        }

        [Test]
        public void Authority_RefusesEdits_OnceTheJournalFailed()
        {
            var stream = new FaultyStream { FailFlush = true };
            using var writer = new JournalWriter(stream, "faulty.log");
            var authority = new WorldAuthority(new EditValidator(ZoneMap.Empty), sink: writer);
            authority.AddChunk(new ChunkData(new ChunkCoord(0, 0, 0), 1, new byte[ChunkMath.Volume]));
            EditResult Place(int x) => authority.Submit("alice", new EditIntent(Guid.NewGuid(), new ChunkCoord(0, 0, 0), new LocalBlock(x, 1, 1).Index,
                authority.GetChunk(new ChunkCoord(0, 0, 0))!.Revision, EditAction.Place, BlockIds.Stone), x + 0.5f, 3f, 1.5f, Array.Empty<WorldBox>(), 1, x);

            Assert.That(Place(1).Accepted, Is.True, "applied and queued; not yet durable");
            WaitFor(() => writer.Failure != null);
            Assert.That(Place(3).Rejection, Is.EqualTo(EditRejection.PersistenceBackpressure));
            Assert.That(authority.CommitServerChanges(Guid.NewGuid(), "server", new[] { new ChunkChanges(new ChunkCoord(0, 0, 0), new[] { new BlockChange(9, BlockIds.Stone) }) }, 1).Rejection,
                Is.EqualTo(EditRejection.PersistenceBackpressure), "server-originated changes too");
        }

        [Test]
        public void TornWrite_IsTruncatedOnRecovery_AndOnlyFlushedRecordsSurvive()
        {
            string dir = Path.Combine(Path.GetTempPath(), "voxelrush-fault-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new WorldStore(dir, 1, _ => new byte[ChunkMath.Volume]);
                using (var writer = new JournalWriter(store.JournalPath))
                {
                    writer.TryEnqueue(Batch(0));
                    writer.TryEnqueue(Batch(1));
                }

                // Half a record from a write the disk cut off.
                byte[] partial = JournalCodec.Encode(Batch(2));
                using (var f = new FileStream(store.JournalPath, FileMode.Append))
                {
                    f.Write(partial, 0, partial.Length / 2);
                }

                var recovered = new WorldStore(dir, 1, _ => new byte[ChunkMath.Volume]);
                recovered.RecoverJournal();

                Assert.That(recovered.RecoveredRecords.Select(r => r.NewRevision), Is.EqualTo(new ulong[] { 1, 2 }));
                Assert.That(recovered.TruncatedBytes, Is.EqualTo(partial.Length / 2));
                Assert.That(recovered.LoadChunk(new ChunkCoord(0, 0, 0)).Revision, Is.EqualTo(2));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
