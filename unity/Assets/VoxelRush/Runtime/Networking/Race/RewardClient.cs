using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Race;

namespace VoxelRush.Networking.Race
{
    /// <summary>
    /// Sends queued race rewards to the backend (SAVE-006) from a background loop, never on the server tick.
    /// Confirmed and permanently rejected grants leave the outbox; network errors and 5xx retry with backoff.
    /// </summary>
    public sealed class RewardClient : IDisposable
    {
        public const string ApiUrlEnvironment = "VOXELRUSH_API_URL";
        public const string ServiceTokenEnvironment = "VOXELRUSH_SERVICE_TOKEN";

        private readonly RewardOutbox _outbox;
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Task _loop;

        public RewardClient(string apiUrl, string serviceToken, RewardOutbox outbox)
        {
            _outbox = outbox;
            _http = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.Add("X-Service-Token", serviceToken);
            _loop = Task.Run(RunAsync);
        }

        public long Sent { get; private set; }

        public static RewardClient? FromEnvironment(string outboxPath)
        {
            string? url = Environment.GetEnvironmentVariable(ApiUrlEnvironment);
            string? token = Environment.GetEnvironmentVariable(ServiceTokenEnvironment);
            return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token) ? null : new RewardClient(url, token, new RewardOutbox(outboxPath));
        }

        public void Enqueue(System.Collections.Generic.IReadOnlyList<RewardGrant> grants) => _outbox.Add(grants);

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            _http.Dispose();
        }

        private async Task RunAsync()
        {
            int backoffSeconds = 1;
            while (!_stop.IsCancellationRequested)
            {
                bool failed = false;
                foreach (RewardGrant grant in _outbox.Snapshot())
                {
                    string body = "{\"grantKey\":\"" + grant.GrantKey + "\",\"kind\":\"xp\",\"amount\":" + grant.Xp + ",\"source\":\"race\"}";
                    try
                    {
                        using HttpResponseMessage response = await _http.PostAsync($"internal/v1/players/{grant.PlayerId}/grants",
                            new StringContent(body, Encoding.UTF8, "application/json"), _stop.Token);
                        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.BadRequest)
                        {
                            _outbox.Complete(grant.GrantKey);
                            Sent++;
                            Debug.Log(LogEvent.Format("reward_grant_sent", ("grantKey", grant.GrantKey), ("xp", grant.Xp), ("status", (int)response.StatusCode)));
                        }
                        else
                        {
                            failed = true;
                        }
                    }
                    catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
                    {
                        failed = true;
                    }
                }

                backoffSeconds = failed ? Math.Min(backoffSeconds * 2, 30) : 1;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(failed ? backoffSeconds : 1), _stop.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }
    }
}
