using System;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Core.Input;
using VoxelRush.Core.Race;

namespace VoxelRush.Tests.Domain.Race
{
    /// <summary>VR-062 engine-free parts: reconnect rules, respawn pose, race messages, bot autopilot.</summary>
    public sealed class RaceFlowTests
    {
        private static Vec3 P(float x, float z) => new Vec3(x, 1, z);

        private static TrackDefinition Track() => new TrackDefinition(7, P(0, -40), new[]
        {
            new Gate(1, 0, 1, 0, 0, 1, 30, 4),
            new Gate(2, 0, 1, 150, 0, 1, 30, 4),
            new Gate(3, 0, 1, 280, 0, 1, 30, 4),
        });

        private static RaceAuthority Racing(out uint tick, params string[] racers)
        {
            var race = new RaceAuthority(Track(), new RaceTimings { CountdownTicks = 3, ReconnectGraceTicks = 300 });
            foreach (string r in racers)
            {
                race.AddRacer(r);
            }

            race.StartCountdown(0);
            tick = 0;
            while (race.Phase != RacePhase.Racing)
            {
                race.Tick(++tick);
            }

            return race;
        }

        [Test]
        public void Disconnect_BeforeCountdown_FreesTheSlot()
        {
            var race = new RaceAuthority(Track());
            race.AddRacer("a");
            race.Disconnect("a", 0);

            Assert.That(race.IsRacer("a"), Is.False);
        }

        [Test]
        public void Disconnect_DuringRace_KeepsProgress_ReconnectResumesIt()
        {
            RaceAuthority race = Racing(out uint tick, "a", "b");
            Assert.That(race.ReportMovement("a", P(0, -1), P(0, 1), tick + 60), Is.EqualTo(CrossingOutcome.Accepted));

            race.Disconnect("a", tick + 61);
            Assert.That(race.ReportMovement("a", P(0, 149), P(0, 151), tick + 200), Is.EqualTo(CrossingOutcome.NotRacing), "no samples while away");
            Assert.That(race.IsConnected("a"), Is.False);

            Assert.That(race.TryReconnect("a", tick + 250), Is.True);
            Assert.That(race.CrossingsOf("a"), Is.EqualTo(1));
            (Vec3 pos, float hx, float hz) = race.RespawnPose("a");
            Assert.That((pos.X, pos.Z, hx, hz), Is.EqualTo((0f, 4f, 0f, 1f)), "4 m past the last validated gate");
            Assert.That(race.TryReconnect("a", tick + 251), Is.False, "already connected");
        }

        [Test]
        public void Reconnect_AfterGrace_OrUnknown_IsRefused()
        {
            RaceAuthority race = Racing(out uint tick, "a", "b");
            race.Disconnect("a", tick);

            Assert.That(race.TryReconnect("a", tick + 301), Is.False);
            Assert.That(race.TryReconnect("stranger", tick), Is.False);
        }

        [Test]
        public void DisconnectedRacers_DontHoldTheRaceOpen()
        {
            RaceAuthority race = Racing(out uint tick, "a", "b");
            race.Disconnect("b", tick);
            race.ReportMovement("a", P(0, -1), P(0, 1), tick += 60);
            race.ReportMovement("a", P(0, 149), P(0, 151), tick += 150);
            Assert.That(race.ReportMovement("a", P(0, 279), P(0, 281), tick += 150), Is.EqualTo(CrossingOutcome.Finished));
            race.Tick(++tick);

            Assert.That(race.Phase, Is.EqualTo(RacePhase.Results));
            Assert.That(race.Results.Select(r => (r.RacerId, r.Finished)), Is.EqualTo(new[] { ("a", true), ("b", false) }));
        }

        [Test]
        public void EveryoneDisconnected_EndsTheRace()
        {
            RaceAuthority race = Racing(out uint tick, "a");
            race.Disconnect("a", tick);
            race.Tick(tick + 1);

            Assert.That(race.Phase, Is.EqualTo(RacePhase.Results));
        }

        [Test]
        public void RespawnPose_BeforeAnyGate_IsTheStart()
        {
            RaceAuthority race = Racing(out _, "a");
            (Vec3 pos, float hx, float hz) = race.RespawnPose("a");

            Assert.That((pos.X, pos.Z, hx, hz), Is.EqualTo((0f, -40f, 0f, 1f)));
        }

        [Test]
        public void TrackMessage_RoundTrips_AndRejectsBadInput()
        {
            byte[] bytes = TrackMessage.Encode(Track());

            Assert.That(TrackMessage.TryDecode(bytes, out TrackDefinition? t), Is.True);
            Assert.That(t!.TrackRevision, Is.EqualTo(7));
            Assert.That(t.Gates.Select(g => (g.Id, g.Z, g.HalfWidth)), Is.EqualTo(Track().Gates.Select(g => (g.Id, g.Z, g.HalfWidth))));
            Assert.That(TrackMessage.TryDecode(bytes.Take(bytes.Length - 1).ToArray(), out _), Is.False);
            byte[] duplicate = (byte[])bytes.Clone();
            Array.Copy(duplicate, 26, duplicate, 26 + 32, 4);   // second gate gets the first gate's id
            Assert.That(TrackMessage.TryDecode(duplicate, out _), Is.False, "fails track validation");
            Assert.That(TrackMessage.TryDecode(new byte[3], out _), Is.False);
        }

        [Test]
        public void ProgressMessage_RoundTrips_AndRejectsBadInput()
        {
            var m = new RaceProgressMessage("P3", 2, 3, CrossingOutcome.Accepted);

            Assert.That(RaceProgressMessage.TryDecode(m.Encode(), out RaceProgressMessage d), Is.True);
            Assert.That((d.RacerName, d.Crossings, d.TotalCrossings, d.LastOutcome), Is.EqualTo(("P3", 2, 3, CrossingOutcome.Accepted)));
            Assert.That(RaceProgressMessage.TryDecode(new RaceProgressMessage("P", 5, 3, CrossingOutcome.None).Encode(), out _), Is.False, "more crossings than gates");
            byte[] bad = m.Encode();
            bad[^1] = 99;
            Assert.That(RaceProgressMessage.TryDecode(bad, out _), Is.False);
        }

        [Test]
        public void ResultsMessage_RoundTrips_TruncatesNames_AndRejectsBadInput()
        {
            var id = Guid.NewGuid();
            var m = new RaceResultsMessage(id, new[]
            {
                ("P1", new RaceResult("x", 1, true, 420, 3)),
                (new string('é', 40), new RaceResult("y", 2, false, 0, 1)),
            });

            Assert.That(RaceResultsMessage.TryDecode(m.Encode(), out RaceResultsMessage? d), Is.True);
            Assert.That(d!.RaceId, Is.EqualTo(id));
            Assert.That(d.Entries[0].Name, Is.EqualTo("P1"));
            Assert.That((d.Entries[0].Result.Place, d.Entries[0].Result.Finished, d.Entries[0].Result.RaceTicks), Is.EqualTo((1, true, 420u)));
            Assert.That(d.Entries[1].Name.Length, Is.EqualTo(16), "32 bytes of 2-byte characters");
            byte[] bytes = m.Encode();
            Assert.That(RaceResultsMessage.TryDecode(bytes.Take(bytes.Length - 2).ToArray(), out _), Is.False);
            Assert.That(RaceResultsMessage.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out _), Is.False, "trailing bytes");
            bytes[16] = 200;
            Assert.That(RaceResultsMessage.TryDecode(bytes, out _), Is.False, "more entries than players");
        }

        [Test]
        public void Autopilot_SteersTowardItsLane_AndThrottlesInSharpTurns()
        {
            Gate gate = Track().Gates[1];   // at z = 150, facing +Z
            float lane = RaceAutopilot.LaneOf(P(-15, 0), gate);

            DrivingInput onLane = RaceAutopilot.Drive(P(-15, 100), 0, 1, gate, lane);
            DrivingInput leftOfLane = RaceAutopilot.Drive(P(-25, 100), 0, 1, gate, lane);
            DrivingInput facingAway = RaceAutopilot.Drive(P(-15, 100), 0, -1, gate, lane);

            Assert.That(onLane.Steer, Is.EqualTo(0f).Within(0.01f));
            Assert.That(onLane.Throttle, Is.EqualTo(1f));
            Assert.That(leftOfLane.Steer, Is.GreaterThan(0.1f), "steer right (+X) back to x = -15");
            Assert.That(facingAway.Throttle, Is.EqualTo(0.5f));
            Assert.That(RaceAutopilot.Drive(P(0, 0), 0, 1, gate, 100f).Steer, Is.LessThan(0f), "lane clamped inside the gate (x = -28)");
        }
    }
}
