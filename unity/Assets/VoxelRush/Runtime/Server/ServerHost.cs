using System;
using UnityEngine;
using VoxelRush.Core;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Hosting;
using VoxelRush.Core.Networking;
using VoxelRush.Gameplay.World;
using VoxelRush.Networking;
using VoxelRush.Networking.Race;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Networking.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;
using VoxelRush.World.Persistence;
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
        // SAVE-005: another server holds this world's lease, or this server lost it.
        public const int ExitCodeWorldLeased = 4;
        public const int ExitCodeLeaseLost = 5;

        private const double MetricsIntervalSeconds = 5;

        private ServerNetwork? _network;
        private VehicleSpawner? _vehicles;
        private NetworkSimulation? _simulation;
        private WorldSyncServer? _world;
        private RaceServer? _race;
        private RewardClient? _rewards;
        private LeaseClient? _lease;
        private ApiSnapshotRemote? _snapshots;
        private double _emptySince = -1;

        // First networked slice: a flat build plot around the spawn row (world x −48..47, z −80..−17, y −32..15).
        private static readonly WorldInfo PlotWorld = new WorldInfo(
            new System.Guid("6f0f7c2e-4d5b-4b0f-9b8e-2a1d3c4e5f60"), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, ChunkData.CurrentSchemaVersion,
            new ChunkCoord(-3, -2, -5), new ChunkCoord(2, 0, -2));
        private double _nextMetricsAt;

        public ServerLaunchOptions? Options { get; private set; }

        public long TicksElapsed { get; private set; }

        private uint CurrentTick => _simulation != null ? _simulation.Tick : (uint)TicksElapsed;

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

            TicketVerifier? tickets = null;
            if (Options.WorldId is Guid worldId)
            {
                // Ticket mode: the key comes from the environment only.
                string? encoded = Environment.GetEnvironmentVariable(ServerLaunchOptions.TicketKeyEnvironment);
                byte[]? key = null;
                try
                {
                    key = encoded == null ? null : Convert.FromBase64String(encoded);
                }
                catch (FormatException)
                {
                }

                if (key == null || key.Length < JoinTicket.MinKeyBytes)
                {
                    Debug.LogError(LogEvent.Format("server_ticket_key_missing", ("variable", ServerLaunchOptions.TicketKeyEnvironment)));
                    Shutdown(ExitCodeInvalidArguments, "ticket_key_missing");
                    return;
                }

                tickets = new TicketVerifier(key, worldId);
                Debug.Log(LogEvent.Format("server_tickets_required", ("worldId", worldId)));

                // One authoritative server per world: take the lease before touching the world.
                string? api = Environment.GetEnvironmentVariable(RewardClient.ApiUrlEnvironment);
                string? token = Environment.GetEnvironmentVariable(RewardClient.ServiceTokenEnvironment);
                if (!string.IsNullOrEmpty(api) && !string.IsNullOrEmpty(token))
                {
                    _lease = new LeaseClient(api, token, worldId);
                    LeaseAcquire acquired = _lease.Acquire();
                    if (acquired != LeaseAcquire.Granted)
                    {
                        Debug.LogError(LogEvent.Format("server_world_leased", ("result", acquired), ("heldBy", _lease.HeldBy ?? "?")));
                        _lease.Dispose();
                        _lease = null;
                        Shutdown(acquired == LeaseAcquire.HeldByOther ? ExitCodeWorldLeased : ExitCodeStartupFailure, "world_lease");
                        return;
                    }

                    Debug.Log(LogEvent.Format("server_lease_acquired", ("holder", _lease.Holder), ("fencing", _lease.Fencing)));
                }
            }

            _network = ServerNetwork.Start(Options.Port, Options.MaxPlayers, tickets);
            if (_network == null)
            {
                Debug.LogError(LogEvent.Format("server_listen_failed", ("port", Options.Port)));
                Shutdown(ExitCodeStartupFailure, "listen_failed");
                return;
            }

            _vehicles = new VehicleSpawner();
            string worldDir = Options.WorldDir ?? WorldSyncServer.DefaultDirectory();
            WorldInfo world = Options.WorldId is Guid id
                ? new WorldInfo(id, PlotWorld.Kind, PlotWorld.Seed, PlotWorld.GeneratorVersion, PlotWorld.SchemaVersion, PlotWorld.RegionMin, PlotWorld.RegionMax)
                : PlotWorld;
            try
            {
                if (_lease != null)
                {
                    _snapshots = new ApiSnapshotRemote(Environment.GetEnvironmentVariable(RewardClient.ApiUrlEnvironment)!,
                        Environment.GetEnvironmentVariable(RewardClient.ServiceTokenEnvironment)!, Options.WorldId!.Value, _lease.Holder, _lease.Fencing);
                    if (!_snapshots.StorageEnabled())
                    {
                        Debug.LogWarning(LogEvent.Format("world_snapshots_disabled", ("reason", "storage_disabled")));
                        _snapshots.Dispose();
                        _snapshots = null;
                    }
                    else if (SnapshotSync.AlignEpoch(worldDir, _lease.Epoch))
                    {
                        // SAVE-009: the world was restored; rebuild this disk from storage.
                        Debug.LogWarning(LogEvent.Format("world_local_reset", ("epoch", _lease.Epoch)));
                    }
                }

                _world = _network.CreateWorldSync(world, worldDir, _vehicles.EyeOf, _vehicles.Occupants, _snapshots);
            }
            catch (System.IO.IOException e)
            {
                // Storage unreachable on a cold start: serving an older world would later overwrite the newer one.
                Debug.LogError(LogEvent.Format("world_restore_failed", ("error", e.Message)));
                Shutdown(ExitCodeStartupFailure, "world_restore_failed");
                return;
            }

            // MVP-003: placed blocks are solid; built before any car spawns.
            WorldSyncServer synced = _world;
            var colliders = new GameObject(nameof(WorldColliders)).AddComponent<WorldColliders>();
            colliders.Bind(() => synced.Info, synced.GetChunk);
            colliders.Refresh(all: true);

            // SAVE-006: XP for validated results, only with API identities (tickets) and API credentials configured.
            if (tickets != null)
            {
                _rewards = RewardClient.FromEnvironment(System.IO.Path.Combine(worldDir, "rewards-outbox.txt"));
                Debug.Log(LogEvent.Format("server_rewards", ("enabled", _rewards != null)));
            }

            _race = _network.CreateRace(ArenaTrack.Create(), _vehicles, Options.RaceAutoStart, _rewards == null ? null : _rewards.Enqueue);
            _simulation = NetworkSimulation.Instance;
            _nextMetricsAt = Time.realtimeSinceStartupAsDouble + MetricsIntervalSeconds;
            NetworkBandwidth.Meter.Flush(Time.realtimeSinceStartupAsDouble);
            _network.ClientConnected += (clientId, players) =>
            {
                Debug.Log(LogEvent.Format("client_connected", ("clientId", clientId), ("players", players), ("maxPlayers", Options.MaxPlayers)));
                var pose = _race.OnClientConnected(clientId, _network.SessionOf(clientId), CurrentTick);
                _vehicles.Spawn(clientId, pose);
                _world.OnClientConnected(clientId);
            };
            _network.ClientDisconnected += (clientId, players) =>
            {
                Debug.Log(LogEvent.Format("client_disconnected", ("clientId", clientId), ("players", players)));
                _vehicles.Despawn(clientId);
                _world.OnClientDisconnected(clientId);
                _race.OnClientDisconnected(clientId, CurrentTick);
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
            _world?.Tick(CurrentTick, now);
            _race?.Tick(CurrentTick);
            if (_simulation != null && now >= _nextMetricsAt)
            {
                _nextMetricsAt = now + MetricsIntervalSeconds;
                LogTickMetrics(now);
            }
            if (_lease != null && _lease.Lost)
            {
                // Another server may already own the world: stop serving it (the journal still flushes on dispose).
                Debug.LogError(LogEvent.Format("server_lease_lost", ("fencing", _lease.Fencing)));
                Shutdown(ExitCodeLeaseLost, "lease_lost");
                return;
            }

            if (Options.IdleShutdownSeconds is int idle && _network != null)
            {
                if (_network.PlayerCount > 0)
                {
                    _emptySince = -1;
                }
                else if (_emptySince < 0)
                {
                    _emptySince = now;
                }
                else if (now - _emptySince >= idle)
                {
                    Debug.Log(LogEvent.Format("server_idle_shutdown", ("idleSeconds", idle)));
                    Shutdown(ExitCodeSuccess, "idle");
                    return;
                }
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
            (int clients, double mean, double max) = NetworkBandwidth.Meter.Flush(now);
            Debug.Log(LogEvent.Format("net_bandwidth", ("clients", clients), ("meanBytesPerSecPerClient", Math.Round(mean)), ("maxBytesPerSecPerClient", Math.Round(max)),
                ("snapshotsSkipped", VehicleNetworkDriver.SnapshotsSkipped)));
        }

        private void OnDestroy()
        {
            _rewards?.Dispose();
            _rewards = null;
            _world?.Dispose();
            _world = null;
            _snapshots?.Dispose();
            _snapshots = null;
            ReleaseLease();
            _network?.Dispose();
            _network = null;
        }

        private void Shutdown(int exitCode, string reason)
        {
            _world?.Dispose();
            _world = null;
            _snapshots?.Dispose();
            _snapshots = null;
            // After the journal is flushed and snapshots published, so the next holder sees everything acknowledged.
            ReleaseLease();
            if (_simulation != null)
            {
                Debug.Log(LogEvent.Format("server_tick_summary", ("totalTicks", _simulation.Metrics.TotalTicks), ("totalOverruns", _simulation.Metrics.TotalOverruns), ("droppedTicks", _simulation.DroppedTicks)));
                Debug.Log(LogEvent.Format("net_bandwidth_summary", ("totalBytes", NetworkBandwidth.Meter.TotalBytes),
                    ("peakBytesPerSecPerClient", Math.Round(NetworkBandwidth.Meter.PeakClientBytesPerSecond)), ("snapshotsSkipped", VehicleNetworkDriver.SnapshotsSkipped)));
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

        private void ReleaseLease()
        {
            _lease?.Dispose();
            _lease = null;
        }
    }
}
