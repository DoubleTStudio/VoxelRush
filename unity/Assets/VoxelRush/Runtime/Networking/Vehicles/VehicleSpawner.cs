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

        /// <summary>Server-side eye position of a player's car (edit reach is measured from here, never from a client claim).</summary>
        public Vector3? EyeOf(ulong clientId) =>
            _cars.TryGetValue(clientId, out NetworkObject car) && car != null ? car.transform.position + Vector3.up * 1.5f : (Vector3?)null;

        /// <summary>World boxes of every car, so placed blocks can't trap a vehicle.</summary>
        public IReadOnlyList<VoxelRush.World.Editing.WorldBox> Occupants()
        {
            var boxes = new List<VoxelRush.World.Editing.WorldBox>(_cars.Count);
            foreach (NetworkObject car in _cars.Values)
            {
                if (car != null)
                {
                    Vector3 p = car.transform.position;
                    boxes.Add(new VoxelRush.World.Editing.WorldBox(p.x - 1.2f, p.y - 1f, p.z - 2.4f, p.x + 1.2f, p.y + 1.5f, p.z + 2.4f));
                }
            }

            return boxes;
        }

        public static GameObject LoadPrefab() =>
            Resources.Load<GameObject>(PrefabResourcePath) ?? throw new System.InvalidOperationException($"Missing Resources/{PrefabResourcePath}.");

        /// <summary>The server-simulated car of a player, if spawned.</summary>
        public VoxelRush.Gameplay.Vehicles.ArcadeVehicle? VehicleOf(ulong clientId) =>
            _cars.TryGetValue(clientId, out NetworkObject car) && car != null ? car.GetComponent<VoxelRush.Gameplay.Vehicles.ArcadeVehicle>() : null;

        /// <summary>The paint component of a player's car, if spawned (COSM-001).</summary>
        public VehiclePaint? PaintOf(ulong clientId) =>
            _cars.TryGetValue(clientId, out NetworkObject car) && car != null ? car.GetComponent<VehiclePaint>() : null;

        /// <summary>Grid slot <paramref name="slot"/>: the scene's spawn points in order (a row fallback without any).</summary>
        public (Vector3 Position, Quaternion Rotation) GridPose(int slot) =>
            slot < _spawnPoints.Count ? (_spawnPoints[slot].position, _spawnPoints[slot].rotation) : (new Vector3(slot * 6f, 1.5f, 0f), Quaternion.identity);

        /// <param name="pose">Where to spawn instead of the next free spawn point (a racer resuming mid-course).</param>
        public void Spawn(ulong clientId, (Vector3 Position, Quaternion Rotation)? pose = null)
        {
            if (_cars.ContainsKey(clientId))
            {
                return;
            }

            (Vector3 position, Quaternion rotation) = pose ?? NextSpawnPose();
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
