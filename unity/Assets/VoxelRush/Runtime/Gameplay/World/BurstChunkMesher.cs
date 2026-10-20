using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using VoxelRush.World.Chunks;
using VoxelRush.World.Meshing;

namespace VoxelRush.Gameplay.World
{
    /// <summary>
    /// Burst-compiled greedy mesher (VR-070): the same algorithm and output as <see cref="ChunkMesher"/> (pinned by
    /// tests), in native buffers. <see cref="Build"/> runs it on the calling thread (critical collider rebuilds before
    /// physics); <see cref="Schedule"/> runs it on a worker so background rebuilds cost the main thread only the fill.
    /// One instance owns one set of buffers: complete a scheduled build before starting another.
    /// </summary>
    public sealed class BurstChunkMesher : IDisposable
    {
        private readonly byte[] _padded = new byte[ChunkMesher.PaddedSize];
        private readonly byte[] _scratch = new byte[ChunkMath.Volume];
        private NativeArray<byte> _nativePadded;
        private NativeArray<ushort> _tiles;
        private NativeArray<byte> _opaque;
        private NativeArray<int> _mask;
        private JobHandle _pending;
        private bool _scheduled;

        public BurstChunkMesher(BlockAtlas atlas)
        {
            _nativePadded = new NativeArray<byte>(ChunkMesher.PaddedSize, Allocator.Persistent);
            _tiles = new NativeArray<ushort>(256 * 3, Allocator.Persistent);
            _opaque = new NativeArray<byte>(256, Allocator.Persistent);
            _mask = new NativeArray<int>(ChunkMath.Size * ChunkMath.Size, Allocator.Persistent);
            for (int id = 0; id < 256; id++)
            {
                _opaque[id] = atlas.IsOpaque((byte)id) ? (byte)1 : (byte)0;
                _tiles[id * 3] = (ushort)atlas.Tile((byte)id, BlockFace.Top);
                _tiles[id * 3 + 1] = (ushort)atlas.Tile((byte)id, BlockFace.Side);
                _tiles[id * 3 + 2] = (ushort)atlas.Tile((byte)id, BlockFace.Bottom);
            }

            Positions = new NativeList<float3>(1024, Allocator.Persistent);
            Normals = new NativeList<float3>(1024, Allocator.Persistent);
            Uvs = new NativeList<float2>(1024, Allocator.Persistent);
            TileIds = new NativeList<float>(1024, Allocator.Persistent);
            Indices = new NativeList<int>(1536, Allocator.Persistent);
        }

        public NativeList<float3> Positions { get; }

        public NativeList<float3> Normals { get; }

        public NativeList<float2> Uvs { get; }

        public NativeList<float> TileIds { get; }

        public NativeList<int> Indices { get; }

        public bool IsEmpty => Indices.Length == 0;

        /// <summary>Meshes on the calling thread (Burst-compiled).</summary>
        public void Build(ChunkData chunk, ChunkData?[] neighbours)
        {
            Schedule(chunk, neighbours);
            Complete();
        }

        /// <summary>Fills the padded volume on this thread and meshes on a worker; call <see cref="Complete"/> before reading.</summary>
        public JobHandle Schedule(ChunkData chunk, ChunkData?[] neighbours)
        {
            if (_scheduled)
            {
                throw new InvalidOperationException("Complete the previous build first.");
            }

            ChunkMesher.FillPadded(chunk, neighbours, _padded, _scratch);
            _nativePadded.CopyFrom(_padded);
            _pending = new GreedyMeshJob
            {
                Padded = _nativePadded,
                Tiles = _tiles,
                Opaque = _opaque,
                Mask = _mask,
                Positions = Positions,
                Normals = Normals,
                Uvs = Uvs,
                TileIds = TileIds,
                Indices = Indices,
            }.Schedule();
            _scheduled = true;
            return _pending;
        }

        public bool IsCompleted => !_scheduled || _pending.IsCompleted;

        public void Complete()
        {
            _pending.Complete();
            _scheduled = false;
        }

        public void Dispose()
        {
            Complete();
            _nativePadded.Dispose();
            _tiles.Dispose();
            _opaque.Dispose();
            _mask.Dispose();
            Positions.Dispose();
            Normals.Dispose();
            Uvs.Dispose();
            TileIds.Dispose();
            Indices.Dispose();
        }

        // Bounds checks are proven unnecessary by the equality tests; players compile them out anyway.
        [BurstCompile(CompileSynchronously = true, DisableSafetyChecks = true)]
        private struct GreedyMeshJob : IJob
        {
            private const int S = ChunkMath.Size;
            private const int P = S + 2;

            [ReadOnly] public NativeArray<byte> Padded;
            [ReadOnly] public NativeArray<ushort> Tiles;
            [ReadOnly] public NativeArray<byte> Opaque;
            public NativeArray<int> Mask;
            public NativeList<float3> Positions;
            public NativeList<float3> Normals;
            public NativeList<float2> Uvs;
            public NativeList<float> TileIds;
            public NativeList<int> Indices;

            public void Execute()
            {
                Positions.Clear();
                Normals.Clear();
                Uvs.Clear();
                TileIds.Clear();
                Indices.Clear();
                for (int direction = 0; direction < 6; direction++)
                {
                    BuildDirection(direction);
                }
            }

            // Directions: +X, -X, +Y, -Y, +Z, -Z, with (d, u, v) cyclic as in ChunkMesher.
            private void BuildDirection(int direction)
            {
                int d = direction / 2, u = (d + 1) % 3, v = (d + 2) % 3;
                int sign = direction % 2 == 0 ? 1 : -1;
                int face = d == 1 ? (sign > 0 ? 0 : 2) : 1;   // BlockFace Top/Side/Bottom
                // Padded index strides per axis (x fastest, then z, then y), so the inner loop is pure arithmetic.
                int sd = Stride(d), su = Stride(u), sv = Stride(v);
                int origin = 1 + P + P * P;
                for (int slice = 0; slice < S; slice++)
                {
                    for (int j = 0; j < S; j++)
                    {
                        int row = origin + slice * sd + j * sv;
                        for (int i = 0; i < S; i++)
                        {
                            int index = row + i * su;
                            byte block = Padded[index];
                            bool visible = Opaque[block] != 0 && Opaque[Padded[index + sign * sd]] == 0;
                            Mask[i + j * S] = visible ? Tiles[block * 3 + face] + 1 : 0;
                        }
                    }

                    for (int j = 0; j < S; j++)
                    {
                        for (int i = 0; i < S;)
                        {
                            int tile = Mask[i + j * S];
                            if (tile == 0)
                            {
                                i++;
                                continue;
                            }

                            int width = 1;
                            while (i + width < S && Mask[i + width + j * S] == tile)
                            {
                                width++;
                            }

                            int height = 1;
                            while (j + height < S && RowMatches(i, j + height, width, tile))
                            {
                                height++;
                            }

                            AddQuad(direction, d, u, v, slice + (sign > 0 ? 1 : 0), i, j, width, height, tile - 1);
                            for (int dj = 0; dj < height; dj++)
                            {
                                for (int di = 0; di < width; di++)
                                {
                                    Mask[i + di + (j + dj) * S] = 0;
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
                    if (Mask[i + k + j * S] != tile)
                    {
                        return false;
                    }
                }

                return true;
            }

            private void AddQuad(int direction, int d, int u, int v, int plane, int i, int j, int width, int height, int tile)
            {
                int start = Positions.Length;
                float3 normal = float3.zero;
                normal[d] = direction % 2 == 0 ? 1 : -1;
                for (int corner = 0; corner < 4; corner++)
                {
                    int du = corner == 1 || corner == 2 ? width : 0;
                    int dv = corner >= 2 ? height : 0;
                    float3 position = float3.zero;
                    position[d] = plane;
                    position[u] = i + du;
                    position[v] = j + dv;
                    Positions.Add(position);
                    Normals.Add(normal);
                    Uvs.Add(new float2(du, dv));
                    TileIds.Add(tile);
                }

                if (direction % 2 == 0)
                {
                    Indices.Add(start); Indices.Add(start + 1); Indices.Add(start + 2);
                    Indices.Add(start); Indices.Add(start + 2); Indices.Add(start + 3);
                }
                else
                {
                    Indices.Add(start); Indices.Add(start + 2); Indices.Add(start + 1);
                    Indices.Add(start); Indices.Add(start + 3); Indices.Add(start + 2);
                }
            }

            private static int Stride(int axis) => axis == 0 ? 1 : axis == 1 ? P * P : P;
        }
    }
}
