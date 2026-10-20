using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Performance
{
    /// <summary>Frame-time summary for device benchmarks (QUALITY_GATES: p95/p99 frames).</summary>
    public readonly struct FrameStats
    {
        public FrameStats(int frames, float p50, float p95, float p99, float max, double meanFps, float overBudgetPercent)
        {
            Frames = frames;
            P50 = p50;
            P95 = p95;
            P99 = p99;
            Max = max;
            MeanFps = meanFps;
            OverBudgetPercent = overBudgetPercent;
        }

        public int Frames { get; }

        public float P50 { get; }

        public float P95 { get; }

        public float P99 { get; }

        public float Max { get; }

        public double MeanFps { get; }

        /// <summary>Frames slower than the budget (e.g. 33.3 ms at 30 fps), in percent.</summary>
        public float OverBudgetPercent { get; }

        public static FrameStats Summarize(IReadOnlyList<float> frameMs, float budgetMs)
        {
            if (frameMs.Count == 0)
            {
                return default;
            }

            var sorted = new List<float>(frameMs);
            sorted.Sort();
            double total = 0;
            int over = 0;
            foreach (float f in sorted)
            {
                total += f;
                over += f > budgetMs ? 1 : 0;
            }

            float At(double q) => sorted[Math.Min(sorted.Count - 1, Math.Max(0, (int)Math.Ceiling(sorted.Count * q) - 1))];
            return new FrameStats(sorted.Count, At(0.5), At(0.95), At(0.99), sorted[sorted.Count - 1], 1000.0 * sorted.Count / total, 100f * over / sorted.Count);
        }
    }
}
