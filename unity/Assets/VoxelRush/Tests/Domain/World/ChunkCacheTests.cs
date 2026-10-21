using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>SAVE-008: disposable, version-checked client chunk cache and its wire codec.</summary>
    public sealed class ChunkCacheTests
    {
        private static readonly Guid WorldId = Guid.NewGuid();
        private static readonly WorldInfo Plot = new WorldInfo(WorldId, GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, ChunkData.CurrentSchemaVersion,
            new ChunkCoord(-1, -1, -1), new ChunkCoord(1, 0, 1));
        private string _root = null!;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "voxelrush-cache-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static ChunkData Chunk(ChunkCoord coord, int edits)
        {
            var chunk = new ChunkData(coord, FlatPlotGenerator.Version, new FlatPlotGenerator().GenerateBlocks(coord));
            for (int i = 0; i < edits; i++)
            {
                chunk.Set(LocalBlock.FromIndex(200 + i), BlockIds.Brick);
            }

            return chunk;
        }

        [Test]
        public void StoredChunks_AreOfferedInTheManifest_AndLoadOnlyAtTheConfirmedVersion()
        {
            ChunkData a = Chunk(new ChunkCoord(0, 0, 0), 4), b = Chunk(new ChunkCoord(1, 0, 0), 2);
            var cache = new ChunkCache(_root, Plot);
            Assert.That(cache.Store(new[] { a, b }), Is.EqualTo(2));

            var reopened = new ChunkCache(_root, Plot);
            List<CachedChunk> manifest = reopened.Manifest();
            Assert.That(manifest.Select(e => (e.Coord, e.Revision, e.ContentHash)),
                Is.EquivalentTo(new[] { (a.Coord, a.Revision, ChunkHash.Compute(a)), (b.Coord, b.Revision, ChunkHash.Compute(b)) }));

            ChunkSnapshotMessage? hit = reopened.TryLoad(a.Coord, a.Revision, ChunkHash.Compute(a));
            var replica = new ClientWorldReplica(Plot);
            Assert.That(replica.ApplySnapshot(hit!), Is.True, "the replica re-verifies the hash");
            Assert.That(ChunkHash.Compute(replica.Get(a.Coord)!), Is.EqualTo(ChunkHash.Compute(a)));
        }

        [Test]
        public void StaleEntries_AreDroppedWhenTheServerHasANewerRevision()
        {
            ChunkData a = Chunk(new ChunkCoord(0, 0, 0), 4);
            var cache = new ChunkCache(_root, Plot);
            cache.Store(new[] { a });

            Assert.That(cache.TryLoad(a.Coord, a.Revision + 1, 123), Is.Null);
            Assert.That(cache.Dropped, Is.EqualTo(1));
            Assert.That(cache.Manifest(), Is.Empty, "never offered again");
        }

        [Test]
        public void SameRevisionDifferentContent_IsAMiss()
        {
            // After a world restore (SAVE-009) revisions can repeat with other content: the hash decides.
            ChunkData a = Chunk(new ChunkCoord(0, 0, 0), 4);
            var cache = new ChunkCache(_root, Plot);
            cache.Store(new[] { a });

            Assert.That(cache.TryLoad(a.Coord, a.Revision, ChunkHash.Compute(a) ^ 1), Is.Null);
        }

        [Test]
        public void GeneratorOrSchemaChange_InvalidatesTheWorldsOldCache()
        {
            new ChunkCache(_root, Plot).Store(new[] { Chunk(new ChunkCoord(0, 0, 0), 3) });
            var other = new ChunkCache(_root, new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, ChunkData.CurrentSchemaVersion, Plot.RegionMin, Plot.RegionMax));
            other.Store(new[] { Chunk(new ChunkCoord(0, 0, 0), 1) });

            var upgraded = new ChunkCache(_root, new WorldInfo(WorldId, GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version + 1, ChunkData.CurrentSchemaVersion, Plot.RegionMin, Plot.RegionMax));

            Assert.That(upgraded.Invalidated, Is.EqualTo(1));
            Assert.That(upgraded.Manifest(), Is.Empty);
            Assert.That(other.Manifest(), Has.Count.EqualTo(1), "other worlds untouched");
        }

        [Test]
        public void CorruptEntries_AreDeleted_NotTrusted()
        {
            ChunkData a = Chunk(new ChunkCoord(0, 0, 0), 4);
            var cache = new ChunkCache(_root, Plot);
            cache.Store(new[] { a });
            File.WriteAllBytes(Directory.GetFiles(cache.Directory, "*.chunk").Single(), new byte[] { 1, 2, 3 });

            Assert.That(cache.Manifest(), Is.Empty);
            Assert.That(Directory.GetFiles(cache.Directory, "*.chunk"), Is.Empty);
        }

        [Test]
        public void TamperedEntry_WithForgedHeader_FailsTheReplicaHashCheck()
        {
            ChunkData a = Chunk(new ChunkCoord(0, 0, 0), 4);
            var cache = new ChunkCache(_root, Plot);
            cache.Store(new[] { a });
            string file = Directory.GetFiles(cache.Directory, "*.chunk").Single();
            byte[] bytes = File.ReadAllBytes(file);
            bytes[bytes.Length - 1] ^= 0x07;   // last edit's block id, header (revision, hash) unchanged
            File.WriteAllBytes(file, bytes);

            ChunkSnapshotMessage? entry = cache.TryLoad(a.Coord, a.Revision, ChunkHash.Compute(a));
            var replica = new ClientWorldReplica(Plot);

            Assert.That(entry, Is.Not.Null);
            Assert.That(replica.ApplySnapshot(entry!), Is.False);
            Assert.That(replica.HashMismatches, Is.EqualTo(1));
            Assert.That(replica.DrainSnapshotRequests(new List<ChunkCoord>()), Is.EqualTo(1), "falls back to the server");
        }

        [Test]
        public void Budget_EvictsLeastRecentlyUsedOtherWorlds_NeverTheCurrentOne()
        {
            var old = new ChunkCache(_root, new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, ChunkData.CurrentSchemaVersion, Plot.RegionMin, Plot.RegionMax));
            old.Store(Enumerable.Range(0, 20).Select(i => Chunk(new ChunkCoord(i, 0, 0), 50)));
            Directory.SetLastWriteTimeUtc(old.Directory, DateTime.UtcNow.AddDays(-1));

            var current = new ChunkCache(_root, Plot, maxBytes: 2000);
            current.Store(Enumerable.Range(0, 5).Select(i => Chunk(new ChunkCoord(i, 0, 0), 50)));

            Assert.That(Directory.Exists(old.Directory), Is.False);
            Assert.That(current.Manifest(), Has.Count.EqualTo(5));
        }

        [Test]
        public void Wire_RoundTrips_AndRejectsMalformedLengths()
        {
            var entries = new List<CachedChunk> { new CachedChunk(new ChunkCoord(-5, 2, 9), 77, 0xDEADBEEFCAFEUL), new CachedChunk(new ChunkCoord(0, 0, 0), 0, 1) };
            byte[] bytes = CachedChunkWire.Encode(entries);

            Assert.That(CachedChunkWire.TryDecode(bytes, out List<CachedChunk> decoded), Is.True);
            Assert.That(decoded.Select(e => (e.Coord, e.Revision, e.ContentHash)), Is.EqualTo(entries.Select(e => (e.Coord, e.Revision, e.ContentHash))));
            Assert.That(CachedChunkWire.TryDecode(bytes.Take(bytes.Length - 1).ToArray(), out _), Is.False);
            Assert.That(CachedChunkWire.TryDecode(new byte[] { 0xFF, 0xFF }, out _), Is.False, "count above the cap");
            Assert.That(CachedChunkWire.TryDecode(null, out _), Is.False);
        }
    }
}
