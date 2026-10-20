using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Race
{
    /// <summary>A checkpoint gate: a vertical plane crossed in the direction of its normal, within its width and height.</summary>
    public readonly struct Gate
    {
        public Gate(int id, float x, float y, float z, float normalX, float normalZ, float halfWidth, float halfHeight)
        {
            float length = (float)Math.Sqrt(normalX * normalX + normalZ * normalZ);
            Id = id;
            X = x;
            Y = y;
            Z = z;
            NormalX = length > 0 ? normalX / length : 0;
            NormalZ = length > 0 ? normalZ / length : 0;
            HalfWidth = halfWidth;
            HalfHeight = halfHeight;
        }

        public int Id { get; }

        public float X { get; }

        /// <summary>Centre height; the gate spans Y ± HalfHeight.</summary>
        public float Y { get; }

        public float Z { get; }

        public float NormalX { get; }

        public float NormalZ { get; }

        public float HalfWidth { get; }

        public float HalfHeight { get; }

        /// <summary>
        /// True when the segment from <paramref name="from"/> to <paramref name="to"/> crosses the plane forwards
        /// (behind → in front) at a point inside the gate's bounds.
        /// </summary>
        public bool CrossedForward(Vec3 from, Vec3 to)
        {
            float d0 = Signed(from), d1 = Signed(to);
            if (!(d0 < 0f && d1 >= 0f))
            {
                return false;
            }

            float t = d0 / (d0 - d1);
            float px = from.X + (to.X - from.X) * t - X;
            float py = from.Y + (to.Y - from.Y) * t - Y;
            float pz = from.Z + (to.Z - from.Z) * t - Z;
            float lateral = px * -NormalZ + pz * NormalX;
            return Math.Abs(lateral) <= HalfWidth && Math.Abs(py) <= HalfHeight;
        }

        private float Signed(Vec3 p) => (p.X - X) * NormalX + (p.Z - Z) * NormalZ;
    }

    /// <summary>Minimal engine-free vector for race positions (server physics samples).</summary>
    public readonly struct Vec3
    {
        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public bool IsFinite => !float.IsNaN(X) && !float.IsInfinity(X) && !float.IsNaN(Y) && !float.IsInfinity(Y) && !float.IsNaN(Z) && !float.IsInfinity(Z);

        public float HorizontalDistance(Vec3 other)
        {
            float dx = other.X - X, dz = other.Z - Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }

    /// <summary>An ordered checkpoint course; the last gate is the finish (and lap) line.</summary>
    public sealed class TrackDefinition
    {
        public TrackDefinition(ulong trackRevision, Vec3 start, IReadOnlyList<Gate> gates, int laps = 1, float maxPlausibleSpeed = 45f)
        {
            if (gates == null || gates.Count < 2)
            {
                throw new ArgumentException("A track needs at least two gates.", nameof(gates));
            }

            var ids = new HashSet<int>();
            foreach (Gate g in gates)
            {
                if (!ids.Add(g.Id) || (g.NormalX == 0 && g.NormalZ == 0) || !(g.HalfWidth > 0) || !(g.HalfHeight > 0))
                {
                    throw new ArgumentException($"Gate {g.Id} is duplicated or malformed.", nameof(gates));
                }
            }

            if (laps < 1 || !(maxPlausibleSpeed > 0) || !start.IsFinite)
            {
                throw new ArgumentOutOfRangeException(nameof(laps));
            }

            TrackRevision = trackRevision;
            Start = start;
            Gates = gates;
            Laps = laps;
            MaxPlausibleSpeed = maxPlausibleSpeed;
        }

        public ulong TrackRevision { get; }

        public Vec3 Start { get; }

        public IReadOnlyList<Gate> Gates { get; }

        public int Laps { get; }

        /// <summary>Upper bound on car speed (m/s, boost included) used for teleport detection and minimum progression times.</summary>
        public float MaxPlausibleSpeed { get; }

        /// <summary>Gates to cross in order over all laps.</summary>
        public int TotalCrossings => Gates.Count * Laps;

        public Gate GateAt(int crossingIndex) => Gates[crossingIndex % Gates.Count];

        /// <summary>Fewest ticks a legitimate car needs from the previous checkpoint (or the start) to crossing <paramref name="crossingIndex"/>.</summary>
        public uint MinTicksTo(int crossingIndex, int tickRate)
        {
            Vec3 from = crossingIndex == 0 ? Start : Position(GateAt(crossingIndex - 1));
            float distance = from.HorizontalDistance(Position(GateAt(crossingIndex)));
            // A car can cross a wide gate anywhere along it, so measure centre to centre minus both half widths.
            float slack = (crossingIndex == 0 ? 0 : GateAt(crossingIndex - 1).HalfWidth) + GateAt(crossingIndex).HalfWidth;
            return (uint)Math.Floor(Math.Max(0f, distance - slack) / MaxPlausibleSpeed * tickRate);
        }

        private static Vec3 Position(Gate g) => new Vec3(g.X, g.Y, g.Z);
    }
}
