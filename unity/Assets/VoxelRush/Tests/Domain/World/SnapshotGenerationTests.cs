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
    /// <summary>SAVE-003: compressed snapshots, two generations, journal compaction, verified import.</summary>
    public sealed class SnapshotGenerationTests
    {
        private static readonly TerrainGenerator Generator = new TerrainGenerator(12345);
        private static readonly ChunkCoord C = new ChunkCoord(0, 0, 0);
        private string _dir = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "voxelrush-snap-" + Guid.NewGuid().ToString("N"));
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

        private WorldStore Store(string? dir = null) => new WorldStore(dir ?? _dir, TerrainGenerator.Version, Generator.GenerateBlocks);

        private string SnapPath => Path.Combine(_dir, "snapshots", "c_0_0_0.snap");

        // Appends one single-block edit per revision starting at `from`.
        private void Journal(ulong from, int count)
        {
            using var writer = new JournalWriter(Path.Combine(_dir, WorldStore.JournalFileName), capacity: 4096);
            for (int i = 0; i < count; i++)
            {
                ulong rev = from + (ulong)i;
                Assert.That(writer.TryEnqueue(new ChunkMutationBatch(Guid.NewGuid(), "p", C, rev, rev + 1, 1,
                    new[] { new BlockChange((ushort)(rev % 4096), rev % 2 == 0 ? BlockIds.Brick : BlockIds.Glass) })), Is.True);
            }
        }

        private ChunkData Reload(WorldStore? store = null)
        {
            store ??= Store();
            store.RecoverJournal();
            return store.LoadChunk(C);
        }

        [Test]
        public void Snapshots_AreCompressed()
        {
            Journal(0, 2000);
            WorldStore store = Store();
            ChunkData chunk = Reload(store);
            store.WriteSnapshot(chunk);

            long size = new FileInfo(SnapPath).Length;
            Assert.That(chunk.EditCount, Is.GreaterThan(1000));
            Assert.That(size, Is.LessThan(chunk.EditCount * 3 / 2), "well under the raw 3 bytes per edit");
            Assert.That(ChunkHash.Compute(Reload()), Is.EqualTo(ChunkHash.Compute(chunk)));
        }

        [Test]
        public void Schema1Snapshots_StillLoad()
        {
            ChunkData chunk = ChunkData.FromSnapshot(C, TerrainGenerator.Version, Generator.GenerateBlocks(C), 9,
                new[] { new KeyValuePair<ushort, byte>(5, BlockIds.Metal), new KeyValuePair<ushort, byte>(4000, BlockIds.Sand) });
            Directory.CreateDirectory(Path.GetDirectoryName(SnapPath)!);
            File.WriteAllBytes(SnapPath, LegacySnapshot(chunk));

            ChunkData loaded = Reload();

            Assert.That(loaded.Revision, Is.EqualTo(9));
            Assert.That(ChunkHash.Compute(loaded), Is.EqualTo(ChunkHash.Compute(chunk)));
        }

        [Test]
        public void CorruptLatestSnapshot_FallsBackToThePreviousGeneration_AfterCompaction()
        {
            Journal(0, 10);
            WorldStore store = Store();
            store.WriteSnapshot(Reload(store));           // rev 10
            Journal(10, 10);
            store.WriteSnapshot(Reload(store));           // rev 20, rev 10 becomes .prev
            Journal(20, 5);
            ChunkData expected = Reload(store);           // rev 25

            Assert.That(store.CompactJournal(), Is.EqualTo(10), "records up to the older generation (rev 10) go");
            byte[] bytes = File.ReadAllBytes(SnapPath);
            bytes[bytes.Length / 2] ^= 0x5A;
            File.WriteAllBytes(SnapPath, bytes);

            WorldStore reopened = Store();
            ChunkData restored = Reload(reopened);

            Assert.That(reopened.RejectedSnapshots, Is.EqualTo(1));
            Assert.That(restored.Revision, Is.EqualTo(25), "previous snapshot (rev 10) + kept journal 11..25");
            Assert.That(ChunkHash.Compute(restored), Is.EqualTo(ChunkHash.Compute(expected)));
            Assert.That(reopened.Problems.Any(p => p.Contains("journal gap")), Is.False);
        }

        [Test]
        public void Compaction_WithOneGeneration_KeepsEverything_AndNeverChangesTheWorld()
        {
            Journal(0, 10);
            WorldStore store = Store();
            store.WriteSnapshot(Reload(store));

            Assert.That(store.CompactJournal(), Is.Zero, "a lone snapshot is not trusted alone");

            Journal(10, 3);
            store.WriteSnapshot(Reload(store));
            ChunkData before = Reload(store);
            int dropped = store.CompactJournal();
            ChunkData after = Reload();

            Assert.That(dropped, Is.EqualTo(10));
            Assert.That(store.RecoveredRecords.Count, Is.EqualTo(3));
            Assert.That((after.Revision, ChunkHash.Compute(after)), Is.EqualTo((before.Revision, ChunkHash.Compute(before))));
            Assert.That(store.CompactJournal(), Is.Zero, "idempotent");
        }

        [Test]
        public void BothSnapshotsCorrupt_RebuildsFromBaseAndRemainingJournal_ReportingTheGap()
        {
            Journal(0, 4);
            WorldStore store = Store();
            store.WriteSnapshot(Reload(store));
            Journal(4, 4);
            store.WriteSnapshot(Reload(store));
            store.CompactJournal();
            File.WriteAllBytes(SnapPath, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(SnapPath + ".prev", new byte[] { 4, 5, 6, 7, 8 });

            WorldStore reopened = Store();
            ChunkData chunk = Reload(reopened);

            Assert.That(reopened.RejectedSnapshots, Is.EqualTo(2));
            Assert.That(chunk.Revision, Is.Zero, "no silent rebuild on top of lost history");
            Assert.That(reopened.Problems.Any(p => p.Contains("journal gap")), Is.True, "operators see the loss");
        }

        [Test]
        public void Import_AcceptsOnlyVerifiedNewerSnapshotsForThisChunk()
        {
            string other = Path.Combine(_dir, "other");
            Journal(0, 6);
            WorldStore source = Store();
            source.WriteSnapshot(Reload(source));
            byte[] good = source.ReadSnapshotBytes(C)!;
            WorldStore target = Store(other);

            byte[] tampered = (byte[])good.Clone();
            tampered[20] ^= 1;
            Assert.That(target.ImportSnapshot(C, tampered), Is.False);
            Assert.That(target.ImportSnapshot(new ChunkCoord(1, 0, 0), good), Is.False, "bytes for another chunk");
            Assert.That(new WorldStore(other, TerrainGenerator.Version + 1, Generator.GenerateBlocks).ImportSnapshot(C, good), Is.False, "other generator");
            Assert.That(target.RejectedSnapshots, Is.EqualTo(2));

            Assert.That(target.ImportSnapshot(C, good), Is.True);
            Assert.That(target.ImportSnapshot(C, good), Is.False, "not newer");
            target.RecoverJournal();
            Assert.That(ChunkHash.Compute(target.LoadChunk(C)), Is.EqualTo(ChunkHash.Compute(Reload(source))));
        }

        [Test]
        public void HostileEditCounts_AreRejected()
        {
            Journal(0, 3);
            WorldStore store = Store();
            store.WriteSnapshot(Reload(store));
            byte[] bytes = File.ReadAllBytes(SnapPath);
            // Edit count sits after magic(4) schema(2) gen(4) xyz(12) rev(8) hash(8) = offset 38.
            bytes[38] = 0xFF;
            bytes[39] = 0xFF;
            JournalCodec.WriteUInt32(bytes, bytes.Length - 4, Crc32.Compute(bytes, 0, bytes.Length - 4));

            Assert.That(Store().ImportSnapshot(new ChunkCoord(0, 0, 0), bytes), Is.False);
        }

        private static byte[] LegacySnapshot(ChunkData chunk)
        {
            using var body = new MemoryStream();
            using (var w = new BinaryWriter(body))
            {
                w.Write(0x4E535256u);
                w.Write((ushort)1);
                w.Write(chunk.GeneratorVersion);
                w.Write(chunk.Coord.X);
                w.Write(chunk.Coord.Y);
                w.Write(chunk.Coord.Z);
                w.Write(chunk.Revision);
                w.Write(ChunkHash.Compute(chunk));
                w.Write((ushort)chunk.EditCount);
                foreach (KeyValuePair<ushort, byte> e in chunk.Edits)
                {
                    w.Write(e.Key);
                    w.Write(e.Value);
                }
            }

            byte[] payload = body.ToArray();
            var bytes = new byte[payload.Length + 4];
            Buffer.BlockCopy(payload, 0, bytes, 0, payload.Length);
            JournalCodec.WriteUInt32(bytes, payload.Length, Crc32.Compute(payload, 0, payload.Length));
            return bytes;
        }
    }
}
