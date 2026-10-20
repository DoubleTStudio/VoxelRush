using System;
using Unity.Netcode;
using VoxelRush.Core;
using VoxelRush.Core.Hosting;
using VoxelRush.Core.Networking;

namespace VoxelRush.Networking
{
    /// <summary>Client connection to a dedicated server; sends the protocol handshake payload.</summary>
    public sealed class ClientNetwork : IDisposable
    {
        private readonly NetworkManager _manager;
        private bool _disposed;

        private ClientNetwork(NetworkManager manager)
        {
            _manager = manager;
            _manager.OnClientConnectedCallback += HandleConnected;
            _manager.OnClientDisconnectCallback += HandleDisconnected;
        }

        public event Action? Connected;

        /// <summary>Raised with the server's reason code (e.g. <c>server_full</c>), or empty for transport loss.</summary>
        public event Action<string>? Disconnected;

        public bool IsConnected => _manager.IsConnectedClient;

        /// <summary>True once NGO has finished shutting down (the disconnect has been sent).</summary>
        public bool IsShutdownComplete => _manager == null || (!_manager.IsListening && !_manager.ShutdownInProgress);

        /// <summary>Starts a graceful disconnect; NGO completes it on a later frame.</summary>
        public void BeginShutdown()
        {
            if (_manager != null && _manager.IsListening)
            {
                _manager.Shutdown();
            }
        }

        public static ClientNetwork? Connect(ServerEndpoint endpoint)
        {
            string address = NetworkRuntime.ResolveIPv4(endpoint.Host);
            NetworkManager manager = NetworkRuntime.Create("ClientNetwork");
            NetworkRuntime.Transport(manager).SetConnectionData(address, endpoint.Port);
            manager.NetworkConfig.ConnectionData = ConnectionPayload.Encode(SimulationConstants.ProtocolVersion);

            var client = new ClientNetwork(manager);
            if (manager.StartClient())
            {
                return client;
            }

            client.Dispose();
            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // At application exit Unity may already have destroyed the manager's GameObject.
            if (_manager == null)
            {
                return;
            }

            _manager.OnClientConnectedCallback -= HandleConnected;
            _manager.OnClientDisconnectCallback -= HandleDisconnected;
            if (_manager.IsListening)
            {
                _manager.Shutdown();
            }

            UnityEngine.Object.Destroy(_manager.gameObject);
        }

        private void HandleConnected(ulong clientId)
        {
            if (clientId == _manager.LocalClientId)
            {
                Connected?.Invoke();
            }
        }

        private void HandleDisconnected(ulong clientId)
        {
            if (clientId == _manager.LocalClientId || clientId == NetworkManager.ServerClientId)
            {
                Disconnected?.Invoke(_manager.DisconnectReason ?? string.Empty);
            }
        }
    }
}
