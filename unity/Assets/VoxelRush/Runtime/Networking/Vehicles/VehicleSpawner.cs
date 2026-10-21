using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>Server-side: one networked car per connected player, owned by that player, at a free spawn point.</summary>
    public sealed class VehicleSpawner
    {
        public const string PrefabResourcePath = "Network/NetworkedCar";
        public const string SpawnPointsName = "SpawnPoints";

        private readonly Dictionary<ulong, NetworkObject> _cars = new Dictionary<ulong, NetworkObject>();
        private readonly List<Transform> _spawnPoints = new List<Transform>();

        public VehicleSpawner()
        {
            GameObject? root = GameObject.Find(SpawnPointsName);
            if (root != null)
            {
                foreach (Transform point in root.transform)
                {
                    _spawnPoints.Add(point);
                }
            }
        }

        public int Count => _cars.Count;

        public static GameObject LoadPrefab() =>
            Resources.Load<GameObject>(PrefabResourcePath) ?? throw new System.InvalidOperationException($"Missing Resources/{PrefabResourcePath}.");

        public void Spawn(ulong clientId)
        {
            if (_cars.ContainsKey(clientId))
            {
                return;
            }

            (Vector3 position, Quaternion rotation) = NextSpawnPose();
            NetworkObject car = NetworkObject.InstantiateAndSpawn(LoadPrefab(), NetworkManager.Singleton, clientId, position: position, rotation: rotation);
            _cars[clientId] = car;
        }

        public void Despawn(ulong clientId)
        {
            if (_cars.Remove(clientId, out NetworkObject car) && car != null && car.IsSpawned)
            {
                car.Despawn();
                Debug.Log(LogEvent.Format("vehicle_despawned", ("ownerClientId", clientId)));
            }
        }

        // First spawn point not occupied by a car; falls back to a row if the scene has none.
        private (Vector3, Quaternion) NextSpawnPose()
        {
            for (int i = 0; i < _spawnPoints.Count; i++)
            {
                if (!Occupied(_spawnPoints[i].position))
                {
                    return (_spawnPoints[i].position, _spawnPoints[i].rotation);
                }
            }

            return (new Vector3(_cars.Count * 6f, 1.5f, 0f), Quaternion.identity);
        }

        private bool Occupied(Vector3 point)
        {
            foreach (NetworkObject car in _cars.Values)
            {
                if (car != null && Vector3.Distance(car.transform.position, point) < 4f)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
