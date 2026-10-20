using System;
using System.Collections.Generic;

namespace VoxelRush.Gameplay.Vehicles
{
    /// <summary>Arcade car tuning. Plain serializable data so it can be validated and unit-tested without a scene.</summary>
    [Serializable]
    public sealed class VehicleTuning
    {
        public float Mass = 1200f;
        public float CenterOfMassHeight = -0.4f;

        public float SuspensionRestLength = 0.45f;
        public float WheelRadius = 0.35f;
        public float SpringStrength = 35000f;
        public float SpringDamping = 3500f;

        public float EngineForce = 9000f;
        public float MaxSpeed = 28f;
        public float ReverseMaxSpeed = 8f;
        public float BrakeForce = 14000f;

        // Coasting deceleration per m/s of speed (1/s) when neither throttle nor brake is held.
        public float RollingResistance = 0.8f;

        public float MaxSteerAngleDegrees = 32f;
        public float HighSpeedSteerAngleDegrees = 10f;
        public float FrontGrip = 1f;
        public float RearGrip = 0.95f;
        public float HandbrakeRearGrip = 0.25f;
        public float MaxGripAcceleration = 25f;

        public float BoostForce = 7000f;
        public float BoostMaxSpeed = 36f;
        public float BoostEnergySeconds = 3f;
        public float BoostRegenPerSecond = 0.5f;
        public float BoostCooldownSeconds = 1f;

        public float AirUprightTorque = 8f;
        public float AirUprightDamping = 2f;
        public float Downforce = 2f;

        public float FlipResetSeconds = 2f;

        public IReadOnlyList<string> Validate()
        {
            var issues = new List<string>();
            Positive(issues, nameof(Mass), Mass);
            Positive(issues, nameof(SuspensionRestLength), SuspensionRestLength);
            Positive(issues, nameof(WheelRadius), WheelRadius);
            Positive(issues, nameof(SpringStrength), SpringStrength);
            NonNegative(issues, nameof(SpringDamping), SpringDamping);
            Positive(issues, nameof(EngineForce), EngineForce);
            Positive(issues, nameof(MaxSpeed), MaxSpeed);
            Positive(issues, nameof(ReverseMaxSpeed), ReverseMaxSpeed);
            Positive(issues, nameof(BrakeForce), BrakeForce);
            NonNegative(issues, nameof(RollingResistance), RollingResistance);
            Positive(issues, nameof(MaxSteerAngleDegrees), MaxSteerAngleDegrees);
            NonNegative(issues, nameof(HighSpeedSteerAngleDegrees), HighSpeedSteerAngleDegrees);
            Range01(issues, nameof(FrontGrip), FrontGrip);
            Range01(issues, nameof(RearGrip), RearGrip);
            Range01(issues, nameof(HandbrakeRearGrip), HandbrakeRearGrip);
            Positive(issues, nameof(MaxGripAcceleration), MaxGripAcceleration);
            NonNegative(issues, nameof(BoostForce), BoostForce);
            Positive(issues, nameof(BoostEnergySeconds), BoostEnergySeconds);
            NonNegative(issues, nameof(BoostRegenPerSecond), BoostRegenPerSecond);
            NonNegative(issues, nameof(BoostCooldownSeconds), BoostCooldownSeconds);
            NonNegative(issues, nameof(AirUprightTorque), AirUprightTorque);
            NonNegative(issues, nameof(AirUprightDamping), AirUprightDamping);
            NonNegative(issues, nameof(Downforce), Downforce);
            Positive(issues, nameof(FlipResetSeconds), FlipResetSeconds);

            if (HighSpeedSteerAngleDegrees > MaxSteerAngleDegrees)
            {
                issues.Add($"{nameof(HighSpeedSteerAngleDegrees)} must not exceed {nameof(MaxSteerAngleDegrees)}.");
            }

            if (BoostMaxSpeed < MaxSpeed)
            {
                issues.Add($"{nameof(BoostMaxSpeed)} must be at least {nameof(MaxSpeed)}.");
            }

            if (HandbrakeRearGrip > RearGrip)
            {
                issues.Add($"{nameof(HandbrakeRearGrip)} must not exceed {nameof(RearGrip)}.");
            }

            return issues;
        }

        private static void Positive(List<string> issues, string name, float value)
        {
            if (!(value > 0) || float.IsInfinity(value))
            {
                issues.Add($"{name} must be positive (got {value}).");
            }
        }

        private static void NonNegative(List<string> issues, string name, float value)
        {
            if (!(value >= 0) || float.IsInfinity(value))
            {
                issues.Add($"{name} must be >= 0 (got {value}).");
            }
        }

        private static void Range01(List<string> issues, string name, float value)
        {
            if (!(value >= 0 && value <= 1))
            {
                issues.Add($"{name} must be in [0, 1] (got {value}).");
            }
        }
    }
}
