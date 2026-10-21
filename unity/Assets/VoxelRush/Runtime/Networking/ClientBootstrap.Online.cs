using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Hosting;
using VoxelRush.Networking.Online;

namespace VoxelRush.Networking
{
    /// <summary>MVP-004: the join screen's online panel (guest account, worlds, ticketed join).</summary>
    public sealed partial class ClientBootstrap
    {
        private const string ApiUrlKey = "voxelrush.apiUrl";

        private PlayerApi? _api;
        private string _apiUrl = string.Empty;
        private string _boundApiUrl = string.Empty;
        private string _newWorldName = "My world";
        private WorldEntry[] _myWorlds = System.Array.Empty<WorldEntry>();
        private WorldEntry[] _publicWorlds = System.Array.Empty<WorldEntry>();
        private string? _onlineStatus;
        private string? _onlineWorldId;
        private bool _onlineBusy;
        private Vector2 _worldScroll;

        public bool OnJoinScreen => _joinScreen;

        /// <summary>Set by an overlay (the garage) so the join panels neither draw nor take clicks under it.</summary>
        public bool HideJoinPanels { get; set; }

        /// <summary>The signed-in API client, for the garage and store (null until signed in).</summary>
        public PlayerApi? SignedInApi => _api != null && _api.SignedIn ? _api : null;

        /// <summary>The online client for tests and tools; created on first use from the entered API URL.</summary>
        public PlayerApi Api(string apiUrl)
        {
            string url = apiUrl.Trim().TrimEnd('/');
            if (_api == null || url != _boundApiUrl)
            {
                _api = new PlayerApi(new UnityApiTransport(url), SecureTokenStore.Create(url));
                _boundApiUrl = url;
            }

            return _api;
        }

        private void DrawOnline(float width, GUIStyle text, GUIStyle button, GUIStyle small)
        {
            if (_apiUrl.Length == 0)
            {
                _apiUrl = PlayerPrefs.GetString(ApiUrlKey, "http://");
            }

            GUILayout.BeginArea(new Rect(width / 2f + 20f, 120f, 560f, 560f), GUI.skin.box);
            GUILayout.Label("Online (API address)", small);
            _apiUrl = GUILayout.TextField(_apiUrl, 96, text, GUILayout.Height(56));
            GUI.enabled = !_onlineBusy;
            if (GUILayout.Button(_api?.SignedIn == true && _boundApiUrl == _apiUrl.Trim().TrimEnd('/') ? "Refresh worlds" : "Sign in", button, GUILayout.Height(56)))
            {
                PlayerPrefs.SetString(ApiUrlKey, _apiUrl.Trim());
                PlayerPrefs.Save();
                _ = Run(LoadWorldsAsync());
            }

            if (_api?.SignedIn == true)
            {
                GUILayout.BeginHorizontal();
                _newWorldName = GUILayout.TextField(_newWorldName, 40, text, GUILayout.Height(52), GUILayout.Width(330));
                if (GUILayout.Button("Create", button, GUILayout.Height(52)))
                {
                    _ = Run(CreateWorldAsync());
                }

                GUILayout.EndHorizontal();
                _worldScroll = GUILayout.BeginScrollView(_worldScroll, GUILayout.Height(260));
                DrawWorlds("Your worlds", _myWorlds, button, small);
                DrawWorlds("Public worlds", _publicWorlds, button, small);
                GUILayout.EndScrollView();
            }

            GUI.enabled = true;
            if (_onlineStatus != null)
            {
                GUILayout.Label(_onlineStatus, small);
            }

            GUILayout.EndArea();
        }

        private void DrawWorlds(string title, WorldEntry[] worlds, GUIStyle button, GUIStyle small)
        {
            GUILayout.Label(worlds.Length == 0 ? $"{title}: none" : title, small);
            foreach (WorldEntry world in worlds)
            {
                if (GUILayout.Button($"{world.name}  ({world.role})", button, GUILayout.Height(52)))
                {
                    _ = Run(JoinOnlineAsync(world.id));
                }
            }
        }

        // One request at a time; failures surface as the status line, never as exceptions.
        private async Task Run(Task work)
        {
            _onlineBusy = true;
            try
            {
                await work;
            }
            catch (System.Exception e)
            {
                _onlineStatus = e.Message;
                Debug.LogWarning(LogEvent.Format("online_error", ("error", e.Message)));
            }
            finally
            {
                _onlineBusy = false;
            }
        }

        private async Task LoadWorldsAsync()
        {
            PlayerApi api = Api(_apiUrl);
            _onlineStatus = "Signing in…";
            ApiResult<string> signIn = await api.SignInAsync();
            if (!signIn.Ok)
            {
                _onlineStatus = signIn.Error;
                return;
            }

            ApiResult<WorldEntry[]> mine = await api.MyWorldsAsync();
            ApiResult<WorldEntry[]> open = await api.PublicWorldsAsync();
            _myWorlds = mine.Value ?? System.Array.Empty<WorldEntry>();
            _publicWorlds = open.Value ?? System.Array.Empty<WorldEntry>();
            _onlineStatus = mine.Error ?? open.Error;
        }

        private async Task CreateWorldAsync()
        {
            ApiResult<WorldEntry> created = await Api(_apiUrl).CreateWorldAsync(_newWorldName.Trim(), "public");
            _onlineStatus = created.Error;
            if (created.Ok)
            {
                await LoadWorldsAsync();
            }
        }

        private async Task JoinOnlineAsync(string worldId)
        {
            _onlineStatus = "Joining…";
            if (!await TryJoinOnline(worldId))
            {
                return;
            }

            _onlineStatus = null;
        }

        private async Task<bool> TryJoinOnline(string worldId)
        {
            ApiResult<JoinGrant> grant = await Api(_apiUrl).JoinAsync(worldId, message => _onlineStatus = message);
            if (!grant.Ok)
            {
                _onlineStatus = grant.Error;
                return false;
            }

            byte[]? ticket = ClientNetwork.DecodeTicket(grant.Value!.ticket);
            if (ticket == null || !ServerEndpoint.TryParse(grant.Value.endpoint, out ServerEndpoint endpoint, out string error))
            {
                _onlineStatus = "The server sent an invalid join ticket.";
                return false;
            }

            _onlineWorldId = worldId;
            _joinError = null;
            _joinScreen = false;
            Connect(endpoint, ticket);
            return true;
        }

        private IEnumerator RejoinOnline()
        {
            Task<bool> rejoin = TryJoinOnline(_onlineWorldId!);
            while (!rejoin.IsCompleted)
            {
                yield return null;
            }

            if (!rejoin.Result)
            {
                _joinError = _onlineStatus;
                _joinScreen = true;
            }
        }
    }
}
