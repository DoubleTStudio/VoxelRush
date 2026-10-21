using System;
using VoxelRush.Core.Input;

namespace VoxelRush.Core.Simulation
{
    /// <summary>One sequenced client input for one simulation tick (CONTRACTS ClientInput).</summary>
    public readonly struct InputCommand : IEquatable<InputCommand>
    {
        public InputCommand(uint sequence, uint clientTick, QuantizedDrivingInput input)
        {
            Sequence = sequence;
            ClientTick = clientTick;
            Input = input;
        }

        public uint Sequence { get; }

        public uint ClientTick { get; }

        public QuantizedDrivingInput Input { get; }

        public bool Equals(InputCommand other) => Sequence == other.Sequence && ClientTick == other.ClientTick && Input.Equals(other.Input);

        public override bool Equals(object? obj) => obj is InputCommand other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Sequence, ClientTick, Input);
    }
}
