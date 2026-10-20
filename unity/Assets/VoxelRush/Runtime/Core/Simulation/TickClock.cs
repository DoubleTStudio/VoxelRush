using System;

namespace VoxelRush.Core.Simulation
{
    /// <summary>Fixed-rate tick accumulator; caps catch-up ticks per frame so a stall can't spiral.</summary>
    public sealed class TickClock
    {
        private double _accumulator;

        public TickClock(int tickRate = SimulationConstants.ServerTickRate, int maxTicksPerFrame = 4)
        {
            if (tickRate < 1 || maxTicksPerFrame < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(tickRate));
            }

            TickRate = tickRate;
            TickSeconds = 1.0 / tickRate;
            MaxTicksPerFrame = maxTicksPerFrame;
        }

        public int TickRate { get; }

        public double TickSeconds { get; }

        public int MaxTicksPerFrame { get; }

        public uint Tick { get; private set; }

        public long DroppedTicks { get; private set; }

        /// <summary>Adds frame time; returns how many ticks to run now.</summary>
        public int Advance(double deltaSeconds)
        {
            if (!(deltaSeconds > 0))
            {
                return 0;
            }

            _accumulator += deltaSeconds;
            int ticks = (int)Math.Floor(_accumulator / TickSeconds);
            _accumulator -= ticks * TickSeconds;
            if (ticks > MaxTicksPerFrame)
            {
                DroppedTicks += ticks - MaxTicksPerFrame;
                ticks = MaxTicksPerFrame;
            }

            Tick += (uint)ticks;
            return ticks;
        }
    }
}
