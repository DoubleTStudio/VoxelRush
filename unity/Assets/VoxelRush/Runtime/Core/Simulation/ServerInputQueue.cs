using System;
using System.Collections.Generic;
using VoxelRush.Core.Input;

namespace VoxelRush.Core.Simulation
{
    public enum InputRejection
    {
        None,
        /// <summary>Already processed or queued: normal for redundant sends, and replays are harmless.</summary>
        Duplicate,
        TooFarAhead,
        Malformed,
        RateLimited,
        QueueFull,
    }

    /// <summary>
    /// Per-client authoritative input queue (VEHICLE_NETWORKING security): accepts only new, in-window, in-contract
    /// sequences; consumes one per server tick; repeats the last input briefly when starved, then goes neutral.
    /// </summary>
    public sealed class ServerInputQueue
    {
        private readonly SortedList<uint, QuantizedDrivingInput> _pending = new SortedList<uint, QuantizedDrivingInput>();
        private readonly int _capacity;
        private readonly uint _maxAhead;
        private readonly int _maxRepeatTicks;
        private readonly int _maxAcceptedPerWindow;
        private readonly int _rateWindowTicks;
        private QuantizedDrivingInput _last;
        private int _starvedTicks;
        private int _acceptedInWindow;
        private uint _windowStartTick;

        public ServerInputQueue(int capacity = 32, uint maxAhead = 64, int maxRepeatTicks = 6, int tickRate = SimulationConstants.ServerTickRate)
        {
            if (capacity < 1 || maxAhead < 1 || maxRepeatTicks < 0 || tickRate < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
            _maxAhead = maxAhead;
            _maxRepeatTicks = maxRepeatTicks;
            _rateWindowTicks = tickRate;
            // Redundant sends repeat inputs, but distinct new sequences can't legitimately exceed ~1 per tick.
            _maxAcceptedPerWindow = tickRate * 2;
        }

        /// <summary>Highest sequence applied to the simulation; echoed to the client in snapshots.</summary>
        public uint LastProcessed { get; private set; }

        public int Pending => _pending.Count;

        public long Rejected { get; private set; }

        public InputRejection Enqueue(InputCommand command, uint serverTick)
        {
            InputRejection result = Validate(command, serverTick);
            if (result == InputRejection.None)
            {
                _pending.Add(command.Sequence, command.Input);
                _acceptedInWindow++;
            }
            else if (result != InputRejection.Duplicate)
            {
                // Duplicates are expected from redundant sends; everything else is suspicious.
                Rejected++;
            }

            return result;
        }

        /// <summary>Input to simulate this tick. Starved: repeat the last input for a few ticks, then neutral.</summary>
        public DrivingInput Consume()
        {
            if (_pending.Count > 0)
            {
                uint sequence = _pending.Keys[0];
                _last = _pending.Values[0];
                _pending.RemoveAt(0);
                LastProcessed = sequence;
                _starvedTicks = 0;
            }
            else if (++_starvedTicks > _maxRepeatTicks)
            {
                _last = default;
            }

            return _last.TryToInput(out DrivingInput input) ? input : DrivingInput.Neutral;
        }

        private InputRejection Validate(InputCommand command, uint serverTick)
        {
            if (!command.Input.IsValid)
            {
                return InputRejection.Malformed;
            }

            if (command.Sequence <= LastProcessed || _pending.ContainsKey(command.Sequence))
            {
                return InputRejection.Duplicate;
            }

            if (command.Sequence > LastProcessed + _maxAhead)
            {
                return InputRejection.TooFarAhead;
            }

            if (serverTick - _windowStartTick >= (uint)_rateWindowTicks)
            {
                _windowStartTick = serverTick;
                _acceptedInWindow = 0;
            }

            if (_acceptedInWindow >= _maxAcceptedPerWindow)
            {
                return InputRejection.RateLimited;
            }

            return _pending.Count >= _capacity ? InputRejection.QueueFull : InputRejection.None;
        }
    }
}
