using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Core.Race;
using VoxelRush.Presentation.Settings;
using VoxelRush.Presentation.UI;
using VoxelRush.Presentation.World;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>RACE-003: checkpoint visuals, the dashboard (speed, arrow, time, cues) and haptics from a fake race.</summary>
    public sealed class RaceGuidanceTests
    {
        private GameObject? _host;

        private sealed class View : IRaceView
        {
            public TrackDefinition? Track { get; set; }

            public RaceStateMessage? State { get; set; }

            public RaceProgressMessage? Progress { get; set; }

            public RaceResultsMessage? Results { get; set; }
        }

        private static TrackDefinition Track() => new TrackDefinition(1, new Vec3(0f, 1.5f, -40f), new[]
        {
            new Gate(1, 0f, 2f, 0f, 0f, 1f, 30f, 6f),
            new Gate(2, 0f, 2f, 150f, 0f, 1f, 30f, 6f),
            new Gate(3, 0f, 2f, 280f, 0f, 1f, 30f, 6f),
        });

        private static View Race(RacePhase phase, int crossings)
        {
            TrackDefinition track = Track();
            return new View
            {
                Track = track,
                State = new RaceStateMessage(Guid.Empty, 1, phase, 100, new[] { 1, 2, 3 }),
                Progress = new RaceProgressMessage("P1", crossings, track.TotalCrossings, crossings == 0 ? CrossingOutcome.None : CrossingOutcome.Accepted),
            };
        }

        private static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
            }
        }

        [UnityTest]
        public IEnumerator Checkpoints_MarkNextPassedAndFinish_ByShapeAndBrightness()
        {
            _host = new GameObject("Host");
            var view = Race(RacePhase.Racing, 1);
            var checkpoints = _host.AddComponent<CheckpointView>();
            checkpoints.Source = view;
            checkpoints.Refresh();
            yield return null;

            Assert.That(checkpoints.GateCount, Is.EqualTo(3));
            Assert.That(new[] { checkpoints.MarkerAt(0), checkpoints.MarkerAt(1), checkpoints.MarkerAt(2) }, Is.EqualTo(new[] { GateMarker.Passed, GateMarker.Next, GateMarker.Upcoming }));
            Assert.That(checkpoints.IsCheckered(2), Is.True, "finish has the checkered banner");
            Assert.That(checkpoints.IsCheckered(1), Is.False);
            Assert.That(_host.GetComponentsInChildren<Collider>(true), Is.Empty, "pure presentation");

            Transform passed = checkpoints.GateAt(0);
            Assert.That(passed.Find("Banner").gameObject.activeSelf, Is.False, "passed gates drop the banner");
            Assert.That(passed.Find("PostL").localScale.y, Is.EqualTo(12f * CheckpointView.PassedHeightFactor).Within(1e-3f), "and are lowered");
            Assert.That(checkpoints.GateAt(1).Find("PostL").localScale.y, Is.EqualTo(12f).Within(1e-3f), "posts span the gate height");
            Assert.That(checkpoints.GateAt(1).Find("Banner").localScale.x, Is.EqualTo(60f).Within(1e-3f), "banner spans the gate width");
            Assert.That(checkpoints.GateAt(1).forward.z, Is.EqualTo(1f).Within(1e-4f), "faces along the gate normal");

            Assert.That(Luminance(checkpoints.ColorOf(1, "post")), Is.GreaterThan(Luminance(checkpoints.ColorOf(2, "post")) + 0.15f), "next is clearly brighter than upcoming");
            Assert.That(Luminance(checkpoints.ColorOf(2, "post")), Is.GreaterThan(Luminance(checkpoints.ColorOf(0, "post")) + 0.15f), "upcoming is clearly brighter than passed");
            Assert.That(checkpoints.NextMarker, Is.Not.Null);
            Assert.That(checkpoints.NextMarker!.position.y, Is.GreaterThan(checkpoints.GateAt(1).position.y + 12f), "V marker floats above the next gate");
            Assert.That(checkpoints.NextMarker.position.z, Is.EqualTo(150f).Within(1e-3f));

            view.Progress = new RaceProgressMessage("P1", 3, 3, CrossingOutcome.Finished);
            checkpoints.Refresh();
            Assert.That(new[] { checkpoints.MarkerAt(0), checkpoints.MarkerAt(1), checkpoints.MarkerAt(2) }, Is.All.EqualTo(GateMarker.Passed));
            Assert.That(checkpoints.NextMarker, Is.Null);

            view.Track = null;
            checkpoints.Refresh();
            Assert.That(checkpoints.GateCount, Is.EqualTo(0), "no track, nothing drawn");
        }

        [Test]
        public void Dashboard_ShowsSpeedArrowDistanceTimeAndCues()
        {
            _host = new GameObject("Host");
            var dashboard = _host.AddComponent<RaceDashboard>();
            dashboard.enabled = false;
            var view = Race(RacePhase.Countdown, 0);
            dashboard.Source = view;
            var forward = new Vector3(0f, -0.3f, 1f);

            dashboard.Step(view, new Vector3(0f, 1f, -40f), forward, 0f, 10f);
            Assert.That(dashboard.SpeedText, Is.EqualTo("0 km/h"));
            Assert.That(dashboard.DistanceText, Is.EqualTo("40 m"), "countdown already points at the first gate");
            Assert.That(dashboard.TimeText, Is.Empty);

            view.State = new RaceStateMessage(Guid.Empty, 1, RacePhase.Racing, 100, new[] { 1, 2, 3 });
            dashboard.Step(view, new Vector3(0f, 1f, -1f), forward, 20f, 11f);
            dashboard.Step(view, new Vector3(0f, 1f, 1f), forward, 20f, 13f);
            Assert.That(dashboard.SpeedText, Is.EqualTo("72 km/h"));
            Assert.That(dashboard.TimeText, Is.EqualTo("0:02.00"), "clock runs from GO");
            Assert.That(dashboard.FlashText, Is.Empty, "the next gate isn't a wrong one");

            view.Progress = new RaceProgressMessage("P1", 1, 3, CrossingOutcome.Accepted);
            dashboard.Step(view, new Vector3(0f, 1f, 50f), new Vector3(1f, 0f, 0f), 20f, 14f);
            Assert.That(dashboard.LastCue, Is.EqualTo(RaceCue.CheckpointPassed));
            Assert.That(dashboard.DistanceText, Is.EqualTo("100 m"));
            Assert.That(dashboard.ArrowDegrees!.Value, Is.EqualTo(-90f).Within(1e-3f), "facing +X, the gate is to the left");

            // Back through gate 1 forwards (after a U-turn) while gate 2 is next.
            dashboard.Step(view, new Vector3(5f, 1f, -1f), forward, 10f, 15f);
            dashboard.Step(view, new Vector3(5f, 1f, 1f), forward, 10f, 15.1f);
            Assert.That(dashboard.LastCue, Is.EqualTo(RaceCue.WrongGate));
            Assert.That(dashboard.FlashText, Is.EqualTo("Wrong checkpoint"));
            dashboard.Step(view, new Vector3(5f, 1f, 3f), forward, 10f, 15.1f + RaceDashboard.FlashSeconds + 0.1f);
            Assert.That(dashboard.FlashText, Is.Empty, "the hint fades");

            // A respawn jump through a gate isn't a wrong gate.
            dashboard.Step(view, new Vector3(0f, 1f, 290f), forward, 0f, 18f);
            Assert.That(dashboard.LastCue, Is.EqualTo(RaceCue.WrongGate));
            Assert.That(dashboard.FlashText, Is.Empty);

            view.Progress = new RaceProgressMessage("P1", 3, 3, CrossingOutcome.Finished);
            dashboard.Step(view, new Vector3(0f, 1f, 290f), forward, 30f, 20f);
            Assert.That(dashboard.LastCue, Is.EqualTo(RaceCue.Finished));
            Assert.That(dashboard.ArrowDegrees, Is.Null, "nothing left to aim at");
            string finishTime = dashboard.TimeText;
            dashboard.Step(view, new Vector3(0f, 1f, 300f), forward, 30f, 25f);
            Assert.That(dashboard.TimeText, Is.EqualTo(finishTime).And.EqualTo("0:09.00"), "the clock stops at the finish");

            dashboard.Step(view, null, forward, 0f, 26f);
            Assert.That(dashboard.SpeedText, Is.Empty, "no car, no speed");
        }

        [Test]
        public void Haptics_RespectTheToggle_AndRateLimit()
        {
            float t = Time.unscaledTime + 1000f;
            int before = Haptics.Played;

            Assert.That(Haptics.Play(RaceCue.CheckpointPassed, enabled: false, t), Is.False, "Vibration off");
            Assert.That(Haptics.Played, Is.EqualTo(before));
            Assert.That(Haptics.Play(RaceCue.None, enabled: true, t), Is.False);
            Assert.That(Haptics.Play(RaceCue.CheckpointPassed, enabled: true, t), Is.True);
            Assert.That(Haptics.LastCue, Is.EqualTo(RaceCue.CheckpointPassed));
            Assert.That(Haptics.Play(RaceCue.Finished, enabled: true, t + 0.05f), Is.False, "too soon after the last");
            Assert.That(Haptics.Play(RaceCue.Finished, enabled: true, t + 0.5f), Is.True);
            Assert.That(Haptics.Played, Is.EqualTo(before + 2));
            Assert.That(Haptics.DurationMs(RaceCue.Finished), Is.GreaterThan(Haptics.DurationMs(RaceCue.WrongGate)));
            Assert.That(Haptics.DurationMs(RaceCue.WrongGate), Is.GreaterThan(Haptics.DurationMs(RaceCue.CheckpointPassed)));
            Assert.That(Haptics.DeviceSupported, Is.False, "never vibrates in the editor or batch runs");
        }

        [UnityTest]
        public IEnumerator ClientScene_NetworkWorldDrawsCheckpointsWithTheSceneMaterial()
        {
            yield return SceneManager.LoadSceneAsync("Client", LoadSceneMode.Single);
            var checkpoints = UnityEngine.Object.FindAnyObjectByType<CheckpointView>();
            var dashboard = UnityEngine.Object.FindAnyObjectByType<RaceDashboard>();
            Assert.That(checkpoints, Is.Not.Null);
            Assert.That(dashboard, Is.Not.Null);
            Assert.That(checkpoints!.gameObject.name, Is.EqualTo("NetworkWorld"));
            Assert.That(dashboard!.gameObject, Is.SameAs(checkpoints.gameObject));
            Assert.That(checkpoints.GateCount, Is.EqualTo(0), "offline: no track");

            checkpoints.Source = Race(RacePhase.Racing, 0);
            checkpoints.Refresh();
            Assert.That(checkpoints.GateCount, Is.EqualTo(3));
            Assert.That(checkpoints.GateAt(0).Find("PostL").GetComponent<Renderer>().sharedMaterial.name, Does.StartWith("Debris"));
            Assert.That(checkpoints.GetComponentsInChildren<Collider>(true), Is.Empty);
            yield return TestScenes.UnloadAll();
        }
    }
}
