using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Persistence;
using VoxelRush.World.Sync;

namespace VoxelRush.Networking.World
{
    /// <summary>Client side of world sync: keeps a <see cref="ClientWorldReplica"/> current and sends edit intents.</summary>
    public sealed class WorldSyncClient
    {
        private readonly NetworkManager _manager;
        private readonly List<ChunkCoord> _requests = new List<ChunkCoord>();
        private readonly Dictionary<Guid, Action<EditRejection, ulong>> _pendingEdits = new Dictionary<Guid, Action<EditRejection, ulong>>();
        private readonly List<ChunkMutationBatch> _scratch = new List<ChunkMutationBatch>();

        private readonly string? _cacheRoot;
        private ChunkCache? _cache;
        private readonly List<ChunkCoord> _region = new List<ChunkCoord>();

        /// <param name="cacheRoot">SAVE-008 device chunk cache directory, or null for none.</param>
        public WorldSyncClient(NetworkManager manager, string? cacheRoot = null)
        {
            _manager = manager;
            _cacheRoot = cacheRoot;
            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.ChunkCurrent, OnChunkCurrent);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.Info, OnInfo);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.Snapshot, OnSnapshot);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.Delta, OnDelta);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.EditResult, OnEditResult);
        }

        public ClientWorldReplica? Replica { get; private set; }

        public long SnapshotsReceived { get; private set; }

        public long CacheHits { get; private set; }

        public long CacheMisses { get; private set; }

        /// <summary>Writes the replica to the device cache (leaving the world, app paused or quitting).</summary>
        public void SaveCache()
        {
            if (_cache == null || Replica == null)
            {
                return;
            }

            var chunks = new List<ChunkData>();
            foreach (ChunkCoord coord in _region)
            {
                if (Replica.Get(coord) is ChunkData chunk)
                {
                    chunks.Add(chunk);
                }
            }

            try
            {
                Debug.Log(LogEvent.Format("world_cache_saved", ("chunks", _cache.Store(chunks))));
            }
            catch (System.IO.IOException e)
            {
                // Disposable: a full or read-only disk only costs a full download next time.
                Debug.LogWarning(LogEvent.Format("world_cache_save_failed", ("error", e.Message)));
            }
        }

        /// <summary>True once every chunk of the synced region has arrived.</summary>
        public bool IsSynced => Replica != null && Replica.ChunkCount >= RegionSize(Replica.Info);

        public Guid SendEdit(ChunkCoord chunk, int localIndex, EditAction action, byte blockType, Action<EditRejection, ulong>? onResult = null)
        {
            ulong expected = Replica?.Get(chunk)?.Revision ?? 0;
            var intent = new EditIntent(Guid.NewGuid(), chunk, localIndex, expected, action, blockType);
            if (onResult != null)
            {
                _pendingEdits[intent.OperationId] = onResult;
            }

            WorldMessages.Send(_manager, WorldMessages.Edit, NetworkManager.ServerClientId, EditWire.EncodeIntent(intent), NetworkDelivery.ReliableSequenced);
            return intent.OperationId;
        }

        public void Tick()
        {
            if (Replica == null || Replica.DrainSnapshotRequests(_requests) == 0)
            {
                return;
            }

            foreach (ChunkCoord c in _requests)
            {
                var payload = new byte[12];
                BitConverter.GetBytes(c.X).CopyTo(payload, 0);
                BitConverter.GetBytes(c.Y).CopyTo(payload, 4);
                BitConverter.GetBytes(c.Z).CopyTo(payload, 8);
                WorldMessages.Send(_manager, WorldMessages.SnapshotRequest, NetworkManager.ServerClientId, payload, NetworkDelivery.ReliableSequenced);
            }
        }

        public void LogHashes()
        {
            if (Replica == null)
            {
                return;
            }

            foreach (ChunkCoord coord in Replica.Info.RegionChunks())
            {
                ChunkData? chunk = Replica.Get(coord);
                if (chunk != null && chunk.Revision > 0)
                {
                    Debug.Log(LogEvent.Format("world_replica_hash", ("chunk", $"{coord.X},{coord.Y},{coord.Z}"), ("revision", chunk.Revision), ("hash", ChunkHash.Compute(chunk).ToString("X16"))));
                }
            }

            Debug.Log(LogEvent.Format("world_replica_stats", ("chunks", Replica.ChunkCount), ("snapshots", SnapshotsReceived), ("deltas", Replica.AppliedDeltas),
                ("duplicates", Replica.DuplicateDeltas), ("gaps", Replica.Gaps), ("hashMismatches", Replica.HashMismatches),
                ("cacheHits", CacheHits), ("cacheMisses", CacheMisses)));
        }

        private void OnInfo(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId || !WorldMessages.TryRead(reader, out byte[] payload))
            {
                return;
            }

            try
            {
                Replica = new ClientWorldReplica(WorldInfo.Decode(payload));
                _region.Clear();
                _region.AddRange(Replica.Info.RegionChunks());
                OpenCache();
                Debug.Log(LogEvent.Format("world_info", ("worldId", Replica.Info.WorldId), ("kind", Replica.Info.Kind), ("generatorVersion", Replica.Info.GeneratorVersion), ("chunks", RegionSize(Replica.Info))));
            }
            catch (Exception e) when (e is NotSupportedException || e is System.IO.InvalidDataException)
            {
                Debug.LogError(LogEvent.Format("world_info_rejected", ("error", e.Message)));
            }
        }

        // Always answer, even with an empty list: the server holds the first snapshots until it hears from us.
        private void OpenCache()
        {
            var manifest = new List<CachedChunk>();
            if (_cacheRoot != null)
            {
                try
                {
                    _cache = new ChunkCache(_cacheRoot, Replica!.Info);
                    manifest = _cache.Manifest();
                    Debug.Log(LogEvent.Format("world_cache_opened", ("entries", manifest.Count), ("invalidated", _cache.Invalidated)));
                }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
                {
                    _cache = null;
                    Debug.LogWarning(LogEvent.Format("world_cache_unavailable", ("error", e.Message)));
                }
            }

            WorldMessages.Send(_manager, WorldMessages.CacheManifest, NetworkManager.ServerClientId, CachedChunkWire.Encode(manifest), NetworkDelivery.ReliableFragmentedSequenced);
        }

        private void OnChunkCurrent(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId || Replica == null || !WorldMessages.TryRead(reader, out byte[] payload)
                || !CachedChunkWire.TryDecode(payload, out List<CachedChunk> current))
            {
                return;
            }

            foreach (CachedChunk c in current)
            {
                // ApplySnapshot re-verifies the content hash; a tampered or torn entry asks the server instead.
                ChunkSnapshotMessage? cached = _cache?.TryLoad(c.Coord, c.Revision, c.ContentHash);
                if (cached != null && Replica.ApplySnapshot(cached))
                {
                    CacheHits++;
                    continue;
                }

                CacheMisses++;
                _cache?.Drop(c.Coord);
                if (cached == null)
                {
                    Replica.Request(c.Coord);
                }
            }
        }

        private void OnSnapshot(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId || Replica == null || !WorldMessages.TryRead(reader, out byte[] payload))
            {
                return;
            }

            try
            {
                Replica.ApplySnapshot(ChunkSnapshotMessage.Decode(payload));
                SnapshotsReceived++;
            }
            catch (System.IO.InvalidDataException e)
            {
                Debug.LogWarning(LogEvent.Format("world_snapshot_rejected", ("error", e.Message)));
            }
        }

        private void OnDelta(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId || Replica == null || !WorldMessages.TryRead(reader, out byte[] payload))
            {
                return;
            }

            _scratch.Clear();
            JournalCodec.ReadAll(new System.IO.MemoryStream(payload), _scratch);
            foreach (ChunkMutationBatch batch in _scratch)
            {
                Replica.ApplyDelta(batch);
            }
        }

        private void OnEditResult(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId || !WorldMessages.TryRead(reader, out byte[] payload)
                || !EditWire.TryDecodeResult(payload, out Guid id, out EditRejection rejection, out ulong revision))
            {
                return;
            }

            if (_pendingEdits.Remove(id, out Action<EditRejection, ulong>? callback))
            {
                callback(rejection, revision);
            }
        }

        private static int RegionSize(WorldInfo info) =>
            (info.RegionMax.X - info.RegionMin.X + 1) * (info.RegionMax.Y - info.RegionMin.Y + 1) * (info.RegionMax.Z - info.RegionMin.Z + 1);
    }
}
