using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Streaming
{
    /// <summary>
    /// Budgeted chunk streaming (VOXEL_ENGINE chunk lifecycle, VR-023): plans columns around observers with priority
    /// (under → ahead → nearby), generates and builds colliders within per-tick budgets, unloads with hysteresis.
    /// A column is Ready only when every vertical layer has its collider: the readiness <see cref="TravelGuard"/> uses.
    /// </summary>
    public sealed class ChunkStreamer
    {
        private readonly IChunkSource _source;
        private readonly IChunkBackend _backend;
        private readonly Dictionary<ChunkCoord, Entry> _chunks = new Dictionary<ChunkCoord, Entry>();
        private readonly HashSet<(int X, int Z)> _desiredColumns = new HashSet<(int, int)>();
        private readonly List<(float Priority, int X, int Z)> _plan = new List<(float, int, int)>();
        private readonly List<ChunkCoord> _scratch = new List<ChunkCoord>();
        private readonly ChunkData?[] _neighbours = new ChunkData?[6];

        public ChunkStreamer(IChunkSource source, IChunkBackend backend, StreamingSettings settings)
        {
            _source = source;
            _backend = backend;
            Settings = settings;
            if (settings.MinChunkY > settings.MaxChunkY || settings.RadiusChunks < 0 || settings.GenerateBudget < 1 || settings.ColliderBudget < 1)
            {
                throw new ArgumentException("Invalid streaming settings.", nameof(settings));
            }
        }

        public StreamingSettings Settings { get; }

        public int Loaded => _chunks.Count;

        public long CollidersBuilt { get; private set; }

        public ChunkState StateOf(ChunkCoord coord) => _chunks.TryGetValue(coord, out Entry? entry) ? entry.State : ChunkState.Unloaded;

        public ChunkData? Get(ChunkCoord coord) => _chunks.TryGetValue(coord, out Entry? entry) ? entry.Data : null;

        /// <summary>Revision the current collider was built from (collider revision), or null without a collider.</summary>
        public ulong? ColliderRevision(ChunkCoord coord) => _chunks.TryGetValue(coord, out Entry? entry) && entry.State == ChunkState.Ready ? entry.ColliderRevision : (ulong?)null;

        public bool IsColumnReady(int chunkX, int chunkZ)
        {
            for (int y = Settings.MinChunkY; y <= Settings.MaxChunkY; y++)
            {
                if (StateOf(new ChunkCoord(chunkX, y, chunkZ)) != ChunkState.Ready)
                {
                    return false;
                }
            }

            return true;
        }

        public bool IsBlockColumnReady(float worldX, float worldZ) =>
            IsColumnReady(ChunkMath.FloorDiv((int)Math.Floor(worldX)), ChunkMath.FloorDiv((int)Math.Floor(worldZ)));

        /// <summary>One bounded streaming step: plan, generate, build colliders, unload.</summary>
        public void Step(IReadOnlyList<StreamingObserver> observers)
        {
            PlanColumns(observers);
            Generate();
            BuildColliders();
            Unload(observers);
        }

        /// <summary>Rebuilds colliders whose chunk changed since they were built (edits; VR-031). Bounded by the collider budget.</summary>
        public int RefreshStaleColliders()
        {
            int rebuilt = 0;
            _scratch.Clear();
            foreach (KeyValuePair<ChunkCoord, Entry> entry in _chunks)
            {
                if (entry.Value.State == ChunkState.Ready && entry.Value.ColliderRevision != entry.Value.Data.Revision)
                {
                    _scratch.Add(entry.Key);
                }
            }

            foreach (ChunkCoord coord in _scratch)
            {
                if (rebuilt >= Settings.ColliderBudget)
                {
                    break;
                }

                BuildCollider(coord, _chunks[coord]);
                rebuilt++;
            }

            return rebuilt;
        }

        private void PlanColumns(IReadOnlyList<StreamingObserver> observers)
        {
            _desiredColumns.Clear();
            _plan.Clear();
            int radius = Settings.RadiusChunks;
            foreach (StreamingObserver o in observers)
            {
                int cx = ChunkMath.FloorDiv((int)Math.Floor(o.X));
                int cz = ChunkMath.FloorDiv((int)Math.Floor(o.Z));
                float aheadX = o.X + o.VelocityX * Settings.LookaheadSeconds;
                float aheadZ = o.Z + o.VelocityZ * Settings.LookaheadSeconds;
                int ax = ChunkMath.FloorDiv((int)Math.Floor(aheadX));
                int az = ChunkMath.FloorDiv((int)Math.Floor(aheadZ));
                AddArea(cx, cz, radius, o, isAhead: false);
                if (ax != cx || az != cz)
                {
                    AddArea(ax, az, radius, o, isAhead: true);
                }
            }

            _plan.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        private void AddArea(int centerX, int centerZ, int radius, StreamingObserver o, bool isAhead)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int x = centerX + dx;
                    int z = centerZ + dz;
                    if (!_desiredColumns.Add((x, z)))
                    {
                        continue;
                    }

                    // Distance from the observer; columns along the velocity get a bonus so travel direction wins ties.
                    float px = (x + 0.5f) * ChunkMath.Size - o.X;
                    float pz = (z + 0.5f) * ChunkMath.Size - o.Z;
                    float distance = (float)Math.Sqrt(px * px + pz * pz);
                    float speed = (float)Math.Sqrt(o.VelocityX * o.VelocityX + o.VelocityZ * o.VelocityZ);
                    float forward = speed > 0.1f ? (px * o.VelocityX + pz * o.VelocityZ) / speed : 0f;
                    float priority = distance - Math.Max(0f, forward) * 0.5f + (isAhead ? 4f : 0f);
                    _plan.Add((priority, x, z));
                }
            }
        }

        private void Generate()
        {
            int budget = Settings.GenerateBudget;
            foreach ((float _, int x, int z) in _plan)
            {
                for (int y = Settings.MinChunkY; y <= Settings.MaxChunkY; y++)
                {
                    var coord = new ChunkCoord(x, y, z);
                    if (_chunks.ContainsKey(coord))
                    {
                        continue;
                    }

                    if (budget-- <= 0)
                    {
                        return;
                    }

                    _chunks[coord] = new Entry(_source.Load(coord));
                }
            }
        }

        private void BuildColliders()
        {
            int budget = Settings.ColliderBudget;
            foreach ((float _, int x, int z) in _plan)
            {
                for (int y = Settings.MinChunkY; y <= Settings.MaxChunkY; y++)
                {
                    var coord = new ChunkCoord(x, y, z);
                    if (!_chunks.TryGetValue(coord, out Entry? entry) || entry.State != ChunkState.Generated || !NeighboursGenerated(coord))
                    {
                        continue;
                    }

                    if (budget-- <= 0)
                    {
                        return;
                    }

                    BuildCollider(coord, entry);
                }
            }
        }

        // A collider built before a desired neighbour exists would get a wall face on the shared border.
        private bool NeighboursGenerated(ChunkCoord coord)
        {
            foreach (ChunkCoord n in FaceNeighbours(coord))
            {
                bool desired = _desiredColumns.Contains((n.X, n.Z)) && n.Y >= Settings.MinChunkY && n.Y <= Settings.MaxChunkY;
                if (desired && !_chunks.ContainsKey(n))
                {
                    return false;
                }
            }

            return true;
        }

        private void BuildCollider(ChunkCoord coord, Entry entry)
        {
            int i = 0;
            foreach (ChunkCoord n in FaceNeighbours(coord))
            {
                _neighbours[i++] = _chunks.TryGetValue(n, out Entry? ne) ? ne.Data : null;
            }

            _backend.BuildCollider(entry.Data, _neighbours);
            entry.State = ChunkState.Ready;
            entry.ColliderRevision = entry.Data.Revision;
            CollidersBuilt++;
        }

        private void Unload(IReadOnlyList<StreamingObserver> observers)
        {
            int keep = Settings.RadiusChunks + Settings.UnloadHysteresisChunks;
            _scratch.Clear();
            foreach (ChunkCoord coord in _chunks.Keys)
            {
                if (_desiredColumns.Contains((coord.X, coord.Z)))
                {
                    continue;
                }

                bool near = false;
                foreach (StreamingObserver o in observers)
                {
                    int cx = ChunkMath.FloorDiv((int)Math.Floor(o.X));
                    int cz = ChunkMath.FloorDiv((int)Math.Floor(o.Z));
                    if (Math.Abs(coord.X - cx) <= keep && Math.Abs(coord.Z - cz) <= keep)
                    {
                        near = true;
                        break;
                    }
                }

                if (!near)
                {
                    _scratch.Add(coord);
                    if (_scratch.Count >= Settings.UnloadBudget)
                    {
                        break;
                    }
                }
            }

            foreach (ChunkCoord coord in _scratch)
            {
                _chunks.Remove(coord);
                _backend.Remove(coord);
            }
        }

        private static IEnumerable<ChunkCoord> FaceNeighbours(ChunkCoord c)
        {
            yield return c.Offset(1, 0, 0);
            yield return c.Offset(-1, 0, 0);
            yield return c.Offset(0, 1, 0);
            yield return c.Offset(0, -1, 0);
            yield return c.Offset(0, 0, 1);
            yield return c.Offset(0, 0, -1);
        }

        private sealed class Entry
        {
            public Entry(ChunkData data) => Data = data;

            public ChunkData Data { get; }

            public ChunkState State { get; set; } = ChunkState.Generated;

            public ulong ColliderRevision { get; set; }
        }
    }
}
