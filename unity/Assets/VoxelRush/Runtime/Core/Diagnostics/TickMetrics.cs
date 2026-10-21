using System;

namespace VoxelRush.Core.Diagnostics
{
    /// <summary>
    /// Server tick telemetry (SYSTEM.md Observability): per-window tick-duration percentiles, overruns against the
    /// tick budget and achieved tick rate. Fixed-size buffers: recording never allocates.
    /// </summary>
    public sealed class TickMetrics
    {
        private readonly double[] _durations;
        private readonly double[] _sorted;
        private int _count;
        private long _overruns;
        private double _windowStart = double.NaN;

        public TickMetrics(double budgetMilliseconds, int capacity = 1024)
        {
            if (!(budgetMilliseconds > 0) || capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds));
            }

            BudgetMilliseconds = budgetMilliseconds;
            _durations = new double[capacity];
            _sorted = new double[capacity];
        }

        public double BudgetMilliseconds { get; }

        public long TotalTicks { get; private set; }

        public long TotalOverruns { get; private set; }

        /// <summary>Records one tick's work time; ticks beyond capacity still count toward totals and overruns.</summary>
        public void Record(double durationMilliseconds, double nowSeconds)
        {
            if (double.IsNaN(_windowStart))
            {
                _windowStart = nowSeconds;
            }

            if (_count < _durations.Length)
            {
                _durations[_count] = durationMilliseconds;
            }

            _count++;
            TotalTicks++;
            if (durationMilliseconds > BudgetMilliseconds)
            {
                _overruns++;
                TotalOverruns++;
            }
        }

        /// <summary>Summarises the window since the last call and starts a new one.</summary>
        public TickWindow Flush(double nowSeconds)
        {
            int stored = Math.Min(_count, _durations.Length);
            Array.Copy(_durations, _sorted, stored);
            Array.Sort(_sorted, 0, stored);
            double elapsed = double.IsNaN(_windowStart) ? 0 : nowSeconds - _windowStart;
            var window = new TickWindow(
                _count,
                elapsed > 0 ? _count / elapsed : 0,
                Percentile(stored, 0.50),
                Percentile(stored, 0.95),
                Percentile(stored, 0.99),
                stored > 0 ? _sorted[stored - 1] : 0,
                _overruns);

            _count = 0;
            _overruns = 0;
            _windowStart = nowSeconds;
            return window;
        }

        // Nearest-rank percentile over the sorted window.
        private double Percentile(int stored, double p)
        {
            if (stored == 0)
            {
                return 0;
            }

            int rank = (int)Math.Ceiling(p * stored) - 1;
            return _sorted[rank < 0 ? 0 : rank];
        }
    }

    public readonly struct TickWindow
    {
        public TickWindow(int ticks, double ticksPerSecond, double p50, double p95, double p99, double max, long overruns)
        {
            Ticks = ticks;
            TicksPerSecond = ticksPerSecond;
            P50 = p50;
            P95 = p95;
            P99 = p99;
            Max = max;
            Overruns = overruns;
        }

        public int Ticks { get; }

        public double TicksPerSecond { get; }

        public double P50 { get; }

        public double P95 { get; }

        public double P99 { get; }

        public double Max { get; }

        public long Overruns { get; }
    }
}
