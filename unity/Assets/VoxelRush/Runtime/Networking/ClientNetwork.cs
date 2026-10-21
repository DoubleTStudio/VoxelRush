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

        public const string JoinTicketArgumentName = "-joinTicket";

        /// <summary>A base64url join ticket from the API (<c>-joinTicket</c>), or null to use the development session.</summary>
        public static byte[]? JoinTicketArgument(string[] args)
        {
            int i = Array.IndexOf(args, JoinTicketArgumentName);
            if (i < 0 || i + 1 >= args.Length)
            {
                return null;
            }

            return DecodeTicket(args[i + 1]);
        }

        /// <summary>Decodes a base64url ticket as the API issues it; null if malformed.</summary>
        public static byte[]? DecodeTicket(string encoded)
        {
            string s = encoded.Replace('-', '+').Replace('_', '/');
            try
            {
                byte[] bytes = Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
                return bytes.Length == JoinTicket.Length ? bytes : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>This process's session id; reused across reconnects so the server can resume the player's race.</summary>
        public static Guid Session { get; } = Guid.NewGuid();

        // Dev builds: real packet loss/delay inside Unity Transport (-simTransportLossPercent / -simTransportDelayMs).
        private static void ConfigureTransportSimulation()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (TransportSimulationOptions.TryParse(Environment.GetCommandLineArgs(), out TransportSimulationOptions options, out _) && options.IsActive)
            {
                Unity.Netcode.Transports.UTP.UnityTransport.s_DriverConstructor = new LossyDriverConstructor(options, (uint)Session.GetHashCode());
                UnityEngine.Debug.Log(VoxelRush.Core.Diagnostics.LogEvent.Format("network_transport_simulation", ("lossPercent", options.LossPercent), ("delayMs", options.DelayMs)));
            }
#endif
        }

        /// <param name="ticket">A join ticket from the API; falls back to <c>-joinTicket</c>, then the development session.</param>
        public static ClientNetwork? Connect(ServerEndpoint endpoint, byte[]? ticket = null)
        {
            string address = NetworkRuntime.ResolveIPv4(endpoint.Host);
            NetworkManager manager = NetworkRuntime.Create("ClientNetwork");
            NetworkRuntime.Transport(manager).SetConnectionData(address, endpoint.Port);
            ticket ??= JoinTicketArgument(Environment.GetCommandLineArgs());
            manager.NetworkConfig.ConnectionData = ticket != null
                ? ConnectionPayload.EncodeTicket(SimulationConstants.ProtocolVersion, ticket)
                : ConnectionPayload.Encode(SimulationConstants.ProtocolVersion, Session);
            ConfigureTransportSimulation();

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
