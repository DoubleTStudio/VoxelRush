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
    /// <summary>SAVE-003: cold restore from object storage and publish on shutdown.</summary>
    public sealed class SnapshotSyncTests
    {
        private static readonly TerrainGenerator Generator = new TerrainGenerator(12345);
        private static readonly ChunkCoord A = new ChunkCoord(0, 0, 0);
        private static readonly ChunkCoord B = new ChunkCoord(1, 0, 0);
        private readonly List<string> _dirs = new List<string>();

        private sealed class FakeRemote : ISnapshotRemote
        {
            public readonly Dictionary<ChunkCoord, (ulong Revision, byte[] Bytes)> Objects = new Dictionary<ChunkCoord, (ulong, byte[])>();
            public bool Down;
            public int Uploads;

            public byte[]? Download(ChunkCoord coord)
            {
                if (Down)
                {
                    throw new IOException("unreachable");
                }

                return Objects.TryGetValue(coord, out var o) ? o.Bytes : null;
            }

            public void Upload(ChunkCoord coord, ulong revision, byte[] bytes)
            {
                if (Down)
                {
                    throw new IOException("unreachable");
                }

                Uploads++;
                Objects[coord] = (revision, bytes);
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (string dir in _dirs.Where(Directory.Exists))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private WorldStore NewMachine()
        {
            string dir = Path.Combine(Path.GetTempPath(), "voxelrush-sync-" + Guid.NewGuid().ToString("N"));
            _dirs.Add(dir);
            return new WorldStore(dir, TerrainGenerator.Version, Generator.GenerateBlocks);
        }

        private static ChunkData Edited(ChunkCoord coord, int edits)
        {
            var chunk = new ChunkData(coord, TerrainGenerator.Version, Generator.GenerateBlocks(coord));
            for (int i = 0; i < edits; i++)
            {
                chunk.Set(LocalBlock.FromIndex(100 + i), i % 2 == 0 ? BlockIds.Brick : BlockIds.Air);
            }

            return chunk;
        }

        [Test]
        public void ShutdownThenColdRestoreOnAnotherMachine_RebuildsTheWorld()
        {
            var remote = new FakeRemote();
            ChunkData a = Edited(A, 7), b = Edited(B, 3);
            var first = new SnapshotSync(NewMachine(), remote);

            Assert.That(first.Publish(new[] { a, b, Edited(new ChunkCoord(2, 0, 0), 0) }), Is.EqualTo((2, 2, 0)), "untouched chunks aren't stored");

            WorldStore fresh = NewMachine();
            Assert.That(new SnapshotSync(fresh, remote).Restore(new[] { A, B, new ChunkCoord(2, 0, 0) }), Is.EqualTo(2));
            fresh.RecoverJournal();
            Assert.That(ChunkHash.Compute(fresh.LoadChunk(A)), Is.EqualTo(ChunkHash.Compute(a)));
            Assert.That(fresh.LoadChunk(B).Revision, Is.EqualTo(b.Revision));
        }

        [Test]
        public void Publish_SkipsWhatIsAlreadyUploaded_AndRetriesFailures()
        {
            var remote = new FakeRemote();
            WorldStore store = NewMachine();
            ChunkData a = Edited(A, 4);
            new SnapshotSync(store, remote).Publish(new[] { a });
            Assert.That(new SnapshotSync(store, remote).Publish(new[] { a }), Is.EqualTo((0, 0, 0)), "index survives restarts");

            a.Set(LocalBlock.FromIndex(900), BlockIds.Metal);
            remote.Down = true;
            Assert.That(new SnapshotSync(store, remote).Publish(new[] { a }), Is.EqualTo((1, 0, 1)));
            remote.Down = false;
            Assert.That(new SnapshotSync(store, remote).Publish(new[] { a }), Is.EqualTo((0, 1, 0)), "local snapshot reused, upload retried");
            Assert.That(remote.Objects[A].Revision, Is.EqualTo(a.Revision));
        }

        [Test]
        public void Restore_KeepsNewerLocalState_AndRejectsCorruptRemoteObjects()
        {
            var remote = new FakeRemote();
            WorldStore store = NewMachine();
            new SnapshotSync(NewMachine(), remote).Publish(new[] { Edited(A, 2), Edited(B, 2) });
            store.WriteSnapshot(Edited(A, 9));
            byte[] corrupt = (byte[])remote.Objects[B].Bytes.Clone();
            corrupt[12] ^= 0xFF;
            remote.Objects[B] = (2, corrupt);

            Assert.That(new SnapshotSync(store, remote).Restore(new[] { A, B }), Is.Zero);
            Assert.That(store.SnapshotRevision(A), Is.EqualTo(9));
            Assert.That(store.SnapshotRevision(B), Is.Null);
            Assert.That(store.Problems.Single(), Does.Contain("imported snapshot rejected"));
        }

        [Test]
        public void Restore_FailsClosed_WhenStorageIsUnreachable()
        {
            Assert.Throws<IOException>(() => new SnapshotSync(NewMachine(), new FakeRemote { Down = true }).Restore(new[] { A }));
        }
    }
}
