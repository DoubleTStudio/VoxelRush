using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VoxelRush.Core.Cosmetics;

namespace VoxelRush.Tests.Domain.Cosmetics
{
    /// <summary>COSM-001: the server shows a player's paint to everyone only if they own it; clients only ask.</summary>
    public sealed class PaintAuthorityTests
    {
        private readonly Dictionary<Guid, TaskCompletionSource<IReadOnlyCollection<string>?>> _lookups = new Dictionary<Guid, TaskCompletionSource<IReadOnlyCollection<string>?>>();
        private readonly List<PaintDecision> _decided = new List<PaintDecision>();
        private int _lookupCalls;

        // NUnit reuses the fixture instance across tests.
        [SetUp]
        public void SetUp()
        {
            _lookups.Clear();
            _lookupCalls = 0;
        }

        // Lookups stay open until the test answers them, as a slow API would.
        private Task<IReadOnlyCollection<string>?> Lookup(Guid player, CancellationToken ct)
        {
            _lookupCalls++;
            var pending = new TaskCompletionSource<IReadOnlyCollection<string>?>();
            _lookups[player] = pending;
            return pending.Task;
        }

        private void Answer(Guid player, params string[] owned) => _lookups[player].SetResult(owned);

        private List<PaintDecision> Drain(PaintAuthority authority, double now)
        {
            _decided.Clear();
            authority.Drain(now, _decided);
            return _decided.ToList();
        }

        [Test]
        public void CatalogPaints_AreWellFormed_AndKnown()
        {
            Assert.That(PaintCatalog.Skus, Is.Not.Empty);
            Assert.That(PaintCatalog.Skus.All(PaintCatalog.IsKnown), Is.True);
            Assert.That(PaintCatalog.Skus.All(s => s.Length <= PaintCatalog.MaxSkuLength), Is.True);
        }

        [TestCase("")]
        [TestCase("paint.")]
        [TestCase("Paint.Racing-Red")]
        [TestCase("paint.racing red")]
        [TestCase("paint.racing-red\n")]
        [TestCase("paint.racing-red\0")]
        [TestCase("paint.\"red\"")]
        [TestCase("paint/../../etc")]
        [TestCase("paint.rouge-é")]
        [TestCase("decal.lightning")]
        public void Malformed_OrNonPaintSkus_AreNotPaints(string sku)
        {
            Assert.That(PaintCatalog.IsWellFormed(sku), Is.False);
        }

        [Test]
        public void OverlongSkus_AreMalformed()
        {
            Assert.That(PaintCatalog.IsWellFormed("paint." + new string('a', PaintCatalog.MaxSkuLength)), Is.False);
            Assert.That(PaintCatalog.IsWellFormed(null), Is.False);
        }

        [Test]
        public void MalformedAndUnknownRequests_AreRefusedAtOnce_AndNeverLookedUp()
        {
            using PaintAuthority authority = PaintAuthority.Verified(Lookup);
            var player = Guid.NewGuid();

            Assert.That(authority.Submit(1, player, "paint.racing-red\"}", 0), Is.EqualTo(PaintVerdict.Malformed));
            Assert.That(authority.Submit(1, player, new string('x', 5000), 0), Is.EqualTo(PaintVerdict.Malformed));
            Assert.That(authority.Submit(1, player, "paint.gold-plated", 0), Is.EqualTo(PaintVerdict.Unknown));
            Assert.That(Drain(authority, 1), Is.Empty);
            Assert.That(_lookupCalls, Is.Zero);
        }

        [Test]
        public void OwnedPaint_IsApplied_OnceTheBackgroundLookupAnswers()
        {
            using PaintAuthority authority = PaintAuthority.Verified(Lookup);
            var player = Guid.NewGuid();

            Assert.That(authority.Submit(1, player, "paint.ocean-blue", 0), Is.EqualTo(PaintVerdict.Queued));
            Assert.That(Drain(authority, 0.1), Is.Empty, "the tick never waits for the API");
            Assert.That(Drain(authority, 0.2), Is.Empty);
            Assert.That(_lookupCalls, Is.EqualTo(1), "one lookup in flight per player");

            Answer(player, "paint.racing-red", "paint.ocean-blue", "flag.checkered-gold");
            List<PaintDecision> decided = Drain(authority, 0.3);

            Assert.That(decided.Select(d => (d.ClientId, d.Sku, d.Verdict)), Is.EqualTo(new[] { (1UL, "paint.ocean-blue", PaintVerdict.Applied) }));
        }

        [Test]
        public void PaintNotOwned_IsRefused()
        {
            using PaintAuthority authority = PaintAuthority.Verified(Lookup);
            var player = Guid.NewGuid();

            authority.Submit(1, player, "paint.chrome", 0);
            Drain(authority, 0.1);
            Answer(player, "paint.racing-red");
            List<PaintDecision> decided = Drain(authority, 0.2);

            Assert.That(decided.Single().Verdict, Is.EqualTo(PaintVerdict.NotOwned));
            Assert.That(decided.Single().Shows, Is.False);
        }

        [Test]
        public void CachedOwnership_IsReused_WithoutAnotherLookup()
        {
            using PaintAuthority authority = PaintAuthority.Verified(Lookup);
            var player = Guid.NewGuid();
            authority.Submit(1, player, "paint.racing-red", 0);
            Drain(authority, 0);
            Answer(player, "paint.racing-red", "paint.ocean-blue");
            Drain(authority, 0.1);

            authority.Submit(1, player, "paint.ocean-blue", 2);

            Assert.That(Drain(authority, 2).Single().Verdict, Is.EqualTo(PaintVerdict.Applied));
            Assert.That(_lookupCalls, Is.EqualTo(1));
        }

        [Test]
        public void AFreshPurchase_IsSeen_ByRecheckingAStaleMiss()
        {
            using PaintAuthority authority = PaintAuthority.Verified(Lookup);
            var player = Guid.NewGuid();
            authority.Submit(1, player, "paint.racing-red", 0);
            Drain(authority, 0);
            Answer(player, "paint.racing-red");
            Drain(authority, 0.1);

            // Bought chrome after the first lookup: the old answer can't refuse it, a new one is fetched.
            authority.Submit(1, player, "paint.chrome", 3);
            Assert.That(Drain(authority, 3), Is.Empty, "waits for the refresh interval");
            Assert.That(Drain(authority, PaintAuthority.RefreshSeconds), Is.Empty);
            Assert.That(_lookupCalls, Is.EqualTo(2));
            Answer(player, "paint.racing-red", "paint.chrome");

            Assert.That(Drain(authority, PaintAuthority.RefreshSeconds + 0.1).Single().Verdict, Is.EqualTo(PaintVerdict.Applied));
        }

        [Test]
        public void ApiFailures_AreRetried_ThenTheRequestTimesOutUnverified()
        {
            using PaintAuthority authority = PaintAuthority.Verified(Lookup);
            var player = Guid.NewGuid();
            authority.Submit(1, player, "paint.racing-red", 0);
            Drain(authority, 0);
            _lookups[player].SetResult(null);

            Assert.That(Drain(authority, 1), Is.Empty);
            Assert.That(Drain(authority, 1 + PaintAuthority.RetrySeconds), Is.Empty);
            Assert.That(_lookupCalls, Is.EqualTo(2), "retried after the back-off");
            _lookups[player].SetException(new InvalidOperationException("api down"));

            List<PaintDecision> decided = Drain(authority, PaintAuthority.PendingTimeoutSeconds);
            Assert.That(decided.Single().Verdict, Is.EqualTo(PaintVerdict.Unverified));
        }

        [Test]
        public void Changes_AreRateLimited_AndTheLatestRequestWins()
        {
            using PaintAuthority authority = PaintAuthority.Development();

            authority.Submit(1, Guid.Empty, "paint.racing-red", 0);
            Assert.That(Drain(authority, 0).Single().Sku, Is.EqualTo("paint.racing-red"));
            authority.Submit(1, Guid.Empty, "paint.ocean-blue", 0.1);
            authority.Submit(1, Guid.Empty, "paint.chrome", 0.2);

            Assert.That(Drain(authority, 0.5), Is.Empty, "at most one visible change per interval");
            Assert.That(Drain(authority, PaintAuthority.MinChangeIntervalSeconds).Select(d => d.Sku), Is.EqualTo(new[] { "paint.chrome" }));
        }

        [Test]
        public void DevelopmentServers_ShowAnyCatalogPaint_ButNothingElse()
        {
            using PaintAuthority authority = PaintAuthority.Development();

            Assert.That(authority.Submit(1, Guid.NewGuid(), "paint.chrome", 0), Is.EqualTo(PaintVerdict.Queued));
            Assert.That(authority.Submit(2, Guid.NewGuid(), "paint.not-in-catalog", 0), Is.EqualTo(PaintVerdict.Unknown));
            Assert.That(Drain(authority, 0).Select(d => (d.ClientId, d.Verdict)), Is.EqualTo(new[] { (1UL, PaintVerdict.Applied) }));
        }

        [Test]
        public void WithoutTheApi_TicketedServers_FailClosed()
        {
            using PaintAuthority authority = PaintAuthority.DefaultOnly();

            authority.Submit(1, Guid.NewGuid(), "paint.racing-red", 0);
            authority.Submit(2, Guid.NewGuid(), string.Empty, 0);

            Assert.That(Drain(authority, 0).Select(d => (d.ClientId, d.Verdict)),
                Is.EquivalentTo(new[] { (1UL, PaintVerdict.Unverified), (2UL, PaintVerdict.Cleared) }));
        }

        [Test]
        public void TheDefaultColour_IsAlwaysAllowed_WithoutALookup()
        {
            using PaintAuthority authority = PaintAuthority.Verified(Lookup);

            authority.Submit(1, Guid.NewGuid(), null, 0);

            Assert.That(Drain(authority, 0).Single().Verdict, Is.EqualTo(PaintVerdict.Cleared));
            Assert.That(_lookupCalls, Is.Zero);
        }

        [Test]
        public void ALeavingClient_IsForgotten_AndLateAnswersAreDropped()
        {
            using PaintAuthority authority = PaintAuthority.Verified(Lookup);
            var player = Guid.NewGuid();
            authority.Submit(1, player, "paint.racing-red", 0);
            Drain(authority, 0);

            authority.Forget(1);
            Answer(player, "paint.racing-red");

            Assert.That(Drain(authority, 1), Is.Empty);
            authority.Submit(2, player, "paint.racing-red", 2);
            Assert.That(Drain(authority, 2), Is.Empty, "a reconnecting player is looked up again");
            Assert.That(_lookupCalls, Is.EqualTo(2));
        }

        [Test]
        public void OwnedResponse_IsParsed_AndBounded()
        {
            Assert.That(PaintCatalog.ParseOwned("{\"skus\":[\"paint.racing-red\",\"flag.checkered-gold\"]}"),
                Is.EquivalentTo(new[] { "paint.racing-red", "flag.checkered-gold" }));
            Assert.That(PaintCatalog.ParseOwned("{\"skus\":[]}"), Is.Empty);
            Assert.That(PaintCatalog.ParseOwned("{\"skus\":[\"paint.ok\",\"BAD SKU\",\"" + new string('a', 200) + "\"]}"), Is.EquivalentTo(new[] { "paint.ok" }));
            Assert.That(PaintCatalog.ParseOwned("<html>502</html>"), Is.Null);
            Assert.That(PaintCatalog.ParseOwned(null), Is.Null);
            string huge = "{\"skus\":[" + string.Join(",", Enumerable.Range(0, 1000).Select(i => $"\"paint.p{i}\"")) + "]}";
            Assert.That(PaintCatalog.ParseOwned(huge)!.Count, Is.EqualTo(PaintCatalog.MaxOwnedSkus));
        }
    }
}
