using NUnit.Framework;
using VoxelRush.Gameplay.Vehicles;

namespace VoxelRush.Tests.EditMode
{
    public sealed class VehicleMathTests
    {
        private static readonly VehicleTuning Tuning = new VehicleTuning();

        [Test]
        public void Suspension_UncompressedOrExtended_PushesNothing()
        {
            Assert.That(VehicleMath.SuspensionForce(0f, 0f, 35000f, 3500f), Is.Zero);
            Assert.That(VehicleMath.SuspensionForce(-0.1f, 5f, 35000f, 3500f), Is.Zero);
        }

        [Test]
        public void Suspension_NeverPulls_WhenExtendingFast()
        {
            // Rebound faster than the spring pushes would mean a negative (pulling) force.
            Assert.That(VehicleMath.SuspensionForce(0.01f, -10f, 35000f, 3500f), Is.Zero);
        }

        [Test]
        public void Suspension_IncreasesWithCompressionAndCompressionSpeed()
        {
            float soft = VehicleMath.SuspensionForce(0.1f, 0f, 35000f, 3500f);
            float hard = VehicleMath.SuspensionForce(0.2f, 0f, 35000f, 3500f);
            float damped = VehicleMath.SuspensionForce(0.1f, 1f, 35000f, 3500f);

            Assert.That(soft, Is.EqualTo(3500f).Within(1e-3f));
            Assert.That(hard, Is.GreaterThan(soft));
            Assert.That(damped, Is.EqualTo(soft + 3500f).Within(1e-3f));
        }

        [Test]
        public void SteerAngle_NarrowsWithSpeed()
        {
            Assert.That(VehicleMath.SteerAngleDegrees(1f, 0f, Tuning), Is.EqualTo(Tuning.MaxSteerAngleDegrees));
            Assert.That(VehicleMath.SteerAngleDegrees(1f, Tuning.MaxSpeed, Tuning), Is.EqualTo(Tuning.HighSpeedSteerAngleDegrees).Within(1e-4f));
            Assert.That(VehicleMath.SteerAngleDegrees(-1f, Tuning.MaxSpeed * 3f, Tuning), Is.EqualTo(-Tuning.HighSpeedSteerAngleDegrees).Within(1e-4f));
            Assert.That(VehicleMath.SteerAngleDegrees(float.NaN, 5f, Tuning), Is.Zero);
        }

        [Test]
        public void Drive_ThrottleFadesToZeroAtTopSpeed()
        {
            Assert.That(VehicleMath.DriveForce(1f, 0f, 0f, Tuning.MaxSpeed, Tuning), Is.EqualTo(Tuning.EngineForce));
            Assert.That(VehicleMath.DriveForce(1f, 0f, Tuning.MaxSpeed, Tuning.MaxSpeed, Tuning), Is.Zero);
            Assert.That(VehicleMath.DriveForce(1f, 0f, Tuning.MaxSpeed + 5f, Tuning.MaxSpeed, Tuning), Is.Zero);
        }

        [Test]
        public void Drive_BrakeSlowsForwardThenReversesUpToLimit()
        {
            Assert.That(VehicleMath.DriveForce(0f, 1f, 10f, Tuning.MaxSpeed, Tuning), Is.EqualTo(-Tuning.BrakeForce));
            Assert.That(VehicleMath.DriveForce(0f, 1f, 0f, Tuning.MaxSpeed, Tuning), Is.LessThan(0f));
            Assert.That(VehicleMath.DriveForce(0f, 1f, -Tuning.ReverseMaxSpeed, Tuning.MaxSpeed, Tuning), Is.Zero);
        }

        [Test]
        public void Drive_NoPedals_RollingResistanceOpposesMotion()
        {
            Assert.That(VehicleMath.DriveForce(0f, 0f, 10f, Tuning.MaxSpeed, Tuning), Is.EqualTo(-10f * Tuning.RollingResistance * Tuning.Mass).Within(1e-2f));
            Assert.That(VehicleMath.DriveForce(0f, 0f, -4f, Tuning.MaxSpeed, Tuning), Is.GreaterThan(0f));
            Assert.That(VehicleMath.DriveForce(0f, 0f, 0f, Tuning.MaxSpeed, Tuning), Is.Zero);
        }

        [Test]
        public void Drive_ThrottleWhileRollingBack_Brakes()
        {
            Assert.That(VehicleMath.DriveForce(1f, 0f, -5f, Tuning.MaxSpeed, Tuning), Is.EqualTo(Tuning.BrakeForce));
        }

        [Test]
        public void Drive_NaNSpeedOrInput_IsSafe()
        {
            Assert.That(VehicleMath.DriveForce(1f, 0f, float.NaN, Tuning.MaxSpeed, Tuning), Is.Zero);
            Assert.That(float.IsNaN(VehicleMath.DriveForce(float.NaN, float.NaN, 3f, Tuning.MaxSpeed, Tuning)), Is.False);
        }

        [Test]
        public void LateralGrip_OpposesSlipAndIsCapped()
        {
            float massPerWheel = 300f;
            float small = VehicleMath.LateralGripForce(0.1f, 1f, massPerWheel, 25f, 1f / 60f);
            float huge = VehicleMath.LateralGripForce(50f, 1f, massPerWheel, 25f, 1f / 60f);

            Assert.That(small, Is.LessThan(0f));
            Assert.That(huge, Is.EqualTo(-25f * massPerWheel));
            Assert.That(VehicleMath.LateralGripForce(-50f, 1f, massPerWheel, 25f, 1f / 60f), Is.EqualTo(25f * massPerWheel));
        }

        [Test]
        public void LateralGrip_LowerGripSlidesMore()
        {
            float grippy = VehicleMath.LateralGripForce(0.5f, 1f, 300f, 1000f, 1f / 60f);
            float drifting = VehicleMath.LateralGripForce(0.5f, 0.25f, 300f, 1000f, 1f / 60f);

            Assert.That(System.Math.Abs(drifting), Is.LessThan(System.Math.Abs(grippy)));
        }

        [Test]
        public void LateralGrip_ZeroDeltaOrNaN_IsZero()
        {
            Assert.That(VehicleMath.LateralGripForce(1f, 1f, 300f, 25f, 0f), Is.Zero);
            Assert.That(VehicleMath.LateralGripForce(float.NaN, 1f, 300f, 25f, 0.02f), Is.Zero);
        }
    }

    public sealed class BoostSystemTests
    {
        [Test]
        public void Boost_DrainsWhileHeld_ThenStopsWhenEmpty()
        {
            var boost = new BoostSystem(capacitySeconds: 1f, regenPerSecond: 0.5f, cooldownSeconds: 1f);

            for (int i = 0; i < 10; i++)
            {
                Assert.That(boost.Step(true, 0.1f), Is.True, $"step {i}");
            }

            Assert.That(boost.Energy, Is.Zero.Within(1e-5f));
            Assert.That(boost.Step(true, 0.1f), Is.False, "empty boost must not fire");
        }

        [Test]
        public void Boost_WaitsCooldownBeforeRegenerating()
        {
            var boost = new BoostSystem(1f, 0.5f, 1f);
            for (int i = 0; i < 10; i++)
            {
                boost.Step(true, 0.1f);
            }

            for (int i = 0; i < 10; i++)
            {
                boost.Step(false, 0.1f);
            }

            Assert.That(boost.Energy, Is.Zero.Within(1e-5f), "no regen during cooldown");
            boost.Step(false, 1f);
            Assert.That(boost.Energy, Is.EqualTo(0.5f).Within(1e-5f));
            boost.Step(false, 5f);
            Assert.That(boost.Energy, Is.EqualTo(1f), "regen caps at capacity");
        }

        [Test]
        public void Boost_RetapDuringCooldown_DoesNotFire()
        {
            var boost = new BoostSystem(1f, 0.5f, 1f);
            boost.Step(true, 0.2f);
            boost.Step(false, 0.1f);

            Assert.That(boost.Step(true, 0.1f), Is.False, "cooldown after release blocks re-trigger");
        }

        [Test]
        public void Boost_IsDeterministic()
        {
            var a = new BoostSystem(3f, 0.5f, 1f);
            var b = new BoostSystem(3f, 0.5f, 1f);
            bool[] pattern = { true, true, false, true, false, false, true, true, true };
            foreach (bool held in pattern)
            {
                Assert.That(a.Step(held, 1f / 30f), Is.EqualTo(b.Step(held, 1f / 30f)));
            }

            Assert.That(a.Energy, Is.EqualTo(b.Energy));
        }

        [Test]
        public void Boost_CaptureRestore_ContinuesIdentically()
        {
            var original = new BoostSystem(3f, 0.5f, 1f);
            original.Step(true, 0.5f);
            original.Step(false, 0.2f);
            BoostState saved = original.Capture();

            var copy = new BoostSystem(3f, 0.5f, 1f);
            copy.Restore(saved);
            foreach (bool held in new[] { false, false, true, true, false })
            {
                Assert.That(copy.Step(held, 0.25f), Is.EqualTo(original.Step(held, 0.25f)));
            }

            Assert.That(copy.Energy, Is.EqualTo(original.Energy));
        }

        [Test]
        public void Boost_Restore_ClampsHostileState()
        {
            var boost = new BoostSystem(3f, 0.5f, 1f);
            boost.Restore(new BoostState(99f, -5f, true));

            Assert.That(boost.Energy, Is.EqualTo(3f));
        }

        [Test]
        public void FlipRecovery_ResetsOnlyAfterSustainedFlip()
        {
            var recovery = new FlipRecovery(resetSeconds: 2f);

            Assert.That(recovery.Step(-1f, 0f, 1.5f), Is.False);
            Assert.That(recovery.Step(1f, 0f, 1f), Is.False, "upright resets the timer");
            Assert.That(recovery.Step(-1f, 0f, 1.5f), Is.False);
            Assert.That(recovery.Step(-1f, 0f, 0.6f), Is.True);
        }

        [Test]
        public void FlipRecovery_IgnoresTiltWhileMoving()
        {
            var recovery = new FlipRecovery(2f);

            Assert.That(recovery.Step(0f, 10f, 5f), Is.False, "banked/airborne fast cars aren't stuck");
        }

        [Test]
        public void Tuning_DefaultsAreValid()
        {
            Assert.That(new VehicleTuning().Validate(), Is.Empty);
        }

        [Test]
        public void Tuning_RejectsInconsistentValues()
        {
            var tuning = new VehicleTuning { Mass = 0f, BoostMaxSpeed = 1f, HandbrakeRearGrip = 2f, SpringStrength = float.NaN };

            var issues = tuning.Validate();

            Assert.That(issues, Has.Some.Contains("Mass"));
            Assert.That(issues, Has.Some.Contains("BoostMaxSpeed"));
            Assert.That(issues, Has.Some.Contains("HandbrakeRearGrip"));
            Assert.That(issues, Has.Some.Contains("SpringStrength"));
        }
    }
}
