using System;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;
using VoxelRush.World.Meshing;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>VR-070: optimizations must not change mesher output (hash pinned from the pre-optimization mesher).</summary>
    public sealed class MeshGoldenTests
    {
        public const ulong Expected = 0xCAD7D507342C2AE9UL;

        /// <summary>FNV-1a over every vertex/index of 75 terrain chunks (with neighbours and a few edits).</summary>
        public static ulong MeshTerrainHash()
        {
            var gen = new TerrainGenerator(12345);
            var mesher = new ChunkMesher(BlockAtlas.Default);
            var output = new ChunkMeshData();
            ulong hash = 14695981039346656037UL;
            void Mix(float f)
            {
                uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
                for (int k = 0; k < 4; k++)
                {
                    hash = (hash ^ ((bits >> (8 * k)) & 0xFF)) * 1099511628211UL;
                }
            }

            for (int x = -2; x <= 2; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -2; z <= 2; z++)
                    {
                        var c = new ChunkCoord(x, y, z);
                        ChunkData chunk = gen.Generate(c);
                        // Carve a few holes so edits and air pockets are covered.
                        for (int i = 0; i < 40; i++)
                        {
                            chunk.Set(LocalBlock.FromIndex((i * 997 + x * 31 + z * 17 + 4096 * 8) % 4096), i % 3 == 0 ? BlockIds.Stone : BlockIds.Air);
                        }

                        var n = new ChunkData?[]
                        {
                            gen.Generate(c.Offset(1, 0, 0)), gen.Generate(c.Offset(-1, 0, 0)), gen.Generate(c.Offset(0, 1, 0)),
                            null, gen.Generate(c.Offset(0, 0, 1)), gen.Generate(c.Offset(0, 0, -1)),
                        };
                        mesher.Build(chunk, n, output);
                        foreach (var list in new[] { output.Positions, output.Normals, output.Uvs, output.Tiles })
                        {
                            list.ForEach(Mix);
                        }

                        output.Indices.ForEach(i => Mix(i));
                    }
                }
            }

            return hash;
        }

        [Test]
        public void OptimizedMesher_MatchesPreOptimizationOutput()
        {
            Assert.That(MeshTerrainHash().ToString("X16"), Is.EqualTo(Expected.ToString("X16")));
        }
    }
}
