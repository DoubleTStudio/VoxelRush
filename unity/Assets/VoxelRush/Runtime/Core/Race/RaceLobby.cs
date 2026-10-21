using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Race
{
    /// <summary>
    /// Ready-up before a race (RACE-002): the countdown starts once every connected racer is ready, at least one.
    /// Spectators who press ready join the next round, so late joiners aren't stuck watching forever.
    /// </summary>
    public sealed class RaceLobby
    {
        private readonly HashSet<string> _ready = new HashSet<string>(StringComparer.Ordinal);

        public int ReadyCount => _ready.Count;

        public bool IsReady(string racerId) => _ready.Contains(racerId);

        /// <summary>Returns true when the ready set changed.</summary>
        public bool SetReady(string racerId, bool ready) => ready ? _ready.Add(racerId) : _ready.Remove(racerId);

        public bool Remove(string racerId) => _ready.Remove(racerId);

        public void Clear() => _ready.Clear();

        /// <summary>True when there is at least one connected racer and all of them are ready.</summary>
        public bool ShouldStart(IEnumerable<string> connectedRacers)
        {
            int count = 0;
            foreach (string racer in connectedRacers)
            {
                if (!_ready.Contains(racer))
                {
                    return false;
                }

                count++;
            }

            return count > 0;
        }
    }

    /// <summary>Server → clients lobby summary: how many of the connected racers are ready.</summary>
    public readonly struct RaceLobbyMessage
    {
        public const int Length = 2;

        public RaceLobbyMessage(int ready, int racers)
        {
            Ready = (byte)Math.Clamp(ready, 0, byte.MaxValue);
            Racers = (byte)Math.Clamp(racers, 0, byte.MaxValue);
        }

        public byte Ready { get; }

        public byte Racers { get; }

        public byte[] Encode() => new[] { Ready, Racers };

        public static bool TryDecode(byte[] payload, out RaceLobbyMessage message)
        {
            message = default;
            if (payload == null || payload.Length != Length || payload[0] > payload[1])
            {
                return false;
            }

            message = new RaceLobbyMessage(payload[0], payload[1]);
            return true;
        }
    }
}
