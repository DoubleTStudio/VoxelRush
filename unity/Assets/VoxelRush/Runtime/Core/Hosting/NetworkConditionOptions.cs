using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoxelRush.Core.Hosting
{
    /// <summary>
    /// Development-only simulated network conditions for gameplay messages: <c>-simRttMs</c>, <c>-simJitterMs</c>,
    /// <c>-simLossPercent</c>. Callers must ignore these in release builds.
    /// </summary>
    public sealed class NetworkConditionOptions
    {
        public const string RttArgument = "-simRttMs";
        public const string JitterArgument = "-simJitterMs";
        public const string LossArgument = "-simLossPercent";
        public const int MaxRttMs = 2000;

        public static NetworkConditionOptions None { get; } = new NetworkConditionOptions(0, 0, 0);

        public NetworkConditionOptions(int rttMs, int jitterMs, int lossPercent)
        {
            if (rttMs < 0 || rttMs > MaxRttMs || jitterMs < 0 || jitterMs * 2 > rttMs || lossPercent < 0 || lossPercent > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(rttMs));
            }

            RttMs = rttMs;
            JitterMs = jitterMs;
            LossPercent = lossPercent;
        }

        public int RttMs { get; }

        public int JitterMs { get; }

        public int LossPercent { get; }

        public bool IsActive => RttMs > 0 || LossPercent > 0;

        /// <summary>Half the RTT is applied each way (outbound input, inbound snapshots).</summary>
        public double OneWaySeconds => RttMs / 2000.0;

        public double OneWayJitterSeconds => JitterMs / 1000.0;

        public static bool TryParse(IReadOnlyList<string> args, out NetworkConditionOptions options, out string error)
        {
            options = None;
            error = string.Empty;
            int rtt = 0;
            int jitter = 0;
            int loss = 0;
            for (int i = 0; i < args.Count; i++)
            {
                ref int target = ref rtt;
                if (string.Equals(args[i], JitterArgument, StringComparison.OrdinalIgnoreCase))
                {
                    target = ref jitter;
                }
                else if (string.Equals(args[i], LossArgument, StringComparison.OrdinalIgnoreCase))
                {
                    target = ref loss;
                }
                else if (!string.Equals(args[i], RttArgument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 >= args.Count || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out target))
                {
                    error = $"{args[i]} requires a non-negative integer.";
                    return false;
                }

                i++;
            }

            if (rtt > MaxRttMs || jitter * 2 > rtt || loss > 100)
            {
                error = $"invalid network conditions (rtt {rtt} ≤ {MaxRttMs}, jitter ≤ rtt/2, loss ≤ 100).";
                return false;
            }

            options = new NetworkConditionOptions(rtt, jitter, loss);
            return true;
        }
    }
}
