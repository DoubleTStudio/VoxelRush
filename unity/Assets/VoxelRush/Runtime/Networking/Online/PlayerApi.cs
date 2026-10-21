using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace VoxelRush.Networking.Online
{
    /// <summary>One HTTP exchange with the API; swapped for a fake in tests.</summary>
    public interface IApiTransport
    {
        Task<(long Status, string Body)> SendAsync(string method, string path, string? json, string? bearer);
    }

    /// <summary>Where the refresh token lives between app launches.</summary>
    public interface ITokenStore
    {
        string? Load();

        void Save(string? refreshToken);
    }

    [Serializable]
    public sealed class WorldEntry
    {
        public string id = string.Empty;
        public string name = string.Empty;
        public string visibility = string.Empty;
        public string role = string.Empty;
    }

    [Serializable]
    public sealed class JoinGrant
    {
        public string ticket = string.Empty;
        public string endpoint = string.Empty;
        public string role = string.Empty;
    }

    [Serializable]
    public sealed class CatalogEntry
    {
        public string sku = string.Empty;
        public string kind = string.Empty;
        public string name = string.Empty;
        public CatalogUnlock unlock = new CatalogUnlock();
        public string productId = string.Empty;
    }

    [Serializable]
    public sealed class CatalogUnlock
    {
        // JsonUtility reads a missing or null level as 0: no level gate.
        public int level;
        public bool store;
    }

    [Serializable]
    public sealed class Progress
    {
        public long xp;
        public int level;
    }

    public enum PurchaseVerdict
    {
        Granted,
        AlreadyOwned,
        Pending,
        Rejected,
        Retry,
    }

    public readonly struct ApiResult<T>
    {
        public ApiResult(T? value, string? error)
        {
            Value = value;
            Error = error;
        }

        public T? Value { get; }

        /// <summary>Short reason for the player (null on success).</summary>
        public string? Error { get; }

        public bool Ok => Error == null;
    }

    /// <summary>
    /// Player-facing API client (MVP-004): guest sign-in kept across launches by its refresh token, world list and
    /// create, and join tickets. Access tokens are refreshed once on 401.
    /// </summary>
    public sealed class PlayerApi
    {
        private readonly IApiTransport _http;
        private readonly ITokenStore _tokens;
        private string? _access;

        public PlayerApi(IApiTransport http, ITokenStore tokens)
        {
            _http = http;
            _tokens = tokens;
        }

        public string? PlayerId { get; private set; }

        public bool SignedIn => _access != null;

        /// <summary>Resumes the stored account, or creates a guest one when there is none (or it was revoked).</summary>
        public async Task<ApiResult<string>> SignInAsync()
        {
            string? refresh = _tokens.Load();
            if (!string.IsNullOrEmpty(refresh) && await RefreshAsync(refresh!))
            {
                return new ApiResult<string>(PlayerId, null);
            }

            (long status, string body) = await SendSafeAsync("POST", "v1/auth/guest", "{}", null);
            if (status != 200)
            {
                return new ApiResult<string>(null, Describe(status, body));
            }

            Accept(body);
            return new ApiResult<string>(PlayerId, null);
        }

        /// <summary>A short-lived code another device can redeem to sign in as this player (SAVE-006).</summary>
        public async Task<ApiResult<string>> CreateLinkCodeAsync()
        {
            (long status, string body) = await AuthorizedAsync("POST", "v1/auth/link-code", "{}");
            return status == 200 ? new ApiResult<string>(JsonUtility.FromJson<LinkCodeReply>(body).code, null) : new ApiResult<string>(null, Describe(status, body));
        }

        /// <summary>Switches this device to the account that issued <paramref name="code"/>; the old guest is left behind.</summary>
        public async Task<ApiResult<string>> RedeemLinkCodeAsync(string code)
        {
            (long status, string body) = await SendSafeAsync("POST", "v1/auth/link", $"{{\"code\":{Quote(code.Trim())}}}", null);
            if (status != 200)
            {
                return new ApiResult<string>(null, status == 404 ? "That code is wrong or expired." : Describe(status, body));
            }

            Accept(body);
            return new ApiResult<string>(PlayerId, null);
        }

        public Task<ApiResult<WorldEntry[]>> MyWorldsAsync() => ListAsync("v1/worlds/mine");

        public Task<ApiResult<WorldEntry[]>> PublicWorldsAsync() => ListAsync("v1/worlds/public?limit=20");

        public async Task<ApiResult<WorldEntry>> CreateWorldAsync(string name, string visibility)
        {
            string json = $"{{\"name\":{Quote(name)},\"visibility\":{Quote(visibility)}}}";
            (long status, string body) = await AuthorizedAsync("POST", "v1/worlds/", json);
            return status == 201 || status == 200
                ? new ApiResult<WorldEntry>(JsonUtility.FromJson<WorldEntry>(body), null)
                : new ApiResult<WorldEntry>(null, Describe(status, body));
        }

        /// <summary>A fresh single-use ticket and the server to present it to; waits while the world's server starts.</summary>
        public async Task<ApiResult<JoinGrant>> JoinAsync(string worldId, Action<string>? progress = null, int maxWaitSeconds = 90)
        {
            string path = $"v1/worlds/{Uri.EscapeDataString(worldId)}/join";
            (long status, string body) = await AuthorizedAsync("POST", path, "{}");
            DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
            // ALLOC-001: 202 = the world's server is starting; retry when told.
            while (status == 202 && DateTime.UtcNow < deadline)
            {
                progress?.Invoke("Starting the world's server…");
                Starting starting = JsonUtility.FromJson<Starting>(body);
                await Delay(Math.Clamp(starting.retryAfterSeconds, 1, 10));
                (status, body) = await AuthorizedAsync("POST", path, "{}");
            }

            if (status == 202)
            {
                return new ApiResult<JoinGrant>(null, "The world's server is taking too long to start. Try again.");
            }

            return status == 200
                ? new ApiResult<JoinGrant>(JsonUtility.FromJson<JoinGrant>(body), null)
                : new ApiResult<JoinGrant>(null, Describe(status, body));
        }

        public async Task<ApiResult<Progress>> ProgressAsync()
        {
            (long status, string body) = await AuthorizedAsync("GET", "v1/players/me/progress", null);
            return status == 200 ? new ApiResult<Progress>(JsonUtility.FromJson<Progress>(body), null) : new ApiResult<Progress>(null, Describe(status, body));
        }

        public async Task<ApiResult<CatalogEntry[]>> CatalogAsync()
        {
            (long status, string body) = await SendSafeAsync("GET", "v1/catalog", null, null);
            return status == 200
                ? new ApiResult<CatalogEntry[]>(JsonUtility.FromJson<CatalogList>(body).items ?? Array.Empty<CatalogEntry>(), null)
                : new ApiResult<CatalogEntry[]>(null, Describe(status, body));
        }

        /// <summary>SKUs this player owns (any source).</summary>
        public async Task<ApiResult<string[]>> OwnedAsync()
        {
            (long status, string body) = await AuthorizedAsync("GET", "v1/players/me/entitlements", null);
            if (status != 200)
            {
                return new ApiResult<string[]>(null, Describe(status, body));
            }

            OwnedList list = JsonUtility.FromJson<OwnedList>("{\"items\":" + body + "}");
            return new ApiResult<string[]>(Array.ConvertAll(list.items ?? Array.Empty<OwnedEntry>(), e => e.sku), null);
        }

        /// <summary>Unlocks a level-gated item; the server checks the level.</summary>
        public async Task<ApiResult<string>> ClaimAsync(string sku)
        {
            (long status, string body) = await AuthorizedAsync("POST", "v1/players/me/entitlements/claim", $"{{\"sku\":{Quote(sku)}}}");
            return status == 200 || status == 201 ? new ApiResult<string>(sku, null) : new ApiResult<string>(null, Describe(status, body));
        }

        /// <summary>
        /// Sends a store receipt for server verification (ECON-002): Apple's signed transaction, or Google's product
        /// and purchase token. Only Granted/AlreadyOwned mean the store order may be confirmed.
        /// </summary>
        public async Task<(PurchaseVerdict Verdict, string? Error)> SubmitPurchaseAsync(string platform, string? signedTransaction, string? productId, string? purchaseToken)
        {
            var json = new StringBuilder("{\"platform\":").Append(Quote(platform));
            if (signedTransaction != null)
            {
                json.Append(",\"signedTransaction\":").Append(Quote(signedTransaction));
            }

            if (productId != null)
            {
                json.Append(",\"productId\":").Append(Quote(productId));
            }

            if (purchaseToken != null)
            {
                json.Append(",\"purchaseToken\":").Append(Quote(purchaseToken));
            }

            (long status, string body) = await AuthorizedAsync("POST", "v1/players/me/purchases", json.Append('}').ToString());
            return status switch
            {
                200 when body.Contains("\"granted\":true") => (PurchaseVerdict.Granted, null),
                200 => (PurchaseVerdict.AlreadyOwned, null),
                202 => (PurchaseVerdict.Pending, null),
                0 or 429 or >= 500 => (PurchaseVerdict.Retry, Describe(status, body)),
                _ => (PurchaseVerdict.Rejected, Describe(status, body)),
            };
        }

        private async Task<ApiResult<WorldEntry[]>> ListAsync(string path)
        {
            (long status, string body) = await AuthorizedAsync("GET", path, null);
            if (status != 200)
            {
                return new ApiResult<WorldEntry[]>(null, Describe(status, body));
            }

            // JsonUtility can't read a top-level array.
            WorldList list = JsonUtility.FromJson<WorldList>("{\"items\":" + body + "}");
            return new ApiResult<WorldEntry[]>(list.items ?? Array.Empty<WorldEntry>(), null);
        }

        private async Task<(long, string)> AuthorizedAsync(string method, string path, string? json)
        {
            if (_access == null)
            {
                ApiResult<string> signIn = await SignInAsync();
                if (!signIn.Ok)
                {
                    return (0, signIn.Error!);
                }
            }

            (long status, string body) = await SendSafeAsync(method, path, json, _access);
            string? refresh = _tokens.Load();
            if (status == 401 && !string.IsNullOrEmpty(refresh) && await RefreshAsync(refresh!))
            {
                (status, body) = await SendSafeAsync(method, path, json, _access);
            }

            return (status, body);
        }

        private async Task<bool> RefreshAsync(string refresh)
        {
            (long status, string body) = await SendSafeAsync("POST", "v1/auth/refresh", $"{{\"refreshToken\":{Quote(refresh)}}}", null);
            if (status != 200)
            {
                _access = null;
                return false;
            }

            Accept(body);
            return true;
        }

        private void Accept(string body)
        {
            var pair = JsonUtility.FromJson<TokenPair>(body);
            _access = pair.accessToken;
            PlayerId = pair.playerId;
            // Refresh tokens rotate: keep only the newest.
            _tokens.Save(pair.refreshToken);
        }

        private async Task<(long, string)> SendSafeAsync(string method, string path, string? json, string? bearer)
        {
            try
            {
                return await _http.SendAsync(method, path, json, bearer);
            }
            catch (Exception e)
            {
                return (0, e.Message);
            }
        }

        /// <summary>Waits between retries; tests replace it to run instantly.</summary>
        public Func<int, Task> Delay { get; set; } = seconds => Task.Delay(TimeSpan.FromSeconds(seconds));

        private static string Describe(long status, string body) => status switch
        {
            _ when body.Contains("not_unlocked") => "Reach the required level first.",
            _ when body.Contains("store_disabled") => "Purchases aren't available yet.",
            0 => "Can't reach the server. Check the address and your connection.",
            401 or 403 => "Not allowed.",
            404 => "World not found.",
            409 => "Conflict. Try again.",
            429 => "Too many requests. Wait a moment.",
            503 => "No game server is free right now. Try again soon.",
            _ when body.Contains("\"title\":\"") => body.Split(new[] { "\"title\":\"" }, StringSplitOptions.None)[1].Split('"')[0].Replace('_', ' '),
            _ => $"Server error ({status}).",
        };

        private static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                sb.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    _ when c < ' ' => $"\\u{(int)c:x4}",
                    _ => c.ToString(),
                });
            }

            return sb.Append('"').ToString();
        }

        [Serializable]
        private sealed class TokenPair
        {
            public string playerId = string.Empty;
            public string accessToken = string.Empty;
            public string refreshToken = string.Empty;
        }

        [Serializable]
        private sealed class LinkCodeReply
        {
            public string code = string.Empty;
        }

        [Serializable]
        private sealed class CatalogList
        {
            public CatalogEntry[]? items;
        }

        [Serializable]
        private sealed class OwnedEntry
        {
            public string sku = string.Empty;
        }

        [Serializable]
        private sealed class OwnedList
        {
            public OwnedEntry[]? items;
        }

        [Serializable]
        private sealed class Starting
        {
            public int retryAfterSeconds = 2;
        }

        [Serializable]
        private sealed class WorldList
        {
            public WorldEntry[]? items;
        }
    }

    /// <summary>UnityWebRequest transport (works on Android and iOS, unlike some HttpClient handlers).</summary>
    public sealed class UnityApiTransport : IApiTransport
    {
        private readonly string _base;

        public UnityApiTransport(string baseUrl) => _base = baseUrl.TrimEnd('/') + "/";

        public async Task<(long Status, string Body)> SendAsync(string method, string path, string? json, string? bearer)
        {
            using var request = new UnityWebRequest(_base + path, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = 10 };
            if (json != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.SetRequestHeader("Content-Type", "application/json");
            }

            if (bearer != null)
            {
                request.SetRequestHeader("Authorization", "Bearer " + bearer);
            }

            await request.SendWebRequest();
            return (request.responseCode, request.downloadHandler.text ?? string.Empty);
        }
    }

    /// <summary>Device-local refresh token (PlayerPrefs; platform secure storage is a follow-up).</summary>
    public sealed class PrefsTokenStore : ITokenStore
    {
        private readonly string _key;

        public PrefsTokenStore(string apiUrl) => _key = "voxelrush.refresh." + apiUrl.Trim().TrimEnd('/');

        public string? Load()
        {
            string value = PlayerPrefs.GetString(_key, string.Empty);
            return value.Length > 0 ? value : null;
        }

        public void Save(string? refreshToken)
        {
            if (refreshToken == null)
            {
                PlayerPrefs.DeleteKey(_key);
            }
            else
            {
                PlayerPrefs.SetString(_key, refreshToken);
            }

            PlayerPrefs.Save();
        }
    }
}
