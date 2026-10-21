using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using VoxelRush.World.Chunks;
using VoxelRush.World.Persistence;

namespace VoxelRush.Networking
{
    /// <summary>
    /// SAVE-003: snapshots through the API's presigned object storage. Writes carry this server's lease, so a server
    /// that lost its world can't overwrite the new holder's snapshots; downloads are checked against the indexed SHA-256.
    /// </summary>
    public sealed class ApiSnapshotRemote : ISnapshotRemote, IDisposable
    {
        private const int MaxSnapshotBytes = 1024 * 1024;

        private readonly HttpClient _api;
        private readonly HttpClient _storage = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly Guid _world;

        public ApiSnapshotRemote(string apiUrl, string serviceToken, Guid world, string leaseHolder, long fencing)
        {
            _world = world;
            _api = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
            _api.DefaultRequestHeaders.Add("X-Service-Token", serviceToken);
            _api.DefaultRequestHeaders.Add("X-Lease-Holder", leaseHolder);
            _api.DefaultRequestHeaders.Add("X-Lease-Fencing", fencing.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>False when the API runs without object storage (501 storage_disabled); throws when unreachable.</summary>
        public bool StorageEnabled()
        {
            Send(HttpMethod.Get, "latest?x=0&y=0&z=0", null, out HttpStatusCode status);
            return status != HttpStatusCode.NotImplemented;
        }

        public byte[]? Download(ChunkCoord coord)
        {
            string body = Send(HttpMethod.Get, $"latest?x={coord.X}&y={coord.Y}&z={coord.Z}", null, out HttpStatusCode status);
            if (status == HttpStatusCode.NotFound)
            {
                return null;
            }

            string? url = LeaseClient.Field(body, "url");
            string? sha = LeaseClient.Field(body, "sha256");
            if (url == null || sha == null)
            {
                throw new IOException("snapshot index response incomplete");
            }

            byte[] bytes = Retry(() =>
            {
                using HttpResponseMessage response = _storage.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxSnapshotBytes)
                {
                    throw new IOException($"snapshot download failed ({(int)response.StatusCode})");
                }

                return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            });
            if (!string.Equals(Hex(bytes), sha, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("snapshot download checksum mismatch");
            }

            return bytes;
        }

        public void Upload(ChunkCoord coord, ulong revision, byte[] bytes)
        {
            string position = $"\"x\":{coord.X},\"y\":{coord.Y},\"z\":{coord.Z},\"revision\":{revision}";
            string body = Send(HttpMethod.Post, "upload", $"{{{position},\"sha256\":\"{Hex(bytes)}\",\"sizeBytes\":{bytes.Length}}}", out HttpStatusCode status);
            string? url = LeaseClient.Field(body, "url");
            if (status != HttpStatusCode.OK || url == null)
            {
                throw new IOException($"snapshot upload refused ({(int)status})");
            }

            Retry(() =>
            {
                using HttpResponseMessage put = _storage.PutAsync(url, new ByteArrayContent(bytes)).GetAwaiter().GetResult();
                return put.IsSuccessStatusCode ? true : throw new IOException($"snapshot PUT failed ({(int)put.StatusCode})");
            });
            Send(HttpMethod.Post, "commit", $"{{{position}}}", out status);
            if (status != HttpStatusCode.NoContent)
            {
                throw new IOException($"snapshot commit refused ({(int)status})");
            }
        }

        public void Dispose()
        {
            _api.Dispose();
            _storage.Dispose();
        }

        private string Send(HttpMethod method, string path, string? json, out HttpStatusCode status)
        {
            (string body, HttpStatusCode code) = Wrap(() =>
            {
                using var request = new HttpRequestMessage(method, $"internal/v1/worlds/{_world}/snapshots/{path}");
                if (json != null)
                {
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                using HttpResponseMessage response = _api.SendAsync(request).GetAwaiter().GetResult();
                return (response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), response.StatusCode);
            });
            status = code;
            if ((int)code >= 500 && code != HttpStatusCode.NotImplemented)
            {
                throw new IOException($"snapshot API error ({(int)code})");
            }

            return body;
        }

        // Object stores return transient 500/503s (S3 asks clients to retry); presigned URLs stay valid for minutes.
        private static T Retry<T>(Func<T> call)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return Wrap(call);
                }
                catch (IOException) when (attempt < 3)
                {
                    System.Threading.Thread.Sleep(500 * attempt);
                }
            }
        }

        // Network failures surface as IOException, which callers already treat as "storage unavailable".
        private static T Wrap<T>(Func<T> call)
        {
            try
            {
                return call();
            }
            catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException)
            {
                throw new IOException(e.Message, e);
            }
        }

        private static string Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var sb = new StringBuilder(64);
            foreach (byte b in sha.ComputeHash(bytes))
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
