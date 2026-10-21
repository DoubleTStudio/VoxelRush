using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Networking.Race;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>
    /// Server-side (COSM-001, COSM-002): what a player owns, from the API's internal entitlements endpoint (service token).
    /// Lookups run on the thread pool; null means the API couldn't answer and the caller retries later.
    /// </summary>
    public sealed class EntitlementClient : IDisposable
    {
        private readonly HttpClient _http;

        public EntitlementClient(string apiUrl, string serviceToken)
        {
            _http = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 64 * 1024 };
            _http.DefaultRequestHeaders.Add("X-Service-Token", serviceToken);
        }

        public static EntitlementClient? FromEnvironment()
        {
            string? url = Environment.GetEnvironmentVariable(RewardClient.ApiUrlEnvironment);
            string? token = Environment.GetEnvironmentVariable(RewardClient.ServiceTokenEnvironment);
            return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token) ? null : new EntitlementClient(url, token);
        }

        // Task.Run: even the synchronous start of a request (DNS, connect) stays off the tick thread.
        public Task<IReadOnlyCollection<string>?> OwnedAsync(Guid player, CancellationToken ct) => Task.Run(async () =>
        {
            try
            {
                using HttpResponseMessage response = await _http.GetAsync($"internal/v1/players/{player}/entitlements", ct);
                return response.IsSuccessStatusCode ? CosmeticCatalog.ParseOwned(await response.Content.ReadAsStringAsync()) : null;
            }
            catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException)
            {
                return null;
            }
        }, ct);

        public void Dispose() => _http.Dispose();
    }
}
