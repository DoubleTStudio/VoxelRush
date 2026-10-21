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
        private readonly HashSet<ChunkCoord> _stale = new HashSet<ChunkCoord>();
        private readonly List<(float Distance, ChunkCoord Coord)> _rebuild = new List<(float, ChunkCoord)>();

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

        /// <summary>Car-adjacent rebuilds done before physics (DES-012).</summary>
        public long CriticalRebuilds { get; private set; }

        /// <summary>Car-adjacent stale chunks left for the next step because the critical budget ran out.</summary>
        public long CriticalDeferred { get; private set; }

        public int StaleCount => _stale.Count;

        public ChunkState StateOf(ChunkCoord coord) => _chunks.TryGetValue(coord, out Entry? entry) ? entry.State : ChunkState.Unloaded;

        public ChunkData? Get(ChunkCoord coord) => _chunks.TryGetValue(coord, out Entry? entry) ? entry.Data : null;

        /// <summary>Revision the current collider was built from (collider revision), or null without a collider.</summary>
        public ulong? ColliderRevision(ChunkCoord coord) => _chunks.TryGetValue(coord, out Entry? entry) && entry.State == ChunkState.Ready ? entry.ColliderRevision : (ulong?)null;

        /// <summary>Ready, and built from the chunk's current blocks and its neighbours' current border blocks.</summary>
        public bool IsColliderCurrent(ChunkCoord coord) =>
            _chunks.TryGetValue(coord, out Entry? entry) && entry.State == ChunkState.Ready && !IsStale(coord, entry);

        /// <summary>
        /// Marks the collider of the chunk holding <paramref name="local"/> stale, plus the neighbour across a border
        /// the block touches (its border faces depend on this block).
        /// </summary>
        public void MarkChanged(ChunkCoord chunk, LocalBlock local)
        {
            MarkStale(chunk);
            int last = ChunkMath.Size - 1;
            if (local.X == 0)
            {
                MarkStale(chunk.Offset(-1, 0, 0));
            }

            if (local.X == last)
            {
                MarkStale(chunk.Offset(1, 0, 0));
            }

            if (local.Y == 0)
            {
                MarkStale(chunk.Offset(0, -1, 0));
            }

            if (local.Y == last)
            {
                MarkStale(chunk.Offset(0, 1, 0));
            }

            if (local.Z == 0)
            {
                MarkStale(chunk.Offset(0, 0, -1));
            }

            if (local.Z == last)
            {
                MarkStale(chunk.Offset(0, 0, 1));
            }
        }

        /// <summary>
        /// Rebuilds stale colliders near cars before physics runs: within <see cref="StreamingSettings.CriticalDistance"/>
        /// of each car and of where it will be after <see cref="StreamingSettings.CriticalLookaheadSeconds"/>, nearest first,
        /// up to the critical budget. Cheap when nothing changed (only nearby chunks are inspected).
        /// </summary>
        public int RefreshCritical(IReadOnlyList<StreamingObserver> observers)
        {
            _rebuild.Clear();
            float reach = Settings.CriticalDistance;
            foreach (StreamingObserver o in observers)
            {
                float ax = o.X + o.VelocityX * Settings.CriticalLookaheadSeconds;
                float az = o.Z + o.VelocityZ * Settings.CriticalLookaheadSeconds;
                int x0 = ChunkMath.FloorDiv((int)Math.Floor(Math.Min(o.X, ax) - reach)), x1 = ChunkMath.FloorDiv((int)Math.Floor(Math.Max(o.X, ax) + reach));
                int z0 = ChunkMath.FloorDiv((int)Math.Floor(Math.Min(o.Z, az) - reach)), z1 = ChunkMath.FloorDiv((int)Math.Floor(Math.Max(o.Z, az) + reach));
                int y0 = Math.Max(Settings.MinChunkY, ChunkMath.FloorDiv((int)Math.Floor(o.Y - reach)));
                int y1 = Math.Min(Settings.MaxChunkY, ChunkMath.FloorDiv((int)Math.Floor(o.Y + reach)));
                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int z = z0; z <= z1; z++)
                        {
                            var coord = new ChunkCoord(x, y, z);
                            if (!_chunks.TryGetValue(coord, out Entry? entry) || entry.State != ChunkState.Ready || !IsStale(coord, entry))
                            {
                                continue;
                            }

                            float d = Math.Min(DistanceToChunk(coord, o.X, o.Y, o.Z), DistanceToChunk(coord, ax, o.Y, az));
                            if (d <= reach && !Contains(_rebuild, coord))
                            {
                                _rebuild.Add((d, coord));
                            }
                        }
                    }
                }
            }

            _rebuild.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            int rebuilt = 0;
            foreach ((float _, ChunkCoord coord) in _rebuild)
            {
                if (rebuilt >= Settings.CriticalColliderBudget)
                {
                    CriticalDeferred += _rebuild.Count - rebuilt;
                    break;
                }

                BuildCollider(coord, _chunks[coord]);
                rebuilt++;
            }

            CriticalRebuilds += rebuilt;
            return rebuilt;
        }

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
        public int RefreshStaleColliders() => RefreshStaleColliders(Array.Empty<StreamingObserver>());

        /// <summary>Background stale rebuilds, nearest to any car first, bounded by the collider budget.</summary>
        public int RefreshStaleColliders(IReadOnlyList<StreamingObserver> observers)
        {
            _rebuild.Clear();
            foreach (KeyValuePair<ChunkCoord, Entry> entry in _chunks)
            {
                if (entry.Value.State == ChunkState.Ready && IsStale(entry.Key, entry.Value))
                {
                    float d = float.MaxValue;
                    foreach (StreamingObserver o in observers)
                    {
                        d = Math.Min(d, DistanceToChunk(entry.Key, o.X, o.Y, o.Z));
                    }

                    _rebuild.Add((d, entry.Key));
                }
            }

            _rebuild.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            int rebuilt = 0;
            foreach ((float _, ChunkCoord coord) in _rebuild)
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

        private bool IsStale(ChunkCoord coord, Entry entry) => entry.ColliderRevision != entry.Data.Revision || _stale.Contains(coord);

        private void MarkStale(ChunkCoord coord)
        {
            if (_chunks.TryGetValue(coord, out Entry? entry) && entry.State == ChunkState.Ready)
            {
                _stale.Add(coord);
            }
        }

        private static bool Contains(List<(float, ChunkCoord Coord)> list, ChunkCoord coord)
        {
            foreach ((float _, ChunkCoord c) in list)
            {
                if (c.Equals(coord))
                {
                    return true;
                }
            }

            return false;
        }

        private static float DistanceToChunk(ChunkCoord c, float x, float y, float z)
        {
            float dx = Axis(x, c.X), dy = Axis(y, c.Y), dz = Axis(z, c.Z);
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Distance from a point to a chunk's extent along one axis (0 inside).
        private static float Axis(float p, int chunk)
        {
            float min = chunk * ChunkMath.Size, max = min + ChunkMath.Size;
            return p < min ? min - p : p > max ? p - max : 0f;
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
            _stale.Remove(coord);
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
                _stale.Remove(coord);
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
