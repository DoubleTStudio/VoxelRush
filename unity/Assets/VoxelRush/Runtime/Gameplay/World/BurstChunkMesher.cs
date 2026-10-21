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
        private NativeArray<byte> _profiles;
        private NativeArray<int> _starts;
        private NativeArray<int> _directions;
        private NativeArray<byte> _templateProfiles;
        private NativeArray<byte> _faces;
        private NativeArray<byte> _vertexCounts;
        private NativeArray<float3> _templatePositions;
        private NativeArray<float3> _templateNormals;
        private NativeArray<float2> _templateUvs;
        private NativeArray<int> _mask;
        private JobHandle _pending;
        private bool _scheduled;

        public BurstChunkMesher(BlockAtlas atlas)
        {
            _nativePadded = new NativeArray<byte>(ChunkMesher.PaddedSize, Allocator.Persistent);
            _tiles = new NativeArray<ushort>(256 * 3, Allocator.Persistent);
            _mask = new NativeArray<int>(ChunkMath.Size * ChunkMath.Size, Allocator.Persistent);
            CopyGeometry();
            for (int id = 0; id < 256; id++)
            {
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

        // Same tables as the managed mesher (BlockGeometry), so ramps mesh identically.
        private void CopyGeometry()
        {
            int count = BlockGeometry.TemplateCount;
            var profiles = new byte[256 * 6];
            var starts = new int[257];
            var directions = new int[count];
            var templateProfiles = new byte[count];
            var faces = new byte[count];
            var vertexCounts = new byte[count];
            var positions = new float[count * 12];
            var normals = new float[count * 3];
            var uvs = new float[count * 8];
            BlockGeometry.CopyTables(profiles, starts, directions, templateProfiles, faces, vertexCounts, positions, normals, uvs);
            _profiles = new NativeArray<byte>(profiles, Allocator.Persistent);
            _starts = new NativeArray<int>(starts, Allocator.Persistent);
            _directions = new NativeArray<int>(directions, Allocator.Persistent);
            _templateProfiles = new NativeArray<byte>(templateProfiles, Allocator.Persistent);
            _faces = new NativeArray<byte>(faces, Allocator.Persistent);
            _vertexCounts = new NativeArray<byte>(vertexCounts, Allocator.Persistent);
            _templatePositions = new NativeArray<float3>(count * 4, Allocator.Persistent);
            _templateUvs = new NativeArray<float2>(count * 4, Allocator.Persistent);
            _templateNormals = new NativeArray<float3>(count, Allocator.Persistent);
            for (int i = 0; i < count * 4; i++)
            {
                _templatePositions[i] = new float3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
                _templateUvs[i] = new float2(uvs[i * 2], uvs[i * 2 + 1]);
            }

            for (int i = 0; i < count; i++)
            {
                _templateNormals[i] = new float3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
            }
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
                Profiles = _profiles,
                Starts = _starts,
                Directions = _directions,
                TemplateProfiles = _templateProfiles,
                Faces = _faces,
                VertexCounts = _vertexCounts,
                TemplatePositions = _templatePositions,
                TemplateNormals = _templateNormals,
                TemplateUvs = _templateUvs,
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
            _profiles.Dispose();
            _starts.Dispose();
            _directions.Dispose();
            _templateProfiles.Dispose();
            _faces.Dispose();
            _vertexCounts.Dispose();
            _templatePositions.Dispose();
            _templateNormals.Dispose();
            _templateUvs.Dispose();
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
            [ReadOnly] public NativeArray<byte> Profiles;
            [ReadOnly] public NativeArray<int> Starts;
            [ReadOnly] public NativeArray<int> Directions;
            [ReadOnly] public NativeArray<byte> TemplateProfiles;
            [ReadOnly] public NativeArray<byte> Faces;
            [ReadOnly] public NativeArray<byte> VertexCounts;
            [ReadOnly] public NativeArray<float3> TemplatePositions;
            [ReadOnly] public NativeArray<float3> TemplateNormals;
            [ReadOnly] public NativeArray<float2> TemplateUvs;
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

                BuildPartialFaces();
            }

            // Ramp slopes and partial faces, cell order y, z, x as in ChunkMesher.
            private void BuildPartialFaces()
            {
                for (int y = 0; y < S; y++)
                {
                    for (int z = 0; z < S; z++)
                    {
                        for (int x = 0; x < S; x++)
                        {
                            int index = (x + 1) + P * ((z + 1) + P * (y + 1));
                            byte block = Padded[index];
                            int end = Starts[block + 1];
                            for (int t = Starts[block]; t < end; t++)
                            {
                                int dir = Directions[t];
                                if (dir != BlockGeometry.Inside)
                                {
                                    byte neighbour = Padded[index + (dir % 2 == 0 ? 1 : -1) * Stride(dir / 2)];
                                    byte profile = TemplateProfiles[t];
                                    byte other = Profiles[neighbour * 6 + (dir ^ 1)];
                                    if (other == BlockGeometry.FullFace || (profile > BlockGeometry.FullFace && profile == other))
                                    {
                                        continue;
                                    }
                                }

                                AddTemplate(t, x, y, z, Tiles[block * 3 + Faces[t]]);
                            }
                        }
                    }
                }
            }

            private void AddTemplate(int t, int x, int y, int z, int tile)
            {
                int start = Positions.Length;
                int count = VertexCounts[t];
                var cell = new float3(x, y, z);
                for (int corner = 0; corner < count; corner++)
                {
                    Positions.Add(cell + TemplatePositions[t * 4 + corner]);
                    Normals.Add(TemplateNormals[t]);
                    Uvs.Add(TemplateUvs[t * 4 + corner]);
                    TileIds.Add(tile);
                }

                Indices.Add(start); Indices.Add(start + 1); Indices.Add(start + 2);
                if (count == 4)
                {
                    Indices.Add(start); Indices.Add(start + 2); Indices.Add(start + 3);
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
                            bool visible = Profiles[block * 6 + direction] == BlockGeometry.FullFace
                                && Profiles[Padded[index + sign * sd] * 6 + (direction ^ 1)] != BlockGeometry.FullFace;
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
