using System.Collections.Generic;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;

namespace VoxelRush.World.Sync
{
    /// <summary>
    /// A client's copy of server chunks (CONTRACTS rules): a delta applies only when its base revision equals the local
    /// revision; older ones are duplicates; newer ones are a gap, so they are buffered and a snapshot is requested.
    /// Snapshots are verified by content hash before use.
    /// </summary>
    public sealed class ClientWorldReplica
    {
        private const int MaxBufferedPerChunk = 64;

        private readonly IBaseGenerator _generator;
        private readonly Dictionary<ChunkCoord, ChunkData> _chunks = new Dictionary<ChunkCoord, ChunkData>();
        private readonly Dictionary<ChunkCoord, SortedList<ulong, ChunkMutationBatch>> _buffered = new Dictionary<ChunkCoord, SortedList<ulong, ChunkMutationBatch>>();
        private readonly HashSet<ChunkCoord> _requested = new HashSet<ChunkCoord>();
        private readonly List<ChunkCoord> _newRequests = new List<ChunkCoord>();

        public ClientWorldReplica(WorldInfo info)
        {
            Info = info;
            _generator = info.CreateGenerator();
        }

        public WorldInfo Info { get; }

        public long AppliedDeltas { get; private set; }

        public long DuplicateDeltas { get; private set; }

        public long Gaps { get; private set; }

        public long HashMismatches { get; private set; }

        public int ChunkCount => _chunks.Count;

        /// <summary>Called per block a delta changes (chunk, local index, old type, new type); not for snapshots.</summary>
        public System.Action<ChunkCoord, ushort, byte, byte>? BlockChanged { get; set; }

        public ChunkData? Get(ChunkCoord coord) => _chunks.TryGetValue(coord, out ChunkData? chunk) ? chunk : null;

        /// <summary>Snapshot requests to send to the server (each chunk once until its snapshot arrives).</summary>
        public int DrainSnapshotRequests(List<ChunkCoord> into)
        {
            into.Clear();
            into.AddRange(_newRequests);
            _newRequests.Clear();
            return into.Count;
        }

        /// <summary>Asks the server for a chunk's snapshot (e.g. a cache miss).</summary>
        public void Request(ChunkCoord coord) => RequestSnapshot(coord, force: true);

        /// <summary>A separate copy for a race's match overlay; the sandbox replica stays untouched by match deltas.</summary>
        public ClientWorldReplica Fork()
        {
            var fork = new ClientWorldReplica(Info);
            foreach (KeyValuePair<ChunkCoord, ChunkData> entry in _chunks)
            {
                fork._chunks[entry.Key] = entry.Value.Clone();
            }

            return fork;
        }

        public bool ApplySnapshot(ChunkSnapshotMessage snapshot)
        {
            if (snapshot.GeneratorVersion != _generator.GeneratorVersion || snapshot.SchemaVersion != ChunkData.CurrentSchemaVersion)
            {
                HashMismatches++;
                return false;
            }

            ChunkData chunk = ChunkData.FromSnapshot(snapshot.Coord, snapshot.GeneratorVersion, _generator.GenerateBlocks(snapshot.Coord), snapshot.Revision, snapshot.Edits);
            if (ChunkHash.Compute(chunk) != snapshot.ContentHash)
            {
                // Corrupt or mismatched: keep the old copy (if any) and ask again.
                HashMismatches++;
                RequestSnapshot(snapshot.Coord, force: true);
                return false;
            }

            if (_chunks.TryGetValue(snapshot.Coord, out ChunkData? existing) && existing.Revision > snapshot.Revision)
            {
                // An older snapshot raced a delta that already moved us forward.
                _requested.Remove(snapshot.Coord);
                return true;
            }

            _chunks[snapshot.Coord] = chunk;
            _requested.Remove(snapshot.Coord);
            ApplyBuffered(snapshot.Coord);
            return true;
        }

        public void ApplyDelta(ChunkMutationBatch batch)
        {
            if (!_chunks.TryGetValue(batch.Chunk, out ChunkData? chunk))
            {
                Buffer(batch);
                RequestSnapshot(batch.Chunk, force: false);
                return;
            }

            if (batch.NewRevision <= chunk.Revision)
            {
                DuplicateDeltas++;
                return;
            }

            if (batch.BaseRevision != chunk.Revision)
            {
                Gaps++;
                Buffer(batch);
                RequestSnapshot(batch.Chunk, force: false);
                return;
            }

            Apply(chunk, batch);
            ApplyBuffered(batch.Chunk);
        }

        private void Apply(ChunkData chunk, ChunkMutationBatch batch)
        {
            foreach (BlockChange change in batch.Changes)
            {
                LocalBlock local = LocalBlock.FromIndex(change.Index);
                byte old = chunk.Get(local);
                if (chunk.Set(local, change.BlockType))
                {
                    BlockChanged?.Invoke(batch.Chunk, change.Index, old, change.BlockType);
                }
            }

            if (chunk.Revision != batch.NewRevision)
            {
                _chunks[batch.Chunk] = ChunkData.FromSnapshot(batch.Chunk, chunk.GeneratorVersion, _generator.GenerateBlocks(batch.Chunk), batch.NewRevision, chunk.Edits);
            }

            AppliedDeltas++;
        }

        private void ApplyBuffered(ChunkCoord coord)
        {
            if (!_buffered.TryGetValue(coord, out SortedList<ulong, ChunkMutationBatch>? pending))
            {
                return;
            }

            ChunkData chunk = _chunks[coord];
            while (pending.Count > 0)
            {
                ChunkMutationBatch next = pending.Values[0];
                if (next.NewRevision <= chunk.Revision)
                {
                    pending.RemoveAt(0);
                    continue;
                }

                if (next.BaseRevision != chunk.Revision)
                {
                    break;
                }

                pending.RemoveAt(0);
                Apply(chunk, next);
                chunk = _chunks[coord];
            }

            if (pending.Count == 0)
            {
                _buffered.Remove(coord);
            }
        }

        private void Buffer(ChunkMutationBatch batch)
        {
            if (!_buffered.TryGetValue(batch.Chunk, out SortedList<ulong, ChunkMutationBatch>? pending))
            {
                pending = new SortedList<ulong, ChunkMutationBatch>();
                _buffered[batch.Chunk] = pending;
            }

            if (pending.Count >= MaxBufferedPerChunk)
            {
                // Bounded: drop the backlog; the requested snapshot will cover it.
                pending.Clear();
            }

            pending[batch.BaseRevision] = batch;
        }

        private void RequestSnapshot(ChunkCoord coord, bool force)
        {
            if ((_requested.Add(coord) || force) && !_newRequests.Contains(coord))
            {
                _newRequests.Add(coord);
            }
        }
    }
}
