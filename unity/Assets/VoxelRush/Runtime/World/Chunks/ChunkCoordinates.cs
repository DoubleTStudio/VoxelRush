using System;

namespace VoxelRush.World.Chunks
{
    /// <summary>World block coordinate (one voxel), signed.</summary>
    public readonly struct BlockPos : IEquatable<BlockPos>
    {
        public BlockPos(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }

        public int Y { get; }

        public int Z { get; }

        public bool Equals(BlockPos other) => X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object? obj) => obj is BlockPos other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        public override string ToString() => $"({X},{Y},{Z})";
    }

    /// <summary>Chunk coordinate: world block = chunk * <see cref="ChunkMath.Size"/> + local.</summary>
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public ChunkCoord(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }

        public int Y { get; }

        public int Z { get; }

        public ChunkCoord Offset(int dx, int dy, int dz) => new ChunkCoord(checked(X + dx), checked(Y + dy), checked(Z + dz));

        public bool Equals(ChunkCoord other) => X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object? obj) => obj is ChunkCoord other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        public override string ToString() => $"chunk({X},{Y},{Z})";
    }

    /// <summary>Position inside a chunk, each axis 0..15; <see cref="Index"/> is the CONTRACTS localIndex.</summary>
    public readonly struct LocalBlock : IEquatable<LocalBlock>
    {
        private readonly byte _x;
        private readonly byte _y;
        private readonly byte _z;

        public LocalBlock(int x, int y, int z)
        {
            if ((uint)x >= ChunkMath.Size || (uint)y >= ChunkMath.Size || (uint)z >= ChunkMath.Size)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"Local block ({x},{y},{z}) outside 0..{ChunkMath.Size - 1}.");
            }

            _x = (byte)x;
            _y = (byte)y;
            _z = (byte)z;
        }

        public int X => _x;

        public int Y => _y;

        public int Z => _z;

        /// <summary>VOXEL_ENGINE layout: x + 16 * (z + 16 * y).</summary>
        public int Index => _x + ChunkMath.Size * (_z + ChunkMath.Size * _y);

        public static LocalBlock FromIndex(int index)
        {
            if ((uint)index >= ChunkMath.Volume)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"Local index {index} outside 0..{ChunkMath.Volume - 1}.");
            }

            return new LocalBlock(index % ChunkMath.Size, index / (ChunkMath.Size * ChunkMath.Size), index / ChunkMath.Size % ChunkMath.Size);
        }

        /// <summary>Bounds check for untrusted wire indices (CONTRACTS: uint16 in [0, 4095]).</summary>
        public static bool TryFromIndex(int index, out LocalBlock local)
        {
            if ((uint)index >= ChunkMath.Volume)
            {
                local = default;
                return false;
            }

            local = FromIndex(index);
            return true;
        }

        public bool Equals(LocalBlock other) => _x == other._x && _y == other._y && _z == other._z;

        public override bool Equals(object? obj) => obj is LocalBlock other && Equals(other);

        public override int GetHashCode() => Index;

        public override string ToString() => $"local({X},{Y},{Z})";
    }

    /// <summary>Signed world ↔ chunk math with floor semantics (CONTRACTS: negative coordinates must floor).</summary>
    public static class ChunkMath
    {
        public const int Size = 16;
        public const int Shift = 4;
        public const int Mask = Size - 1;
        public const int Volume = Size * Size * Size;

        /// <summary>Largest |chunk coordinate| whose blocks still fit in int32 world coordinates.</summary>
        public const int MaxChunkCoordinate = int.MaxValue / Size - 1;

        // Arithmetic shift floors for negative values (-1 >> 4 == -1), unlike integer division.
        public static int FloorDiv(int value) => value >> Shift;

        public static int FloorMod(int value) => value & Mask;

        public static ChunkCoord ToChunk(BlockPos world) => new ChunkCoord(FloorDiv(world.X), FloorDiv(world.Y), FloorDiv(world.Z));

        public static LocalBlock ToLocal(BlockPos world) => new LocalBlock(FloorMod(world.X), FloorMod(world.Y), FloorMod(world.Z));

        public static BlockPos ToWorld(ChunkCoord chunk, LocalBlock local)
        {
            if (Math.Abs((long)chunk.X) > MaxChunkCoordinate || Math.Abs((long)chunk.Y) > MaxChunkCoordinate || Math.Abs((long)chunk.Z) > MaxChunkCoordinate)
            {
                throw new ArgumentOutOfRangeException(nameof(chunk), $"{chunk} is outside the addressable world.");
            }

            return new BlockPos(chunk.X * Size + local.X, chunk.Y * Size + local.Y, chunk.Z * Size + local.Z);
        }

        /// <summary>
        /// Face-adjacent chunks whose meshes/colliders depend on this block (edit on a chunk border dirties the neighbour);
        /// 0 for an interior block, up to 3 at a corner.
        /// </summary>
        public static int FaceNeighboursAffected(ChunkCoord chunk, LocalBlock local, Span<ChunkCoord> result)
        {
            int count = 0;
            if (local.X == 0) result[count++] = chunk.Offset(-1, 0, 0);
            else if (local.X == Mask) result[count++] = chunk.Offset(1, 0, 0);
            if (local.Y == 0) result[count++] = chunk.Offset(0, -1, 0);
            else if (local.Y == Mask) result[count++] = chunk.Offset(0, 1, 0);
            if (local.Z == 0) result[count++] = chunk.Offset(0, 0, -1);
            else if (local.Z == Mask) result[count++] = chunk.Offset(0, 0, 1);
            return count;
        }
    }
}
