using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoxelRush.Core.Hosting
{
    /// <summary>
    /// Development-only packet loss/delay applied inside Unity Transport (every channel, reliable ones included):
    /// <c>-simTransportLossPercent</c>, <c>-simTransportDelayMs</c>. Unlike <see cref="NetworkConditionOptions"/>,
    /// which delays vehicle messages in the game layer, this exercises the transport's real resend and sequencing.
    /// </summary>
    public sealed class TransportSimulationOptions
    {
        public const string LossArgument = "-simTransportLossPercent";
        public const string DelayArgument = "-simTransportDelayMs";
        public const int MaxLossPercent = 50;
        public const int MaxDelayMs = 1000;

        public TransportSimulationOptions(int lossPercent, int delayMs)
        {
            if (lossPercent < 0 || lossPercent > MaxLossPercent || delayMs < 0 || delayMs > MaxDelayMs)
            {
                throw new ArgumentOutOfRangeException(nameof(lossPercent));
            }

            LossPercent = lossPercent;
            DelayMs = delayMs;
        }

        public int LossPercent { get; }

        /// <summary>Applied to sent and received packets alike, so it adds twice this to the round trip.</summary>
        public int DelayMs { get; }

        public bool IsActive => LossPercent > 0 || DelayMs > 0;

        public static bool TryParse(IReadOnlyList<string> args, out TransportSimulationOptions options, out string error)
        {
            options = new TransportSimulationOptions(0, 0);
            error = string.Empty;
            int loss = 0, delay = 0;
            for (int i = 0; i < args.Count; i++)
            {
                bool isLoss = string.Equals(args[i], LossArgument, StringComparison.OrdinalIgnoreCase);
                if (!isLoss && !string.Equals(args[i], DelayArgument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 >= args.Count || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                {
                    error = $"{args[i]} requires a non-negative integer.";
                    return false;
                }

                if (isLoss)
                {
                    loss = value;
                }
                else
                {
                    delay = value;
                }

                i++;
            }

            if (loss > MaxLossPercent || delay > MaxDelayMs)
            {
                error = $"transport simulation out of range (loss ≤ {MaxLossPercent}%, delay ≤ {MaxDelayMs} ms).";
                return false;
            }

            options = new TransportSimulationOptions(loss, delay);
            return true;
        }
    }
}
