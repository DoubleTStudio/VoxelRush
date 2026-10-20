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
    }

    /// <summary>
    /// Server-side admission: validates the payload and reserves a player slot per approved client.
    /// Slots are held from approval until disconnect so simultaneous approvals cannot exceed the cap.
    /// </summary>
    public sealed class ConnectionGate
    {
        private readonly Dictionary<ulong, Slot> _slots = new Dictionary<ulong, Slot>();

        public ConnectionGate(int maxPlayers, ushort protocolVersion, double pendingTimeoutSeconds = 10)
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
        }

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
            if (!ConnectionPayload.TryDecode(payload, out ushort version))
            {
                return ConnectionRejection.MalformedPayload;
            }

            if (version != ProtocolVersion)
            {
                return ConnectionRejection.ProtocolMismatch;
            }

            // A repeated request for an already admitted id keeps its slot rather than taking a second one.
            if (_slots.ContainsKey(clientId))
            {
                return ConnectionRejection.None;
            }

            if (_slots.Count >= MaxPlayers)
            {
                return ConnectionRejection.ServerFull;
            }

            _slots.Add(clientId, new Slot(nowSeconds));
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
            public Slot(double admittedAtSeconds, bool connected = false)
            {
                AdmittedAtSeconds = admittedAtSeconds;
                Connected = connected;
            }

            public double AdmittedAtSeconds { get; }

            public bool Connected { get; }

            public Slot AsConnected() => new Slot(AdmittedAtSeconds, true);
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
            _ => throw new ArgumentOutOfRangeException(nameof(rejection)),
        };
    }
}
