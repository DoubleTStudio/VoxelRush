using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core;
using VoxelRush.Core.Networking;

namespace VoxelRush.Networking
{
    /// <summary>Authoritative server listener with protocol/capacity admission via <see cref="ConnectionGate"/>.</summary>
    public sealed class ServerNetwork : IDisposable
    {
        public const string HandshakeTimeoutReason = "handshake_timeout";

        private readonly NetworkManager _manager;
        private readonly ConnectionGate _gate;
        private readonly List<ulong> _expired = new List<ulong>();
        private bool _disposed;

        private ServerNetwork(NetworkManager manager, ConnectionGate gate)
        {
            _manager = manager;
            _gate = gate;
            _manager.ConnectionApprovalCallback = Approve;
            _manager.OnClientConnectedCallback += HandleConnected;
            _manager.OnClientDisconnectCallback += HandleDisconnected;
        }

        /// <summary>Raised with the client id and the number of connected players.</summary>
        public event Action<ulong, int>? ClientConnected;

        /// <summary>Raised with the client id and the number of connected players.</summary>
        public event Action<ulong, int>? ClientDisconnected;

        public event Action<ulong, ConnectionRejection>? ClientRejected;

        public event Action<ulong>? PendingClientExpired;

        public int PlayerCount => _gate.ConnectedCount;

        /// <summary>Creates the world sync server on this listener (keeps NGO types inside the Networking assembly).</summary>
        public World.WorldSyncServer CreateWorldSync(
            VoxelRush.World.Sync.WorldInfo info, string directory, Func<ulong, UnityEngine.Vector3?> actorEye,
            Func<IReadOnlyList<VoxelRush.World.Editing.WorldBox>> occupants) =>
            new World.WorldSyncServer(_manager, info, directory, actorEye, occupants, RoleOf, visitorsCanBuild: !_gate.RequiresTickets);

        /// <summary>A connected client's world role: from its join ticket, or editor on development servers.</summary>
        public VoxelRush.World.Editing.WorldRole RoleOf(ulong clientId) => _gate.RoleOf(clientId) switch
        {
            WorldAccessRole.Owner => VoxelRush.World.Editing.WorldRole.Owner,
            WorldAccessRole.Editor => VoxelRush.World.Editing.WorldRole.Editor,
            _ => VoxelRush.World.Editing.WorldRole.Visitor,
        };

        /// <summary>Race lobby and flow on this listener.</summary>
        public Race.RaceServer CreateRace(VoxelRush.Core.Race.TrackDefinition track, Vehicles.VehicleSpawner vehicles, int? autoStart) =>
            new Race.RaceServer(_manager, track, vehicles, autoStart);

        /// <summary>The session id a connected client presented (VR-062); empty if unknown.</summary>
        public Guid SessionOf(ulong clientId) => _gate.SessionOf(clientId);

        /// <param name="tickets">When set, only valid join tickets are admitted (VR-080).</param>
        public static ServerNetwork? Start(ushort port, int maxPlayers, TicketVerifier? tickets = null)
        {
            NetworkManager manager = NetworkRuntime.Create("ServerNetwork");
            NetworkRuntime.Transport(manager).SetConnectionData("0.0.0.0", port, "0.0.0.0");
            var server = new ServerNetwork(manager, new ConnectionGate(maxPlayers, SimulationConstants.ProtocolVersion, tickets: tickets));
            if (manager.StartServer())
            {
                return server;
            }

            server.Dispose();
            return null;
        }

        /// <summary>Call once per tick; frees slots of clients that were approved but never finished connecting.</summary>
        public void Tick(double nowSeconds)
        {
            _expired.Clear();
            if (_gate.ExpirePending(nowSeconds, _expired) == 0)
            {
                return;
            }

            foreach (ulong clientId in _expired)
            {
                PendingClientExpired?.Invoke(clientId);
            }
        }

        public void Dispose()
        {
            // NGO types the callback as non-nullable, but clearing it on dispose is intended.
            _manager.ConnectionApprovalCallback = null!;
            _manager.OnClientConnectedCallback -= HandleConnected;
            _manager.OnClientDisconnectCallback -= HandleDisconnected;
            if (_manager.IsListening)
            {
                _manager.Shutdown();
            }

            UnityEngine.Object.Destroy(_manager.gameObject);
        }

        private void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            ConnectionRejection rejection = _gate.TryAdmit(request.ClientNetworkId, request.Payload, Time.realtimeSinceStartupAsDouble);
            response.Approved = rejection == ConnectionRejection.None;
            response.Reason = rejection.ToReasonCode();
            // Vehicles are spawned explicitly by gameplay (VR-012), never implicitly on connect.
            response.CreatePlayerObject = false;
            response.Pending = false;

            if (!response.Approved)
            {
                ClientRejected?.Invoke(request.ClientNetworkId, rejection);
            }
        }

        private void HandleConnected(ulong clientId)
        {
            if (!_gate.MarkConnected(clientId))
            {
                // Its pending slot already expired; keep NGO's view and the player cap consistent.
                _manager.DisconnectClient(clientId, HandshakeTimeoutReason);
                return;
            }

            ClientConnected?.Invoke(clientId, _gate.ConnectedCount);
        }

        private void HandleDisconnected(ulong clientId)
        {
            if (_gate.Release(clientId))
            {
                ClientDisconnected?.Invoke(clientId, _gate.ConnectedCount);
            }
        }
    }
}
