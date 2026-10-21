using System;
using NUnit.Framework;
using VoxelRush.Core.Race;

namespace VoxelRush.Tests.Domain.Race
{
    /// <summary>RACE-003: checkpoint markers, direction to the next gate and feedback cues from server state.</summary>
    public sealed class RaceGuideTests
    {
        private sealed class View : IRaceView
        {
            public TrackDefinition? Track { get; set; }

            public RaceStateMessage? State { get; set; }

            public RaceProgressMessage? Progress { get; set; }

            public RaceResultsMessage? Results { get; set; }
        }

        // Three gates along +Z (like the arena), 60 m wide.
        private static TrackDefinition Track(int laps = 1) => new TrackDefinition(1, new Vec3(0f, 1.5f, -40f), new[]
        {
            new Gate(1, 0f, 2f, 0f, 0f, 1f, 30f, 6f),
            new Gate(2, 0f, 2f, 150f, 0f, 1f, 30f, 6f),
            new Gate(3, 0f, 2f, 280f, 0f, 1f, 30f, 6f),
        }, laps);

        private static View Racing(int crossings, int laps = 1, RacePhase phase = RacePhase.Racing)
        {
            TrackDefinition track = Track(laps);
            return new View
            {
                Track = track,
                State = new RaceStateMessage(Guid.NewGuid(), 1, phase, 100, new[] { 1, 2, 3 }),
                Progress = new RaceProgressMessage("P1", crossings, track.TotalCrossings, CrossingOutcome.Accepted),
            };
        }

        private static GateMarker[] Markers(IRaceView view) =>
            new[] { RaceGuide.MarkerOf(view, 0), RaceGuide.MarkerOf(view, 1), RaceGuide.MarkerOf(view, 2) };

        [Test]
        public void Markers_NextIsTheGateAfterTheCrossings_EarlierOnesArePassed()
        {
            Assert.That(Markers(Racing(0)), Is.EqualTo(new[] { GateMarker.Next, GateMarker.Upcoming, GateMarker.Upcoming }));
            Assert.That(Markers(Racing(1)), Is.EqualTo(new[] { GateMarker.Passed, GateMarker.Next, GateMarker.Upcoming }));
            Assert.That(Markers(Racing(2)), Is.EqualTo(new[] { GateMarker.Passed, GateMarker.Passed, GateMarker.Next }));
        }

        [Test]
        public void Markers_AfterTheFinish_AllPassed()
        {
            View view = Racing(3);

            Assert.That(RaceGuide.HasFinished(view), Is.True);
            Assert.That(Markers(view), Is.EqualTo(new[] { GateMarker.Passed, GateMarker.Passed, GateMarker.Passed }));
            Assert.That(RaceGuide.NextCrossing(view), Is.Null);
        }

        [Test]
        public void Markers_SecondLap_StartsOverFromTheFirstGate()
        {
            View view = Racing(3, laps: 2);

            Assert.That(RaceGuide.HasFinished(view), Is.False);
            Assert.That(RaceGuide.NextCrossing(view), Is.EqualTo(3));
            Assert.That(Markers(view), Is.EqualTo(new[] { GateMarker.Next, GateMarker.Upcoming, GateMarker.Upcoming }));
        }

        [TestCase(RacePhase.Countdown, true)]
        [TestCase(RacePhase.Finishing, true)]
        [TestCase(RacePhase.Waiting, false)]
        [TestCase(RacePhase.Results, false)]
        public void Markers_ShowANextGateOnlyAroundARace(RacePhase phase, bool hasNext)
        {
            View view = Racing(1, phase: phase);

            Assert.That(RaceGuide.NextCrossing(view).HasValue, Is.EqualTo(hasNext));
            Assert.That(RaceGuide.MarkerOf(view, 1), Is.EqualTo(hasNext ? GateMarker.Next : GateMarker.Upcoming));
        }

        [Test]
        public void Markers_WithoutProgressOrTrack_AreNeutral()
        {
            View view = Racing(1);
            view.Progress = null;
            Assert.That(Markers(view), Is.EqualTo(new[] { GateMarker.Upcoming, GateMarker.Upcoming, GateMarker.Upcoming }));
            Assert.That(RaceGuide.NextCrossing(new View()), Is.Null);
        }

        [Test]
        public void Finish_IsTheLastGate()
        {
            TrackDefinition track = Track();

            Assert.That(new[] { RaceGuide.IsFinish(track, 0), RaceGuide.IsFinish(track, 1), RaceGuide.IsFinish(track, 2) }, Is.EqualTo(new[] { false, false, true }));
        }

        [Test]
        public void Direction_StraightAhead_RightAndBehind()
        {
            Gate gate = Track().Gates[1];

            (float distance, float bearing) = RaceGuide.Direction(gate, new Vec3(0f, 1f, 50f), 0f, 1f);
            Assert.That(distance, Is.EqualTo(100f).Within(1e-3f));
            Assert.That(bearing, Is.EqualTo(0f).Within(1e-3f));

            (_, float right) = RaceGuide.Direction(gate, new Vec3(0f, 1f, 50f), -1f, 0f);
            Assert.That(right, Is.EqualTo(90f).Within(1e-3f), "facing -X, +Z is to the right");

            (_, float left) = RaceGuide.Direction(gate, new Vec3(0f, 1f, 50f), 1f, 0f);
            Assert.That(left, Is.EqualTo(-90f).Within(1e-3f));

            (_, float behind) = RaceGuide.Direction(gate, new Vec3(0f, 1f, 50f), 0f, -1f);
            Assert.That(Math.Abs(behind), Is.EqualTo(180f).Within(1e-3f));
        }

        [Test]
        public void Direction_AimsInsideTheGateNearestTheCar_NotAlwaysItsCentre()
        {
            Gate gate = Track().Gates[1];

            Vec3 aim = RaceGuide.AimPoint(gate, new Vec3(10f, 1f, 100f));
            Assert.That((aim.X, aim.Z), Is.EqualTo((10f, 150f)), "any lane counts, so keep the car's lane");

            Vec3 outside = RaceGuide.AimPoint(gate, new Vec3(80f, 1f, 100f));
            Assert.That(outside.X, Is.EqualTo(24f).Within(1e-3f), "kept 20% inside the posts");

            (float distance, float bearing) = RaceGuide.Direction(gate, new Vec3(80f, 1f, 100f), 0f, 1f);
            Assert.That(distance, Is.EqualTo((float)Math.Sqrt(56 * 56 + 50 * 50)).Within(1e-3f));
            Assert.That(bearing, Is.LessThan(0f), "gate is to the left");
        }

        [Test]
        public void Direction_DegenerateInputs_DontProduceNaN()
        {
            Gate gate = Track().Gates[0];

            (float d, float b) = RaceGuide.Direction(gate, new Vec3(0f, 0f, 0f), 0f, 0f);
            Assert.That(float.IsNaN(d) || float.IsNaN(b), Is.False);
            Assert.That(b, Is.EqualTo(0f));
        }

        [Test]
        public void Cue_FromProgressChanges()
        {
            var start = new RaceProgressMessage("P1", 0, 3, CrossingOutcome.None);
            var one = new RaceProgressMessage("P1", 1, 3, CrossingOutcome.Accepted);
            var tooFast = new RaceProgressMessage("P1", 1, 3, CrossingOutcome.TooFast);
            var done = new RaceProgressMessage("P1", 3, 3, CrossingOutcome.Finished);

            Assert.That(RaceGuide.Cue(null, start), Is.EqualTo(RaceCue.None), "first message isn't an event");
            Assert.That(RaceGuide.Cue(start, one), Is.EqualTo(RaceCue.CheckpointPassed));
            Assert.That(RaceGuide.Cue(one, tooFast), Is.EqualTo(RaceCue.NotCounted));
            Assert.That(RaceGuide.Cue(tooFast, tooFast), Is.EqualTo(RaceCue.None), "same refusal isn't repeated");
            Assert.That(RaceGuide.Cue(new RaceProgressMessage("P1", 2, 3, CrossingOutcome.Accepted), done), Is.EqualTo(RaceCue.Finished));
            Assert.That(RaceGuide.Cue(done, RaceGuide.AtCountdown(done)!.Value), Is.EqualTo(RaceCue.None), "a new race isn't a crossing");
        }

        [Test]
        public void AtCountdown_ResetsCrossings_KeepsName()
        {
            RaceProgressMessage reset = RaceGuide.AtCountdown(new RaceProgressMessage("P5", 3, 3, CrossingOutcome.Finished))!.Value;

            Assert.That((reset.RacerName, reset.Crossings, reset.TotalCrossings, reset.LastOutcome), Is.EqualTo(("P5", 0, 3, CrossingOutcome.None)));
            Assert.That(RaceGuide.AtCountdown(null), Is.Null);
        }

        [Test]
        public void WrongGate_ForwardThroughAnotherGate_OrBackwardsThroughTheNext()
        {
            TrackDefinition track = Track();
            var beforeGate1 = new Vec3(0f, 1f, -1f);
            var afterGate1 = new Vec3(0f, 1f, 1f);
            var beforeGate2 = new Vec3(0f, 1f, 149f);
            var afterGate2 = new Vec3(0f, 1f, 151f);

            Assert.That(RaceGuide.CrossedWrongGate(track, 0, beforeGate1, afterGate1), Is.False, "the next gate, forwards");
            Assert.That(RaceGuide.CrossedWrongGate(track, 0, beforeGate2, afterGate2), Is.True, "gate 2 before gate 1");
            Assert.That(RaceGuide.CrossedWrongGate(track, 1, beforeGate1, afterGate1), Is.True, "gate 1 again");
            Assert.That(RaceGuide.CrossedWrongGate(track, 1, afterGate2, beforeGate2), Is.True, "the next gate, backwards");
            Assert.That(RaceGuide.CrossedWrongGate(track, 1, new Vec3(0f, 1f, 50f), new Vec3(0f, 1f, 52f)), Is.False, "between gates");
            Assert.That(RaceGuide.CrossedWrongGate(track, 1, new Vec3(40f, 1f, 149f), new Vec3(40f, 1f, 151f)), Is.False, "beside the posts");
            Assert.That(RaceGuide.CrossedWrongGate(track, 3, beforeGate1, afterGate1), Is.False, "finished");
        }

        [TestCase(0f, 0)]
        [TestCase(10f, 36)]
        [TestCase(-5f, 18)]
        [TestCase(36.1f, 130)]
        [TestCase(float.NaN, 0)]
        public void Speed_InKilometresPerHour(float metresPerSecond, int kmh) =>
            Assert.That(RaceGuide.KilometresPerHour(metresPerSecond), Is.EqualTo(kmh));

        [TestCase(0f, "0 m")]
        [TestCase(149.6f, "150 m")]
        [TestCase(1234f, "1.2 km")]
        [TestCase(-1f, "")]
        public void Distance_Formatting(float metres, string text) =>
            Assert.That(RaceGuide.FormatDistance(metres), Is.EqualTo(text));
    }
}
