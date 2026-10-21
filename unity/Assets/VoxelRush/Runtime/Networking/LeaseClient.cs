using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Networking
{
    public enum LeaseAcquire
    {
        Granted,
        HeldByOther,
        Unavailable,
    }

    /// <summary>
    /// SAVE-005: holds this server's lease on its world through the API. Renewals run in the background; if the lease
    /// is lost (another server took over, or it expired while the API was unreachable) <see cref="Lost"/> turns true and
    /// the host must stop serving the world.
    /// </summary>
    public sealed class LeaseClient : IDisposable
    {
        public const int TtlSeconds = 30;

        private readonly HttpClient _http;
        private readonly Guid _world;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task? _renewals;
        private DateTimeOffset _expiresAt;

        public LeaseClient(string apiUrl, string serviceToken, Guid world)
        {
            _world = world;
            Holder = $"{Environment.MachineName}:{System.Diagnostics.Process.GetCurrentProcess().Id}:{Guid.NewGuid().ToString("N").Substring(0, 8)}";
            _http = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(5) };
            _http.DefaultRequestHeaders.Add("X-Service-Token", serviceToken);
        }

        public string Holder { get; }

        public long Fencing { get; private set; }

        public string? HeldBy { get; private set; }

        public volatile bool Lost;

        public LeaseAcquire Acquire()
        {
            try
            {
                using HttpResponseMessage response = Post("acquire", $"{{\"holder\":\"{Holder}\",\"ttlSeconds\":{TtlSeconds}}}").GetAwaiter().GetResult();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    HeldBy = Field(body, "holder");
                    return LeaseAcquire.HeldByOther;
                }

                if (!response.IsSuccessStatusCode || !long.TryParse(Field(body, "fencing"), out long fencing))
                {
                    return LeaseAcquire.Unavailable;
                }

                Fencing = fencing;
                _expiresAt = DateTimeOffset.UtcNow.AddSeconds(TtlSeconds);
                _renewals = Task.Run(RenewLoopAsync);
                return LeaseAcquire.Granted;
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
            {
                return LeaseAcquire.Unavailable;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _renewals?.Wait(TimeSpan.FromSeconds(2));
                if (Fencing > 0 && !Lost)
                {
                    using HttpResponseMessage released = Post("release", $"{{\"holder\":\"{Holder}\",\"fencing\":{Fencing}}}").GetAwaiter().GetResult();
                    Debug.Log(LogEvent.Format("server_lease_released", ("fencing", Fencing), ("status", (int)released.StatusCode)));
                }
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is AggregateException)
            {
                // The lease expires on its own; the next server waits at most one TTL.
            }

            _http.Dispose();
        }

        private async Task RenewLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(TtlSeconds / 3.0), _stop.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                try
                {
                    using HttpResponseMessage response = await Post("renew", $"{{\"holder\":\"{Holder}\",\"fencing\":{Fencing},\"ttlSeconds\":{TtlSeconds}}}");
                    if (response.IsSuccessStatusCode)
                    {
                        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(TtlSeconds);
                        continue;
                    }

                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        Lost = true;
                        return;
                    }
                }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
                {
                }

                // Unreachable API: still ours until the last confirmed expiry, then assume another server may take over.
                if (DateTimeOffset.UtcNow >= _expiresAt)
                {
                    Lost = true;
                    return;
                }
            }
        }

        private Task<HttpResponseMessage> Post(string action, string json) =>
            _http.PostAsync($"internal/v1/worlds/{_world}/lease/{action}", new StringContent(json, Encoding.UTF8, "application/json"));

        // Minimal field read: the API's lease JSON is flat.
        internal static string? Field(string json, string name)
        {
            Match m = Regex.Match(json, $"\"{name}\"\\s*:\\s*\"?([^\",}}]+)");
            // The API's JSON escapes characters such as & as \u0026 (presigned URLs).
            return m.Success ? Regex.Replace(m.Groups[1].Value, @"\\u([0-9a-fA-F]{4})", u => ((char)Convert.ToInt32(u.Groups[1].Value, 16)).ToString()) : null;
        }
    }
}
