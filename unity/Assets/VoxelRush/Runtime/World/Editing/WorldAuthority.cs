using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Editing
{
    /// <summary>
    /// Single authoritative writer for block edits (PERSISTENCE_SECURITY Concurrency, ADR-002): validates every intent
    /// against server-side state, serializes them in a deterministic order, assigns each accepted operation exactly one
    /// chunk revision, and treats operation ids idempotently. Engine-free; the network/persistence layers wrap it.
    /// </summary>
    public sealed class WorldAuthority : IEditWorld
    {
        public const int DefaultMaxEditsPerSecond = 10;
        public const int DefaultMaxQueuedPerTick = 64;
        public const int DefaultOperationMemory = 4096;

        private readonly Dictionary<ChunkCoord, ChunkData> _chunks = new Dictionary<ChunkCoord, ChunkData>();
        private readonly Dictionary<ChunkCoord, Dictionary<ushort, ulong>> _lastChange = new Dictionary<ChunkCoord, Dictionary<ushort, ulong>>();
        private readonly Dictionary<Guid, EditResult> _processed = new Dictionary<Guid, EditResult>();
        private readonly Queue<Guid> _processedOrder = new Queue<Guid>();
        private readonly Dictionary<string, TokenBucket> _rates = new Dictionary<string, TokenBucket>();
        private readonly EditValidator _validator;
        private readonly IMutationSink? _sink;
        private readonly int _maxEditsPerSecond;
        private readonly int _maxQueuedPerTick;
        private readonly int _operationMemory;
        private uint _queuedTick;
        private int _queuedThisTick;

        /// <param name="sink">Durability target; null only for transient worlds (race overlays, tests).</param>
        public WorldAuthority(EditValidator validator, int maxEditsPerSecond = DefaultMaxEditsPerSecond, int maxQueuedPerTick = DefaultMaxQueuedPerTick, int operationMemory = DefaultOperationMemory, IMutationSink? sink = null)
        {
            _validator = validator;
            _sink = sink;
            _maxEditsPerSecond = maxEditsPerSecond;
            _maxQueuedPerTick = maxQueuedPerTick;
            _operationMemory = operationMemory;
        }

        public long Accepted { get; private set; }

        public long Rejected { get; private set; }

        public void AddChunk(ChunkData chunk) => _chunks[chunk.Coord] = chunk;

        /// <summary>
        /// Re-seeds idempotency from recovered journal records, so a client retrying an operation whose ACK was lost
        /// in a crash gets the original (durable) result instead of a second application.
        /// </summary>
        public void RememberRecovered(IEnumerable<ChunkMutationBatch> batches)
        {
            foreach (ChunkMutationBatch batch in batches)
            {
                if (!_processed.ContainsKey(batch.OperationId))
                {
                    Remember(batch.OperationId, EditResult.Accept(batch));
                }

                foreach (BlockChange change in batch.Changes)
                {
                    SetLastChange(batch.Chunk, change.Index, batch.NewRevision);
                }
            }
        }

        public bool RemoveChunk(ChunkCoord coord)
        {
            _lastChange.Remove(coord);
            return _chunks.Remove(coord);
        }

        public ChunkData? GetChunk(ChunkCoord coord) => _chunks.TryGetValue(coord, out ChunkData? chunk) ? chunk : null;

        public bool IsLoaded(BlockPos block) => _chunks.ContainsKey(ChunkMath.ToChunk(block));

        public byte GetBlock(BlockPos block) =>
            _chunks.TryGetValue(ChunkMath.ToChunk(block), out ChunkData? chunk) ? chunk.Get(ChunkMath.ToLocal(block)) : BlockIds.Air;

        /// <summary>
        /// Validates and applies one intent. Callers must process intents in arrival order (one writer per world);
        /// <paramref name="actorEye"/> and <paramref name="occupants"/> come from server state, never from the client.
        /// </summary>
        public EditResult Submit(string actorId, EditIntent intent, float eyeX, float eyeY, float eyeZ, IReadOnlyList<WorldBox> occupants, uint serverTick, double nowSeconds)
        {
            if (_processed.TryGetValue(intent.OperationId, out EditResult earlier))
            {
                return earlier.AsDuplicate();
            }

            EditResult result = Process(actorId, intent, eyeX, eyeY, eyeZ, occupants, serverTick, nowSeconds);
            // Transient refusals may be retried with the same operation id; final outcomes are fixed.
            if (!IsTransient(result.Rejection))
            {
                Remember(intent.OperationId, result);
            }

            if (result.Accepted)
            {
                Accepted++;
            }
            else
            {
                Rejected++;
            }

            return result;
        }

        private EditResult Process(string actorId, EditIntent intent, float eyeX, float eyeY, float eyeZ, IReadOnlyList<WorldBox> occupants, uint serverTick, double nowSeconds)
        {
            if (serverTick != _queuedTick)
            {
                _queuedTick = serverTick;
                _queuedThisTick = 0;
            }

            if (++_queuedThisTick > _maxQueuedPerTick)
            {
                return EditResult.Reject(EditRejection.ServerBusy);
            }

            if (!Rate(actorId).TryTake(nowSeconds))
            {
                return EditResult.Reject(EditRejection.RateLimited);
            }

            if (intent.Action == EditAction.Rotate || (intent.Action != EditAction.Place && intent.Action != EditAction.Remove))
            {
                return EditResult.Reject(EditRejection.UnsupportedAction);
            }

            if (!LocalBlock.TryFromIndex(intent.LocalIndex, out LocalBlock local))
            {
                return EditResult.Reject(EditRejection.InvalidIndex);
            }

            if (!_chunks.TryGetValue(intent.Chunk, out ChunkData? chunk))
            {
                return EditResult.Reject(EditRejection.ChunkNotLoaded);
            }

            if (intent.ExpectedRevision > chunk.Revision)
            {
                // A client can't have seen a revision the server never produced.
                return EditResult.Reject(EditRejection.InvalidRevision);
            }

            if (LastChange(intent.Chunk, (ushort)local.Index) > intent.ExpectedRevision)
            {
                return EditResult.Reject(EditRejection.StaleRevision);
            }

            BlockPos target = ChunkMath.ToWorld(intent.Chunk, local);
            byte blockType = intent.Action == EditAction.Remove ? BlockIds.Air : intent.BlockType;
            EditRejection rule = _validator.Validate(intent.Action, target, intent.BlockType, eyeX, eyeY, eyeZ, this, occupants);
            if (rule != EditRejection.None)
            {
                return EditResult.Reject(rule);
            }

            // Ensure durability is possible before assigning a revision: a refused enqueue after applying
            // would leave a hole in the journaled revision chain (found by tools/crash-test.sh).
            if (_sink != null && !_sink.CanAccept)
            {
                return EditResult.Reject(EditRejection.PersistenceBackpressure);
            }

            ulong baseRevision = chunk.Revision;
            if (!chunk.Set(local, blockType))
            {
                // Validation guarantees a change; treat a no-op as already applied rather than inventing a revision.
                return EditResult.Reject(intent.Action == EditAction.Place ? EditRejection.Occupied : EditRejection.NothingToRemove);
            }

            SetLastChange(intent.Chunk, (ushort)local.Index, chunk.Revision);
            var batch = new ChunkMutationBatch(intent.OperationId, actorId, intent.Chunk, baseRevision, chunk.Revision, serverTick,
                new[] { new BlockChange((ushort)local.Index, blockType) });
            _sink?.Accept(batch);
            return EditResult.Accept(batch);
        }

        private static bool IsTransient(EditRejection rejection) =>
            rejection == EditRejection.RateLimited || rejection == EditRejection.ServerBusy || rejection == EditRejection.ChunkNotLoaded
            || rejection == EditRejection.PersistenceBackpressure;

        private ulong LastChange(ChunkCoord chunk, ushort index) =>
            _lastChange.TryGetValue(chunk, out Dictionary<ushort, ulong>? changes) && changes.TryGetValue(index, out ulong revision) ? revision : 0;

        private void SetLastChange(ChunkCoord chunk, ushort index, ulong revision)
        {
            if (!_lastChange.TryGetValue(chunk, out Dictionary<ushort, ulong>? changes))
            {
                changes = new Dictionary<ushort, ulong>();
                _lastChange[chunk] = changes;
            }

            changes[index] = revision;
        }

        // Bounded idempotency memory: oldest operation ids are forgotten first.
        private void Remember(Guid operationId, EditResult result)
        {
            _processed[operationId] = result;
            _processedOrder.Enqueue(operationId);
            while (_processedOrder.Count > _operationMemory)
            {
                _processed.Remove(_processedOrder.Dequeue());
            }
        }

        private TokenBucket Rate(string actorId)
        {
            if (!_rates.TryGetValue(actorId, out TokenBucket? bucket))
            {
                bucket = new TokenBucket(_maxEditsPerSecond, _maxEditsPerSecond);
                _rates[actorId] = bucket;
            }

            return bucket;
        }

        private sealed class TokenBucket
        {
            private readonly double _capacity;
            private readonly double _refillPerSecond;
            private double _tokens;
            private double _last = double.NaN;

            public TokenBucket(double capacity, double refillPerSecond)
            {
                _capacity = capacity;
                _refillPerSecond = refillPerSecond;
                _tokens = capacity;
            }

            public bool TryTake(double now)
            {
                if (!double.IsNaN(_last) && now > _last)
                {
                    _tokens = Math.Min(_capacity, _tokens + (now - _last) * _refillPerSecond);
                }

                _last = double.IsNaN(_last) || now > _last ? now : _last;
                if (_tokens < 1)
                {
                    return false;
                }

                _tokens -= 1;
                return true;
            }
        }
    }
}
