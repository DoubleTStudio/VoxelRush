using UnityEngine;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>
    /// Remote-vehicle interpolation (VEHICLE_NETWORKING): buffers server snapshots and samples the server timeline
    /// a fixed delay in the past. Handles out-of-order arrival, starvation (hold, then bounded extrapolation) and teleports.
    /// </summary>
    public sealed class SnapshotInterpolator
    {
        private const int Capacity = 32;
        private const double MaxExtrapolationSeconds = 0.1;

        private readonly Entry[] _entries = new Entry[Capacity];
        private readonly double _delaySeconds;
        private readonly float _teleportMeters;
        private int _count;
        private double _serverTimeOffset;
        private bool _hasOffset;

        public SnapshotInterpolator(double delaySeconds = 0.12, float teleportMeters = 10f)
        {
            _delaySeconds = delaySeconds;
            _teleportMeters = teleportMeters;
        }

        public int Count => _count;

        public long Received { get; private set; }

        public long DroppedOutOfOrder { get; private set; }

        public void Add(double serverTime, Vector3 position, Quaternion rotation, double localTime)
        {
            if (_count > 0 && serverTime <= _entries[_count - 1].ServerTime)
            {
                DroppedOutOfOrder++;
                return;
            }

            // Offset is smoothed so a single late packet doesn't jerk the timeline.
            double offset = serverTime - localTime;
            _serverTimeOffset = _hasOffset ? _serverTimeOffset + (offset - _serverTimeOffset) * 0.1 : offset;
            _hasOffset = true;

            if (_count == Capacity)
            {
                System.Array.Copy(_entries, 1, _entries, 0, Capacity - 1);
                _count--;
            }

            _entries[_count++] = new Entry(serverTime, position, rotation);
            Received++;
        }

        public bool TrySample(double localTime, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (_count == 0)
            {
                return false;
            }

            double renderTime = localTime + _serverTimeOffset - EffectiveDelay();
            if (renderTime <= _entries[0].ServerTime || _count == 1)
            {
                position = _entries[0].Position;
                rotation = _entries[0].Rotation;
                return true;
            }

            for (int i = 1; i < _count; i++)
            {
                Entry b = _entries[i];
                if (renderTime > b.ServerTime)
                {
                    continue;
                }

                Entry a = _entries[i - 1];
                if (Vector3.Distance(a.Position, b.Position) > _teleportMeters)
                {
                    position = b.Position;
                    rotation = b.Rotation;
                    return true;
                }

                float t = (float)((renderTime - a.ServerTime) / (b.ServerTime - a.ServerTime));
                position = Vector3.LerpUnclamped(a.Position, b.Position, t);
                rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t);
                return true;
            }

            // Starved: extrapolate briefly from the last two snapshots, then hold.
            Entry last = _entries[_count - 1];
            Entry prev = _entries[_count - 2];
            double ahead = System.Math.Min(renderTime - last.ServerTime, MaxExtrapolationSeconds);
            Vector3 velocity = (last.Position - prev.Position) / (float)(last.ServerTime - prev.ServerTime);
            position = Vector3.Distance(prev.Position, last.Position) > _teleportMeters ? last.Position : last.Position + velocity * (float)ahead;
            rotation = last.Rotation;
            return true;
        }

        /// <summary>
        /// The configured delay, or 1.5× the recent snapshot spacing when snapshots arrive slower than that delay
        /// (distant cars in VR-072 interest tiers), so the timeline stays between two snapshots instead of starving.
        /// </summary>
        public double EffectiveDelay()
        {
            if (_count < 2)
            {
                return _delaySeconds;
            }

            int first = System.Math.Max(0, _count - 8);
            double spacing = (_entries[_count - 1].ServerTime - _entries[first].ServerTime) / (_count - 1 - first);
            // 5% margin: spacing equal to the delay (up to rounding) isn't sparse.
            return spacing > _delaySeconds * 1.05 ? spacing * 1.5 : _delaySeconds;
        }

        private readonly struct Entry
        {
            public Entry(double serverTime, Vector3 position, Quaternion rotation)
            {
                ServerTime = serverTime;
                Position = position;
                Rotation = rotation;
            }

            public double ServerTime { get; }

            public Vector3 Position { get; }

            public Quaternion Rotation { get; }
        }
    }
}
