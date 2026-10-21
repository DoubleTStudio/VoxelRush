using System;
using System.IO;

namespace VoxelRush.Core.Race
{
    /// <summary>CONTRACTS RaceState {raceId, trackRevision, state, serverStartTick, orderedCheckpointIds}; reliable.</summary>
    public sealed class RaceStateMessage
    {
        public const int MaxCheckpoints = 64;

        public RaceStateMessage(Guid raceId, ulong trackRevision, RacePhase state, uint serverStartTick, int[] orderedCheckpointIds)
        {
            RaceId = raceId;
            TrackRevision = trackRevision;
            State = state;
            ServerStartTick = serverStartTick;
            OrderedCheckpointIds = orderedCheckpointIds;
        }

        public Guid RaceId { get; }

        public ulong TrackRevision { get; }

        public RacePhase State { get; }

        public uint ServerStartTick { get; }

        public int[] OrderedCheckpointIds { get; }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream))
            {
                w.Write(RaceId.ToByteArray());
                w.Write(TrackRevision);
                w.Write((byte)State);
                w.Write(ServerStartTick);
                w.Write((byte)OrderedCheckpointIds.Length);
                foreach (int id in OrderedCheckpointIds)
                {
                    w.Write(id);
                }
            }

            return stream.ToArray();
        }

        public static bool TryDecode(byte[] payload, out RaceStateMessage? message)
        {
            message = null;
            // 16 + 8 + 1 + 4 + 1 header bytes, then 4 per checkpoint.
            if (payload == null || payload.Length < 30)
            {
                return false;
            }

            int count = payload[29];
            if (count > MaxCheckpoints || payload.Length != 30 + count * 4 || payload[24] > (byte)RacePhase.Reset)
            {
                return false;
            }

            using var r = new BinaryReader(new MemoryStream(payload));
            var raceId = new Guid(r.ReadBytes(16));
            ulong revision = r.ReadUInt64();
            var state = (RacePhase)r.ReadByte();
            uint start = r.ReadUInt32();
            r.ReadByte();
            var ids = new int[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = r.ReadInt32();
            }

            message = new RaceStateMessage(raceId, revision, state, start, ids);
            return true;
        }
    }
}
