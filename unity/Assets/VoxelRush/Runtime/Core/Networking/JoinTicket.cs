using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace VoxelRush.Core.Networking
{
    public enum WorldAccessRole : byte
    {
        Visitor = 0,
        Editor = 1,
        Owner = 2,
    }

    public enum TicketRejection : byte
    {
        None,
        Malformed,
        BadSignature,
        Expired,
        NotYetValid,
        WrongWorld,
        Replayed,
    }

    /// <summary>
    /// Signed, short-lived permission for one player to join one world (PERSISTENCE_SECURITY: signed short-lived
    /// tickets, per-session nonce). Issued by the API, verified by the game server with a shared HMAC key that never
    /// reaches clients. Binary layout (98 bytes): version, ticket id (nonce), player id, world id, role, issued-at,
    /// expires-at (Unix seconds, little-endian), HMAC-SHA256 over the first 66 bytes.
    /// </summary>
    public readonly struct JoinTicket
    {
        public const byte Version = 1;
        public const int BodyLength = 1 + 16 + 16 + 16 + 1 + 8 + 8;
        public const int Length = BodyLength + 32;
        public const int MinKeyBytes = 32;

        public JoinTicket(Guid ticketId, Guid playerId, Guid worldId, WorldAccessRole role, long issuedAt, long expiresAt)
        {
            TicketId = ticketId;
            PlayerId = playerId;
            WorldId = worldId;
            Role = role;
            IssuedAt = issuedAt;
            ExpiresAt = expiresAt;
        }

        public Guid TicketId { get; }

        public Guid PlayerId { get; }

        public Guid WorldId { get; }

        public WorldAccessRole Role { get; }

        public long IssuedAt { get; }

        public long ExpiresAt { get; }

        public byte[] Sign(byte[] key)
        {
            RequireKey(key);
            var bytes = new byte[Length];
            bytes[0] = Version;
            TicketId.ToByteArray().CopyTo(bytes, 1);
            PlayerId.ToByteArray().CopyTo(bytes, 17);
            WorldId.ToByteArray().CopyTo(bytes, 33);
            bytes[49] = (byte)Role;
            WriteInt64(bytes, 50, IssuedAt);
            WriteInt64(bytes, 58, ExpiresAt);
            using (var hmac = new HMACSHA256(key))
            {
                hmac.ComputeHash(bytes, 0, BodyLength).CopyTo(bytes, BodyLength);
            }

            return bytes;
        }

        /// <summary>Signature, version, role and time window checks (no world or replay check; see <see cref="TicketVerifier"/>).</summary>
        public static TicketRejection TryVerify(byte[]? bytes, byte[] key, long now, out JoinTicket ticket, long clockSkewSeconds = 30)
        {
            ticket = default;
            RequireKey(key);
            if (bytes == null || bytes.Length != Length || bytes[0] != Version)
            {
                return TicketRejection.Malformed;
            }

            byte[] expected;
            using (var hmac = new HMACSHA256(key))
            {
                expected = hmac.ComputeHash(bytes, 0, BodyLength);
            }

            if (!FixedTimeEquals(expected, bytes, BodyLength))
            {
                return TicketRejection.BadSignature;
            }

            if (bytes[49] > (byte)WorldAccessRole.Owner)
            {
                return TicketRejection.Malformed;
            }

            ticket = new JoinTicket(Read(bytes, 1), Read(bytes, 17), Read(bytes, 33), (WorldAccessRole)bytes[49], ReadInt64(bytes, 50), ReadInt64(bytes, 58));
            if (ticket.IssuedAt > now + clockSkewSeconds)
            {
                return TicketRejection.NotYetValid;
            }

            return now > ticket.ExpiresAt + clockSkewSeconds ? TicketRejection.Expired : TicketRejection.None;
        }

        private static void RequireKey(byte[] key)
        {
            if (key == null || key.Length < MinKeyBytes)
            {
                throw new ArgumentException($"Ticket keys must be at least {MinKeyBytes} bytes.", nameof(key));
            }
        }

        private static bool FixedTimeEquals(byte[] expected, byte[] actual, int offset)
        {
            int diff = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                diff |= expected[i] ^ actual[offset + i];
            }

            return diff == 0;
        }

        private static Guid Read(byte[] bytes, int offset)
        {
            var g = new byte[16];
            Array.Copy(bytes, offset, g, 0, 16);
            return new Guid(g);
        }

        private static void WriteInt64(byte[] bytes, int offset, long value)
        {
            for (int i = 0; i < 8; i++)
            {
                bytes[offset + i] = (byte)(value >> (8 * i));
            }
        }

        private static long ReadInt64(byte[] bytes, int offset)
        {
            long value = 0;
            for (int i = 0; i < 8; i++)
            {
                value |= (long)bytes[offset + i] << (8 * i);
            }

            return value;
        }
    }

    /// <summary>
    /// Game-server side: verifies tickets for this server's world and remembers used ticket ids until they expire, so
    /// a captured ticket can't be replayed (memory is bounded by issue rate × lifetime).
    /// </summary>
    public sealed class TicketVerifier
    {
        private readonly byte[] _key;
        private readonly Guid _worldId;
        private readonly Dictionary<Guid, long> _used = new Dictionary<Guid, long>();
        private readonly List<Guid> _expired = new List<Guid>();

        public TicketVerifier(byte[] key, Guid worldId)
        {
            _key = (byte[])key.Clone();
            _worldId = worldId;
        }

        public int Remembered => _used.Count;

        public TicketRejection Verify(byte[]? bytes, long now, out JoinTicket ticket)
        {
            Forget(now);
            TicketRejection result = JoinTicket.TryVerify(bytes, _key, now, out ticket);
            if (result != TicketRejection.None)
            {
                return result;
            }

            if (ticket.WorldId != _worldId)
            {
                return TicketRejection.WrongWorld;
            }

            if (_used.ContainsKey(ticket.TicketId))
            {
                return TicketRejection.Replayed;
            }

            _used[ticket.TicketId] = ticket.ExpiresAt;
            return TicketRejection.None;
        }

        private void Forget(long now)
        {
            _expired.Clear();
            foreach (KeyValuePair<Guid, long> entry in _used)
            {
                // Keep past expiry + skew so a ticket accepted near its end can't be replayed in the skew window.
                if (now > entry.Value + 60)
                {
                    _expired.Add(entry.Key);
                }
            }

            foreach (Guid id in _expired)
            {
                _used.Remove(id);
            }
        }
    }
}
