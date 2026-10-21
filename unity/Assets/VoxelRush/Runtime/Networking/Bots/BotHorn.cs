using Unity.Netcode;
using UnityEngine;
using VoxelRush.Networking.Vehicles;

namespace VoxelRush.Networking.Bots
{
    /// <summary>
    /// Development-only horn spammer (<c>-botHorn</c>, COSM-002): presses the horn five times a second, bypassing the
    /// client cooldown as a modified client would. The server must relay at most one press per second.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BotHorn : MonoBehaviour
    {
        public const string HornArgument = "-botHorn";

        private const float IntervalSeconds = 0.2f;

        private float _nextAt;

        public long Sent { get; private set; }

        private void Update()
        {
            if (!Debug.isDebugBuild || Time.realtimeSinceStartup < _nextAt || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsConnectedClient)
            {
                return;
            }

            _nextAt = Time.realtimeSinceStartup + IntervalSeconds;
            foreach (VehicleLoadout car in FindObjectsByType<VehicleLoadout>(FindObjectsSortMode.None))
            {
                if (car.IsOwner)
                {
                    car.SendHornUnthrottled();
                    Sent++;
                }
            }
        }
    }
}
