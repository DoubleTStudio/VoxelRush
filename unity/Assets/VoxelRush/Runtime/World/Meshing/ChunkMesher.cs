using System;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Meshing
{
    /// <summary>
    /// Greedy mesher (VOXEL_ENGINE Meshing): culls faces hidden by opaque neighbours (reading one block past each
    /// chunk border from the face neighbours; missing neighbours count as air) and merges coplanar faces with the same
    /// atlas tile into maximal rectangles. Engine-free output; reusable buffers, no allocation per call after warm-up.
    /// </summary>
    public sealed class ChunkMesher
    {
        private const int S = ChunkMath.Size;

        // Directions: +X, -X, +Y, -Y, +Z, -Z.
        private static readonly int[][] Axes = { new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, new[] { 1, 2, 0 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 0, 1 } };
        private static readonly int[] Signs = { 1, -1, 1, -1, 1, -1 };

        private readonly BlockAtlas _atlas;
        private readonly byte[] _padded = new byte[(S + 2) * (S + 2) * (S + 2)];
        private readonly int[] _mask = new int[S * S];

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
        }

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
                        bool visible = _atlas.IsOpaque(block) && !_atlas.IsOpaque(Padded(q[0], q[1], q[2]));
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

        private void FillPadded(ChunkData chunk, ChunkData?[] neighbours)
        {
            Array.Clear(_padded, 0, _padded.Length);
            for (int y = -1; y <= S; y++)
            {
                for (int z = -1; z <= S; z++)
                {
                    for (int x = -1; x <= S; x++)
                    {
                        int outside = (x < 0 || x >= S ? 1 : 0) + (y < 0 || y >= S ? 1 : 0) + (z < 0 || z >= S ? 1 : 0);
                        // Only face neighbours matter for face culling; edges/corners stay air.
                        if (outside > 1)
                        {
                            continue;
                        }

                        ChunkData? source = chunk;
                        int lx = x, ly = y, lz = z;
                        if (x < 0) { source = neighbours[1]; lx = S - 1; }
                        else if (x >= S) { source = neighbours[0]; lx = 0; }
                        else if (y < 0) { source = neighbours[3]; ly = S - 1; }
                        else if (y >= S) { source = neighbours[2]; ly = 0; }
                        else if (z < 0) { source = neighbours[5]; lz = S - 1; }
                        else if (z >= S) { source = neighbours[4]; lz = 0; }

                        if (source != null)
                        {
                            _padded[(x + 1) + (S + 2) * ((z + 1) + (S + 2) * (y + 1))] = source.Get(new LocalBlock(lx, ly, lz));
                        }
                    }
                }
            }
        }
    }
}
