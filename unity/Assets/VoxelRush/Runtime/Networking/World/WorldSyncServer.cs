using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Destruction;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Persistence;
using VoxelRush.World.Sync;

namespace VoxelRush.Networking.World
{
    /// <summary>
    /// Server side of the voxel world (VR-031/032/033): authoritative edits, durable journal before ACK/broadcast,
    /// late-join snapshots and gap-recovery snapshots, each within per-tick budgets.
    /// </summary>
    public sealed class WorldSyncServer : IDisposable
    {
        private const int SnapshotsPerClientPerTick = 4;
        private const int SnapshotRequestsPerClientPerSecond = 8;

        private readonly NetworkManager _manager;
        private readonly WorldInfo _info;
        private readonly WorldStore _store;
        private readonly JournalWriter _journal;
        private readonly WorldAuthority _authority;
        private readonly CollapseScheduler _collapse;
        private readonly Func<ulong, Vector3?> _actorEye;
        private readonly Func<IReadOnlyList<WorldBox>> _occupants;
        private readonly Queue<(ulong ClientId, EditIntent Intent)> _edits = new Queue<(ulong, EditIntent)>();
        private readonly Dictionary<ulong, Queue<ChunkCoord>> _pendingSnapshots = new Dictionary<ulong, Queue<ChunkCoord>>();
        private readonly Dictionary<ulong, (double WindowStart, int Count)> _requestRates = new Dictionary<ulong, (double, int)>();
        private readonly Dictionary<Guid, ulong> _awaitingDurable = new Dictionary<Guid, ulong>();
        private readonly List<ChunkMutationBatch> _durable = new List<ChunkMutationBatch>();

        /// <param name="roleOf">Server-side role of a client (its join ticket; DES-002 zones decide what each role may do).</param>
        public WorldSyncServer(NetworkManager manager, WorldInfo info, string directory, Func<ulong, Vector3?> actorEye, Func<IReadOnlyList<WorldBox>> occupants,
            Func<ulong, WorldRole>? roleOf = null, bool visitorsCanBuild = true)
        {
            _manager = manager;
            _info = info;
            _actorEye = actorEye;
            _occupants = occupants;
            IBaseGenerator generator = info.CreateGenerator();
            _store = new WorldStore(directory, generator.GeneratorVersion, generator.GenerateBlocks);
            _store.RecoverJournal();
            _journal = new JournalWriter(_store.JournalPath);
            _authority = new WorldAuthority(new EditValidator(ZoneMap.ProtectedOnly(Array.Empty<WorldBox>(), visitorsCanBuild)), sink: _journal,
                roleOf: roleOf == null ? null : actor => actor.StartsWith("client:", StringComparison.Ordinal) && ulong.TryParse(actor.Substring(7), out ulong id) ? roleOf(id) : WorldRole.Visitor);
            foreach (ChunkCoord coord in info.RegionChunks())
            {
                _authority.AddChunk(_store.LoadChunk(coord));
            }

            _authority.RememberRecovered(_store.RecoveredRecords);
            _collapse = new CollapseScheduler(_authority, new AuthorityStructureView(_authority));
            _authority.IsBlocked = _collapse.IsBlocked;
            foreach (string problem in _store.Problems)
            {
                Debug.LogError(LogEvent.Format("world_recovery_problem", ("problem", problem)));
            }

            Debug.Log(LogEvent.Format("world_recovered",
                ("dir", directory), ("records", _store.RecoveredRecords.Count), ("truncatedBytes", _store.TruncatedBytes),
                ("rejectedSnapshots", _store.RejectedSnapshots), ("chunks", CountRegion())));

            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.Edit, OnEdit);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(WorldMessages.SnapshotRequest, OnSnapshotRequest);
        }

        public long Accepted => _authority.Accepted;

        public long Rejected => _authority.Rejected;

        public void OnClientConnected(ulong clientId)
        {
            WorldMessages.Send(_manager, WorldMessages.Info, clientId, _info.Encode(), NetworkDelivery.ReliableSequenced);
            var queue = new Queue<ChunkCoord>(_info.RegionChunks());
            _pendingSnapshots[clientId] = queue;
        }

        public void OnClientDisconnected(ulong clientId)
        {
            _pendingSnapshots.Remove(clientId);
            _requestRates.Remove(clientId);
        }

        /// <summary>Per tick: apply queued edits in arrival order, publish durable ones, stream snapshots within budget.</summary>
        public void Tick(uint serverTick, double now)
        {
            if (_journal.Failure != null)
            {
                Debug.LogError(LogEvent.Format("world_journal_failed", ("error", _journal.Failure.Message)));
            }

            while (_edits.Count > 0)
            {
                (ulong clientId, EditIntent intent) = _edits.Dequeue();
                Vector3? eye = _actorEye(clientId);
                if (eye == null)
                {
                    SendResult(clientId, intent.OperationId, EditRejection.OutOfReach, 0);
                    continue;
                }

                if (intent.Action == EditAction.Remove && _collapse.IsSaturated)
                {
                    // Structural analysis is behind: refuse new removals rather than queue unbounded work.
                    SendResult(clientId, intent.OperationId, EditRejection.ServerBusy, 0);
                    continue;
                }

                EditResult result = _authority.Submit($"client:{clientId}", intent, eye.Value.x, eye.Value.y, eye.Value.z, _occupants(), serverTick, now);
                if (result.Accepted && !result.IsDuplicate && intent.Action == EditAction.Remove)
                {
                    _collapse.Enqueue(ChunkMath.ToWorld(intent.Chunk, LocalBlock.FromIndex(intent.LocalIndex)));
                }

                if (!result.Accepted)
                {
                    SendResult(clientId, intent.OperationId, result.Rejection, 0);
                }
                else if (result.IsDuplicate)
                {
                    // Already durable from an earlier attempt: acknowledge again, never re-apply.
                    SendResult(clientId, intent.OperationId, EditRejection.None, result.Batch!.NewRevision);
                }
                else
                {
                    _awaitingDurable[intent.OperationId] = clientId;
                }
            }

            long collapsedBefore = _collapse.BlocksRemoved;
            CollapseTickStats collapse = _collapse.Tick(serverTick);
            if (collapse.Removed > 0)
            {
                Debug.Log(LogEvent.Format("world_collapse", ("removed", collapse.Removed), ("totalRemoved", collapsedBefore + collapse.Removed),
                    ("checks", collapse.Checks), ("activeJobs", collapse.ActiveJobs), ("pending", collapse.Pending)));
            }

            if (_journal.DrainDurable(_durable) > 0)
            {
                foreach (ChunkMutationBatch batch in _durable)
                {
                    // Commit protocol: publish and acknowledge only after the journal record is on disk.
                    WorldMessages.SendToAll(_manager, WorldMessages.Delta, JournalCodec.Encode(batch), NetworkDelivery.ReliableSequenced);
                    if (_awaitingDurable.Remove(batch.OperationId, out ulong actor))
                    {
                        SendResult(actor, batch.OperationId, EditRejection.None, batch.NewRevision);
                    }
                }
            }

            foreach (KeyValuePair<ulong, Queue<ChunkCoord>> pending in _pendingSnapshots)
            {
                for (int i = 0; i < SnapshotsPerClientPerTick && pending.Value.Count > 0; i++)
                {
                    SendSnapshot(pending.Key, pending.Value.Dequeue());
                }
            }
        }

        /// <summary>Graceful shutdown: drain and flush the journal, then log canonical chunk hashes.</summary>
        public void Dispose()
        {
            _journal.Dispose();
            foreach (ChunkCoord coord in _info.RegionChunks())
            {
                ChunkData chunk = _authority.GetChunk(coord)!;
                if (chunk.Revision > 0)
                {
                    Debug.Log(LogEvent.Format("world_chunk_hash", ("chunk", $"{coord.X},{coord.Y},{coord.Z}"), ("revision", chunk.Revision), ("hash", ChunkHash.Compute(chunk).ToString("X16"))));
                }
            }

            Debug.Log(LogEvent.Format("world_closed", ("accepted", _authority.Accepted), ("rejected", _authority.Rejected), ("durable", _journal.DurableCount),
                ("collapses", _collapse.Collapses), ("collapsedBlocks", _collapse.BlocksRemoved), ("collapseAbandoned", _collapse.Abandoned)));
        }

        public static string DefaultDirectory() =>
            Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), "voxelrush-world");

        private void OnEdit(ulong senderClientId, FastBufferReader reader)
        {
            if (WorldMessages.TryRead(reader, out byte[] payload) && EditWire.TryDecodeIntent(payload, out EditIntent intent))
            {
                // Queued and applied on the tick in arrival order (single writer).
                _edits.Enqueue((senderClientId, intent));
            }
        }

        private void OnSnapshotRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (!WorldMessages.TryRead(reader, out byte[] payload) || payload.Length != 12)
            {
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            (double start, int count) = _requestRates.TryGetValue(senderClientId, out var rate) ? rate : (now, 0);
            if (now - start >= 1)
            {
                (start, count) = (now, 0);
            }

            if (count >= SnapshotRequestsPerClientPerSecond)
            {
                return;
            }

            _requestRates[senderClientId] = (start, count + 1);
            var coord = new ChunkCoord(BitConverter.ToInt32(payload, 0), BitConverter.ToInt32(payload, 4), BitConverter.ToInt32(payload, 8));
            if (_authority.GetChunk(coord) != null)
            {
                SendSnapshot(senderClientId, coord);
            }
        }

        private void SendSnapshot(ulong clientId, ChunkCoord coord)
        {
            ChunkData? chunk = _authority.GetChunk(coord);
            if (chunk != null)
            {
                WorldMessages.Send(_manager, WorldMessages.Snapshot, clientId, ChunkSnapshotMessage.From(chunk).Encode(), NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void SendResult(ulong clientId, Guid operationId, EditRejection rejection, ulong newRevision) =>
            WorldMessages.Send(_manager, WorldMessages.EditResult, clientId, EditWire.EncodeResult(operationId, rejection, newRevision), NetworkDelivery.ReliableSequenced);

        private int CountRegion()
        {
            int count = 0;
            foreach (ChunkCoord _ in _info.RegionChunks())
            {
                count++;
            }

            return count;
        }
    }
}
