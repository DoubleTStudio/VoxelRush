using System;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Core.Race;

namespace VoxelRush.Tests.Domain.Race
{
    public sealed class RaceRewardsTests
    {
        [Test]
        public void Finishers_ByPlace_NonFinishers_ByCheckpoints_UnknownPlayersSkipped()
        {
            var race = Guid.NewGuid();
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            var results = new[]
            {
                new RaceResult(a.ToString("N"), 1, true, 400, 3),
                new RaceResult(b.ToString("N"), 2, true, 450, 3),
                new RaceResult("dev-session", 3, false, 0, 1),
                new RaceResult(Guid.NewGuid().ToString("N"), 4, false, 0, 0),
            };

            var grants = RaceRewards.For(race, results, id => id == "dev-session" ? null : Guid.ParseExact(id, "N"));

            Assert.That(grants.Select(g => (g.PlayerId, g.Xp)), Is.EqualTo(new[] { (a, 175), (b, 150) }), "0-XP and unknown racers get no grant");
            Assert.That(grants[0].GrantKey, Is.EqualTo($"race:{race:N}:{a:N}"), "stable key: retries can't double-grant");
            Assert.That(grants.All(g => g.GrantKey.Length <= 128), Is.True);
        }
    }
}
