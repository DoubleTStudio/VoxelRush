using System;
using System.Collections.Generic;
using NUnit.Framework;
using VoxelRush.Core.Input;
using VoxelRush.Core.Simulation;

namespace VoxelRush.Tests.Domain.Simulation
{
    public sealed class ClientInputHistoryTests
    {
        private static readonly QuantizedDrivingInput Gas = QuantizedDrivingInput.From(new DrivingInput(0f, 1f, 0f, DrivingButtons.None));

        [Test]
        public void Record_AssignsIncreasingSequencesFromOne()
        {
            var history = new ClientInputHistory(8);

            Assert.That(history.Record(10, Gas).Sequence, Is.EqualTo(1));
            Assert.That(history.Record(11, Gas).Sequence, Is.EqualTo(2));
            Assert.That(history.Count, Is.EqualTo(2));
        }

        [Test]
        public void Acknowledge_DropsProcessedInputsOnly()
        {
            var history = new ClientInputHistory(8);
            for (uint t = 0; t < 5; t++)
            {
                history.Record(t, Gas);
            }

            history.Acknowledge(3);

            Assert.That(history.Count, Is.EqualTo(2));
            Assert.That(history[0].Sequence, Is.EqualTo(4));
            history.Acknowledge(1);
            Assert.That(history.Count, Is.EqualTo(2), "an older ack must not resurrect or drop anything");
        }

        [Test]
        public void Record_WhenFull_DropsOldest()
        {
            var history = new ClientInputHistory(3);
            for (uint t = 0; t < 5; t++)
            {
                history.Record(t, Gas);
            }

            Assert.That(history.Count, Is.EqualTo(3));
            Assert.That(history[0].Sequence, Is.EqualTo(3));
            Assert.That(history[2].Sequence, Is.EqualTo(5));
        }

        [Test]
        public void CopyNewest_ReturnsOldestFirst()
        {
            var history = new ClientInputHistory(8);
            for (uint t = 0; t < 5; t++)
            {
                history.Record(t, Gas);
            }

            var batch = new List<InputCommand>();
            Assert.That(history.CopyNewest(batch, 3), Is.EqualTo(3));
            Assert.That(batch.ConvertAll(c => c.Sequence), Is.EqualTo(new uint[] { 3, 4, 5 }));
        }
    }

    public sealed class ServerInputQueueTests
    {
        private static readonly QuantizedDrivingInput Gas = QuantizedDrivingInput.From(new DrivingInput(0.5f, 1f, 0f, DrivingButtons.None));

        private static InputCommand Cmd(uint sequence, QuantizedDrivingInput? input = null) => new InputCommand(sequence, sequence, input ?? Gas);

        [Test]
        public void Consume_AppliesInputsInSequenceOrder_EvenIfReceivedOutOfOrder()
        {
            var queue = new ServerInputQueue();
            queue.Enqueue(Cmd(2, QuantizedDrivingInput.From(new DrivingInput(-1f, 0f, 0f, DrivingButtons.None))), 0);
            queue.Enqueue(Cmd(1), 0);

            Assert.That(queue.Consume().Steer, Is.EqualTo(0.5f));
            Assert.That(queue.LastProcessed, Is.EqualTo(1));
            Assert.That(queue.Consume().Steer, Is.EqualTo(-1f));
            Assert.That(queue.LastProcessed, Is.EqualTo(2));
        }

        [Test]
        public void Enqueue_RedundantResends_AreDuplicatesNotCountedAsAbuse()
        {
            var queue = new ServerInputQueue();
            queue.Enqueue(Cmd(1), 0);
            queue.Consume();

            Assert.That(queue.Enqueue(Cmd(1), 1), Is.EqualTo(InputRejection.Duplicate));
            queue.Enqueue(Cmd(2), 1);
            Assert.That(queue.Enqueue(Cmd(2), 1), Is.EqualTo(InputRejection.Duplicate));
            Assert.That(queue.Rejected, Is.Zero);
        }

        [Test]
        public void Enqueue_SpeedHackOutOfContractInput_IsRejectedAndNeverSimulated()
        {
            var queue = new ServerInputQueue();
            var hacked = new QuantizedDrivingInput(0, 5000, 0, 0);

            Assert.That(queue.Enqueue(Cmd(1, hacked), 0), Is.EqualTo(InputRejection.Malformed));
            Assert.That(queue.Rejected, Is.EqualTo(1));
            Assert.That(queue.Consume(), Is.EqualTo(DrivingInput.Neutral));
            Assert.That(queue.LastProcessed, Is.Zero);
        }

        [Test]
        public void Enqueue_FarFutureSequence_IsRejected()
        {
            var queue = new ServerInputQueue(maxAhead: 64);

            Assert.That(queue.Enqueue(Cmd(65), 0), Is.EqualTo(InputRejection.TooFarAhead));
            Assert.That(queue.Enqueue(Cmd(64), 0), Is.EqualTo(InputRejection.None));
        }

        [Test]
        public void Enqueue_FloodOfNewSequences_IsRateLimited()
        {
            var queue = new ServerInputQueue(capacity: 1000, maxAhead: 1000, tickRate: 30);
            int accepted = 0;
            for (uint s = 1; s <= 200; s++)
            {
                if (queue.Enqueue(Cmd(s), 5) == InputRejection.None)
                {
                    accepted++;
                }
            }

            Assert.That(accepted, Is.EqualTo(60), "at most 2x tick rate new inputs per second");
            Assert.That(queue.Rejected, Is.EqualTo(140));
        }

        [Test]
        public void Enqueue_RespectsCapacity()
        {
            var queue = new ServerInputQueue(capacity: 2);
            queue.Enqueue(Cmd(1), 0);
            queue.Enqueue(Cmd(2), 0);

            Assert.That(queue.Enqueue(Cmd(3), 0), Is.EqualTo(InputRejection.QueueFull));
        }

        [Test]
        public void Consume_WhenStarved_RepeatsLastBrieflyThenGoesNeutral()
        {
            var queue = new ServerInputQueue(maxRepeatTicks: 2);
            queue.Enqueue(Cmd(1), 0);
            queue.Consume();

            Assert.That(queue.Consume().Throttle, Is.EqualTo(1f), "repeat 1");
            Assert.That(queue.Consume().Throttle, Is.EqualTo(1f), "repeat 2");
            Assert.That(queue.Consume(), Is.EqualTo(DrivingInput.Neutral), "then neutral: a disconnected client doesn't keep accelerating");
        }

        [Test]
        public void Constructor_RejectsInvalidArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerInputQueue(capacity: 0));
        }
    }

    public sealed class CorrectionPolicyAndClockTests
    {
        [TestCase(0.01f, 0.5f, CorrectionKind.None)]
        [TestCase(0.5f, 1f, CorrectionKind.Blend)]
        [TestCase(0.01f, 10f, CorrectionKind.Blend)]
        [TestCase(3f, 0f, CorrectionKind.Snap)]
        [TestCase(0f, 90f, CorrectionKind.Snap)]
        [TestCase(float.NaN, 0f, CorrectionKind.Snap)]
        public void Policy_ClassifiesErrors(float meters, float degrees, CorrectionKind expected)
        {
            Assert.That(new CorrectionPolicy().Decide(meters, degrees), Is.EqualTo(expected));
        }

        [Test]
        public void Clock_EmitsFixedTicksAndCarriesRemainder()
        {
            var clock = new TickClock(tickRate: 30);

            Assert.That(clock.Advance(0.02), Is.Zero);
            Assert.That(clock.Advance(0.02), Is.EqualTo(1));
            Assert.That(clock.Advance(1.0 / 30 * 2), Is.EqualTo(2));
            Assert.That(clock.Tick, Is.EqualTo(3));
        }

        [Test]
        public void Clock_CapsCatchUpAfterStall()
        {
            var clock = new TickClock(tickRate: 30, maxTicksPerFrame: 4);

            Assert.That(clock.Advance(1.0), Is.EqualTo(4));
            Assert.That(clock.DroppedTicks, Is.EqualTo(26));
        }

        [Test]
        public void Clock_IgnoresNonPositiveDelta()
        {
            var clock = new TickClock();

            Assert.That(clock.Advance(0), Is.Zero);
            Assert.That(clock.Advance(-1), Is.Zero);
            Assert.That(clock.Advance(double.NaN), Is.Zero);
        }
    }
}

namespace VoxelRush.Tests.Domain.Simulation
{
    public sealed class DelayLineTests
    {
        [Test]
        public void Receive_ReleasesOnlyAfterDelay()
        {
            var line = new DelayLine<int>(0.05);
            var ready = new List<int>();
            line.Send(1, 0.0);

            Assert.That(line.Receive(0.049, ready), Is.Zero);
            Assert.That(line.Receive(0.05, ready), Is.EqualTo(1));
            Assert.That(ready, Is.EqualTo(new[] { 1 }));
            Assert.That(line.InFlight, Is.Zero);
        }

        [Test]
        public void ZeroDelay_IsImmediate_AndPreservesOrder()
        {
            var line = new DelayLine<int>(0);
            var ready = new List<int>();
            line.Send(1, 1.0);
            line.Send(2, 1.0);

            line.Receive(1.0, ready);
            Assert.That(ready, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void Jitter_StaysWithinBounds_AndCanReorder()
        {
            var line = new DelayLine<int>(0.05, jitterSeconds: 0.02, seed: 7);
            var ready = new List<int>();
            for (int i = 0; i < 200; i++)
            {
                line.Send(i, i * 0.001);
            }

            Assert.That(line.Receive(0.029, ready), Is.Zero, "nothing earlier than delay - jitter");
            line.Receive(0.199 + 0.0701, ready);
            Assert.That(ready.Count, Is.EqualTo(200), "everything by delay + jitter");
        }

        [Test]
        public void Loss_DropsRoughlyTheConfiguredPercentage_Reproducibly()
        {
            var a = new DelayLine<int>(0, lossPercent: 10, seed: 42);
            var b = new DelayLine<int>(0, lossPercent: 10, seed: 42);
            for (int i = 0; i < 10000; i++)
            {
                a.Send(i, 0);
                b.Send(i, 0);
            }

            Assert.That(a.Dropped, Is.InRange(850, 1150));
            Assert.That(a.Dropped, Is.EqualTo(b.Dropped), "same seed, same losses");
        }

        [TestCase(-1, 0, 0)]
        [TestCase(0.01, 0.02, 0)]
        [TestCase(0, 0, 101)]
        public void Constructor_RejectsInvalidParameters(double delay, double jitter, double loss)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new DelayLine<int>(delay, jitter, loss));
        }
    }
}
