using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VoxelRush.Core.Race
{
    /// <summary>Track geometry for clients (HUD direction, bots). Bounds-checked like every wire message.</summary>
    public static class TrackMessage
    {
        public const int MaxGates = RaceStateMessage.MaxCheckpoints;

        public static byte[] Encode(TrackDefinition track)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream))
            {
                w.Write(track.TrackRevision);
                w.Write((byte)track.Laps);
                w.Write(track.MaxPlausibleSpeed);
                w.Write(track.Start.X);
                w.Write(track.Start.Y);
                w.Write(track.Start.Z);
                w.Write((byte)track.Gates.Count);
                foreach (Gate g in track.Gates)
                {
                    w.Write(g.Id);
                    w.Write(g.X);
                    w.Write(g.Y);
                    w.Write(g.Z);
                    w.Write(g.NormalX);
                    w.Write(g.NormalZ);
                    w.Write(g.HalfWidth);
                    w.Write(g.HalfHeight);
                }
            }

            return stream.ToArray();
        }

        public static bool TryDecode(byte[] payload, out TrackDefinition? track)
        {
            track = null;
            const int header = 8 + 1 + 4 + 12 + 1, perGate = 4 + 4 * 7;
            if (payload == null || payload.Length < header)
            {
                return false;
            }

            int count = payload[header - 1];
            if (count < 2 || count > MaxGates || payload.Length != header + count * perGate)
            {
                return false;
            }

            try
            {
                using var r = new BinaryReader(new MemoryStream(payload));
                ulong revision = r.ReadUInt64();
                int laps = r.ReadByte();
                float maxSpeed = r.ReadSingle();
                var start = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                r.ReadByte();
                var gates = new Gate[count];
                for (int i = 0; i < count; i++)
                {
                    gates[i] = new Gate(r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }

                track = new TrackDefinition(revision, start, gates, laps, maxSpeed);
                return true;
            }
            catch (ArgumentException)
            {
                // Validation failed in TrackDefinition (duplicate ids, zero normals, NaN...).
                return false;
            }
        }
    }

    /// <summary>Per-racer progress, sent to that racer: its display name, gates done, total, and the last outcome.</summary>
    public readonly struct RaceProgressMessage
    {
        public const int MaxNameLength = 32;

        public RaceProgressMessage(string racerName, int crossings, int totalCrossings, CrossingOutcome lastOutcome)
        {
            RacerName = racerName;
            Crossings = crossings;
            TotalCrossings = totalCrossings;
            LastOutcome = lastOutcome;
        }

        public string RacerName { get; }

        public int Crossings { get; }

        public int TotalCrossings { get; }

        public CrossingOutcome LastOutcome { get; }

        public byte[] Encode()
        {
            byte[] name = RaceResultsMessage.NameBytes(RacerName);
            var payload = new byte[1 + name.Length + 5];
            payload[0] = (byte)name.Length;
            name.CopyTo(payload, 1);
            int o = 1 + name.Length;
            payload[o] = (byte)Crossings;
            payload[o + 1] = (byte)(Crossings >> 8);
            payload[o + 2] = (byte)TotalCrossings;
            payload[o + 3] = (byte)(TotalCrossings >> 8);
            payload[o + 4] = (byte)LastOutcome;
            return payload;
        }

        public static bool TryDecode(byte[] payload, out RaceProgressMessage message)
        {
            message = default;
            if (payload == null || payload.Length < 6 || payload[0] > MaxNameLength || payload.Length != 1 + payload[0] + 5)
            {
                return false;
            }

            int o = 1 + payload[0];
            int crossings = payload[o] | (payload[o + 1] << 8), total = payload[o + 2] | (payload[o + 3] << 8);
            if (payload[o + 4] > (byte)CrossingOutcome.InvalidPosition || crossings > total)
            {
                return false;
            }

            message = new RaceProgressMessage(Encoding.UTF8.GetString(payload, 1, payload[0]), crossings, total, (CrossingOutcome)payload[o + 4]);
            return true;
        }
    }

    /// <summary>Final standings, broadcast when the race enters Results. Names are display names, never session ids.</summary>
    public sealed class RaceResultsMessage
    {
        public RaceResultsMessage(Guid raceId, IReadOnlyList<(string Name, RaceResult Result)> entries)
        {
            RaceId = raceId;
            Entries = entries;
        }

        public Guid RaceId { get; }

        public IReadOnlyList<(string Name, RaceResult Result)> Entries { get; }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream))
            {
                w.Write(RaceId.ToByteArray());
                w.Write((byte)Entries.Count);
                foreach ((string name, RaceResult result) in Entries)
                {
                    byte[] bytes = NameBytes(name);
                    w.Write((byte)bytes.Length);
                    w.Write(bytes);
                    w.Write((byte)result.Place);
                    w.Write(result.Finished);
                    w.Write(result.RaceTicks);
                    w.Write((ushort)result.Crossings);
                }
            }

            return stream.ToArray();
        }

        public static bool TryDecode(byte[] payload, out RaceResultsMessage? message)
        {
            message = null;
            if (payload == null || payload.Length < 17 || payload[16] > SimulationConstants.MaxPlayersPerServer)
            {
                return false;
            }

            try
            {
                using var r = new BinaryReader(new MemoryStream(payload));
                var raceId = new Guid(r.ReadBytes(16));
                int count = r.ReadByte();
                var entries = new List<(string, RaceResult)>(count);
                for (int i = 0; i < count; i++)
                {
                    int length = r.ReadByte();
                    if (length > RaceProgressMessage.MaxNameLength)
                    {
                        return false;
                    }

                    string name = Encoding.UTF8.GetString(r.ReadBytes(length));
                    int place = r.ReadByte();
                    bool finished = r.ReadBoolean();
                    uint ticks = r.ReadUInt32();
                    int crossings = r.ReadUInt16();
                    entries.Add((name, new RaceResult(name, place, finished, ticks, crossings)));
                }

                if (r.BaseStream.Position != payload.Length)
                {
                    return false;
                }

                message = new RaceResultsMessage(raceId, entries);
                return true;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        internal static byte[] NameBytes(string name)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
            if (bytes.Length <= RaceProgressMessage.MaxNameLength)
            {
                return bytes;
            }

            // Truncate on a character boundary.
            string s = name!;
            while (Encoding.UTF8.GetByteCount(s) > RaceProgressMessage.MaxNameLength)
            {
                s = s.Substring(0, s.Length - 1);
            }

            return Encoding.UTF8.GetBytes(s);
        }
    }
}
