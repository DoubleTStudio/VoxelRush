using System;

namespace VoxelRush.Core.Networking
{
    public enum PayloadKind : byte
    {
        /// <summary>Development: a client-chosen session id (ADR-017). Refused by servers that require tickets.</summary>
        Session = 0,
        /// <summary>A signed join ticket from the API (VR-080).</summary>
        Ticket = 1,
    }

    /// <summary>
    /// Connection request payload sent by clients during approval. Protocol 3: version (uint16 LE), kind (uint8),
    /// then a 16-byte session id or a <see cref="JoinTicket"/>. Older layouts (protocol 1: version only; protocol 2:
    /// version + session) still decode far enough to be refused as a version mismatch rather than malformed.
    /// </summary>
    public static class ConnectionPayload
    {
        public const int SessionLength = 2 + 1 + 16;
        public const int TicketLength = 2 + 1 + JoinTicket.Length;
        private const int LegacyV1Length = 2;
        private const int LegacyV2Length = 18;

        public static byte[] Encode(ushort protocolVersion, Guid session)
        {
            var payload = new byte[SessionLength];
            WriteHeader(payload, protocolVersion, PayloadKind.Session);
            session.ToByteArray().CopyTo(payload, 3);
            return payload;
        }

        public static byte[] EncodeTicket(ushort protocolVersion, byte[] ticket)
        {
            if (ticket == null || ticket.Length != JoinTicket.Length)
            {
                throw new ArgumentException("Not a join ticket.", nameof(ticket));
            }

            var payload = new byte[TicketLength];
            WriteHeader(payload, protocolVersion, PayloadKind.Ticket);
            ticket.CopyTo(payload, 3);
            return payload;
        }

        public static bool TryDecode(byte[]? payload, out ushort protocolVersion, out PayloadKind kind, out Guid session, out byte[]? ticket)
        {
            protocolVersion = 0;
            kind = PayloadKind.Session;
            session = Guid.Empty;
            ticket = null;
            // Exact lengths only: anything else is malformed, never partially trusted.
            if (payload == null || payload.Length < 2)
            {
                return false;
            }

            protocolVersion = (ushort)(payload[0] | (payload[1] << 8));
            if (payload.Length == LegacyV1Length || payload.Length == LegacyV2Length)
            {
                return protocolVersion < 3;
            }

            if (payload.Length == SessionLength && payload[2] == (byte)PayloadKind.Session)
            {
                var bytes = new byte[16];
                Array.Copy(payload, 3, bytes, 0, 16);
                session = new Guid(bytes);
                return true;
            }

            if (payload.Length == TicketLength && payload[2] == (byte)PayloadKind.Ticket)
            {
                kind = PayloadKind.Ticket;
                ticket = new byte[JoinTicket.Length];
                Array.Copy(payload, 3, ticket, 0, JoinTicket.Length);
                return true;
            }

            return false;
        }

        private static void WriteHeader(byte[] payload, ushort version, PayloadKind kind)
        {
            payload[0] = (byte)(version & 0xFF);
            payload[1] = (byte)(version >> 8);
            payload[2] = (byte)kind;
        }
    }
}
