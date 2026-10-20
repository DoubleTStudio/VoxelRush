using System;
using System.IO;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Networking.World
{
    /// <summary>Binary layout of EditIntent (client → server) and its result (server → actor).</summary>
    internal static class EditWire
    {
        public static byte[] EncodeIntent(EditIntent intent)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream))
            {
                w.Write(intent.OperationId.ToByteArray());
                w.Write(intent.Chunk.X);
                w.Write(intent.Chunk.Y);
                w.Write(intent.Chunk.Z);
                w.Write((ushort)intent.LocalIndex);
                w.Write(intent.ExpectedRevision);
                w.Write((byte)intent.Action);
                w.Write(intent.BlockType);
                w.Write(intent.Orientation);
            }

            return stream.ToArray();
        }

        public static bool TryDecodeIntent(byte[] bytes, out EditIntent intent)
        {
            intent = default;
            try
            {
                using var r = new BinaryReader(new MemoryStream(bytes));
                var id = new Guid(r.ReadBytes(16));
                var chunk = new ChunkCoord(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                // Index stays as read (0..65535); WorldAuthority rejects anything outside 0..4095.
                int index = r.ReadUInt16();
                ulong expected = r.ReadUInt64();
                var action = (EditAction)r.ReadByte();
                byte block = r.ReadByte();
                byte orientation = r.ReadByte();
                intent = new EditIntent(id, chunk, index, expected, action, block, orientation);
                return true;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        public static byte[] EncodeResult(Guid operationId, EditRejection rejection, ulong newRevision)
        {
            var bytes = new byte[16 + 1 + 8];
            operationId.ToByteArray().CopyTo(bytes, 0);
            bytes[16] = (byte)rejection;
            BitConverter.GetBytes(newRevision).CopyTo(bytes, 17);
            return bytes;
        }

        public static bool TryDecodeResult(byte[] bytes, out Guid operationId, out EditRejection rejection, out ulong newRevision)
        {
            operationId = default;
            rejection = default;
            newRevision = 0;
            if (bytes.Length != 25)
            {
                return false;
            }

            var id = new byte[16];
            Array.Copy(bytes, id, 16);
            operationId = new Guid(id);
            rejection = (EditRejection)bytes[16];
            newRevision = BitConverter.ToUInt64(bytes, 17);
            return true;
        }
    }
}
