using System.Collections.Generic;

namespace VoxelRush.Core.Cosmetics
{
    public enum HornVerdict
    {
        Played,
        /// <summary>The car shows no horn (not equipped, or not owned).</summary>
        NoHorn,
        RateLimited,
    }

    /// <summary>
    /// Server-side (COSM-002): a horn press is relayed to other players only if the car shows a horn the server
    /// approved, and at most once per <see cref="MinIntervalSeconds"/> per player, whatever the client sends.
    /// </summary>
    public sealed class HornLimiter
    {
        public const double MinIntervalSeconds = 1;

        private readonly Dictionary<ulong, double> _nextAt = new Dictionary<ulong, double>();

        /// <summary>Presses dropped by the rate limit (for metrics).</summary>
        public long Limited { get; private set; }

        public HornVerdict Press(ulong clientId, bool hornShown, double now)
        {
            if (!hornShown)
            {
                return HornVerdict.NoHorn;
            }

            if (_nextAt.TryGetValue(clientId, out double next) && now < next)
            {
                Limited++;
                return HornVerdict.RateLimited;
            }

            _nextAt[clientId] = now + MinIntervalSeconds;
            return HornVerdict.Played;
        }

        public void Forget(ulong clientId) => _nextAt.Remove(clientId);
    }
}
