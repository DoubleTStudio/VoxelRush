using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Networking;
using VoxelRush.Networking.Online;
using VoxelRush.Networking.Vehicles;

namespace VoxelRush.Presentation.Store
{
    /// <summary>
    /// Garage on the join screen (ECON-003): level, cosmetics catalog with owned/claim/locked/buy states, and one
    /// equipped item per kind (paint, decal, trail, horn, flag). Equipped items are sent to the server, which shows
    /// them to everyone if owned (COSM-001, COSM-002).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GarageMenu : MonoBehaviour
    {
        public const string PaintKey = "voxelrush.paint";

        private CatalogEntry[] _catalog = Array.Empty<CatalogEntry>();
        private readonly HashSet<string> _owned = new HashSet<string>();
        private Progress? _progress;
        private StoreClient? _store;
        private bool _open;
        private bool _busy;
        private string? _status;
        private Vector2 _scroll;

        public string? EquippedPaint => EquippedOf(CosmeticKind.Paint);

        /// <summary>Device key of the equipped item of a kind (paint keeps its ECON-003 key).</summary>
        public static string KeyOf(CosmeticKind kind) => "voxelrush." + CosmeticCatalog.NameOf(kind);

        public static string? EquippedOf(CosmeticKind kind) => PlayerPrefs.GetString(KeyOf(kind), string.Empty) is { Length: > 0 } sku ? sku : null;

        /// <summary>Equips (or, if already equipped, unequips) an item in its kind's slot and tells the own car.</summary>
        public static void Toggle(CosmeticKind kind, string sku)
        {
            PlayerPrefs.SetString(KeyOf(kind), EquippedOf(kind) == sku ? string.Empty : sku);
            PlayerPrefs.Save();
            LocalPlayer.Equip(kind, EquippedOf(kind));
        }

        /// <summary>What the garage shows for an item, from the player's level and what they own.</summary>
        public static string StateOf(CatalogEntry item, int level, bool owned) =>
            owned ? "owned" : item.unlock.store ? "buy" : level >= item.unlock.level ? "claim" : "locked";

        private void Awake()
        {
            for (int i = 0; i < CosmeticCatalog.KindCount; i++)
            {
                LocalPlayer.Equip((CosmeticKind)i, EquippedOf((CosmeticKind)i));
            }
        }

        private void OnGUI()
        {
            ClientBootstrap? bootstrap = ClientBootstrap.Instance;
            PlayerApi? api = bootstrap?.SignedInApi;
            if (bootstrap == null || !bootstrap.OnJoinScreen || api == null)
            {
                _open = false;
                if (bootstrap != null)
                {
                    bootstrap.HideJoinPanels = false;
                }

                return;
            }

            bootstrap.HideJoinPanels = _open;

            float scale = Screen.height / 720f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var button = new GUIStyle(GUI.skin.button) { fontSize = 26 };
            var small = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = true };
            if (!_open)
            {
                if (GUI.Button(new Rect(20f, 20f, 200f, 60f), "Garage", button))
                {
                    _open = true;
                    _ = Run(LoadAsync(api));
                }

                return;
            }

            float width = Screen.width / scale;
            GUILayout.BeginArea(new Rect((width - 760f) / 2f, 40f, 760f, 640f), GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(_progress != null ? $"Level {_progress.level}  ·  {_progress.xp} XP" : "Garage", new GUIStyle(small) { fontSize = 30 });
            if (GUILayout.Button("Close", button, GUILayout.Width(160), GUILayout.Height(56)))
            {
                _open = false;
            }

            GUILayout.EndHorizontal();
            GUI.enabled = !_busy;
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(470));
            foreach (CatalogEntry item in _catalog)
            {
                DrawItem(api, item, button, small);
            }

            GUILayout.EndScrollView();
            GUI.enabled = true;
            string? status = _status ?? _store?.Status;
            if (status != null)
            {
                GUILayout.Label(status, small);
            }

            GUILayout.EndArea();
        }

        private void DrawItem(PlayerApi api, CatalogEntry item, GUIStyle button, GUIStyle small)
        {
            bool owned = _owned.Contains(item.sku);
            string state = StateOf(item, _progress?.level ?? 0, owned);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{item.name}  ({item.kind})", small, GUILayout.Width(400));
            switch (state)
            {
                case "owned" when CosmeticCatalog.TryKindOf(item.sku, out CosmeticKind kind):
                    bool equipped = EquippedOf(kind) == item.sku;
                    if (GUILayout.Button(equipped ? "Unequip" : "Equip", button, GUILayout.Height(52)))
                    {
                        Toggle(kind, item.sku);
                    }

                    break;
                case "owned":
                    GUILayout.Label("Owned", small);
                    break;
                case "claim":
                    if (GUILayout.Button("Unlock", button, GUILayout.Height(52)))
                    {
                        _ = Run(ClaimAsync(api, item.sku));
                    }

                    break;
                case "locked":
                    GUILayout.Label($"Reach level {item.unlock.level}", small);
                    break;
                default:
                    string price = _store?.PriceOf(item.productId) ?? "Buy";
                    if (GUILayout.Button(price, button, GUILayout.Height(52)))
                    {
                        _store?.Buy(item.productId);
                    }

                    break;
            }

            GUILayout.EndHorizontal();
        }

        private async Task Run(Task work)
        {
            _busy = true;
            try
            {
                await work;
            }
            catch (Exception e)
            {
                _status = e.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task LoadAsync(PlayerApi api)
        {
            _status = "Loading…";
            ApiResult<CatalogEntry[]> catalog = await api.CatalogAsync();
            ApiResult<Progress> progress = await api.ProgressAsync();
            await RefreshOwnedAsync(api);
            _catalog = catalog.Value ?? Array.Empty<CatalogEntry>();
            _progress = progress.Value;
            _status = catalog.Error ?? progress.Error;

            var products = new List<string>();
            foreach (CatalogEntry item in _catalog)
            {
                if (item.unlock.store && !string.IsNullOrEmpty(item.productId))
                {
                    products.Add(item.productId);
                }
            }

            if (products.Count > 0 && _store == null)
            {
                _store = new StoreClient(api);
                _store.Granted += sku => _ = Run(RefreshOwnedAsync(api));
                await _store.ConnectAsync(products);
            }
        }

        private async Task RefreshOwnedAsync(PlayerApi api)
        {
            ApiResult<string[]> owned = await api.OwnedAsync();
            _owned.Clear();
            foreach (string sku in owned.Value ?? Array.Empty<string>())
            {
                _owned.Add(sku);
            }
        }

        private async Task ClaimAsync(PlayerApi api, string sku)
        {
            ApiResult<string> claimed = await api.ClaimAsync(sku);
            _status = claimed.Error ?? "Unlocked!";
            await RefreshOwnedAsync(api);
        }
    }
}
