using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Simulation
{
    /// <summary>
    /// Development-only network condition simulator for gameplay messages: holds each item for a delay ± jitter and
    /// drops a percentage with a seeded RNG (reproducible). Order of release follows release time, like a real network.
    /// </summary>
    public sealed class DelayLine<T>
    {
        private readonly List<(double ReleaseAt, long Order, T Item)> _items = new List<(double, long, T)>();
        private readonly Random _random;
        private long _order;

        public DelayLine(double delaySeconds, double jitterSeconds = 0, double lossPercent = 0, int seed = 1)
        {
            if (delaySeconds < 0 || jitterSeconds < 0 || jitterSeconds > delaySeconds || lossPercent < 0 || lossPercent > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(delaySeconds));
            }

            DelaySeconds = delaySeconds;
            JitterSeconds = jitterSeconds;
            LossPercent = lossPercent;
            _random = new Random(seed);
        }

        public double DelaySeconds { get; }

        public double JitterSeconds { get; }

        public double LossPercent { get; }

        public int InFlight => _items.Count;

        public long Dropped { get; private set; }

        public void Send(T item, double nowSeconds)
        {
            if (LossPercent > 0 && _random.NextDouble() * 100 < LossPercent)
            {
                Dropped++;
                return;
            }

            double jitter = JitterSeconds > 0 ? (_random.NextDouble() * 2 - 1) * JitterSeconds : 0;
            _items.Add((nowSeconds + DelaySeconds + jitter, _order++, item));
        }

        /// <summary>Moves every item due at <paramref name="nowSeconds"/> into <paramref name="ready"/>, in release order.</summary>
        public int Receive(double nowSeconds, List<T> ready)
        {
            ready.Clear();
            _items.Sort((a, b) => a.ReleaseAt != b.ReleaseAt ? a.ReleaseAt.CompareTo(b.ReleaseAt) : a.Order.CompareTo(b.Order));
            int due = 0;
            while (due < _items.Count && _items[due].ReleaseAt <= nowSeconds)
            {
                ready.Add(_items[due].Item);
                due++;
            }

            _items.RemoveRange(0, due);
            return due;
        }
    }
}
