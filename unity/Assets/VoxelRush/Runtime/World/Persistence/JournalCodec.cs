using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.World.Persistence
{
    /// <summary>
    /// Journal record framing: [payload length u32][CRC-32 of payload u32][payload], little-endian.
    /// A crash mid-write leaves a short or CRC-failing tail, detected and discarded on recovery.
    /// </summary>
    public static class JournalCodec
    {
        public const byte RecordVersion = 1;
        public const int MaxPayloadBytes = 64 * 1024;
        private const int MaxActorIdBytes = 128;
        private const int MaxChanges = 4096;

        public static byte[] Encode(ChunkMutationBatch batch)
        {
            using var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(RecordVersion);
                w.Write(batch.OperationId.ToByteArray());
                byte[] actor = Encoding.UTF8.GetBytes(batch.ActorId);
                if (actor.Length > MaxActorIdBytes)
                {
                    throw new ArgumentException("Actor id too long.", nameof(batch));
                }

                w.Write((byte)actor.Length);
                w.Write(actor);
                w.Write(batch.Chunk.X);
                w.Write(batch.Chunk.Y);
                w.Write(batch.Chunk.Z);
                w.Write(batch.BaseRevision);
                w.Write(batch.NewRevision);
                w.Write(batch.ServerTick);
                w.Write((ushort)batch.Changes.Count);
                foreach (BlockChange change in batch.Changes)
                {
                    w.Write(change.Index);
                    w.Write(change.BlockType);
                }
            }

            byte[] body = payload.ToArray();
            var record = new byte[8 + body.Length];
            WriteUInt32(record, 0, (uint)body.Length);
            WriteUInt32(record, 4, Crc32.Compute(body, 0, body.Length));
            Buffer.BlockCopy(body, 0, record, 8, body.Length);
            return record;
        }

        /// <summary>
        /// Reads records until the end or the first incomplete/corrupt one. Returns the byte length of the valid prefix,
        /// which recovery truncates to.
        /// </summary>
        public static long ReadAll(Stream stream, List<ChunkMutationBatch> records)
        {
            var header = new byte[8];
            long valid = 0;
            while (true)
            {
                if (ReadExactly(stream, header, 8) < 8)
                {
                    return valid;
                }

                uint length = ReadUInt32(header, 0);
                uint crc = ReadUInt32(header, 4);
                if (length == 0 || length > MaxPayloadBytes)
                {
                    return valid;
                }

                var body = new byte[length];
                if (ReadExactly(stream, body, (int)length) < length || Crc32.Compute(body, 0, body.Length) != crc)
                {
                    return valid;
                }

                ChunkMutationBatch? batch = TryDecode(body);
                if (batch == null)
                {
                    return valid;
                }

                records.Add(batch);
                valid += 8 + length;
            }
        }

        private static ChunkMutationBatch? TryDecode(byte[] body)
        {
            try
            {
                using var r = new BinaryReader(new MemoryStream(body), Encoding.UTF8);
                if (r.ReadByte() != RecordVersion)
                {
                    return null;
                }

                var operationId = new Guid(r.ReadBytes(16));
                int actorLength = r.ReadByte();
                string actor = Encoding.UTF8.GetString(r.ReadBytes(actorLength));
                var chunk = new ChunkCoord(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                ulong baseRevision = r.ReadUInt64();
                ulong newRevision = r.ReadUInt64();
                uint tick = r.ReadUInt32();
                int count = r.ReadUInt16();
                if (count > MaxChanges || newRevision <= baseRevision)
                {
                    return null;
                }

                var changes = new BlockChange[count];
                for (int i = 0; i < count; i++)
                {
                    ushort index = r.ReadUInt16();
                    if (index >= ChunkMath.Volume)
                    {
                        return null;
                    }

                    changes[i] = new BlockChange(index, r.ReadByte());
                }

                return new ChunkMutationBatch(operationId, actor, chunk, baseRevision, newRevision, tick, changes);
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }

        private static int ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return read;
        }

        internal static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        internal static uint ReadUInt32(byte[] buffer, int offset) =>
            buffer[offset] | ((uint)buffer[offset + 1] << 8) | ((uint)buffer[offset + 2] << 16) | ((uint)buffer[offset + 3] << 24);
    }

    /// <summary>CRC-32 (IEEE 802.3, reflected 0xEDB88320).</summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
            {
                crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
