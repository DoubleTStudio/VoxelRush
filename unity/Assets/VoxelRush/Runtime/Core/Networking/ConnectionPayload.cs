using System;

namespace VoxelRush.Core.Networking
{
    /// <summary>
    /// Connection request payload sent by clients during approval: protocol version (uint16, little-endian) and, from
    /// protocol 2, a 16-byte client session id that survives reconnects (VR-062). Not authentication: join tickets
    /// replace the self-chosen id in VR-080.
    /// </summary>
    public static class ConnectionPayload
    {
        public const int Length = 18;

        /// <summary>Protocol 1 sent only the version; decoded so old clients get "protocol_mismatch", not "malformed".</summary>
        public const int LegacyLength = 2;

        public static byte[] Encode(ushort protocolVersion, Guid session)
        {
            var payload = new byte[Length];
            payload[0] = (byte)(protocolVersion & 0xFF);
            payload[1] = (byte)(protocolVersion >> 8);
            session.ToByteArray().CopyTo(payload, 2);
            return payload;
        }

        public static bool TryDecode(byte[]? payload, out ushort protocolVersion, out Guid session)
        {
            protocolVersion = 0;
            session = Guid.Empty;
            // Exact lengths only: anything else is malformed, never partially trusted.
            if (payload == null || (payload.Length != Length && payload.Length != LegacyLength))
            {
                return false;
            }

            protocolVersion = (ushort)(payload[0] | (payload[1] << 8));
            if (payload.Length == Length)
            {
                var bytes = new byte[16];
                Array.Copy(payload, 2, bytes, 0, 16);
                session = new Guid(bytes);
            }

            return true;
        }
    }
}
