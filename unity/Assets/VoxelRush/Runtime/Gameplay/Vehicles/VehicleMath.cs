using System;

namespace VoxelRush.Gameplay.Vehicles
{
    /// <summary>Pure per-step vehicle force math (SI units); the MonoBehaviour only gathers state and applies results.</summary>
    public static class VehicleMath
    {
        /// <summary>Spring-damper suspension force along the wheel's up axis; never negative (suspension pushes, never pulls).</summary>
        public static float SuspensionForce(float compression, float compressionVelocity, float strength, float damping)
        {
            if (!(compression > 0))
            {
                return 0f;
            }

            float force = compression * strength + compressionVelocity * damping;
            return force > 0 && !float.IsNaN(force) ? force : 0f;
        }

        /// <summary>Steering lock narrows linearly from full at standstill to the high-speed angle at max speed.</summary>
        public static float SteerAngleDegrees(float steer, float speed, VehicleTuning tuning)
        {
            float t = Clamp01(Math.Abs(speed) / tuning.MaxSpeed);
            float maxAngle = tuning.MaxSteerAngleDegrees + (tuning.HighSpeedSteerAngleDegrees - tuning.MaxSteerAngleDegrees) * t;
            return Clamp(steer, -1f, 1f) * maxAngle;
        }

        /// <summary>
        /// Longitudinal force from throttle/brake given signed forward speed. Throttle fades out at top speed;
        /// brake slows a forward-moving car, and reverses up to the reverse limit once nearly stopped.
        /// With no pedal held, rolling resistance slows the car toward rest.
        /// </summary>
        public static float DriveForce(float throttle, float brake, float forwardSpeed, float maxSpeed, VehicleTuning tuning)
        {
            throttle = Clamp01(throttle);
            brake = Clamp01(brake);
            if (float.IsNaN(forwardSpeed))
            {
                return 0f;
            }

            if (throttle <= 0 && brake <= 0)
            {
                return -forwardSpeed * tuning.RollingResistance * tuning.Mass;
            }

            float force = 0f;
            if (throttle > 0)
            {
                // Throttle while rolling backwards brakes first.
                force += forwardSpeed < -0.5f
                    ? throttle * tuning.BrakeForce
                    : throttle * tuning.EngineForce * Clamp01(1f - forwardSpeed / maxSpeed);
            }

            if (brake > 0)
            {
                force -= forwardSpeed > 0.5f
                    ? brake * tuning.BrakeForce
                    : brake * tuning.EngineForce * 0.6f * Clamp01(1f + forwardSpeed / tuning.ReverseMaxSpeed);
            }

            return force;
        }

        /// <summary>
        /// Lateral friction force (N) that cancels sideways slip at a wheel, scaled by grip and capped so the
        /// tyre can slide (drift) instead of producing unbounded corrective forces.
        /// </summary>
        public static float LateralGripForce(float lateralVelocity, float grip, float massPerWheel, float maxGripAcceleration, float deltaTime)
        {
            if (!(deltaTime > 0) || float.IsNaN(lateralVelocity))
            {
                return 0f;
            }

            float desired = -lateralVelocity * Clamp01(grip) * massPerWheel / deltaTime;
            float limit = maxGripAcceleration * massPerWheel;
            return Clamp(desired, -limit, limit);
        }

        public static float Clamp01(float value) => Clamp(value, 0f, 1f);

        public static float Clamp(float value, float min, float max) =>
            float.IsNaN(value) ? 0f : value < min ? min : value > max ? max : value;
    }
}
