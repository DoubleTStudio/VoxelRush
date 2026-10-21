namespace VoxelRush.World.Chunks
{
    public enum BlockShape : byte
    {
        Empty,
        Cube,
        /// <summary>1:2 slope, 0 → 0.5 m; with <see cref="RampHigh"/> uphill of it, a two-block 1:2 ramp onto a block.</summary>
        RampLow,
        /// <summary>1:2 slope, 0.5 → 1 m.</summary>
        RampHigh,
        /// <summary>Jump lip, 0 → <see cref="BlockShapes.KickerHeight"/> over one block.</summary>
        Kicker,
    }

    /// <summary>
    /// Block geometry by id (BUILD-002). Ramps encode their yaw in the id, so edits, deltas, snapshots and the journal
    /// keep carrying a single byte. Yaw is the uphill direction in quarter turns about +Y: 0 = +Z, 1 = +X, 2 = -Z, 3 = -X.
    /// </summary>
    public static class BlockShapes
    {
        public const int Yaws = 4;

        /// <summary>5:8 (32°): a 1:1 lip hits the ArcadeCar's front overhang, 3:4 bleeds half its speed (RampDriveTests).</summary>
        public const float KickerHeight = 0.625f;

        // Face directions as the meshers number them: +X, -X, +Y, -Y, +Z, -Z.
        public const int PosX = 0, NegX = 1, PosY = 2, NegY = 3, PosZ = 4, NegZ = 5;

        public static bool IsKnown(byte id) => id < BlockIds.Count;

        public static bool IsRamp(byte id) => id >= BlockIds.FirstRamp && id <= BlockIds.LastRamp;

        /// <summary>Players may place it: defined, not air, not world-authored protected infrastructure.</summary>
        public static bool IsPlaceable(byte id) => id != BlockIds.Air && id != BlockIds.Protected && IsKnown(id);

        /// <summary>Unknown ids keep meshing as cubes, as they did before ramps existed.</summary>
        public static BlockShape Shape(byte id) =>
            id == BlockIds.Air ? BlockShape.Empty
            : !IsRamp(id) ? BlockShape.Cube
            : (BlockShape)((int)BlockShape.RampLow + (id - BlockIds.FirstRamp) / Yaws);

        public static int Yaw(byte id) => IsRamp(id) ? (id - BlockIds.FirstRamp) % Yaws : 0;

        /// <summary>The ramp id for a variant and yaw (any integer; wrapped to 0..3).</summary>
        public static byte RampId(BlockShape variant, int yaw)
        {
            if (variant < BlockShape.RampLow || variant > BlockShape.Kicker)
            {
                throw new System.ArgumentOutOfRangeException(nameof(variant));
            }

            return (byte)(BlockIds.FirstRamp + ((int)variant - (int)BlockShape.RampLow) * Yaws + ((yaw % Yaws) + Yaws) % Yaws);
        }

        /// <summary>The same block turned by quarter turns about +Y; cubes don't change.</summary>
        public static byte Rotate(byte id, int quarterTurns) => IsRamp(id) ? RampId(Shape(id), Yaw(id) + quarterTurns) : id;

        /// <summary>Unrotated variant id (yaw 0), e.g. for palettes and materials.</summary>
        public static byte Variant(byte id) => IsRamp(id) ? RampId(Shape(id), 0) : id;

        /// <summary>Slope heights at the downhill and uphill edges (multiples of 1/8 m, exact in floats).</summary>
        public static void RampHeights(BlockShape shape, out float low, out float high)
        {
            switch (shape)
            {
                case BlockShape.RampLow: low = 0f; high = 0.5f; break;
                case BlockShape.RampHigh: low = 0.5f; high = 1f; break;
                case BlockShape.Kicker: low = 0f; high = KickerHeight; break;
                default: throw new System.ArgumentOutOfRangeException(nameof(shape));
            }
        }

        /// <summary>The face direction a yaw climbs toward.</summary>
        public static int UphillDirection(int yaw) => (((yaw % Yaws) + Yaws) % Yaws) switch { 0 => PosZ, 1 => PosX, 2 => NegZ, _ => NegX };

        public static int Opposite(int direction) => direction ^ 1;
    }
}
