using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Gameplay.Vehicles;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>A car this client shows, own (predicted) or remote (interpolated), for presentation without Netcode types.</summary>
    public sealed class ClientCar
    {
        public ClientCar(Transform transform, ArcadeVehicle? vehicle, bool isLocal)
        {
            Transform = transform;
            Vehicle = vehicle;
            IsLocal = isLocal;
        }

        public Transform Transform { get; }

        /// <summary>Simulated only for the own car; a remote car's body is kinematic and its speed isn't computed.</summary>
        public ArcadeVehicle? Vehicle { get; }

        public bool IsLocal { get; }

        /// <summary>Own car: the predicted boost; remote: the last snapshot's flag.</summary>
        public bool IsBoosting => IsLocal && Vehicle != null ? Vehicle.IsBoosting : RemoteBoosting;

        internal bool RemoteBoosting { get; set; }
    }

    /// <summary>Every car this client shows (AUDIO-001 engine sounds); never filled on the server.</summary>
    public static class ClientCars
    {
        private static readonly List<ClientCar> s_all = new List<ClientCar>(Core.SimulationConstants.MaxPlayersPerServer);

        public static IReadOnlyList<ClientCar> All => s_all;

        internal static void Add(ClientCar car)
        {
            if (!s_all.Contains(car))
            {
                s_all.Add(car);
            }
        }

        internal static void Remove(ClientCar car) => s_all.Remove(car);
    }
}
