using System;

namespace VoxelRush.Core.Input
{
    /// <summary>
    /// Ramps steering toward the raw target so digital touch buttons don't snap to full lock;
    /// returns toward centre faster than it turns in, and analog sticks pass through when instant.
    /// </summary>
    public sealed class SteeringFilter
    {
        public SteeringFilter(float turnRatePerSecond, float returnRatePerSecond)
        {
            if (!(turnRatePerSecond > 0) || !(returnRatePerSecond > 0))
            {
                throw new ArgumentOutOfRangeException(nameof(turnRatePerSecond), "Rates must be positive.");
            }

            TurnRatePerSecond = turnRatePerSecond;
            ReturnRatePerSecond = returnRatePerSecond;
        }

        public float TurnRatePerSecond { get; }

        public float ReturnRatePerSecond { get; }

        public float Value { get; private set; }

        public float Step(float target, float deltaSeconds)
        {
            target = DrivingInput.Clamp(target, -1f, 1f);
            if (!(deltaSeconds > 0))
            {
                return Value;
            }

            // Moving toward zero or reversing direction uses the faster return rate.
            bool returning = Math.Abs(target) < Math.Abs(Value) || Math.Sign(target) * Math.Sign(Value) < 0;
            float maxStep = (returning ? ReturnRatePerSecond : TurnRatePerSecond) * deltaSeconds;
            float delta = target - Value;
            Value = Math.Abs(delta) <= maxStep ? target : Value + Math.Sign(delta) * maxStep;
            return Value;
        }

        public void Reset() => Value = 0f;
    }
}
