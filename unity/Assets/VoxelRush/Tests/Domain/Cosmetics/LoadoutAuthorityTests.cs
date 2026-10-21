using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VoxelRush.Core.Cosmetics;

namespace VoxelRush.Tests.Domain.Cosmetics
{
    /// <summary>COSM-001/002: the server shows a player's cosmetics to everyone only if they own them; clients only ask.</summary>
    public sealed class LoadoutAuthorityTests
    {
        private readonly Dictionary<Guid, TaskCompletionSource<IReadOnlyCollection<string>?>> _lookups = new Dictionary<Guid, TaskCompletionSource<IReadOnlyCollection<string>?>>();
        private readonly List<LoadoutDecision> _decided = new List<LoadoutDecision>();
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

        private List<LoadoutDecision> Drain(LoadoutAuthority authority, double now)
        {
            _decided.Clear();
            authority.Drain(now, _decided);
            return _decided.ToList();
        }

        [Test]
        public void CatalogItems_AreWellFormed_AndKnown_ForEveryKind()
        {
            Assert.That(CosmeticCatalog.SkusOf(CosmeticKind.Paint), Is.Not.Empty);
            for (int i = 0; i < CosmeticCatalog.KindCount; i++)
            {
                var kind = (CosmeticKind)i;
                Assert.That(CosmeticCatalog.SkusOf(kind), Is.Not.Empty, kind.ToString());
                Assert.That(CosmeticCatalog.SkusOf(kind).All(s => CosmeticCatalog.IsKnown(kind, s)), Is.True);
                Assert.That(CosmeticCatalog.SkusOf(kind).All(s => s.Length <= CosmeticCatalog.MaxSkuLength), Is.True);
            }
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
            Assert.That(CosmeticCatalog.IsWellFormed(CosmeticKind.Paint, sku), Is.False);
        }

        [Test]
        public void OverlongSkus_AreMalformed()
        {
            Assert.That(CosmeticCatalog.IsWellFormed(CosmeticKind.Paint, "paint." + new string('a', CosmeticCatalog.MaxSkuLength)), Is.False);
            Assert.That(CosmeticCatalog.IsWellFormed(CosmeticKind.Paint, null), Is.False);
        }

        [Test]
        public void MalformedAndUnknownRequests_AreRefusedAtOnce_AndNeverLookedUp()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);
            var player = Guid.NewGuid();

            Assert.That(authority.Submit(1, player, CosmeticKind.Paint, "paint.racing-red\"}", 0), Is.EqualTo(CosmeticVerdict.Malformed));
            Assert.That(authority.Submit(1, player, CosmeticKind.Paint, new string('x', 5000), 0), Is.EqualTo(CosmeticVerdict.Malformed));
            Assert.That(authority.Submit(1, player, CosmeticKind.Paint, "paint.gold-plated", 0), Is.EqualTo(CosmeticVerdict.Unknown));
            Assert.That(Drain(authority, 1), Is.Empty);
            Assert.That(_lookupCalls, Is.Zero);
        }

        [Test]
        public void OwnedPaint_IsApplied_OnceTheBackgroundLookupAnswers()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);
            var player = Guid.NewGuid();

            Assert.That(authority.Submit(1, player, CosmeticKind.Paint, "paint.ocean-blue", 0), Is.EqualTo(CosmeticVerdict.Queued));
            Assert.That(Drain(authority, 0.1), Is.Empty, "the tick never waits for the API");
            Assert.That(Drain(authority, 0.2), Is.Empty);
            Assert.That(_lookupCalls, Is.EqualTo(1), "one lookup in flight per player");

            Answer(player, "paint.racing-red", "paint.ocean-blue", "flag.checkered-gold");
            List<LoadoutDecision> decided = Drain(authority, 0.3);

            Assert.That(decided.Select(d => (d.ClientId, d.Sku, d.Verdict)), Is.EqualTo(new[] { (1UL, "paint.ocean-blue", CosmeticVerdict.Applied) }));
        }

        [Test]
        public void PaintNotOwned_IsRefused()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);
            var player = Guid.NewGuid();

            authority.Submit(1, player, CosmeticKind.Paint, "paint.chrome", 0);
            Drain(authority, 0.1);
            Answer(player, "paint.racing-red");
            List<LoadoutDecision> decided = Drain(authority, 0.2);

            Assert.That(decided.Single().Verdict, Is.EqualTo(CosmeticVerdict.NotOwned));
            Assert.That(decided.Single().Shows, Is.False);
        }

        [Test]
        public void CachedOwnership_IsReused_WithoutAnotherLookup()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);
            var player = Guid.NewGuid();
            authority.Submit(1, player, CosmeticKind.Paint, "paint.racing-red", 0);
            Drain(authority, 0);
            Answer(player, "paint.racing-red", "paint.ocean-blue");
            Drain(authority, 0.1);

            authority.Submit(1, player, CosmeticKind.Paint, "paint.ocean-blue", 2);

            Assert.That(Drain(authority, 2).Single().Verdict, Is.EqualTo(CosmeticVerdict.Applied));
            Assert.That(_lookupCalls, Is.EqualTo(1));
        }

        [Test]
        public void AFreshPurchase_IsSeen_ByRecheckingAStaleMiss()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);
            var player = Guid.NewGuid();
            authority.Submit(1, player, CosmeticKind.Paint, "paint.racing-red", 0);
            Drain(authority, 0);
            Answer(player, "paint.racing-red");
            Drain(authority, 0.1);

            // Bought chrome after the first lookup: the old answer can't refuse it, a new one is fetched.
            authority.Submit(1, player, CosmeticKind.Paint, "paint.chrome", 3);
            Assert.That(Drain(authority, 3), Is.Empty, "waits for the refresh interval");
            Assert.That(Drain(authority, LoadoutAuthority.RefreshSeconds), Is.Empty);
            Assert.That(_lookupCalls, Is.EqualTo(2));
            Answer(player, "paint.racing-red", "paint.chrome");

            Assert.That(Drain(authority, LoadoutAuthority.RefreshSeconds + 0.1).Single().Verdict, Is.EqualTo(CosmeticVerdict.Applied));
        }

        [Test]
        public void ApiFailures_AreRetried_ThenTheRequestTimesOutUnverified()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);
            var player = Guid.NewGuid();
            authority.Submit(1, player, CosmeticKind.Paint, "paint.racing-red", 0);
            Drain(authority, 0);
            _lookups[player].SetResult(null);

            Assert.That(Drain(authority, 1), Is.Empty);
            Assert.That(Drain(authority, 1 + LoadoutAuthority.RetrySeconds), Is.Empty);
            Assert.That(_lookupCalls, Is.EqualTo(2), "retried after the back-off");
            _lookups[player].SetException(new InvalidOperationException("api down"));

            List<LoadoutDecision> decided = Drain(authority, LoadoutAuthority.PendingTimeoutSeconds);
            Assert.That(decided.Single().Verdict, Is.EqualTo(CosmeticVerdict.Unverified));
        }

        [Test]
        public void Changes_AreRateLimited_AndTheLatestRequestWins()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Development();

            authority.Submit(1, Guid.Empty, CosmeticKind.Paint, "paint.racing-red", 0);
            Assert.That(Drain(authority, 0).Single().Sku, Is.EqualTo("paint.racing-red"));
            authority.Submit(1, Guid.Empty, CosmeticKind.Paint, "paint.ocean-blue", 0.1);
            authority.Submit(1, Guid.Empty, CosmeticKind.Paint, "paint.chrome", 0.2);

            Assert.That(Drain(authority, 0.5), Is.Empty, "at most one visible change per interval");
            Assert.That(Drain(authority, LoadoutAuthority.MinChangeIntervalSeconds).Select(d => d.Sku), Is.EqualTo(new[] { "paint.chrome" }));
        }

        [Test]
        public void DevelopmentServers_ShowAnyCatalogPaint_ButNothingElse()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Development();

            Assert.That(authority.Submit(1, Guid.NewGuid(), CosmeticKind.Paint, "paint.chrome", 0), Is.EqualTo(CosmeticVerdict.Queued));
            Assert.That(authority.Submit(2, Guid.NewGuid(), CosmeticKind.Paint, "paint.not-in-catalog", 0), Is.EqualTo(CosmeticVerdict.Unknown));
            Assert.That(Drain(authority, 0).Select(d => (d.ClientId, d.Verdict)), Is.EqualTo(new[] { (1UL, CosmeticVerdict.Applied) }));
        }

        [Test]
        public void WithoutTheApi_TicketedServers_FailClosed()
        {
            using LoadoutAuthority authority = LoadoutAuthority.DefaultOnly();

            authority.Submit(1, Guid.NewGuid(), CosmeticKind.Paint, "paint.racing-red", 0);
            authority.Submit(2, Guid.NewGuid(), CosmeticKind.Paint, string.Empty, 0);

            Assert.That(Drain(authority, 0).Select(d => (d.ClientId, d.Verdict)),
                Is.EquivalentTo(new[] { (1UL, CosmeticVerdict.Unverified), (2UL, CosmeticVerdict.Cleared) }));
        }

        [Test]
        public void TheDefaultColour_IsAlwaysAllowed_WithoutALookup()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);

            authority.Submit(1, Guid.NewGuid(), CosmeticKind.Paint, null, 0);

            Assert.That(Drain(authority, 0).Single().Verdict, Is.EqualTo(CosmeticVerdict.Cleared));
            Assert.That(_lookupCalls, Is.Zero);
        }

        [Test]
        public void ALeavingClient_IsForgotten_AndLateAnswersAreDropped()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);
            var player = Guid.NewGuid();
            authority.Submit(1, player, CosmeticKind.Paint, "paint.racing-red", 0);
            Drain(authority, 0);

            authority.Forget(1);
            Answer(player, "paint.racing-red");

            Assert.That(Drain(authority, 1), Is.Empty);
            authority.Submit(2, player, CosmeticKind.Paint, "paint.racing-red", 2);
            Assert.That(Drain(authority, 2), Is.Empty, "a reconnecting player is looked up again");
            Assert.That(_lookupCalls, Is.EqualTo(2));
        }

        [Test]
        public void OwnedResponse_IsParsed_AndBounded()
        {
            Assert.That(CosmeticCatalog.ParseOwned("{\"skus\":[\"paint.racing-red\",\"flag.checkered-gold\"]}"),
                Is.EquivalentTo(new[] { "paint.racing-red", "flag.checkered-gold" }));
            Assert.That(CosmeticCatalog.ParseOwned("{\"skus\":[]}"), Is.Empty);
            Assert.That(CosmeticCatalog.ParseOwned("{\"skus\":[\"paint.ok\",\"BAD SKU\",\"" + new string('a', 200) + "\"]}"), Is.EquivalentTo(new[] { "paint.ok" }));
            Assert.That(CosmeticCatalog.ParseOwned("<html>502</html>"), Is.Null);
            Assert.That(CosmeticCatalog.ParseOwned(null), Is.Null);
            string huge = "{\"skus\":[" + string.Join(",", Enumerable.Range(0, 1000).Select(i => $"\"paint.p{i}\"")) + "]}";
            Assert.That(CosmeticCatalog.ParseOwned(huge)!.Count, Is.EqualTo(CosmeticCatalog.MaxOwnedSkus));
        }

        // COSM-002: every non-paint kind follows the same rules as paint.
        [TestCase(CosmeticKind.Decal, "decal.lightning")]
        [TestCase(CosmeticKind.Trail, "trail.sparks")]
        [TestCase(CosmeticKind.Horn, "horn.retro")]
        [TestCase(CosmeticKind.Flag, "flag.checkered-gold")]
        public void OwnedItem_IsApplied_InItsSlot(CosmeticKind kind, string sku)
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);
            var player = Guid.NewGuid();

            Assert.That(authority.Submit(1, player, kind, sku, 0), Is.EqualTo(CosmeticVerdict.Queued));
            Drain(authority, 0);
            Answer(player, sku);

            Assert.That(Drain(authority, 0.1).Select(d => (d.Kind, d.Sku, d.Verdict)), Is.EqualTo(new[] { (kind, sku, CosmeticVerdict.Applied) }));
        }

        [TestCase(CosmeticKind.Decal, "decal.lightning")]
        [TestCase(CosmeticKind.Trail, "trail.sparks")]
        [TestCase(CosmeticKind.Horn, "horn.retro")]
        [TestCase(CosmeticKind.Flag, "flag.checkered-gold")]
        public void ItemNotOwned_IsRefused(CosmeticKind kind, string sku)
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);
            var player = Guid.NewGuid();

            authority.Submit(1, player, kind, sku, 0);
            Drain(authority, 0);
            Answer(player, "paint.racing-red");
            LoadoutDecision decision = Drain(authority, 0.1).Single();

            Assert.That(decision.Verdict, Is.EqualTo(CosmeticVerdict.NotOwned));
            Assert.That(decision.Shows, Is.False);
        }

        [TestCase(CosmeticKind.Decal, "decal.lightning\"}")]
        [TestCase(CosmeticKind.Trail, "TRAIL.SPARKS")]
        [TestCase(CosmeticKind.Horn, "horn.")]
        [TestCase(CosmeticKind.Flag, "flag.checkered-gold\n")]
        [TestCase(CosmeticKind.Decal, "flag.checkered-gold")]
        [TestCase(CosmeticKind.Horn, "paint.racing-red")]
        [TestCase((CosmeticKind)5, "decal.lightning")]
        [TestCase((CosmeticKind)255, "")]
        public void MalformedRequests_PerKind_AreRefusedAtOnce(CosmeticKind kind, string sku)
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);

            Assert.That(authority.Submit(1, Guid.NewGuid(), kind, sku, 0), Is.EqualTo(CosmeticVerdict.Malformed));
            Assert.That(Drain(authority, 1), Is.Empty);
            Assert.That(_lookupCalls, Is.Zero);
        }

        [TestCase(CosmeticKind.Decal, "decal.skulls")]
        [TestCase(CosmeticKind.Trail, "trail.rainbow")]
        [TestCase(CosmeticKind.Horn, "horn.air")]
        [TestCase(CosmeticKind.Flag, "flag.pirate")]
        public void UnknownItems_PerKind_AreRefusedAtOnce(CosmeticKind kind, string sku)
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);

            Assert.That(authority.Submit(1, Guid.NewGuid(), kind, sku, 0), Is.EqualTo(CosmeticVerdict.Unknown));
            Assert.That(_lookupCalls, Is.Zero);
        }

        [Test]
        public void Slots_AreDecidedIndependently_FromOneSharedLookup()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Verified(Lookup);
            var player = Guid.NewGuid();

            authority.Submit(1, player, CosmeticKind.Paint, "paint.racing-red", 0);
            authority.Submit(1, player, CosmeticKind.Horn, "horn.retro", 0);
            authority.Submit(1, player, CosmeticKind.Flag, "flag.checkered-gold", 0);
            Drain(authority, 0);
            Assert.That(_lookupCalls, Is.EqualTo(1), "one ownership lookup serves every slot");
            Answer(player, "paint.racing-red", "horn.retro");

            Assert.That(Drain(authority, 0.1).Select(d => (d.Kind, d.Verdict)), Is.EquivalentTo(new[]
            {
                (CosmeticKind.Paint, CosmeticVerdict.Applied),
                (CosmeticKind.Horn, CosmeticVerdict.Applied),
                (CosmeticKind.Flag, CosmeticVerdict.NotOwned),
            }));
        }

        [Test]
        public void RateLimit_IsPerSlot_AndTheLatestRequestPerSlotWins()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Development();

            authority.Submit(1, Guid.Empty, CosmeticKind.Paint, "paint.racing-red", 0);
            Drain(authority, 0);
            authority.Submit(1, Guid.Empty, CosmeticKind.Paint, "paint.chrome", 0.1);
            authority.Submit(1, Guid.Empty, CosmeticKind.Decal, "decal.lightning", 0.1);
            authority.Submit(1, Guid.Empty, CosmeticKind.Decal, string.Empty, 0.2);

            Assert.That(Drain(authority, 0.3).Select(d => (d.Kind, d.Verdict)), Is.EqualTo(new[] { (CosmeticKind.Decal, CosmeticVerdict.Cleared) }),
                "the decal slot isn't held back by a paint change");
            Assert.That(Drain(authority, LoadoutAuthority.MinChangeIntervalSeconds).Select(d => d.Sku), Is.EqualTo(new[] { "paint.chrome" }));
        }

        [Test]
        public void ALeavingClient_LosesEverySlotsRequest()
        {
            using LoadoutAuthority authority = LoadoutAuthority.Development();
            authority.Submit(1, Guid.Empty, CosmeticKind.Trail, "trail.sparks", 0);
            authority.Submit(1, Guid.Empty, CosmeticKind.Horn, "horn.retro", 0);

            authority.Forget(1);

            Assert.That(Drain(authority, 0), Is.Empty);
        }

        [Test]
        public void KindOf_FindsTheSlotOfKnownSkusOnly()
        {
            Assert.That(CosmeticCatalog.TryKindOf("horn.retro", out CosmeticKind horn) && horn == CosmeticKind.Horn, Is.True);
            Assert.That(CosmeticCatalog.TryKindOf("flag.checkered-gold", out CosmeticKind flag) && flag == CosmeticKind.Flag, Is.True);
            Assert.That(CosmeticCatalog.TryKindOf("horn.unknown", out _), Is.False);
            Assert.That(CosmeticCatalog.TryKindOf(null, out _), Is.False);
            Assert.That(CosmeticCatalog.NameOf(CosmeticKind.Trail), Is.EqualTo("trail"));
        }
    }

    /// <summary>COSM-002: horn presses are relayed only for an approved horn, at most once a second per player.</summary>
    public sealed class HornLimiterTests
    {
        [Test]
        public void APress_WithoutAShownHorn_IsNeverRelayed()
        {
            var horns = new HornLimiter();

            Assert.That(horns.Press(1, hornShown: false, 0), Is.EqualTo(HornVerdict.NoHorn));
            Assert.That(horns.Press(1, hornShown: true, 0), Is.EqualTo(HornVerdict.Played), "a refused press doesn't start the cooldown");
        }

        [Test]
        public void Spam_IsLimitedToOnePressPerSecond()
        {
            var horns = new HornLimiter();
            int played = 0;

            // A modified client pressing 20 times a second for 3 s.
            for (int i = 0; i < 60; i++)
            {
                played += horns.Press(1, hornShown: true, i * 0.05) == HornVerdict.Played ? 1 : 0;
            }

            Assert.That(played, Is.EqualTo(3));
            Assert.That(horns.Limited, Is.EqualTo(57));
        }

        [Test]
        public void Limits_ArePerPlayer_AndForgottenOnLeave()
        {
            var horns = new HornLimiter();
            Assert.That(horns.Press(1, true, 0), Is.EqualTo(HornVerdict.Played));
            Assert.That(horns.Press(2, true, 0.1), Is.EqualTo(HornVerdict.Played), "another player's horn isn't held back");
            Assert.That(horns.Press(1, true, 0.5), Is.EqualTo(HornVerdict.RateLimited));
            Assert.That(horns.Press(1, true, HornLimiter.MinIntervalSeconds), Is.EqualTo(HornVerdict.Played));

            horns.Forget(1);

            Assert.That(horns.Press(1, true, 1.1), Is.EqualTo(HornVerdict.Played));
        }
    }
}
