using System;
using System.Globalization;

namespace VoxelRush.Core.Hosting
{
    /// <summary>Validated <c>host:port</c> a client connects to; host is an IPv4 address or DNS name.</summary>
    public readonly struct ServerEndpoint : IEquatable<ServerEndpoint>
    {
        private const int MaxHostLength = 253;

        public ServerEndpoint(string host, ushort port)
        {
            if (!IsValidHost(host))
            {
                throw new ArgumentException($"Invalid host '{host}'.", nameof(host));
            }

            if (port == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            Host = host;
            Port = port;
        }

        public string Host { get; }

        public ushort Port { get; }

        public static bool TryParse(string? text, out ServerEndpoint endpoint, out string error)
        {
            endpoint = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "endpoint is empty";
                return false;
            }

            int colon = text!.LastIndexOf(':');
            if (colon <= 0 || colon == text.Length - 1 || text.IndexOf(':') != colon)
            {
                error = $"expected host:port (got '{text}')";
                return false;
            }

            string host = text.Substring(0, colon);
            if (!IsValidHost(host))
            {
                error = $"invalid host '{host}'";
                return false;
            }

            if (!ushort.TryParse(text.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out ushort port) || port == 0)
            {
                error = $"port must be an integer in 1..65535 (got '{text.Substring(colon + 1)}')";
                return false;
            }

            endpoint = new ServerEndpoint(host, port);
            error = string.Empty;
            return true;
        }

        public override string ToString() => $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

        public bool Equals(ServerEndpoint other) => string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase) && Port == other.Port;

        public override bool Equals(object? obj) => obj is ServerEndpoint other && Equals(other);

        public override int GetHashCode() => (StringComparer.OrdinalIgnoreCase.GetHashCode(Host ?? string.Empty) * 397) ^ Port;

        private static bool IsValidHost(string? host)
        {
            if (string.IsNullOrEmpty(host) || host!.Length > MaxHostLength || host[0] == '.' || host[0] == '-' || host[host.Length - 1] == '.')
            {
                return false;
            }

            foreach (char c in host)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
