using System;
using VoxelRush.Core.Input;

namespace VoxelRush.Core.Race
{
    /// <summary>
    /// Headless test driver for races: steers toward the next gate on its own lane (a fixed lateral offset, so bots
    /// don't converge and pile up), eases off the throttle in sharp turns. Bots only; players drive themselves.
    /// </summary>
    public static class RaceAutopilot
    {
        /// <summary>A position's lateral offset across a gate, in the convention <see cref="Drive"/> expects.</summary>
        public static float LaneOf(Vec3 position, Gate gate) => -(position.X - gate.X) * gate.NormalZ + (position.Z - gate.Z) * gate.NormalX;

        public static DrivingInput Drive(Vec3 position, float headingX, float headingZ, Gate next, float laneOffset, float steerGain = 1.5f)
        {
            // Lane point on the gate line (perpendicular to its normal), kept inside the gate with a 2 m margin.
            float lane = Math.Max(-(next.HalfWidth - 2f), Math.Min(next.HalfWidth - 2f, laneOffset));
            float tx = next.X - next.NormalZ * lane + next.NormalX * 6f;
            float tz = next.Z + next.NormalX * lane + next.NormalZ * 6f;
            float dx = tx - position.X, dz = tz - position.Z;
            float length = (float)Math.Sqrt(dx * dx + dz * dz);
            if (length < 1e-3f)
            {
                return new DrivingInput(0f, 1f, 0f, DrivingButtons.None);
            }

            // Signed angle from heading to target (positive = target to the right, as steering expects).
            float cross = headingZ * (dx / length) - headingX * (dz / length);
            float dot = headingX * (dx / length) + headingZ * (dz / length);
            float angle = (float)Math.Atan2(cross, dot);
            float steer = Math.Max(-1f, Math.Min(1f, angle * steerGain));
            float throttle = Math.Abs(angle) > 0.8f ? 0.5f : 1f;
            return new DrivingInput(steer, throttle, 0f, DrivingButtons.None);
        }
    }
}
