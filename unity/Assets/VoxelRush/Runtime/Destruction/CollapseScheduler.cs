using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Destruction
{
    /// <summary>DESTRUCTION Structure initial caps (tunable).</summary>
    public sealed class CollapseLimits
    {
        public int ChecksPerTick { get; set; } = 512;

        public int MaxRemovedPerTick { get; set; } = 256;

        public int MaxActiveJobs { get; set; } = 4;

        /// <summary>Removal points waiting for a job; beyond this the world refuses new destructive edits.</summary>
        public int MaxPending { get; set; } = 1024;

        /// <summary>A search that grows past this is abandoned as supported (counted) instead of holding a job forever.</summary>
        public int MaxChecksPerJob { get; set; } = 65536;
    }

    public readonly struct CollapseTickStats
    {
        public CollapseTickStats(int checks, int removed, int activeJobs, int pending, int queuedRemovals)
        {
            Checks = checks;
            Removed = removed;
            ActiveJobs = activeJobs;
            Pending = pending;
            QueuedRemovals = queuedRemovals;
        }

        public int Checks { get; }

        public int Removed { get; }

        public int ActiveJobs { get; }

        public int Pending { get; }

        public int QueuedRemovals { get; }
    }

    /// <summary>
    /// Incremental, bounded structural collapse (DESTRUCTION Structure). Every removal enqueues a job; up to
    /// <see cref="CollapseLimits.MaxActiveJobs"/> jobs search their removal point's player-built neighbours, sharing
    /// <see cref="CollapseLimits.ChecksPerTick"/>; disconnected components are removed through the authority (journaled,
    /// replicated) at most <see cref="CollapseLimits.MaxRemovedPerTick"/> blocks per tick. Regions under analysis or
    /// awaiting removal are blocked for client edits until resolved.
    /// </summary>
    public sealed class CollapseScheduler
    {
        public const string ActorId = "server:collapse";

        private readonly WorldAuthority _world;
        private readonly IStructureWorld _view;
        private readonly CollapseLimits _limits;
        private readonly Queue<BlockPos> _pending = new Queue<BlockPos>();
        private readonly HashSet<BlockPos> _pendingSet = new HashSet<BlockPos>();
        private readonly List<Job> _active = new List<Job>();
        private readonly Stack<Job> _jobPool = new Stack<Job>();
        private readonly Queue<BlockPos> _removals = new Queue<BlockPos>();
        private readonly HashSet<BlockPos> _removalSet = new HashSet<BlockPos>();
        private readonly List<BlockPos> _batch = new List<BlockPos>();
        private readonly Dictionary<ChunkCoord, List<BlockChange>> _byChunk = new Dictionary<ChunkCoord, List<BlockChange>>();

        public CollapseScheduler(WorldAuthority world, IStructureWorld view, CollapseLimits? limits = null)
        {
            _world = world;
            _view = view;
            _limits = limits ?? new CollapseLimits();
        }

        public CollapseLimits Limits => _limits;

        public int Pending => _pending.Count;

        public int ActiveJobs => _active.Count;

        public int QueuedRemovals => _removals.Count;

        /// <summary>Removal points can't be queued; callers must refuse further destructive edits.</summary>
        public bool IsSaturated => _pending.Count >= _limits.MaxPending;

        public bool IsIdle => _pending.Count == 0 && _active.Count == 0 && _removals.Count == 0;

        public long Collapses { get; private set; }

        public long BlocksRemoved { get; private set; }

        public long Abandoned { get; private set; }

        public long Refused { get; private set; }

        /// <summary>Queues analysis around a block that just became air. False when saturated (nothing queued).</summary>
        public bool Enqueue(BlockPos removed)
        {
            if (_pendingSet.Contains(removed))
            {
                return true;
            }

            if (IsSaturated)
            {
                Refused++;
                return false;
            }

            _pending.Enqueue(removed);
            _pendingSet.Add(removed);
            return true;
        }

        /// <summary>True where an edit could change an unresolved answer or touch a block about to fall.</summary>
        public bool IsBlocked(BlockPos block)
        {
            if (_removalSet.Contains(block))
            {
                return true;
            }

            foreach (Job job in _active)
            {
                if (job.Covers(block))
                {
                    return true;
                }
            }

            if (_pendingSet.Count == 0)
            {
                return false;
            }

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (_pendingSet.Contains(new BlockPos(block.X + dx, block.Y + dy, block.Z + dz)))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public CollapseTickStats Tick(uint serverTick)
        {
            int removed = RemoveQueued(serverTick);
            int checks = 0;
            Activate();
            while (checks < _limits.ChecksPerTick && _active.Count > 0)
            {
                int share = Math.Max(1, (_limits.ChecksPerTick - checks) / _active.Count);
                for (int i = 0; i < _active.Count && checks < _limits.ChecksPerTick; i++)
                {
                    Job job = _active[i];
                    checks += job.Step(Math.Min(share, _limits.ChecksPerTick - checks));
                    if (job.TakeDisconnected())
                    {
                        QueueRemoval(job.Search.Component, job);
                    }

                    if (job.Checks > _limits.MaxChecksPerJob)
                    {
                        job.Abandon();
                        Abandoned++;
                    }

                    if (job.IsFinished)
                    {
                        _active.RemoveAt(i--);
                        _jobPool.Push(job);
                    }
                }

                Activate();
            }

            return new CollapseTickStats(checks, removed, _active.Count, _pending.Count, _removals.Count);
        }

        private void Activate()
        {
            while (_active.Count < _limits.MaxActiveJobs && _pending.Count > 0)
            {
                BlockPos point = _pending.Dequeue();
                _pendingSet.Remove(point);
                Job job = _jobPool.Count > 0 ? _jobPool.Pop() : new Job(_view);
                job.Start(point);
                _active.Add(job);
            }
        }

        private void QueueRemoval(IReadOnlyList<BlockPos> component, Job job)
        {
            bool any = false;
            foreach (BlockPos block in component)
            {
                job.Claim(block);
                if (_removalSet.Add(block))
                {
                    _removals.Enqueue(block);
                    any = true;
                }
            }

            if (any)
            {
                Collapses++;
            }
        }

        private int RemoveQueued(uint serverTick)
        {
            if (_removals.Count == 0)
            {
                return 0;
            }

            _batch.Clear();
            foreach (List<BlockChange> list in _byChunk.Values)
            {
                list.Clear();
            }

            while (_batch.Count < _limits.MaxRemovedPerTick && _removals.Count > 0)
            {
                BlockPos block = _removals.Peek();
                _removals.Dequeue();
                _batch.Add(block);
                // Something else may have changed it meanwhile (an explosion); only remove what is still player-built.
                if (_view.TryGetBlock(block, out byte type, out bool built) && type != BlockIds.Air && built)
                {
                    ChunkCoord chunk = ChunkMath.ToChunk(block);
                    if (!_byChunk.TryGetValue(chunk, out List<BlockChange>? list))
                    {
                        list = new List<BlockChange>();
                        _byChunk[chunk] = list;
                    }

                    list.Add(new BlockChange((ushort)ChunkMath.ToLocal(block).Index, BlockIds.Air));
                }
            }

            var changes = new List<ChunkChanges>();
            foreach (KeyValuePair<ChunkCoord, List<BlockChange>> entry in _byChunk)
            {
                if (entry.Value.Count > 0)
                {
                    changes.Add(new ChunkChanges(entry.Key, entry.Value.ToArray()));
                }
            }

            ServerCommitResult commit = changes.Count == 0
                ? new ServerCommitResult(EditRejection.None, Array.Empty<ChunkMutationBatch>(), false)
                : _world.CommitServerChanges(Guid.NewGuid(), ActorId, changes, serverTick);
            if (!commit.Accepted)
            {
                // Backpressure: put the batch back in front and retry next tick (the region stays blocked meanwhile).
                var rest = _removals.ToArray();
                _removals.Clear();
                foreach (BlockPos block in _batch)
                {
                    _removals.Enqueue(block);
                }

                foreach (BlockPos block in rest)
                {
                    _removals.Enqueue(block);
                }

                return 0;
            }

            int count = 0;
            foreach (BlockPos block in _batch)
            {
                _removalSet.Remove(block);
            }

            foreach (ChunkChanges c in changes)
            {
                count += c.Changes.Count;
            }

            BlocksRemoved += count;
            return count;
        }

        private sealed class Job
        {
            private readonly HashSet<BlockPos> _claimed = new HashSet<BlockPos>();
            private BlockPos _point;
            private int _face;
            private bool _searching;
            private bool _disconnected;

            public Job(IStructureWorld view) => Search = new SupportSearch(view);

            public SupportSearch Search { get; }

            public int Checks { get; private set; }

            public bool IsFinished => _face >= SupportSearch.Faces.Length && !_searching;

            public void Start(BlockPos point)
            {
                _point = point;
                _face = 0;
                _searching = false;
                _disconnected = false;
                _claimed.Clear();
                Checks = 0;
            }

            public void Claim(BlockPos block) => _claimed.Add(block);

            /// <summary>True once per finished search that found a disconnected component (read it from <see cref="Search"/>).</summary>
            public bool TakeDisconnected()
            {
                bool result = _disconnected;
                _disconnected = false;
                return result;
            }

            public bool Covers(BlockPos block) =>
                (_searching && Search.Covers(block))
                || (Math.Abs(block.X - _point.X) <= 1 && Math.Abs(block.Y - _point.Y) <= 1 && Math.Abs(block.Z - _point.Z) <= 1);

            /// <summary>Advances through the removal point's neighbours; each neighbour probe costs one check.</summary>
            public int Step(int budget)
            {
                int used = 0;
                while (used < budget && !IsFinished)
                {
                    if (_searching)
                    {
                        used += Search.Step(budget - used);
                        if (!Search.IsDone)
                        {
                            break;
                        }

                        _searching = false;
                        // Hand a disconnected component to the scheduler before probing the next neighbour.
                        _disconnected = Search.Status == SupportStatus.Disconnected;
                        if (_disconnected)
                        {
                            break;
                        }

                        continue;
                    }

                    (int dx, int dy, int dz) = SupportSearch.Faces[_face++];
                    var neighbour = new BlockPos(_point.X + dx, _point.Y + dy, _point.Z + dz);
                    used++;
                    if (_claimed.Contains(neighbour))
                    {
                        continue;
                    }

                    Search.Begin(neighbour);
                    _searching = !Search.IsDone;
                }

                Checks += used;
                return used;
            }

            public void Abandon()
            {
                Search.Abandon();
                _searching = false;
                _face = SupportSearch.Faces.Length;
            }
        }
    }
}
