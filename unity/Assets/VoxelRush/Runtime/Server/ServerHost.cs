using System;
using UnityEngine;
using VoxelRush.Core;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Hosting;

namespace VoxelRush.Server
{
    /// <summary>Headless dedicated server entry point. Networking and simulation attach here in VR-002/VR-012.</summary>
    [DisallowMultipleComponent]
    public sealed class ServerHost : MonoBehaviour
    {
        public const int ExitCodeSuccess = 0;
        public const int ExitCodeInvalidArguments = 2;

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

        private void Update()
        {
            if (Options == null)
            {
                return;
            }

            TicksElapsed++;
            if (Options.SmokeTestTicks is int limit && TicksElapsed >= limit)
            {
                Debug.Log(LogEvent.Format("server_smoke_test_passed", ("ticks", TicksElapsed), ("seconds", Time.realtimeSinceStartupAsDouble)));
                Shutdown(ExitCodeSuccess, "smoke_test_complete");
            }
        }

        private void Shutdown(int exitCode, string reason)
        {
            Debug.Log(LogEvent.Format("server_stopping", ("reason", reason), ("exitCode", exitCode)));
            enabled = false;

            // Never kill the editor from a play-mode session; only the standalone player exits.
            if (!Application.isEditor)
            {
                Application.Quit(exitCode);
            }
        }
    }
}
