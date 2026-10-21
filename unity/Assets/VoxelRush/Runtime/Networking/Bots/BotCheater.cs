using System;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Input;
using VoxelRush.Core.Simulation;
using VoxelRush.Networking.Vehicles;

namespace VoxelRush.Networking.Bots
{
    /// <summary>
    /// Development-only hostile client (<c>-botCheat</c>): once a second sends a speed-hack input (throttle 5000 of 1000)
    /// through the real input RPC, exactly as a modified client would. The server must reject it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BotCheater : MonoBehaviour
    {
        public const string CheatArgument = "-botCheat";

        private bool _active;
        private float _nextAt;
        private uint _sequence = 1_000_000;

        public long Sent { get; private set; }

        private void Awake()
        {
            _active = Debug.isDebugBuild && Array.IndexOf(Environment.GetCommandLineArgs(), CheatArgument) >= 0;
        }

        private void Update()
        {
            if (!_active || Time.realtimeSinceStartup < _nextAt || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsConnectedClient)
            {
                return;
            }

            _nextAt = Time.realtimeSinceStartup + 1f;
            foreach (VehicleNetworkDriver driver in FindObjectsByType<VehicleNetworkDriver>(FindObjectsSortMode.None))
            {
                if (!driver.IsOwner)
                {
                    continue;
                }

                var hacked = new InputCommand(_sequence++, 0, new QuantizedDrivingInput(0, 5000, 0, 0));
                driver.SubmitInputsRpc(new InputBatch { Commands = new[] { hacked } });
                Sent++;
                if (Sent % 5 == 1)
                {
                    Debug.Log(LogEvent.Format("bot_cheat_sent", ("count", Sent)));
                }
            }
        }
    }
}
