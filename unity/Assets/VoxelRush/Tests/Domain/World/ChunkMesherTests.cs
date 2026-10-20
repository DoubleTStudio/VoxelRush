using System;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;
using VoxelRush.World.Meshing;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>VR-022: greedy face counts, culling across chunk borders, atlas tiles and winding (VOXEL_ENGINE tests).</summary>
    public sealed class ChunkMesherTests
    {
        private static readonly ChunkData?[] NoNeighbours = new ChunkData?[6];
        private readonly ChunkMesher _mesher = new ChunkMesher(BlockAtlas.Default);
        private readonly ChunkMeshData _mesh = new ChunkMeshData();

        private static ChunkData Chunk(Func<int, int, int, byte> fill, ChunkCoord? coord = null)
        {
            var blocks = new byte[ChunkMath.Volume];
            for (int i = 0; i < blocks.Length; i++)
            {
                LocalBlock l = LocalBlock.FromIndex(i);
                blocks[i] = fill(l.X, l.Y, l.Z);
            }

            return new ChunkData(coord ?? new ChunkCoord(0, 0, 0), 1, blocks);
        }

        [Test]
        public void Empty_ProducesNoGeometry()
        {
            _mesher.Build(Chunk((x, y, z) => BlockIds.Air), NoNeighbours, _mesh);

            Assert.That(_mesh.IsEmpty, Is.True);
        }

        [Test]
        public void SingleBlock_HasSixQuads()
        {
            _mesher.Build(Chunk((x, y, z) => x == 5 && y == 5 && z == 5 ? BlockIds.Stone : BlockIds.Air), NoNeighbours, _mesh);

            Assert.That(_mesh.QuadCount, Is.EqualTo(6));
            Assert.That(_mesh.VertexCount, Is.EqualTo(24));
        }

        [Test]
        public void FlatLayer_MergesIntoSixQuads()
        {
            // 16×16×1 stone slab: 256 top faces → 1 quad, likewise bottom and each side strip.
            _mesher.Build(Chunk((x, y, z) => y == 0 ? BlockIds.Stone : BlockIds.Air), NoNeighbours, _mesh);

            Assert.That(_mesh.QuadCount, Is.EqualTo(6));
        }

        [Test]
        public void SolidChunk_WithoutNeighbours_IsSixFullSides()
        {
            _mesher.Build(Chunk((x, y, z) => BlockIds.Stone), NoNeighbours, _mesh);

            Assert.That(_mesh.QuadCount, Is.EqualTo(6));
        }

        [Test]
        public void SolidChunk_SurroundedBySolidNeighbours_HasNoFaces()
        {
            ChunkData solid = Chunk((x, y, z) => BlockIds.Stone);
            var neighbours = new ChunkData?[] { solid, solid, solid, solid, solid, solid };

            _mesher.Build(solid, neighbours, _mesh);

            Assert.That(_mesh.IsEmpty, Is.True, "faces against opaque neighbour chunks are culled");
        }

        [Test]
        public void BorderFace_CulledOnlyByTheMatchingNeighbour()
        {
            ChunkData block = Chunk((x, y, z) => x == 0 && y == 3 && z == 3 ? BlockIds.Stone : BlockIds.Air);
            ChunkData wall = Chunk((x, y, z) => x == ChunkMath.Size - 1 ? BlockIds.Stone : BlockIds.Air, new ChunkCoord(-1, 0, 0));

            _mesher.Build(block, new ChunkData?[] { null, wall, null, null, null, null }, _mesh);

            Assert.That(_mesh.QuadCount, Is.EqualTo(5), "the -X face touches the neighbour's +X wall");
        }

        [Test]
        public void DifferentTiles_DoNotMerge()
        {
            // Two-layer column: grass on top of dirt. Sides differ (grass side vs dirt), so 2 side quads per side.
            _mesher.Build(Chunk((x, y, z) => y == 0 ? BlockIds.Dirt : y == 1 ? BlockIds.Grass : BlockIds.Air), NoNeighbours, _mesh);

            Assert.That(_mesh.QuadCount, Is.EqualTo(2 + 4 * 2), "top + bottom + two strips per side");
        }

        [Test]
        public void Checkerboard_IsWorstCase_NoMerging()
        {
            _mesher.Build(Chunk((x, y, z) => (x + y + z) % 2 == 0 ? BlockIds.Stone : BlockIds.Air), NoNeighbours, _mesh);

            Assert.That(_mesh.QuadCount, Is.EqualTo(ChunkMath.Volume / 2 * 6));
        }

        [Test]
        public void AtlasTiles_FollowFaceDirection()
        {
            _mesher.Build(Chunk((x, y, z) => x == 1 && y == 1 && z == 1 ? BlockIds.Grass : BlockIds.Air), NoNeighbours, _mesh);

            for (int q = 0; q < _mesh.QuadCount; q++)
            {
                float ny = _mesh.Normals[q * 12 + 1];
                float tile = _mesh.Tiles[q * 4];
                float expected = ny > 0 ? BlockAtlas.Default.Tile(BlockIds.Grass, BlockFace.Top)
                    : ny < 0 ? BlockAtlas.Default.Tile(BlockIds.Grass, BlockFace.Bottom)
                    : BlockAtlas.Default.Tile(BlockIds.Grass, BlockFace.Side);
                Assert.That(tile, Is.EqualTo(expected));
            }
        }

        [Test]
        public void EveryTriangle_FacesAlongItsNormal()
        {
            _mesher.Build(new TerrainGenerator(12345).Generate(new ChunkCoord(0, 0, 0)), NoNeighbours, _mesh);
            Assert.That(_mesh.QuadCount, Is.GreaterThan(0));

            for (int t = 0; t < _mesh.Indices.Count; t += 3)
            {
                float[] a = Vertex(_mesh.Indices[t]), b = Vertex(_mesh.Indices[t + 1]), c = Vertex(_mesh.Indices[t + 2]);
                float[] e1 = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
                float[] e2 = { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
                float[] cross = { e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0] };
                int n = _mesh.Indices[t] * 3;
                float dot = cross[0] * _mesh.Normals[n] + cross[1] * _mesh.Normals[n + 1] + cross[2] * _mesh.Normals[n + 2];
                Assert.That(dot, Is.GreaterThan(0f), $"triangle {t / 3} winds away from its normal");
            }
        }

        [Test]
        public void GeneratedTerrain_GreedyBeatsNaiveFaceCount()
        {
            ChunkData terrain = new TerrainGenerator(12345).Generate(new ChunkCoord(0, 0, 0));
            int naive = 0;
            for (int i = 0; i < ChunkMath.Volume; i++)
            {
                LocalBlock l = LocalBlock.FromIndex(i);
                if (terrain.Get(l) == BlockIds.Air)
                {
                    continue;
                }

                foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                {
                    int x = l.X + dx, y = l.Y + dy, z = l.Z + dz;
                    bool outside = x < 0 || y < 0 || z < 0 || x >= ChunkMath.Size || y >= ChunkMath.Size || z >= ChunkMath.Size;
                    if (outside || terrain.Get(new LocalBlock(x, y, z)) == BlockIds.Air)
                    {
                        naive++;
                    }
                }
            }

            _mesher.Build(terrain, NoNeighbours, _mesh);

            Assert.That(_mesh.QuadCount, Is.LessThan(naive / 3), $"greedy {_mesh.QuadCount} vs culled-only {naive}");
        }

        [Test]
        public void Build_RequiresSixNeighbourSlots()
        {
            Assert.Throws<ArgumentException>(() => _mesher.Build(Chunk((x, y, z) => BlockIds.Air), new ChunkData?[4], _mesh));
        }

        private float[] Vertex(int index) => new[] { _mesh.Positions[index * 3], _mesh.Positions[index * 3 + 1], _mesh.Positions[index * 3 + 2] };
    }
}
