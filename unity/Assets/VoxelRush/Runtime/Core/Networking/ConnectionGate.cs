using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Networking
{
    public enum ConnectionRejection
    {
        None,
        MalformedPayload,
        ProtocolMismatch,
        ServerFull,
        /// <summary>Another connection holds this session (a duplicate or an unclean reconnect; retry after it times out).</summary>
        SessionInUse,
        /// <summary>This server only admits API-issued join tickets.</summary>
        TicketRequired,
        TicketInvalid,
        TicketExpired,
        TicketReplayed,
        TicketWrongWorld,
    }

    /// <summary>
    /// Server-side admission: validates the payload and reserves a player slot per approved client.
    /// Slots are held from approval until disconnect so simultaneous approvals cannot exceed the cap.
    /// </summary>
    public sealed class ConnectionGate
    {
        private readonly Dictionary<ulong, Slot> _slots = new Dictionary<ulong, Slot>();
        private readonly TicketVerifier? _tickets;
        private readonly Func<long> _unixNow;

        /// <param name="tickets">When set, only valid join tickets for this server's world are admitted (VR-080).</param>
        public ConnectionGate(int maxPlayers, ushort protocolVersion, double pendingTimeoutSeconds = 10, TicketVerifier? tickets = null, Func<long>? unixNow = null)
        {
            if (maxPlayers < 1 || maxPlayers > SimulationConstants.MaxPlayersPerServer)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPlayers));
            }

            if (pendingTimeoutSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pendingTimeoutSeconds));
            }

            MaxPlayers = maxPlayers;
            ProtocolVersion = protocolVersion;
            PendingTimeoutSeconds = pendingTimeoutSeconds;
            _tickets = tickets;
            _unixNow = unixNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        public bool RequiresTickets => _tickets != null;

        public int MaxPlayers { get; }

        public ushort ProtocolVersion { get; }

        public double PendingTimeoutSeconds { get; }

        /// <summary>Approved clients, pending or connected; this is what the cap applies to.</summary>
        public int Count => _slots.Count;

        /// <summary>Clients that completed the handshake.</summary>
        public int ConnectedCount
        {
            get
            {
                int connected = 0;
                foreach (Slot slot in _slots.Values)
                {
                    if (slot.Connected)
                    {
                        connected++;
                    }
                }

                return connected;
            }
        }

        public ConnectionRejection TryAdmit(ulong clientId, byte[]? payload, double nowSeconds)
        {
            if (!ConnectionPayload.TryDecode(payload, out ushort version, out PayloadKind kind, out Guid session, out byte[]? ticketBytes))
            {
                return ConnectionRejection.MalformedPayload;
            }

            if (version != ProtocolVersion)
            {
                return ConnectionRejection.ProtocolMismatch;
            }

            // Development servers keep self-chosen sessions as editors (the open test sandbox).
            WorldAccessRole role = WorldAccessRole.Editor;
            if (_tickets != null)
            {
                if (kind != PayloadKind.Ticket)
                {
                    return ConnectionRejection.TicketRequired;
                }

                TicketRejection verdict = _tickets.Verify(ticketBytes, _unixNow(), out JoinTicket ticket);
                if (verdict != TicketRejection.None)
                {
                    return verdict switch
                    {
                        TicketRejection.Expired or TicketRejection.NotYetValid => ConnectionRejection.TicketExpired,
                        TicketRejection.Replayed => ConnectionRejection.TicketReplayed,
                        TicketRejection.WrongWorld => ConnectionRejection.TicketWrongWorld,
                        _ => ConnectionRejection.TicketInvalid,
                    };
                }

                // The player id is stable across tickets, so races and edits resume by player, not by connection.
                session = ticket.PlayerId;
                role = ticket.Role;
            }
            else if (kind != PayloadKind.Session)
            {
                return ConnectionRejection.MalformedPayload;
            }

            if (session == Guid.Empty)
            {
                return ConnectionRejection.ProtocolMismatch;
            }

            // A repeated request for an already admitted id keeps its slot rather than taking a second one.
            if (_slots.TryGetValue(clientId, out Slot existing))
            {
                return existing.Session == session ? ConnectionRejection.None : ConnectionRejection.SessionInUse;
            }

            foreach (Slot slot in _slots.Values)
            {
                if (slot.Session == session)
                {
                    return ConnectionRejection.SessionInUse;
                }
            }

            if (_slots.Count >= MaxPlayers)
            {
                return ConnectionRejection.ServerFull;
            }

            _slots.Add(clientId, new Slot(nowSeconds, session, role));
            return ConnectionRejection.None;
        }

        /// <summary>False when the client holds no slot (e.g. it expired while pending); the caller must drop it.</summary>
        public bool MarkConnected(ulong clientId)
        {
            if (!_slots.TryGetValue(clientId, out Slot slot))
            {
                return false;
            }

            _slots[clientId] = slot.AsConnected();
            return true;
        }

        public bool Release(ulong clientId) => _slots.Remove(clientId);

        /// <summary>The session id an admitted client presented (its player id with tickets), or empty.</summary>
        public Guid SessionOf(ulong clientId) => _slots.TryGetValue(clientId, out Slot slot) ? slot.Session : Guid.Empty;

        /// <summary>The client's role in this world (from its ticket; editor on development servers); visitor if unknown.</summary>
        public WorldAccessRole RoleOf(ulong clientId) => _slots.TryGetValue(clientId, out Slot slot) ? slot.Role : WorldAccessRole.Visitor;

        /// <summary>Frees slots approved but never connected within the timeout (half-open handshakes).</summary>
        public int ExpirePending(double nowSeconds, List<ulong>? expired = null)
        {
            List<ulong>? stale = null;
            foreach (KeyValuePair<ulong, Slot> entry in _slots)
            {
                if (!entry.Value.Connected && nowSeconds - entry.Value.AdmittedAtSeconds >= PendingTimeoutSeconds)
                {
                    (stale ??= new List<ulong>()).Add(entry.Key);
                }
            }

            if (stale == null)
            {
                return 0;
            }

            foreach (ulong clientId in stale)
            {
                _slots.Remove(clientId);
                expired?.Add(clientId);
            }

            return stale.Count;
        }

        private readonly struct Slot
        {
            public Slot(double admittedAtSeconds, Guid session, WorldAccessRole role, bool connected = false)
            {
                AdmittedAtSeconds = admittedAtSeconds;
                Session = session;
                Role = role;
                Connected = connected;
            }

            public double AdmittedAtSeconds { get; }

            public Guid Session { get; }

            public WorldAccessRole Role { get; }

            public bool Connected { get; }

            public Slot AsConnected() => new Slot(AdmittedAtSeconds, Session, Role, true);
        }
    }

    public static class ConnectionRejectionExtensions
    {
        /// <summary>Stable reason codes sent to clients and logs.</summary>
        public static string ToReasonCode(this ConnectionRejection rejection) => rejection switch
        {
            ConnectionRejection.None => "none",
            ConnectionRejection.MalformedPayload => "malformed_payload",
            ConnectionRejection.ProtocolMismatch => "protocol_mismatch",
            ConnectionRejection.ServerFull => "server_full",
            ConnectionRejection.SessionInUse => "session_in_use",
            ConnectionRejection.TicketRequired => "ticket_required",
            ConnectionRejection.TicketInvalid => "ticket_invalid",
            ConnectionRejection.TicketExpired => "ticket_expired",
            ConnectionRejection.TicketReplayed => "ticket_replayed",
            ConnectionRejection.TicketWrongWorld => "ticket_wrong_world",
            _ => throw new ArgumentOutOfRangeException(nameof(rejection)),
        };
    }
}
