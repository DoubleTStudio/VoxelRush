using System;

namespace VoxelRush.Core.Networking
{
    /// <summary>Connection request payload sent by clients during approval: protocol version, little-endian.</summary>
    public static class ConnectionPayload
    {
        public const int Length = 2;

        public static byte[] Encode(ushort protocolVersion) =>
            new[] { (byte)(protocolVersion & 0xFF), (byte)(protocolVersion >> 8) };

        public static bool TryDecode(byte[]? payload, out ushort protocolVersion)
        {
            // Exact length only: anything else is malformed, never partially trusted.
            if (payload == null || payload.Length != Length)
            {
                protocolVersion = 0;
                return false;
            }

            protocolVersion = (ushort)(payload[0] | (payload[1] << 8));
            return true;
        }
    }
}
