using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Core.Race;

namespace VoxelRush.Tests.Domain.Race
{
    /// <summary>VR-060: server race state and ordered checkpoint validation.</summary>
    public sealed class RaceAuthorityTests
    {
        // A 100 m square driven anticlockwise from the origin: north, east, south, then west over the finish line.
        private static readonly Vec3[] Loop = { P(0, 0), P(0, 100), P(100, 100), P(100, 0), P(0, 0) };

        private static Vec3 P(float x, float z, float y = 1) => new Vec3(x, y, z);

        private static TrackDefinition Track(int laps = 1) => new TrackDefinition(42, P(0, 0), new[]
        {
            new Gate(11, 0, 1, 50, 0, 1, 6, 4),
            new Gate(12, 50, 1, 100, 1, 0, 6, 4),
            new Gate(13, 100, 1, 50, 0, -1, 6, 4),
            new Gate(99, 10, 1, 0, -1, 0, 6, 4),     // finish line
        }, laps);

        private uint _tick;

        private RaceAuthority Racing(TrackDefinition? track = null, params string[] racers)
        {
            var race = new RaceAuthority(track ?? Track(), new RaceTimings { CountdownTicks = 90, FinishingTicks = 900, ResultsTicks = 60 });
            foreach (string r in racers.Length == 0 ? new[] { "a" } : racers)
            {
                Assert.That(race.AddRacer(r), Is.True);
            }

            _tick = 1000;
            Assert.That(race.StartCountdown(_tick), Is.True);
            while (race.Phase != RacePhase.Racing)
            {
                race.Tick(++_tick);
            }

            return race;
        }

        /// <summary>Moves along waypoints at <paramref name="speed"/> m/s, one server sample per tick; samples after finishing are dropped.</summary>
        private List<CrossingOutcome> Drive(RaceAuthority race, string racer, IReadOnlyList<Vec3> path, float speed, Func<int, bool>? respawnAt = null)
        {
            var outcomes = new List<CrossingOutcome>();
            float step = speed / 30f;
            Vec3 pos = path[0];
            int sample = 0;
            for (int i = 1; i < path.Count; i++)
            {
                while (pos.HorizontalDistance(path[i]) > 1e-3f)
                {
                    float d = pos.HorizontalDistance(path[i]);
                    float t = Math.Min(1f, step / d);
                    var next = new Vec3(pos.X + (path[i].X - pos.X) * t, pos.Y + (path[i].Y - pos.Y) * t, pos.Z + (path[i].Z - pos.Z) * t);
                    CrossingOutcome o = race.ReportMovement(racer, pos, next, ++_tick, respawnAt?.Invoke(sample++) ?? false);
                    if (o != CrossingOutcome.None && o != CrossingOutcome.NotRacing)
                    {
                        outcomes.Add(o);
                    }

                    race.Tick(_tick);
                    pos = next;
                }
            }

            return outcomes;
        }

        [Test]
        public void LegitimateLap_FinishesInOrder_AndPhasesAdvanceOnTheServerClock()
        {
            RaceAuthority race = Racing(racers: new[] { "a", "b" });
            Assert.That(race.StartTick, Is.EqualTo(1090));
            Assert.That(race.LockedTrackRevision, Is.EqualTo(42));

            List<CrossingOutcome> a = Drive(race, "a", Loop, 25f);
            Assert.That(a, Is.EqualTo(new[] { CrossingOutcome.Accepted, CrossingOutcome.Accepted, CrossingOutcome.Accepted, CrossingOutcome.Finished }));
            Assert.That(race.Phase, Is.EqualTo(RacePhase.Finishing));

            Drive(race, "b", Loop, 20f);
            Assert.That(race.Phase, Is.EqualTo(RacePhase.Results), "everyone finished");
            Assert.That(race.Results.Select(r => r.RacerId), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(race.Results[0].RaceTicks, Is.InRange(460u, 475u), "390 m at 25 m/s = 468 ticks from the start signal");

            uint resultsFrom = _tick;
            while (race.Phase == RacePhase.Results)
            {
                race.Tick(++_tick);
            }

            Assert.That(race.Phase, Is.EqualTo(RacePhase.Reset));
            Assert.That(_tick - resultsFrom, Is.LessThanOrEqualTo(60u));
            race.Tick(++_tick);
            Assert.That(race.Phase, Is.EqualTo(RacePhase.Waiting));
            Assert.That(race.CrossingsOf("a"), Is.Zero, "progress discarded on reset");
            Assert.That(race.Results, Is.Empty);
        }

        [Test]
        public void CrossingsDuringCountdown_DontCount()
        {
            var race = new RaceAuthority(Track());
            race.AddRacer("a");
            race.StartCountdown(0);

            Assert.That(race.ReportMovement("a", P(0, 49), P(0, 51), 5), Is.EqualTo(CrossingOutcome.NotRacing));
            Assert.That(race.AddRacer("late"), Is.False, "roster locked after countdown starts");
        }

        [Test]
        public void DoD_ClientFinishClaim_IsRejected()
        {
            RaceAuthority race = Racing();

            Assert.That(race.ClaimFinishFromClient("a"), Is.False);
            Assert.That(race.FinishTickOf("a"), Is.Null);
            Assert.That(race.Phase, Is.EqualTo(RacePhase.Racing));
            Assert.That(race.RejectedClaims, Is.EqualTo(1));
        }

        [Test]
        public void DoD_TeleportAcrossTheFinish_IsRejected()
        {
            RaceAuthority race = Racing();
            Drive(race, "a", new[] { P(0, 0), P(0, 100), P(100, 100), P(100, 0), P(30, 0) }, 25f);
            Assert.That(race.CrossingsOf("a"), Is.EqualTo(3));

            // 30 m in one tick over the line.
            Assert.That(race.ReportMovement("a", P(30, 0), P(0, 0), ++_tick), Is.EqualTo(CrossingOutcome.Teleported));
            Assert.That(race.FinishTickOf("a"), Is.Null);
            Assert.That(race.SuspiciousSamples, Is.EqualTo(1));
        }

        [Test]
        public void DoD_SkippingGates_CannotFinish()
        {
            RaceAuthority race = Racing();

            // Out east past the finish line and straight back over it: the next gate is still gate 11.
            List<CrossingOutcome> outcomes = Drive(race, "a", new[] { P(0, 0), P(30, 0), P(-10, 0) }, 25f);

            Assert.That(outcomes, Is.Empty);
            Assert.That(race.CrossingsOf("a"), Is.Zero);

            // Even with three gates done, gate 13 skipped means the finish doesn't count.
            Drive(race, "a", new[] { P(-10, 0), P(0, 0), P(0, 100), P(100, 100), P(60, 60), P(30, 0), P(0, 0) }, 25f);
            Assert.That(race.CrossingsOf("a"), Is.EqualTo(2));
            Assert.That(race.FinishTickOf("a"), Is.Null);
        }

        [Test]
        public void DoD_SpeedHack_BelowTeleportThreshold_IsTooFast()
        {
            RaceAuthority race = Racing();

            // 60 m/s (2 m per tick, under the 3.25 m teleport bound) beats the 45 m/s plausibility limit to gate 11.
            List<CrossingOutcome> outcomes = Drive(race, "a", Loop, 60f);

            Assert.That(outcomes.First(), Is.EqualTo(CrossingOutcome.TooFast));
            Assert.That(race.FinishTickOf("a"), Is.Null);
        }

        [Test]
        public void DoD_RespawnAcrossAGate_DoesNotCount()
        {
            RaceAuthority race = Racing();

            CrossingOutcome o = race.ReportMovement("a", P(0, 45), P(0, 55), _tick + 100, respawned: true);

            Assert.That(o, Is.EqualTo(CrossingOutcome.Respawned));
            Assert.That(race.CrossingsOf("a"), Is.Zero);
            Assert.That(race.RespawnsOf("a"), Is.EqualTo(1));
        }

        [Test]
        public void OutsideGateBounds_Backwards_AndOverTheTop_DontCount()
        {
            RaceAuthority race = Racing();
            uint t = _tick + 200;

            Assert.That(race.ReportMovement("a", P(20, 49), P(20, 51), t), Is.EqualTo(CrossingOutcome.None), "14 m beside a 12 m wide gate");
            Assert.That(race.ReportMovement("a", P(0, 51), P(0, 49), t + 1), Is.EqualTo(CrossingOutcome.None), "backwards");
            Assert.That(race.ReportMovement("a", P(0, 49, 9), P(0, 51, 9), t + 2), Is.EqualTo(CrossingOutcome.None), "jumped over the gate");
            Assert.That(race.ReportMovement("a", P(0, 49), P(0, 51), t + 3), Is.EqualTo(CrossingOutcome.Accepted));
        }

        [Test]
        public void InvalidSamples_AndUnknownRacers_AreRejected()
        {
            RaceAuthority race = Racing();

            Assert.That(race.ReportMovement("nobody", P(0, 49), P(0, 51), _tick + 100), Is.EqualTo(CrossingOutcome.UnknownRacer));
            Assert.That(race.ReportMovement("a", P(float.NaN, 49), P(0, 51), _tick + 100), Is.EqualTo(CrossingOutcome.InvalidPosition));
        }

        [Test]
        public void TwoLaps_NeedEveryGateTwice()
        {
            RaceAuthority race = Racing(Track(laps: 2));

            List<CrossingOutcome> first = Drive(race, "a", Loop, 25f);
            Assert.That(first.Last(), Is.EqualTo(CrossingOutcome.Accepted), "lap line, not the finish");
            List<CrossingOutcome> second = Drive(race, "a", Loop, 25f);
            Assert.That(second.Last(), Is.EqualTo(CrossingOutcome.Finished));
            Assert.That(race.CrossingsOf("a"), Is.EqualTo(8));
        }

        [Test]
        public void FinishingWindow_EndsWithDnfsRankedByProgress()
        {
            RaceAuthority race = Racing(racers: new[] { "a", "b", "c" });
            Drive(race, "b", new[] { P(0, 0), P(0, 100), P(60, 100) }, 25f);   // 2 gates
            Drive(race, "c", new[] { P(0, 0), P(0, 60) }, 25f);                // 1 gate
            Drive(race, "a", Loop, 25f);
            for (int i = 0; i < 901; i++)
            {
                race.Tick(++_tick);
            }

            Assert.That(race.Phase, Is.EqualTo(RacePhase.Results));
            Assert.That(race.Results.Select(r => (r.RacerId, r.Finished)), Is.EqualTo(new[] { ("a", true), ("b", false), ("c", false) }));
        }

        [Test]
        public void NobodyFinishes_RaceStillEnds()
        {
            RaceAuthority race = Racing();
            race.Timings.MaxRaceTicks = 100;
            for (int i = 0; i < 101; i++)
            {
                race.Tick(++_tick);
            }

            Assert.That(race.Phase, Is.EqualTo(RacePhase.Results));
            Assert.That(race.Results.Single().Finished, Is.False);
        }

        [Test]
        public void RaceStateMessage_RoundTrips_AndRejectsMalformed()
        {
            RaceAuthority race = Racing();
            byte[] bytes = race.ToMessage().Encode();

            Assert.That(RaceStateMessage.TryDecode(bytes, out RaceStateMessage? m), Is.True);
            Assert.That(m!.RaceId, Is.EqualTo(race.RaceId));
            Assert.That(m.TrackRevision, Is.EqualTo(42));
            Assert.That(m.State, Is.EqualTo(RacePhase.Racing));
            Assert.That(m.ServerStartTick, Is.EqualTo(race.StartTick));
            Assert.That(m.OrderedCheckpointIds, Is.EqualTo(new[] { 11, 12, 13, 99 }));

            Assert.That(RaceStateMessage.TryDecode(bytes.Take(bytes.Length - 1).ToArray(), out _), Is.False, "truncated");
            byte[] badState = (byte[])bytes.Clone();
            badState[24] = 200;
            Assert.That(RaceStateMessage.TryDecode(badState, out _), Is.False);
            byte[] tooMany = (byte[])bytes.Clone();
            tooMany[29] = 255;
            Assert.That(RaceStateMessage.TryDecode(tooMany, out _), Is.False);
            Assert.That(RaceStateMessage.TryDecode(Array.Empty<byte>(), out _), Is.False);
        }

        [Test]
        public void TrackValidation()
        {
            Assert.Throws<ArgumentException>(() => new TrackDefinition(1, P(0, 0), new[] { new Gate(1, 0, 0, 0, 1, 0, 1, 1) }));
            Assert.Throws<ArgumentException>(() => new TrackDefinition(1, P(0, 0), new[] { new Gate(1, 0, 0, 0, 1, 0, 1, 1), new Gate(1, 5, 0, 0, 1, 0, 1, 1) }));
            Assert.Throws<ArgumentException>(() => new TrackDefinition(1, P(0, 0), new[] { new Gate(1, 0, 0, 0, 0, 0, 1, 1), new Gate(2, 5, 0, 0, 1, 0, 1, 1) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => Track(laps: 0));
        }
    }
}
