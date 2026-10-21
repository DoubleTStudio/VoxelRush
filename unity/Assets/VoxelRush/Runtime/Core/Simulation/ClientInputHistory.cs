using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Simulation
{
    /// <summary>
    /// Client-side record of inputs not yet acknowledged by the server: replayed after a correction and resent
    /// redundantly so a lost unreliable packet doesn't lose input. Bounded; the oldest entries drop first.
    /// </summary>
    public sealed class ClientInputHistory
    {
        private readonly InputCommand[] _ring;
        private int _start;
        private int _count;

        public ClientInputHistory(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _ring = new InputCommand[capacity];
        }

        public int Count => _count;

        public uint NextSequence { get; private set; } = 1;

        public InputCommand Record(uint clientTick, Input.QuantizedDrivingInput input)
        {
            var command = new InputCommand(NextSequence++, clientTick, input);
            if (_count == _ring.Length)
            {
                _start = (_start + 1) % _ring.Length;
                _count--;
            }

            _ring[(_start + _count) % _ring.Length] = command;
            _count++;
            return command;
        }

        /// <summary>Drops every input the server has processed (sequence ≤ <paramref name="lastProcessed"/>).</summary>
        public void Acknowledge(uint lastProcessed)
        {
            while (_count > 0 && _ring[_start].Sequence <= lastProcessed)
            {
                _start = (_start + 1) % _ring.Length;
                _count--;
            }
        }

        public InputCommand this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _ring[(_start + index) % _ring.Length];
            }
        }

        /// <summary>Copies the newest up-to-<paramref name="max"/> commands, oldest first, for a redundant send.</summary>
        public int CopyNewest(List<InputCommand> target, int max)
        {
            target.Clear();
            int take = Math.Min(max, _count);
            for (int i = _count - take; i < _count; i++)
            {
                target.Add(this[i]);
            }

            return take;
        }
    }
}
