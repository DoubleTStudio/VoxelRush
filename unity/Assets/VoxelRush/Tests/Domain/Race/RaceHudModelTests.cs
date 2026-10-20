using System;
using NUnit.Framework;
using VoxelRush.Core.Race;

namespace VoxelRush.Tests.Domain.Race
{
    public sealed class RaceHudModelTests
    {
        private sealed class View : IRaceView
        {
            public TrackDefinition? Track { get; set; }

            public RaceStateMessage? State { get; set; }

            public RaceProgressMessage? Progress { get; set; }

            public RaceResultsMessage? Results { get; set; }
        }

        private static RaceStateMessage State(RacePhase phase) => new RaceStateMessage(Guid.NewGuid(), 1, phase, 100, new[] { 1, 2, 3 });

        [Test]
        public void Countdown_CountsDownThenGo()
        {
            var view = new View { State = State(RacePhase.Countdown) };

            Assert.That(RaceHudModel.Describe(view, 0.0).Title, Is.EqualTo("3"));
            Assert.That(RaceHudModel.Describe(view, 1.2).Title, Is.EqualTo("2"));
            Assert.That(RaceHudModel.Describe(view, 2.9).Title, Is.EqualTo("1"));
            Assert.That(RaceHudModel.Describe(view, 3.1).Title, Is.EqualTo("GO"));
        }

        [Test]
        public void Racing_ShowsNextCheckpoint_AndRejectionFeedback()
        {
            var view = new View { State = State(RacePhase.Racing), Progress = new RaceProgressMessage("P2", 1, 3, CrossingOutcome.Accepted) };
            Assert.That(RaceHudModel.Describe(view, 5), Is.EqualTo(("Checkpoint 2/3", "")));

            view.Progress = new RaceProgressMessage("P2", 1, 3, CrossingOutcome.TooFast);
            Assert.That(RaceHudModel.Describe(view, 5).Detail, Is.EqualTo("Checkpoint not counted"));

            view.Progress = new RaceProgressMessage("P2", 3, 3, CrossingOutcome.Finished);
            Assert.That(RaceHudModel.Describe(view, 5).Title, Is.EqualTo("Finished!"));

            Assert.That(RaceHudModel.Describe(new View { State = State(RacePhase.Racing) }, 5).Title, Is.EqualTo("Spectating"));
        }

        [Test]
        public void Results_ListPlacesTimesDnfsAndMarkMe()
        {
            var view = new View
            {
                State = State(RacePhase.Results),
                Progress = new RaceProgressMessage("P2", 3, 3, CrossingOutcome.Finished),
                Results = new RaceResultsMessage(Guid.NewGuid(), new[]
                {
                    ("P2", new RaceResult("a", 1, true, 455, 3)),
                    ("P1", new RaceResult("b", 2, false, 0, 1)),
                }),
            };

            (string title, string detail) = RaceHudModel.Describe(view, 0);

            Assert.That(title, Is.EqualTo("Results"));
            Assert.That(detail, Is.EqualTo("1. P2  0:15.17  < you\n2. P1  DNF"));
        }

        [Test]
        public void FormatTime_UsesServerTicks()
        {
            Assert.That(RaceHudModel.FormatTime(0), Is.EqualTo("0:00.00"));
            Assert.That(RaceHudModel.FormatTime(30 * 75 + 15), Is.EqualTo("1:15.50"));
        }

        [Test]
        public void WaitingAndNoRace()
        {
            Assert.That(RaceHudModel.Describe(new View { State = State(RacePhase.Waiting) }, 0).Title, Is.EqualTo("Waiting for racers"));
            Assert.That(RaceHudModel.Describe(new View(), 0), Is.EqualTo(("", "")));
        }
    }
}
