using System;
using UnityEngine;
using VoxelRush.Core;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Hosting;
using VoxelRush.Core.Networking;
using VoxelRush.Networking;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Networking.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;

namespace VoxelRush.Server
{
    /// <summary>Headless dedicated server entry point: parses launch options and runs the authoritative listener.</summary>
    [DisallowMultipleComponent]
    public sealed class ServerHost : MonoBehaviour
    {
        public const int ExitCodeSuccess = 0;
        public const int ExitCodeInvalidArguments = 2;
        public const int ExitCodeStartupFailure = 3;

        private const double MetricsIntervalSeconds = 5;

        private ServerNetwork? _network;
        private VehicleSpawner? _vehicles;
        private NetworkSimulation? _simulation;
        private WorldSyncServer? _world;

        // First networked slice: a flat build plot around the spawn row (world x −48..47, z −80..−17, y −32..15).
        private static readonly WorldInfo PlotWorld = new WorldInfo(
            new System.Guid("6f0f7c2e-4d5b-4b0f-9b8e-2a1d3c4e5f60"), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, ChunkData.CurrentSchemaVersion,
            new ChunkCoord(-3, -2, -5), new ChunkCoord(2, 0, -2));
        private double _nextMetricsAt;

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

            _vehicles = new VehicleSpawner();
            string worldDir = Options.WorldDir ?? WorldSyncServer.DefaultDirectory();
            _world = _network.CreateWorldSync(PlotWorld, worldDir, _vehicles.EyeOf, _vehicles.Occupants);
            _simulation = NetworkSimulation.Instance;
            _nextMetricsAt = Time.realtimeSinceStartupAsDouble + MetricsIntervalSeconds;
            _network.ClientConnected += (clientId, players) =>
            {
                Debug.Log(LogEvent.Format("client_connected", ("clientId", clientId), ("players", players), ("maxPlayers", Options.MaxPlayers)));
                _vehicles.Spawn(clientId);
                _world.OnClientConnected(clientId);
            };
            _network.ClientDisconnected += (clientId, players) =>
            {
                Debug.Log(LogEvent.Format("client_disconnected", ("clientId", clientId), ("players", players)));
                _vehicles.Despawn(clientId);
                _world.OnClientDisconnected(clientId);
            };
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
            double now = Time.realtimeSinceStartupAsDouble;
            _network?.Tick(now);
            _world?.Tick(_simulation != null ? _simulation.Tick : (uint)TicksElapsed, now);
            if (_simulation != null && now >= _nextMetricsAt)
            {
                _nextMetricsAt = now + MetricsIntervalSeconds;
                LogTickMetrics(now);
            }
            if (Options.SmokeTestTicks is int limit && TicksElapsed >= limit)
            {
                Debug.Log(LogEvent.Format("server_smoke_test_passed", ("ticks", TicksElapsed), ("seconds", Time.realtimeSinceStartupAsDouble)));
                Shutdown(ExitCodeSuccess, "smoke_test_complete");
            }
        }

        private void LogTickMetrics(double now)
        {
            TickWindow window = _simulation!.Metrics.Flush(now);
            Debug.Log(LogEvent.Format(
                "server_tick_metrics",
                ("ticks", window.Ticks),
                ("tickRate", window.TicksPerSecond),
                ("p50Ms", window.P50),
                ("p95Ms", window.P95),
                ("p99Ms", window.P99),
                ("maxMs", window.Max),
                ("overruns", window.Overruns),
                ("droppedTicks", _simulation.DroppedTicks),
                ("players", _network?.PlayerCount ?? 0),
                ("snapshotsSent", VehicleNetworkDriver.SnapshotsSent)));
        }

        private void OnDestroy()
        {
            _world?.Dispose();
            _world = null;
            _network?.Dispose();
            _network = null;
        }

        private void Shutdown(int exitCode, string reason)
        {
            _world?.Dispose();
            _world = null;
            if (_simulation != null)
            {
                Debug.Log(LogEvent.Format("server_tick_summary", ("totalTicks", _simulation.Metrics.TotalTicks), ("totalOverruns", _simulation.Metrics.TotalOverruns), ("droppedTicks", _simulation.DroppedTicks)));
            }

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
