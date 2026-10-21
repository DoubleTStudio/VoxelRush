using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Core.Race;

namespace VoxelRush.Tests.Domain.Race
{
    public sealed class RewardOutboxTests
    {
        private string _path = null!;

        [SetUp]
        public void SetUp() => _path = Path.Combine(Path.GetTempPath(), "outbox-" + Guid.NewGuid().ToString("N") + ".txt");

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }

        private static RewardGrant G(string key, int xp = 100) => new RewardGrant(key, Guid.NewGuid(), xp);

        [Test]
        public void PendingGrants_SurviveARestart_UntilConfirmed()
        {
            var outbox = new RewardOutbox(_path);
            outbox.Add(new[] { G("race:1:a"), G("race:1:b", 150) });
            outbox.Complete("race:1:a");

            var reopened = new RewardOutbox(_path);

            Assert.That(reopened.Snapshot().Select(g => (g.GrantKey, g.Xp)), Is.EqualTo(new[] { ("race:1:b", 150) }));
        }

        [Test]
        public void DuplicateKeys_AreQueuedOnce()
        {
            var outbox = new RewardOutbox(_path);
            outbox.Add(new[] { G("race:1:a") });
            outbox.Add(new[] { G("race:1:a") });

            Assert.That(outbox.Count, Is.EqualTo(1));
            Assert.That(new RewardOutbox(_path).Count, Is.EqualTo(1));
        }

        [Test]
        public void TornOrGarbageLines_AreSkipped()
        {
            var good = G("race:2:x", 75);
            File.WriteAllText(_path, $"race:2:x {good.PlayerId:N} 75\nrace:2:y not-a-guid 10\nrace:2:z {Guid.NewGuid():N} -5\nrace:2:");

            Assert.That(new RewardOutbox(_path).Snapshot().Select(g => g.GrantKey), Is.EqualTo(new[] { "race:2:x" }));
        }

        [Test]
        public void CompletingTheLastGrant_LeavesAnEmptyQueue()
        {
            var outbox = new RewardOutbox(_path);
            outbox.Add(new[] { G("k") });
            outbox.Complete("k");
            outbox.Complete("unknown");

            Assert.That(new RewardOutbox(_path).Count, Is.Zero);
        }
    }
}
