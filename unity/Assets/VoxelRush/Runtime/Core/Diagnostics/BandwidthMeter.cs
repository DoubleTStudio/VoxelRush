using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Diagnostics
{
    /// <summary>
    /// Server → client application payload bytes per client over windows (VR-072). Counts what the game sends (not
    /// transport headers or resends), which is what interest management controls.
    /// </summary>
    public sealed class BandwidthMeter
    {
        private readonly Dictionary<ulong, long> _window = new Dictionary<ulong, long>();
        private double _windowStart = double.NaN;

        public long TotalBytes { get; private set; }

        /// <summary>Highest per-client rate seen in any flushed window.</summary>
        public double PeakClientBytesPerSecond { get; private set; }

        public void Record(ulong clientId, int bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            _window.TryGetValue(clientId, out long current);
            _window[clientId] = current + bytes;
            TotalBytes += bytes;
        }

        /// <summary>Closes the window: returns (clients, mean and max bytes/s per client) and starts a new one.</summary>
        public (int Clients, double MeanBytesPerSecond, double MaxBytesPerSecond) Flush(double now)
        {
            if (double.IsNaN(_windowStart))
            {
                _windowStart = now;
                _window.Clear();
                return (0, 0, 0);
            }

            double seconds = Math.Max(1e-6, now - _windowStart);
            double sum = 0, max = 0;
            foreach (long bytes in _window.Values)
            {
                double rate = bytes / seconds;
                sum += rate;
                max = Math.Max(max, rate);
            }

            int clients = _window.Count;
            PeakClientBytesPerSecond = Math.Max(PeakClientBytesPerSecond, max);
            _window.Clear();
            _windowStart = now;
            return (clients, clients == 0 ? 0 : sum / clients, max);
        }
    }
}
