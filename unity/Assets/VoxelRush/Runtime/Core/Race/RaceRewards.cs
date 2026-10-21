using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Race
{
    /// <summary>One XP grant for a race result, keyed so the backend applies it at most once (SAVE-006).</summary>
    public readonly struct RewardGrant
    {
        public RewardGrant(string grantKey, Guid playerId, int xp)
        {
            GrantKey = grantKey;
            PlayerId = playerId;
            Xp = xp;
        }

        public string GrantKey { get; }

        public Guid PlayerId { get; }

        public int Xp { get; }
    }

    /// <summary>
    /// XP from server-validated results only. Finishers: 100 + 25 per racer beaten. Non-finishers: 10 per validated
    /// checkpoint. Placeholders for tuning; cosmetic progression only, never gameplay power (MONETIZATION_ECONOMY).
    /// </summary>
    public static class RaceRewards
    {
        public static IReadOnlyList<RewardGrant> For(Guid raceId, IReadOnlyList<RaceResult> results, Func<string, Guid?> playerOf)
        {
            var grants = new List<RewardGrant>(results.Count);
            foreach (RaceResult r in results)
            {
                if (!(playerOf(r.RacerId) is Guid player))
                {
                    continue;
                }

                int xp = r.Finished ? 100 + 25 * (results.Count - r.Place) : 10 * r.Crossings;
                if (xp > 0)
                {
                    grants.Add(new RewardGrant($"race:{raceId:N}:{player:N}", player, xp));
                }
            }

            return grants;
        }
    }
}
