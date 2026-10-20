using System;
using System.Collections;
using System.Net.Sockets;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Hosting;
using VoxelRush.Networking;

namespace VoxelRush.Presentation
{
    /// <summary>
    /// Connects the client to a dedicated server from <c>-connect host:port</c> (desktop/headless test clients)
    /// or, in the editor, from the dev endpoint below. Mobile endpoints arrive with join tickets (VR-080).
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
        private int? _smokeTestSeconds;
        private double _startedAt;
        private double _connectedAt = -1;

        public bool IsConnected => _network != null && _network.IsConnected;

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
            if (_smokeTestSeconds is not int seconds)
            {
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (_connectedAt >= 0 && now - _connectedAt >= seconds)
            {
                Debug.Log(LogEvent.Format("client_smoke_test_passed", ("connectedSeconds", now - _connectedAt)));
                Exit(ExitCodeSuccess);
            }
            else if (_connectedAt < 0 && now - _startedAt >= seconds + ConnectTimeoutSeconds)
            {
                Debug.LogError(LogEvent.Format("client_connect_timeout", ("seconds", now - _startedAt)));
                Exit(ExitCodeConnectFailed);
            }
        }

        private void OnDestroy()
        {
            _network?.Dispose();
            _network = null;
        }

        private void Connect(ServerEndpoint endpoint)
        {
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
                _connectedAt = Time.realtimeSinceStartupAsDouble;
                Debug.Log(LogEvent.Format("client_connected", ("endpoint", endpoint), ("seconds", _connectedAt - _startedAt)));
            };
            _network.Disconnected += reason =>
            {
                Debug.LogWarning(LogEvent.Format("client_disconnected", ("reason", reason.Length > 0 ? reason : "transport")));
                if (_smokeTestSeconds.HasValue && enabled)
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
