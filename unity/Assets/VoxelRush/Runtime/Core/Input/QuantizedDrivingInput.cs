using System;

namespace VoxelRush.Core.Input
{
    /// <summary>Wire form of <see cref="DrivingInput"/> per CONTRACTS.md ClientInput: steering int16 [-1000, 1000], throttle/brake uint16 [0, 1000], flags uint8.</summary>
    public readonly struct QuantizedDrivingInput : IEquatable<QuantizedDrivingInput>
    {
        public const int Scale = 1000;

        public QuantizedDrivingInput(short steering, ushort throttle, ushort brake, byte flags)
        {
            Steering = steering;
            Throttle = throttle;
            Brake = brake;
            Flags = flags;
        }

        public short Steering { get; }

        public ushort Throttle { get; }

        public ushort Brake { get; }

        public byte Flags { get; }

        /// <summary>True when every field is inside its contract range; the server must reject anything else.</summary>
        public bool IsValid =>
            Steering >= -Scale && Steering <= Scale && Throttle <= Scale && Brake <= Scale
            && (Flags & ~(byte)(DrivingButtons.Boost | DrivingButtons.Handbrake)) == 0;

        public static QuantizedDrivingInput From(DrivingInput input) => new QuantizedDrivingInput(
            (short)Math.Round(input.Steer * Scale, MidpointRounding.AwayFromZero),
            (ushort)Math.Round(input.Throttle * Scale, MidpointRounding.AwayFromZero),
            (ushort)Math.Round(input.Brake * Scale, MidpointRounding.AwayFromZero),
            (byte)input.Buttons);

        /// <summary>Decodes only valid values; out-of-range wire data never becomes simulation input.</summary>
        public bool TryToInput(out DrivingInput input)
        {
            if (!IsValid)
            {
                input = DrivingInput.Neutral;
                return false;
            }

            input = new DrivingInput(Steering / (float)Scale, Throttle / (float)Scale, Brake / (float)Scale, (DrivingButtons)Flags);
            return true;
        }

        public bool Equals(QuantizedDrivingInput other) =>
            Steering == other.Steering && Throttle == other.Throttle && Brake == other.Brake && Flags == other.Flags;

        public override bool Equals(object? obj) => obj is QuantizedDrivingInput other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Steering, Throttle, Brake, Flags);
    }
}
