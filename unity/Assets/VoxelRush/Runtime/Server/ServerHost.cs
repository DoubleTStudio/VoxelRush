using System;
using UnityEngine;
using VoxelRush.Core;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Hosting;
using VoxelRush.Core.Networking;
using VoxelRush.Networking;

namespace VoxelRush.Server
{
    /// <summary>Headless dedicated server entry point: parses launch options and runs the authoritative listener.</summary>
    [DisallowMultipleComponent]
    public sealed class ServerHost : MonoBehaviour
    {
        public const int ExitCodeSuccess = 0;
        public const int ExitCodeInvalidArguments = 2;
        public const int ExitCodeStartupFailure = 3;

        private ServerNetwork? _network;

        public ServerLaunchOptions? Options { get; private set; }

        public long TicksElapsed { get; private set; }

        private void Awake()
        {
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);

            ServerLaunchParseResult result = ServerLaunchOptions.Parse(Environment.GetCommandLineArgs());
            if (!result.IsSuccess)
            {
                foreach (string error in result.Errors)
                {
                    Debug.LogError(LogEvent.Format("server_invalid_argument", ("error", error)));
                }

                Shutdown(ExitCodeInvalidArguments, "invalid_arguments");
                return;
            }

            Options = result.Options!;

            // Headless player has no vsync; frame rate is the tick budget until the fixed-tick loop lands.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = SimulationConstants.ServerTickRate;

            Debug.Log(LogEvent.Format(
                "server_started",
                ("port", Options.Port),
                ("maxPlayers", Options.MaxPlayers),
                ("tickRate", SimulationConstants.ServerTickRate),
                ("protocolVersion", SimulationConstants.ProtocolVersion),
                ("unityVersion", Application.unityVersion),
                ("headless", Application.isBatchMode),
                ("smokeTestTicks", Options.SmokeTestTicks)));
        }

        // NGO registers its message types after scene Awake, so the listener starts here.
        private void Start()
        {
            if (Options == null || !enabled)
            {
                return;
            }

            _network = ServerNetwork.Start(Options.Port, Options.MaxPlayers);
            if (_network == null)
            {
                Debug.LogError(LogEvent.Format("server_listen_failed", ("port", Options.Port)));
                Shutdown(ExitCodeStartupFailure, "listen_failed");
                return;
            }

            _network.ClientConnected += (clientId, players) =>
                Debug.Log(LogEvent.Format("client_connected", ("clientId", clientId), ("players", players), ("maxPlayers", Options.MaxPlayers)));
            _network.ClientDisconnected += (clientId, players) =>
                Debug.Log(LogEvent.Format("client_disconnected", ("clientId", clientId), ("players", players)));
            _network.ClientRejected += (clientId, rejection) =>
                Debug.LogWarning(LogEvent.Format("client_rejected", ("clientId", clientId), ("reason", rejection.ToReasonCode())));
            _network.PendingClientExpired += clientId =>
                Debug.LogWarning(LogEvent.Format("client_pending_expired", ("clientId", clientId)));

            Debug.Log(LogEvent.Format("server_listening", ("port", Options.Port), ("transport", "udp")));
        }

        private void Update()
        {
            if (Options == null)
            {
                return;
            }

            TicksElapsed++;
            _network?.Tick(Time.realtimeSinceStartupAsDouble);
            if (Options.SmokeTestTicks is int limit && TicksElapsed >= limit)
            {
                Debug.Log(LogEvent.Format("server_smoke_test_passed", ("ticks", TicksElapsed), ("seconds", Time.realtimeSinceStartupAsDouble)));
                Shutdown(ExitCodeSuccess, "smoke_test_complete");
            }
        }

        private void OnDestroy()
        {
            _network?.Dispose();
            _network = null;
        }

        private void Shutdown(int exitCode, string reason)
        {
            Debug.Log(LogEvent.Format("server_stopping", ("reason", reason), ("exitCode", exitCode)));
            enabled = false;
            _network?.Dispose();
            _network = null;

            // Never kill the editor from a play-mode session; only the standalone player exits.
            if (!Application.isEditor)
            {
                Application.Quit(exitCode);
            }
        }
    }
}
