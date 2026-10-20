using System;

namespace VoxelRush.Core.Input
{
    /// <summary>Normalized anchors (0..1) that fit UI inside the device safe area; pixel rect origin is bottom-left.</summary>
    public readonly struct SafeAreaLayout : IEquatable<SafeAreaLayout>
    {
        public SafeAreaLayout(float minX, float minY, float maxX, float maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public float MinX { get; }

        public float MinY { get; }

        public float MaxX { get; }

        public float MaxY { get; }

        public static SafeAreaLayout Full { get; } = new SafeAreaLayout(0f, 0f, 1f, 1f);

        /// <summary>Clamps the safe rect to the screen; a degenerate rect or screen falls back to full screen.</summary>
        public static SafeAreaLayout Compute(float x, float y, float width, float height, int screenWidth, int screenHeight)
        {
            if (screenWidth <= 0 || screenHeight <= 0 || !(width > 0) || !(height > 0))
            {
                return Full;
            }

            float minX = Clamp01(x / screenWidth);
            float minY = Clamp01(y / screenHeight);
            float maxX = Clamp01((x + width) / screenWidth);
            float maxY = Clamp01((y + height) / screenHeight);
            return maxX > minX && maxY > minY ? new SafeAreaLayout(minX, minY, maxX, maxY) : Full;
        }

        public bool Equals(SafeAreaLayout other) =>
            MinX.Equals(other.MinX) && MinY.Equals(other.MinY) && MaxX.Equals(other.MaxX) && MaxY.Equals(other.MaxY);

        public override bool Equals(object? obj) => obj is SafeAreaLayout other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(MinX, MinY, MaxX, MaxY);

        public override string ToString() => $"({MinX:0.###},{MinY:0.###})-({MaxX:0.###},{MaxY:0.###})";

        private static float Clamp01(float value) => float.IsNaN(value) ? 0f : value < 0f ? 0f : value > 1f ? 1f : value;
    }
}
