using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using VoxelRush.Core;

namespace VoxelRush.Networking
{
    /// <summary>Creates the single NetworkManager/UnityTransport pair for this process.</summary>
    internal static class NetworkRuntime
    {
        public static NetworkManager Create(string name)
        {
            if (NetworkManager.Singleton != null)
            {
                throw new InvalidOperationException("A NetworkManager already exists in this process.");
            }

            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var transport = go.AddComponent<UnityTransport>();
            var manager = go.AddComponent<NetworkManager>();
            manager.NetworkConfig ??= new NetworkConfig();
            manager.NetworkConfig.NetworkTransport = transport;
            manager.NetworkConfig.TickRate = SimulationConstants.ServerTickRate;
            manager.NetworkConfig.ConnectionApproval = true;
            // Server and client run different scenes; the world is streamed as chunks, not NGO scene sync.
            manager.NetworkConfig.EnableSceneManagement = false;
            // Server and clients must register the same networked prefabs.
            manager.NetworkConfig.Prefabs.Add(new NetworkPrefab { Prefab = Vehicles.VehicleSpawner.LoadPrefab() });
            return manager;
        }

        public static UnityTransport Transport(NetworkManager manager) => (UnityTransport)manager.NetworkConfig.NetworkTransport;

        /// <summary>Unity Transport needs a literal IP; resolve DNS names (e.g. Docker service names) once at connect time.</summary>
        public static string ResolveIPv4(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress? literal) && literal.AddressFamily == AddressFamily.InterNetwork)
            {
                return literal.ToString();
            }

            IPAddress? address = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return address?.ToString() ?? throw new SocketException((int)SocketError.HostNotFound);
        }
    }
}
