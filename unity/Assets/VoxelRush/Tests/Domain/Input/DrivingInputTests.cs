using System;
using NUnit.Framework;
using VoxelRush.Core.Input;

namespace VoxelRush.Tests.Domain.Input
{
    public sealed class DrivingInputTests
    {
        [Test]
        public void Constructor_ClampsRangesAndNaN()
        {
            var input = new DrivingInput(-3f, 7f, float.NaN, DrivingButtons.Boost);

            Assert.That(input.Steer, Is.EqualTo(-1f));
            Assert.That(input.Throttle, Is.EqualTo(1f));
            Assert.That(input.Brake, Is.EqualTo(0f));
            Assert.That(input.Boost, Is.True);
            Assert.That(input.Handbrake, Is.False);
        }

        [Test]
        public void Constructor_DropsUnknownButtonBits()
        {
            var input = new DrivingInput(0f, 0f, 0f, (DrivingButtons)0xFF);

            Assert.That(input.Buttons, Is.EqualTo(DrivingButtons.Boost | DrivingButtons.Handbrake));
        }

        [Test]
        public void SteerAndThrottle_AreIndependent()
        {
            // DoD: steering and gas held at the same time.
            var input = new DrivingInput(-0.6f, 1f, 0f, DrivingButtons.None);

            Assert.That(input.Steer, Is.EqualTo(-0.6f));
            Assert.That(input.Throttle, Is.EqualTo(1f));
        }

        [TestCase(1f, 1000)]
        [TestCase(-1f, -1000)]
        [TestCase(0.0004f, 0)]
        [TestCase(0.0005f, 1)]
        [TestCase(-0.0005f, -1)]
        [TestCase(0.333f, 333)]
        public void Quantize_SteeringRoundsToContractScale(float steer, int expected)
        {
            var q = QuantizedDrivingInput.From(new DrivingInput(steer, 0f, 0f, DrivingButtons.None));

            Assert.That(q.Steering, Is.EqualTo(expected));
            Assert.That(q.IsValid, Is.True);
        }

        [Test]
        public void Quantize_RoundTripsWithinOneUnit()
        {
            var original = new DrivingInput(0.4567f, 0.75f, 0.125f, DrivingButtons.Handbrake);

            Assert.That(QuantizedDrivingInput.From(original).TryToInput(out DrivingInput decoded), Is.True);
            Assert.That(decoded.Steer, Is.EqualTo(original.Steer).Within(0.001f));
            Assert.That(decoded.Throttle, Is.EqualTo(original.Throttle).Within(0.001f));
            Assert.That(decoded.Brake, Is.EqualTo(original.Brake).Within(0.001f));
            Assert.That(decoded.Buttons, Is.EqualTo(DrivingButtons.Handbrake));
        }

        [TestCase((short)1001, (ushort)0, (ushort)0, (byte)0)]
        [TestCase((short)-1001, (ushort)0, (ushort)0, (byte)0)]
        [TestCase(short.MinValue, (ushort)0, (ushort)0, (byte)0)]
        [TestCase((short)0, (ushort)1001, (ushort)0, (byte)0)]
        [TestCase((short)0, (ushort)0, ushort.MaxValue, (byte)0)]
        [TestCase((short)0, (ushort)0, (ushort)0, (byte)0x04)]
        public void TryToInput_RejectsOutOfContractWireValues(short steering, ushort throttle, ushort brake, byte flags)
        {
            var q = new QuantizedDrivingInput(steering, throttle, brake, flags);

            Assert.That(q.IsValid, Is.False);
            Assert.That(q.TryToInput(out DrivingInput input), Is.False);
            Assert.That(input, Is.EqualTo(DrivingInput.Neutral));
        }
    }

    public sealed class SteeringFilterTests
    {
        [Test]
        public void Step_RampsTowardTargetAtTurnRate()
        {
            var filter = new SteeringFilter(turnRatePerSecond: 4f, returnRatePerSecond: 8f);

            Assert.That(filter.Step(1f, 0.1f), Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(filter.Step(1f, 0.1f), Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(filter.Step(1f, 0.1f), Is.EqualTo(1f));
        }

        [Test]
        public void Step_ReturnsToCentreFasterThanTurnIn()
        {
            var filter = new SteeringFilter(4f, 8f);
            filter.Step(1f, 1f);

            Assert.That(filter.Step(0f, 0.1f), Is.EqualTo(0.2f).Within(1e-5f));
        }

        [Test]
        public void Step_ReversingDirectionUsesReturnRate()
        {
            var filter = new SteeringFilter(4f, 8f);
            filter.Step(-1f, 1f);

            Assert.That(filter.Step(1f, 0.1f), Is.EqualTo(-0.2f).Within(1e-5f));
        }

        [Test]
        public void Step_IgnoresNonPositiveDeltaAndNaNTarget()
        {
            var filter = new SteeringFilter(4f, 8f);
            filter.Step(1f, 0.1f);

            Assert.That(filter.Step(1f, 0f), Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(filter.Step(1f, -1f), Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(filter.Step(float.NaN, 1f), Is.EqualTo(0f));
        }

        [Test]
        public void Constructor_RejectsNonPositiveRates()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SteeringFilter(0f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SteeringFilter(1f, float.NaN));
        }

        [Test]
        public void ControlSettings_SensitivityScalesTurnRate()
        {
            Assert.That(new ControlSettings(2f, 1f).CreateSteeringFilter().TurnRatePerSecond,
                Is.EqualTo(2f * ControlSettings.Default.CreateSteeringFilter().TurnRatePerSecond).Within(1e-5f));
        }

        [TestCase(0.49f, 1f)]
        [TestCase(2.01f, 1f)]
        [TestCase(float.NaN, 1f)]
        [TestCase(1f, 0.74f)]
        [TestCase(1f, 1.51f)]
        public void ControlSettings_RejectsOutOfRange(float sensitivity, float buttonScale)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ControlSettings(sensitivity, buttonScale));
        }
    }

    public sealed class SafeAreaLayoutTests
    {
        [Test]
        public void Compute_LandscapeNotchInsets()
        {
            // 2532x1170 landscape with 132 px notch insets left/right and 63 px home indicator at the bottom.
            var layout = SafeAreaLayout.Compute(132f, 63f, 2268f, 1107f, 2532, 1170);

            Assert.That(layout.MinX, Is.EqualTo(132f / 2532f).Within(1e-6f));
            Assert.That(layout.MinY, Is.EqualTo(63f / 1170f).Within(1e-6f));
            Assert.That(layout.MaxX, Is.EqualTo(2400f / 2532f).Within(1e-6f));
            Assert.That(layout.MaxY, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void Compute_FullScreenSafeArea_IsFull()
        {
            Assert.That(SafeAreaLayout.Compute(0f, 0f, 1920f, 1080f, 1920, 1080), Is.EqualTo(SafeAreaLayout.Full));
        }

        [Test]
        public void Compute_ClampsRectOutsideScreen()
        {
            var layout = SafeAreaLayout.Compute(-50f, -10f, 2100f, 1200f, 1920, 1080);

            Assert.That(layout, Is.EqualTo(SafeAreaLayout.Full));
        }

        [TestCase(0, 1080)]
        [TestCase(1920, 0)]
        public void Compute_DegenerateScreen_FallsBackToFull(int width, int height)
        {
            Assert.That(SafeAreaLayout.Compute(0f, 0f, 100f, 100f, width, height), Is.EqualTo(SafeAreaLayout.Full));
        }

        [Test]
        public void Compute_DegenerateSafeRect_FallsBackToFull()
        {
            Assert.That(SafeAreaLayout.Compute(10f, 10f, 0f, 500f, 1920, 1080), Is.EqualTo(SafeAreaLayout.Full));
            Assert.That(SafeAreaLayout.Compute(10f, 10f, float.NaN, 500f, 1920, 1080), Is.EqualTo(SafeAreaLayout.Full));
        }
    }
}
