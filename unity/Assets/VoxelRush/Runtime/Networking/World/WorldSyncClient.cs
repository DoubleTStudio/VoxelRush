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

        public WorldSyncClient(NetworkManager manager)
        {
            _manager = manager;
            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.Info, OnInfo);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.Snapshot, OnSnapshot);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.Delta, OnDelta);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.EditResult, OnEditResult);
        }

        public ClientWorldReplica? Replica { get; private set; }

        public long SnapshotsReceived { get; private set; }

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
                ("duplicates", Replica.DuplicateDeltas), ("gaps", Replica.Gaps), ("hashMismatches", Replica.HashMismatches)));
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
                Debug.Log(LogEvent.Format("world_info", ("worldId", Replica.Info.WorldId), ("kind", Replica.Info.Kind), ("generatorVersion", Replica.Info.GeneratorVersion), ("chunks", RegionSize(Replica.Info))));
            }
            catch (Exception e) when (e is NotSupportedException || e is System.IO.InvalidDataException)
            {
                Debug.LogError(LogEvent.Format("world_info_rejected", ("error", e.Message)));
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
