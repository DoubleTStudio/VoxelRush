using System;

namespace VoxelRush.Core.Input
{
    [Flags]
    public enum DrivingButtons : byte
    {
        None = 0,
        Boost = 1 << 0,
        Handbrake = 1 << 1,
    }

    /// <summary>One frame of driving intent: steering [-1, 1], throttle/brake [0, 1], held buttons.</summary>
    public readonly struct DrivingInput : IEquatable<DrivingInput>
    {
        public static readonly DrivingInput Neutral = default;

        public DrivingInput(float steer, float throttle, float brake, DrivingButtons buttons)
        {
            Steer = Clamp(steer, -1f, 1f);
            Throttle = Clamp(throttle, 0f, 1f);
            Brake = Clamp(brake, 0f, 1f);
            Buttons = buttons & (DrivingButtons.Boost | DrivingButtons.Handbrake);
        }

        public float Steer { get; }

        public float Throttle { get; }

        public float Brake { get; }

        public DrivingButtons Buttons { get; }

        public bool Boost => (Buttons & DrivingButtons.Boost) != 0;

        public bool Handbrake => (Buttons & DrivingButtons.Handbrake) != 0;

        public bool Equals(DrivingInput other) =>
            Steer.Equals(other.Steer) && Throttle.Equals(other.Throttle) && Brake.Equals(other.Brake) && Buttons == other.Buttons;

        public override bool Equals(object? obj) => obj is DrivingInput other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Steer, Throttle, Brake, Buttons);

        public override string ToString() => $"steer={Steer:0.###} throttle={Throttle:0.###} brake={Brake:0.###} buttons={Buttons}";

        // NaN (e.g. from a broken device) becomes neutral rather than propagating into simulation.
        internal static float Clamp(float value, float min, float max) =>
            float.IsNaN(value) ? 0f : value < min ? min : value > max ? max : value;
    }
}
