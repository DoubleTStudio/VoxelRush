using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>
    /// VR-021 DoD: reproducible generation. Golden hashes were produced once by generator v1 and must match on every
    /// platform that runs these tests (macOS arm64 .NET + Unity Mono locally, Linux x86-64 in CI).
    /// A deliberate generator change must bump TerrainGenerator.Version and regenerate these values.
    /// </summary>
    public sealed class TerrainGeneratorTests
    {
        private const long Seed = 12345;

        [TestCase(0, 0, 0, 0xE78FA815ACE698E4UL)]
        [TestCase(0, 1, 0, 0xFE4D9DFC611A5E84UL)]
        [TestCase(-1, 0, -1, 0x0CAD1B64741FADE5UL)]
        [TestCase(3, 1, -7, 0xECF1376E6484ABABUL)]
        [TestCase(-20, -1, 15, 0xA8C137977129AAE1UL)]
        [TestCase(1000, 2, -1000, 0x454F9936A6495100UL)]
        public void GoldenHash_MatchesPinnedV1Output(int x, int y, int z, ulong expected)
        {
            ChunkData chunk = new TerrainGenerator(Seed).Generate(new ChunkCoord(x, y, z));

            Assert.That(ChunkHash.Compute(chunk), Is.EqualTo(expected), $"generator v{TerrainGenerator.Version} output changed for chunk ({x},{y},{z})");
        }

        [Test]
        public void Version_IsPinned()
        {
            Assert.That(TerrainGenerator.Version, Is.EqualTo(1), "bump only with regenerated golden hashes and a migration note");
        }

        [Test]
        public void SameSeed_IsReproducible_AcrossInstances()
        {
            var a = new TerrainGenerator(Seed);
            var b = new TerrainGenerator(Seed);
            for (int x = -3; x <= 3; x++)
            {
                for (int z = -3; z <= 3; z++)
                {
                    var coord = new ChunkCoord(x, 0, z);
                    Assert.That(a.GenerateBlocks(coord), Is.EqualTo(b.GenerateBlocks(coord)), $"{coord}");
                }
            }
        }

        [Test]
        public void DifferentSeed_ProducesDifferentTerrain()
        {
            Assert.That(ChunkHash.Compute(new TerrainGenerator(54321).Generate(new ChunkCoord(0, 0, 0))), Is.EqualTo(0x51EF468EE8E378A5UL));
            Assert.That(new TerrainGenerator(54321).GenerateBlocks(new ChunkCoord(0, 0, 0)), Is.Not.EqualTo(new TerrainGenerator(Seed).GenerateBlocks(new ChunkCoord(0, 0, 0))));
        }

        [Test]
        public void Columns_AreLayeredGrassDirtStone_AirAbove()
        {
            var generator = new TerrainGenerator(Seed);
            foreach (var (x, z) in new[] { (0, 0), (-1, -1), (100, -37), (-250, 999) })
            {
                int surface = generator.SurfaceHeight(x, z);
                Assert.That(BlockAt(generator, x, surface + 1, z), Is.EqualTo(BlockIds.Air));
                Assert.That(BlockAt(generator, x, surface, z), Is.EqualTo(BlockIds.Grass));
                Assert.That(BlockAt(generator, x, surface - 1, z), Is.EqualTo(BlockIds.Dirt));
                Assert.That(BlockAt(generator, x, surface - 2, z), Is.EqualTo(BlockIds.Dirt));
                Assert.That(BlockAt(generator, x, surface - 3, z), Is.EqualTo(BlockIds.Stone));
            }
        }

        [Test]
        public void Terrain_IsContinuousAcrossChunkBorders_IncludingNegative()
        {
            var generator = new TerrainGenerator(Seed);
            // A world column's blocks don't depend on which chunk generated them.
            foreach (int x in new[] { -17, -16, -1, 0, 15, 16 })
            {
                int surface = generator.SurfaceHeight(x, -1);
                Assert.That(BlockAt(generator, x, surface, -1), Is.EqualTo(BlockIds.Grass), $"x={x}");
            }

            // Neighbouring columns across the 0/-1 border differ by at most a couple of blocks (hills, not cliffs).
            for (int z = -40; z < 40; z++)
            {
                Assert.That(System.Math.Abs(generator.SurfaceHeight(-1, z) - generator.SurfaceHeight(0, z)), Is.LessThanOrEqualTo(2), $"z={z}");
            }
        }

        [Test]
        public void SurfaceHeights_StayWithinExpectedBand()
        {
            var generator = new TerrainGenerator(Seed);
            for (int x = -512; x < 512; x += 7)
            {
                for (int z = -512; z < 512; z += 7)
                {
                    Assert.That(generator.SurfaceHeight(x, z), Is.InRange(-8, 40));
                }
            }
        }

        [Test]
        public void ChunkHash_ChangesWithEdits_AndCoordinate()
        {
            var generator = new TerrainGenerator(Seed);
            ChunkData chunk = generator.Generate(new ChunkCoord(0, 0, 0));
            ulong original = ChunkHash.Compute(chunk);

            chunk.Set(new LocalBlock(0, 0, 0), BlockIds.Air);
            Assert.That(ChunkHash.Compute(chunk), Is.Not.EqualTo(original));

            chunk.Set(new LocalBlock(0, 0, 0), chunk.GetBase(new LocalBlock(0, 0, 0)));
            Assert.That(ChunkHash.Compute(chunk), Is.EqualTo(original), "hash depends on content, not edit history");

            var sameBlocks = new byte[ChunkMath.Volume];
            Assert.That(ChunkHash.Compute(1, 1, new ChunkCoord(0, 0, 0), sameBlocks), Is.Not.EqualTo(ChunkHash.Compute(1, 1, new ChunkCoord(-1, 0, 0), sameBlocks)));
        }

        private static byte BlockAt(TerrainGenerator generator, int x, int y, int z)
        {
            var world = new BlockPos(x, y, z);
            ChunkData chunk = generator.Generate(ChunkMath.ToChunk(world));
            return chunk.Get(ChunkMath.ToLocal(world));
        }
    }
}
