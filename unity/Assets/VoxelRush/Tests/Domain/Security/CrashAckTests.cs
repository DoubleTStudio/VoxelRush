using System;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Persistence;

namespace VoxelRush.Tests.Domain.Security
{
    /// <summary>
    /// SAVE-002 acceptance (SAVE_GAME QA: "kill server after ACK: committed changes survive; kill before ACK: operation
    /// either committed once or safely retried; never duplicated"), end to end through store, journal and authority.
    /// </summary>
    public sealed class CrashAckTests
    {
        private static readonly ChunkCoord C = new ChunkCoord(0, 0, 0);
        private string _dir = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "voxelrush-ack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_dir, recursive: true);

        private WorldStore Store() => new WorldStore(_dir, 1, _ => new byte[ChunkMath.Volume]);

        /// <summary>A server process: recovers the store, re-seeds idempotency, edits through the journal.</summary>
        private (WorldAuthority Authority, JournalWriter Journal) Boot(Stream? journalStream = null)
        {
            WorldStore store = Store();
            store.RecoverJournal();
            JournalWriter journal = journalStream == null ? new JournalWriter(store.JournalPath) : new JournalWriter(journalStream, store.JournalPath);
            var authority = new WorldAuthority(new EditValidator(ZoneMap.Empty), sink: journal);
            authority.AddChunk(store.LoadChunk(C));
            authority.RememberRecovered(store.RecoveredRecords);
            return (authority, journal);
        }

        private static EditResult Place(WorldAuthority a, Guid op, ulong expected) =>
            a.Submit("alice", new EditIntent(op, C, new LocalBlock(3, 1, 3).Index, expected, EditAction.Place, BlockIds.Stone), 3.5f, 3f, 4.5f, Array.Empty<WorldBox>(), 1, 0);

        private static void WaitDurable(JournalWriter journal, long count)
        {
            var drained = new System.Collections.Generic.List<ChunkMutationBatch>();
            for (int i = 0; i < 300 && journal.DurableCount < count; i++)
            {
                journal.DrainDurable(drained);
                Thread.Sleep(5);
            }

            Assert.That(journal.DurableCount, Is.EqualTo(count), "acknowledged (durable)");
        }

        [Test]
        public void CrashAfterAck_ChangeSurvives_AndTheRetryIsNotAppliedTwice()
        {
            var op = Guid.NewGuid();
            (WorldAuthority a, JournalWriter j) = Boot();
            Assert.That(Place(a, op, 0).Accepted, Is.True);
            WaitDurable(j, 1);
            // Crash: the process dies without a graceful shutdown (the journal file is simply left as is).
            j.Dispose();

            (WorldAuthority restarted, JournalWriter j2) = Boot();
            // The client never saw the ACK (lost with the connection) and retries the same operation.
            EditResult retry = Place(restarted, op, 0);

            Assert.That(retry.Accepted && retry.IsDuplicate, Is.True, "original result, nothing applied again");
            Assert.That(restarted.GetChunk(C)!.Revision, Is.EqualTo(1));
            Assert.That(restarted.GetBlock(new BlockPos(3, 1, 3)), Is.EqualTo(BlockIds.Stone));
            j2.Dispose();
            Assert.That(Store().Also(s => s.RecoverJournal()).RecoveredRecords.Count, Is.EqualTo(1), "one journal record, not two");
        }

        [Test]
        public void CrashBeforeAck_ChangeIsAbsent_AndTheRetryAppliesExactlyOnce()
        {
            var op = Guid.NewGuid();
            // The disk accepts nothing durable: the server crashes before the record is fsynced (no ACK sent).
            (WorldAuthority a, JournalWriter j) = Boot(new FailingFlushStream(Path.Combine(_dir, WorldStore.JournalFileName)));
            Assert.That(Place(a, op, 0).Accepted, Is.True, "applied in memory, queued, never durable");
            for (int i = 0; i < 100 && j.Failure == null; i++)
            {
                Thread.Sleep(5);
            }

            Assert.That(j.DurableCount, Is.Zero, "never acknowledged");
            j.Dispose();

            (WorldAuthority restarted, JournalWriter j2) = Boot();
            Assert.That(restarted.GetChunk(C)!.Revision, Is.Zero, "the unacknowledged change is gone");
            EditResult retry = Place(restarted, op, 0);
            Assert.That(retry.Accepted && !retry.IsDuplicate, Is.True, "safe retry applies");
            WaitDurable(j2, 1);
            Assert.That(Place(restarted, op, 0).IsDuplicate, Is.True, "a second retry is a duplicate");
            j2.Dispose();

            WorldStore final = Store();
            final.RecoverJournal();
            Assert.That(final.RecoveredRecords.Select(r => r.OperationId), Is.EqualTo(new[] { op }), "committed exactly once");
        }

        /// <summary>Writes reach the OS but fsync fails, like a crash right before durability.</summary>
        private sealed class FailingFlushStream : FileStream
        {
            public FailingFlushStream(string path) : base(path, FileMode.OpenOrCreate, FileAccess.Write)
            {
            }

            public override void Write(byte[] array, int offset, int count)
            {
                // Nothing reaches the file: a power cut loses unsynced data.
            }

            public override void Flush(bool flushToDisk) => throw new IOException("power lost before fsync");

            public override void Flush() => throw new IOException("power lost before fsync");
        }
    }

    internal static class TestExtensions
    {
        public static T Also<T>(this T value, Action<T> action)
        {
            action(value);
            return value;
        }
    }
}
