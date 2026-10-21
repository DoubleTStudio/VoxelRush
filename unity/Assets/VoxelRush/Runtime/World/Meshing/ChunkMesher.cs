using System;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Meshing
{
    /// <summary>
    /// Greedy mesher (VOXEL_ENGINE Meshing): culls faces hidden by full neighbour faces (reading one block past each
    /// chunk border from the face neighbours; missing neighbours count as air) and merges coplanar full faces with the
    /// same atlas tile into maximal rectangles. Ramp slopes and partial faces follow per cell (<see cref="BlockGeometry"/>).
    /// Engine-free output; reusable buffers, no allocation per call after warm-up (tested).
    /// </summary>
    public sealed class ChunkMesher
    {
        private const int S = ChunkMath.Size;

        // Directions: +X, -X, +Y, -Y, +Z, -Z.
        private static readonly int[][] Axes = { new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, new[] { 1, 2, 0 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 0, 1 } };
        private static readonly int[] Signs = { 1, -1, 1, -1, 1, -1 };

        private readonly BlockAtlas _atlas;
        private readonly byte[] _padded = new byte[PaddedSize];
        private readonly int[] _mask = new int[S * S];
        private readonly byte[] _scratch = new byte[ChunkMath.Volume];

        public ChunkMesher(BlockAtlas atlas) => _atlas = atlas;

        /// <param name="neighbours">Face neighbours in order +X, -X, +Y, -Y, +Z, -Z; null entries are treated as air.</param>
        public void Build(ChunkData chunk, ChunkData?[] neighbours, ChunkMeshData output)
        {
            if (neighbours == null || neighbours.Length != 6)
            {
                throw new ArgumentException("Six face neighbours (+X, -X, +Y, -Y, +Z, -Z) are required.", nameof(neighbours));
            }

            output.Clear();
            FillPadded(chunk, neighbours);
            for (int direction = 0; direction < 6; direction++)
            {
                BuildDirection(direction, output);
            }

            BuildPartialFaces(output);
        }

        // Same cell order and culling as the Burst job: y, then z, then x.
        private void BuildPartialFaces(ChunkMeshData output)
        {
            for (int y = 0; y < S; y++)
            {
                for (int z = 0; z < S; z++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        byte block = Padded(x, y, z);
                        int end = BlockGeometry.TemplateEnd(block);
                        for (int t = BlockGeometry.TemplateStart(block); t < end; t++)
                        {
                            int dir = BlockGeometry.TemplateDirection(t);
                            if (dir != BlockGeometry.Inside)
                            {
                                byte neighbour = Padded(x + Step(dir, 0), y + Step(dir, 1), z + Step(dir, 2));
                                if (BlockGeometry.IsHidden(BlockGeometry.TemplateProfile(t), BlockGeometry.Profile(neighbour, dir ^ 1)))
                                {
                                    continue;
                                }
                            }

                            output.AddTemplate(t, x, y, z, _atlas.Tile(block, BlockGeometry.TemplateFace(t)));
                        }
                    }
                }
            }
        }

        // Unit step of a direction (+X, -X, +Y, -Y, +Z, -Z) along an axis.
        private static int Step(int direction, int axis) => direction / 2 == axis ? (direction % 2 == 0 ? 1 : -1) : 0;

        private void BuildDirection(int direction, ChunkMeshData output)
        {
            int d = Axes[direction][0];
            int u = Axes[direction][1];
            int v = Axes[direction][2];
            int sign = Signs[direction];
            BlockFace face = d == 1 ? (sign > 0 ? BlockFace.Top : BlockFace.Bottom) : BlockFace.Side;
            Span<int> p = stackalloc int[3];
            Span<int> q = stackalloc int[3];

            for (int slice = 0; slice < S; slice++)
            {
                // Mask of visible faces in this slice: tile + 1 (0 = no face).
                for (int j = 0; j < S; j++)
                {
                    for (int i = 0; i < S; i++)
                    {
                        p[d] = slice;
                        p[u] = i;
                        p[v] = j;
                        q[0] = p[0];
                        q[1] = p[1];
                        q[2] = p[2];
                        q[d] += sign;
                        byte block = Padded(p[0], p[1], p[2]);
                        bool visible = BlockGeometry.Profile(block, direction) == BlockGeometry.FullFace
                            && BlockGeometry.Profile(Padded(q[0], q[1], q[2]), direction ^ 1) != BlockGeometry.FullFace;
                        _mask[i + j * S] = visible ? _atlas.Tile(block, face) + 1 : 0;
                    }
                }

                // Greedy merge: extend each run along u, then along v while the whole row matches.
                for (int j = 0; j < S; j++)
                {
                    for (int i = 0; i < S;)
                    {
                        int tile = _mask[i + j * S];
                        if (tile == 0)
                        {
                            i++;
                            continue;
                        }

                        int width = 1;
                        while (i + width < S && _mask[i + width + j * S] == tile)
                        {
                            width++;
                        }

                        int height = 1;
                        while (j + height < S && RowMatches(i, j + height, width, tile))
                        {
                            height++;
                        }

                        output.AddQuad(direction, d, u, v, slice + (sign > 0 ? 1 : 0), i, j, width, height, tile - 1);
                        for (int dj = 0; dj < height; dj++)
                        {
                            for (int di = 0; di < width; di++)
                            {
                                _mask[i + di + (j + dj) * S] = 0;
                            }
                        }

                        i += width;
                    }
                }
            }
        }

        private bool RowMatches(int i, int j, int width, int tile)
        {
            for (int k = 0; k < width; k++)
            {
                if (_mask[i + k + j * S] != tile)
                {
                    return false;
                }
            }

            return true;
        }

        private byte Padded(int x, int y, int z) => _padded[(x + 1) + (S + 2) * ((z + 1) + (S + 2) * (y + 1))];

        private void FillPadded(ChunkData chunk, ChunkData?[] neighbours) => FillPadded(chunk, neighbours, _padded, _scratch);

        /// <summary>Padded block volume ((S+2)^3, x fastest, then z, then y) with face-neighbour borders; shared with the Burst mesher.</summary>
        public const int PaddedSize = (S + 2) * (S + 2) * (S + 2);

        /// <summary>
        /// Fills <paramref name="padded"/> with the chunk's blocks plus one border layer from each face neighbour
        /// (edges/corners stay air; missing neighbours are air). <paramref name="scratch"/> is a 4096-byte buffer.
        /// </summary>
        public static void FillPadded(ChunkData chunk, ChunkData?[] neighbours, byte[] padded, byte[] scratch)
        {
            Array.Clear(padded, 0, padded.Length);
            chunk.CopyEffectiveBlocks(scratch);
            const int P = S + 2;
            for (int y = 0; y < S; y++)
            {
                for (int z = 0; z < S; z++)
                {
                    // A chunk row (x = 0..15) is contiguous in both layouts.
                    Buffer.BlockCopy(scratch, S * (z + S * y), padded, 1 + P * ((z + 1) + P * (y + 1)), S);
                }
            }

            for (int n = 0; n < 6; n++)
            {
                ChunkData? source = neighbours[n];
                if (source == null)
                {
                    continue;
                }

                source.CopyEffectiveBlocks(scratch);
                for (int a = 0; a < S; a++)
                {
                    for (int b = 0; b < S; b++)
                    {
                        // n: +X, -X, +Y, -Y, +Z, -Z; read the neighbour's touching layer into our border layer.
                        (int px, int py, int pz, int lx, int ly, int lz) = n switch
                        {
                            0 => (S, a, b, 0, a, b),
                            1 => (-1, a, b, S - 1, a, b),
                            2 => (a, S, b, a, 0, b),
                            3 => (a, -1, b, a, S - 1, b),
                            4 => (a, b, S, a, b, 0),
                            _ => (a, b, -1, a, b, S - 1),
                        };
                        padded[(px + 1) + P * ((pz + 1) + P * (py + 1))] = scratch[lx + S * (lz + S * ly)];
                    }
                }
            }
        }
    }
}
