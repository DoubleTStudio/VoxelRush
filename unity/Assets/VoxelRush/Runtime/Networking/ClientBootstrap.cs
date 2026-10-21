using System;
using System.Collections;
using System.Net.Sockets;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Hosting;
using VoxelRush.Networking.Bots;
using VoxelRush.Networking.Race;
using VoxelRush.Networking.World;

namespace VoxelRush.Networking
{
    /// <summary>
    /// Connects to a dedicated server from <c>-connect host:port</c> or, in the editor, from the dev endpoint below.
    /// Used by the mobile client scene and by headless bots (Bot.unity, built as a server-subtarget player).
    /// Mobile endpoints arrive with join tickets (VR-080).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientBootstrap : MonoBehaviour
    {
        public const int ExitCodeSuccess = 0;
        public const int ExitCodeInvalidArguments = 2;
        public const int ExitCodeConnectFailed = 3;

        // Grace period for the handshake on top of the smoke duration.
        private const double ConnectTimeoutSeconds = 15;

        // Upper bound on waiting for NGO to send the disconnect before quitting.
        private const float ShutdownTimeoutSeconds = 1f;

        [SerializeField] private bool _connectInEditor;
        [SerializeField] private string _editorEndpoint = "127.0.0.1:7777";

        private ClientNetwork? _network;
        private WorldSyncClient? _world;
        private int? _smokeTestSeconds;
        private double _startedAt;
        private double _connectedAt = -1;
        private ServerEndpoint? _endpoint;
        private bool _reconnecting;
        private string? _cacheRoot;

        // SAVE-008: players cache chunks under persistentDataPath; headless bots only when given a directory.
        public const string ChunkCacheArgument = "-chunkCache";

        public bool IsConnected => _network != null && _network.IsConnected;

        /// <summary>The scene's bootstrap (one per client/bot process).</summary>
        public static ClientBootstrap? Instance { get; private set; }

        public int Reconnects { get; private set; }

        private void Awake() => Instance = this;

        private void Start()
        {
            _startedAt = Time.realtimeSinceStartupAsDouble;
            ClientLaunchParseResult result = ClientLaunchOptions.Parse(Environment.GetCommandLineArgs());
            if (!result.IsSuccess)
            {
                foreach (string error in result.Errors)
                {
                    Debug.LogError(LogEvent.Format("client_invalid_argument", ("error", error)));
                }

                Exit(ExitCodeInvalidArguments);
                return;
            }

            _smokeTestSeconds = result.Options!.SmokeTestSeconds;
            string[] launchArgs = Environment.GetCommandLineArgs();
            int cacheArg = Array.IndexOf(launchArgs, ChunkCacheArgument);
            _cacheRoot = cacheArg >= 0 && cacheArg + 1 < launchArgs.Length ? launchArgs[cacheArg + 1]
                : Application.isBatchMode ? null : System.IO.Path.Combine(Application.persistentDataPath, "chunk-cache");
            string[] botArgs = Environment.GetCommandLineArgs();
            if (Array.IndexOf(botArgs, BotBuilder.BuildArgument) >= 0 || Array.IndexOf(botArgs, BotBuilder.TowerArgument) >= 0)
            {
                gameObject.AddComponent<BotBuilder>();
            }

            ServerEndpoint? endpoint = result.Options.Connect ?? EditorEndpoint();
            if (endpoint == null)
            {
                Debug.Log(LogEvent.Format("client_offline"));
                return;
            }

            Connect(endpoint.Value);
        }

        private void Update()
        {
            _world?.Tick();
            if (_smokeTestSeconds is not int seconds)
            {
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (_connectedAt >= 0 && now - _connectedAt >= seconds)
            {
                _world?.LogHashes();
                Debug.Log(LogEvent.Format("client_smoke_test_passed", ("connectedSeconds", now - _connectedAt)));
                Exit(ExitCodeSuccess);
            }
            else if (_connectedAt < 0 && now - _startedAt >= seconds + ConnectTimeoutSeconds)
            {
                Debug.LogError(LogEvent.Format("client_connect_timeout", ("seconds", now - _startedAt)));
                Exit(ExitCodeConnectFailed);
            }
        }

        // Mobile apps are often killed in the background without a quit callback.
        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                _world?.SaveCache();
            }
        }

        private void OnDestroy()
        {
            _network?.Dispose();
            _network = null;
        }

        /// <summary>
        /// Drops the connection cleanly and connects again with the same session id (VR-062 race resume). The server
        /// frees the old slot on the clean disconnect, so the new connection isn't refused as a duplicate session.
        /// </summary>
        public void Reconnect()
        {
            if (_reconnecting || _network == null || _endpoint == null || !enabled)
            {
                return;
            }

            _reconnecting = true;
            StartCoroutine(ReconnectRoutine(_endpoint.Value));
        }

        private IEnumerator ReconnectRoutine(ServerEndpoint endpoint)
        {
            Debug.Log(LogEvent.Format("client_reconnecting", ("endpoint", endpoint)));
            _world = null;
            RaceClient.Current = null;
            _network!.BeginShutdown();
            float deadline = Time.realtimeSinceStartup + ShutdownTimeoutSeconds;
            while (!_network.IsShutdownComplete && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            _network.Dispose();
            _network = null;
            // Give the server a moment to process the disconnect before the new handshake.
            yield return new WaitForSecondsRealtime(0.5f);
            Reconnects++;
            Connect(endpoint);
            _reconnecting = false;
        }

        private void Connect(ServerEndpoint endpoint)
        {
            _endpoint = endpoint;
            Debug.Log(LogEvent.Format("client_connecting", ("endpoint", endpoint)));
            try
            {
                _network = ClientNetwork.Connect(endpoint);
            }
            catch (SocketException e)
            {
                Debug.LogError(LogEvent.Format("client_connect_failed", ("endpoint", endpoint), ("reason", "dns"), ("error", e.SocketErrorCode)));
                Exit(ExitCodeConnectFailed);
                return;
            }

            if (_network == null)
            {
                Debug.LogError(LogEvent.Format("client_connect_failed", ("endpoint", endpoint), ("reason", "start_failed")));
                Exit(ExitCodeConnectFailed);
                return;
            }

            _network.Connected += () =>
            {
                // A reconnect keeps the original smoke-test clock.
                if (_connectedAt < 0)
                {
                    _connectedAt = Time.realtimeSinceStartupAsDouble;
                }

                _world = new WorldSyncClient(Unity.Netcode.NetworkManager.Singleton, _cacheRoot);
                RaceClient.Current = new RaceClient(Unity.Netcode.NetworkManager.Singleton);
                BotBuilder.World = _world;
                Debug.Log(LogEvent.Format("client_connected", ("endpoint", endpoint), ("seconds", _connectedAt - _startedAt)));
            };
            _network.Disconnected += reason =>
            {
                Debug.LogWarning(LogEvent.Format("client_disconnected", ("reason", reason.Length > 0 ? reason : "transport")));
                _world?.SaveCache();
                if (_smokeTestSeconds.HasValue && enabled && !_reconnecting)
                {
                    Exit(ExitCodeConnectFailed);
                }
            };
        }

        private ServerEndpoint? EditorEndpoint()
        {
            if (!Application.isEditor || !_connectInEditor)
            {
                return null;
            }

            if (ServerEndpoint.TryParse(_editorEndpoint, out ServerEndpoint endpoint, out string error))
            {
                return endpoint;
            }

            Debug.LogError(LogEvent.Format("client_invalid_editor_endpoint", ("error", error)));
            return null;
        }

        private void Exit(int exitCode)
        {
            if (!enabled)
            {
                return;
            }

            Debug.Log(LogEvent.Format("client_stopping", ("exitCode", exitCode)));
            _world?.SaveCache();
            enabled = false;
            StartCoroutine(ShutdownAndQuit(exitCode));
        }

        // Lets NGO send the disconnect so the server frees the slot now, not after a transport timeout.
        private IEnumerator ShutdownAndQuit(int exitCode)
        {
            if (_network != null)
            {
                _network.BeginShutdown();
                float deadline = Time.realtimeSinceStartup + ShutdownTimeoutSeconds;
                while (!_network.IsShutdownComplete && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                _network.Dispose();
                _network = null;
            }

            if (!Application.isEditor)
            {
                Application.Quit(exitCode);
            }
        }
    }
}
