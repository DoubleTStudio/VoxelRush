using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using VoxelRush.Core.Race;
using VoxelRush.Presentation.UI;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>VR-062: the race HUD shows server race state (countdown, checkpoints, results) on a real canvas.</summary>
    public sealed class RaceHudTests
    {
        private GameObject? _canvas;

        private sealed class View : IRaceView
        {
            public TrackDefinition? Track { get; set; }

            public RaceStateMessage? State { get; set; }

            public RaceProgressMessage? Progress { get; set; }

            public RaceResultsMessage? Results { get; set; }
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvas != null)
            {
                UnityEngine.Object.Destroy(_canvas);
            }
        }

        [UnityTest]
        public IEnumerator Hud_FollowsTheRaceFromCountdownToResults()
        {
            _canvas = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var hud = new GameObject("RaceHud", typeof(RectTransform)).AddComponent<RaceHud>();
            hud.transform.SetParent(_canvas.transform, false);
            var view = new View();
            hud.Source = view;

            view.State = new RaceStateMessage(Guid.NewGuid(), 1, RacePhase.Countdown, 100, new[] { 1, 2, 3 });
            yield return null;
            Assert.That(hud.TitleText, Is.EqualTo("3"));
            Assert.That(hud.GetComponentsInChildren<Text>().Length, Is.EqualTo(2), "title + detail");

            view.State = new RaceStateMessage(view.State.RaceId, 1, RacePhase.Racing, 100, new[] { 1, 2, 3 });
            view.Progress = new RaceProgressMessage("P4", 1, 3, CrossingOutcome.Accepted);
            yield return null;
            Assert.That(hud.TitleText, Is.EqualTo("Checkpoint 2/3"));

            view.State = new RaceStateMessage(view.State.RaceId, 1, RacePhase.Results, 100, new[] { 1, 2, 3 });
            view.Results = new RaceResultsMessage(view.State.RaceId, new[] { ("P4", new RaceResult("x", 1, true, 300, 3)) });
            yield return null;
            Assert.That(hud.TitleText, Is.EqualTo("Results"));
            Assert.That(hud.DetailText, Does.Contain("1. P4  0:10.00  < you"));
        }
    }
}
