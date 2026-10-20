using System;
using System.Linq;
using NUnit.Framework;
using VoxelRush.World.Chunks;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>VR-020 DoD: chunk/world boundary math, including negative coordinates (CONTRACTS: floor semantics).</summary>
    public sealed class ChunkBoundaryTests
    {
        [TestCase(0, 0, 0)]
        [TestCase(15, 0, 15)]
        [TestCase(16, 1, 0)]
        [TestCase(17, 1, 1)]
        [TestCase(-1, -1, 15)]
        [TestCase(-15, -1, 1)]
        [TestCase(-16, -1, 0)]
        [TestCase(-17, -2, 15)]
        [TestCase(-32, -2, 0)]
        [TestCase(-33, -3, 15)]
        [TestCase(int.MaxValue, int.MaxValue >> 4, 15)]
        [TestCase(int.MinValue, int.MinValue >> 4, 0)]
        public void WorldAxis_FloorsIntoChunkAndLocal(int world, int expectedChunk, int expectedLocal)
        {
            Assert.That(ChunkMath.FloorDiv(world), Is.EqualTo(expectedChunk));
            Assert.That(ChunkMath.FloorMod(world), Is.EqualTo(expectedLocal));
        }

        [Test]
        public void ToChunkAndLocal_NegativeWorldBlock()
        {
            var world = new BlockPos(-1, -16, -17);

            Assert.That(ChunkMath.ToChunk(world), Is.EqualTo(new ChunkCoord(-1, -1, -2)));
            Assert.That(ChunkMath.ToLocal(world), Is.EqualTo(new LocalBlock(15, 0, 15)));
        }

        [Test]
        public void ToWorld_RoundTripsForRandomSignedCoordinates()
        {
            var random = new Random(20261021);
            int limit = ChunkMath.MaxChunkCoordinate * ChunkMath.Size;
            for (int i = 0; i < 20000; i++)
            {
                var world = new BlockPos(random.Next(-limit, limit), random.Next(-limit, limit), random.Next(-limit, limit));
                ChunkCoord chunk = ChunkMath.ToChunk(world);
                LocalBlock local = ChunkMath.ToLocal(world);

                Assert.That(ChunkMath.ToWorld(chunk, local), Is.EqualTo(world), $"round trip failed for {world}");
            }
        }

        [Test]
        public void AdjacentBlocksAcrossZero_LandInAdjacentChunks()
        {
            Assert.That(ChunkMath.ToChunk(new BlockPos(-1, 0, 0)).X + 1, Is.EqualTo(ChunkMath.ToChunk(new BlockPos(0, 0, 0)).X));
            Assert.That(ChunkMath.ToLocal(new BlockPos(-1, 0, 0)).X, Is.EqualTo(15));
            Assert.That(ChunkMath.ToLocal(new BlockPos(0, 0, 0)).X, Is.EqualTo(0));
        }

        [Test]
        public void ToWorld_RejectsChunksOutsideAddressableWorld()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMath.ToWorld(new ChunkCoord(ChunkMath.MaxChunkCoordinate + 1, 0, 0), new LocalBlock(0, 0, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => ChunkMath.ToWorld(new ChunkCoord(0, int.MinValue, 0), new LocalBlock(0, 0, 0)));
        }

        [Test]
        public void LocalIndex_UsesSpecLayout_AndRoundTripsAll4096()
        {
            Assert.That(new LocalBlock(1, 0, 0).Index, Is.EqualTo(1));
            Assert.That(new LocalBlock(0, 0, 1).Index, Is.EqualTo(16));
            Assert.That(new LocalBlock(0, 1, 0).Index, Is.EqualTo(256));
            Assert.That(new LocalBlock(15, 15, 15).Index, Is.EqualTo(4095));
            for (int i = 0; i < ChunkMath.Volume; i++)
            {
                Assert.That(LocalBlock.FromIndex(i).Index, Is.EqualTo(i));
            }
        }

        [TestCase(-1)]
        [TestCase(4096)]
        [TestCase(65535)]
        [TestCase(int.MinValue)]
        public void LocalIndex_UntrustedOutOfRange_IsRejected(int index)
        {
            Assert.That(LocalBlock.TryFromIndex(index, out _), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => LocalBlock.FromIndex(index));
        }

        [TestCase(16, 0, 0)]
        [TestCase(0, -1, 0)]
        [TestCase(0, 0, 255)]
        public void LocalBlock_RejectsOutOfRangeAxes(int x, int y, int z)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LocalBlock(x, y, z));
        }

        [Test]
        public void FaceNeighbours_InteriorEdgeAndCorner()
        {
            var chunk = new ChunkCoord(-1, 0, 2);
            Span<ChunkCoord> buffer = stackalloc ChunkCoord[3];

            Assert.That(ChunkMath.FaceNeighboursAffected(chunk, new LocalBlock(5, 5, 5), buffer), Is.Zero);

            Assert.That(ChunkMath.FaceNeighboursAffected(chunk, new LocalBlock(0, 5, 5), buffer), Is.EqualTo(1));
            Assert.That(buffer[0], Is.EqualTo(new ChunkCoord(-2, 0, 2)), "crossing into more-negative chunk");

            Assert.That(ChunkMath.FaceNeighboursAffected(chunk, new LocalBlock(15, 5, 0), buffer), Is.EqualTo(2));
            Assert.That(buffer.Slice(0, 2).ToArray(), Is.EquivalentTo(new[] { new ChunkCoord(0, 0, 2), new ChunkCoord(-1, 0, 1) }));

            Assert.That(ChunkMath.FaceNeighboursAffected(chunk, new LocalBlock(0, 15, 15), buffer), Is.EqualTo(3));
            Assert.That(buffer.ToArray(), Is.EquivalentTo(new[] { new ChunkCoord(-2, 0, 2), new ChunkCoord(-1, 1, 2), new ChunkCoord(-1, 0, 3) }));
        }

        [Test]
        public void Coordinates_EqualityAndHashing()
        {
            Assert.That(new ChunkCoord(-1, 2, -3), Is.EqualTo(new ChunkCoord(-1, 2, -3)));
            Assert.That(new ChunkCoord(-1, 2, -3).GetHashCode(), Is.EqualTo(new ChunkCoord(-1, 2, -3).GetHashCode()));
            Assert.That(new ChunkCoord(1, 0, 0), Is.Not.EqualTo(new ChunkCoord(0, 1, 0)));
            var set = new System.Collections.Generic.HashSet<ChunkCoord>(Enumerable.Range(-50, 100).Select(i => new ChunkCoord(i, -i, i * 7)));
            Assert.That(set.Count, Is.EqualTo(100));
        }
    }

    public sealed class ChunkDataTests
    {
        private static byte[] Base(byte fill = 3)
        {
            var blocks = new byte[ChunkMath.Volume];
            for (int i = 0; i < blocks.Length; i++)
            {
                blocks[i] = i < 256 ? fill : BlockIds.Air;
            }

            return blocks;
        }

        [Test]
        public void Get_ReturnsBaseUntilEdited()
        {
            var chunk = new ChunkData(new ChunkCoord(0, 0, 0), generatorVersion: 1, Base());

            Assert.That(chunk.Get(new LocalBlock(0, 0, 0)), Is.EqualTo(3));
            Assert.That(chunk.Get(new LocalBlock(0, 1, 0)), Is.EqualTo(BlockIds.Air));
            Assert.That(chunk.EditCount, Is.Zero);
            Assert.That(chunk.Revision, Is.Zero);
        }

        [Test]
        public void Set_RecordsSparseEditAndBumpsRevision()
        {
            var chunk = new ChunkData(new ChunkCoord(-1, 0, -1), 1, Base());
            var local = new LocalBlock(4, 0, 4);

            Assert.That(chunk.Set(local, BlockIds.Air), Is.True);
            Assert.That(chunk.Get(local), Is.EqualTo(BlockIds.Air));
            Assert.That(chunk.GetBase(local), Is.EqualTo(3), "base is immutable");
            Assert.That(chunk.EditCount, Is.EqualTo(1));
            Assert.That(chunk.Revision, Is.EqualTo(1));
        }

        [Test]
        public void Set_SameValue_IsNoOpWithoutRevision()
        {
            var chunk = new ChunkData(new ChunkCoord(0, 0, 0), 1, Base());

            Assert.That(chunk.Set(new LocalBlock(0, 0, 0), 3), Is.False);
            Assert.That(chunk.Revision, Is.Zero);
        }

        [Test]
        public void Set_BackToBase_DropsEditButKeepsRevisionHistory()
        {
            var chunk = new ChunkData(new ChunkCoord(0, 0, 0), 1, Base());
            var local = new LocalBlock(1, 0, 1);
            chunk.Set(local, 9);
            chunk.Set(local, 3);

            Assert.That(chunk.EditCount, Is.Zero);
            Assert.That(chunk.Revision, Is.EqualTo(2), "both changes are ordered revisions");
        }

        [Test]
        public void Edits_AreEnumeratedInIndexOrder()
        {
            var chunk = new ChunkData(new ChunkCoord(0, 0, 0), 1, Base());
            chunk.Set(LocalBlock.FromIndex(4000), 7);
            chunk.Set(LocalBlock.FromIndex(5), 7);
            chunk.Set(LocalBlock.FromIndex(300), 7);

            Assert.That(chunk.Edits.Select(e => (int)e.Key), Is.EqualTo(new[] { 5, 300, 4000 }));
        }

        [Test]
        public void Constructor_CopiesBase_SoCallerMutationCannotLeakIn()
        {
            byte[] blocks = Base();
            var chunk = new ChunkData(new ChunkCoord(0, 0, 0), 1, blocks);
            blocks[0] = 99;

            Assert.That(chunk.Get(LocalBlock.FromIndex(0)), Is.EqualTo(3));
        }

        [Test]
        public void Constructor_ValidatesInputs()
        {
            Assert.Throws<ArgumentException>(() => new ChunkData(new ChunkCoord(0, 0, 0), 1, new byte[100]));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkData(new ChunkCoord(0, 0, 0), 0, Base()));
            Assert.Throws<NotSupportedException>(() => new ChunkData(new ChunkCoord(0, 0, 0), 1, Base(), schemaVersion: 2));
        }

        [Test]
        public void Memory_BaseIsExactly4096Bytes()
        {
            // VOXEL_ENGINE memory budget: 4,096 bytes of raw block ids per chunk.
            Assert.That(ChunkMath.Volume, Is.EqualTo(4096));
        }
    }
}
