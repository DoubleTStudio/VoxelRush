using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.Destruction
{
    /// <summary>One cosmetic debris piece to show on a client: never simulated by physics, never networked.</summary>
    public readonly struct DebrisSpawn
    {
        public DebrisSpawn(float x, float y, float z, float vx, float vy, float vz, byte blockType)
        {
            X = x;
            Y = y;
            Z = z;
            Vx = vx;
            Vy = vy;
            Vz = vz;
            BlockType = blockType;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public float Vx { get; }

        public float Vy { get; }

        public float Vz { get; }

        public byte BlockType { get; }
    }

    /// <summary>
    /// Turns removed blocks into a capped number of debris pieces per frame (DESTRUCTION Cosmetic debris). Big blasts
    /// thin out instead of growing the effect cost: beyond the per-frame cap blocks get no pieces.
    /// </summary>
    public sealed class DebrisPlanner
    {
        private uint _rng;

        public DebrisPlanner(int piecesPerBlock = 3, int maxPiecesPerFrame = 96, uint seed = 0x9E3779B9u)
        {
            PiecesPerBlock = piecesPerBlock < 1 ? 1 : piecesPerBlock;
            MaxPiecesPerFrame = maxPiecesPerFrame < 1 ? 1 : maxPiecesPerFrame;
            _rng = seed == 0 ? 1u : seed;
        }

        public int PiecesPerBlock { get; }

        public int MaxPiecesPerFrame { get; }

        public int PlannedThisFrame { get; private set; }

        public long Dropped { get; private set; }

        public void BeginFrame() => PlannedThisFrame = 0;

        /// <summary>Appends up to <see cref="PiecesPerBlock"/> pieces for a removed block; returns how many.</summary>
        public int Plan(BlockPos block, byte blockType, List<DebrisSpawn> into)
        {
            int count = 0;
            while (count < PiecesPerBlock && PlannedThisFrame < MaxPiecesPerFrame)
            {
                into.Add(new DebrisSpawn(
                    block.X + 0.25f + 0.5f * Next(), block.Y + 0.25f + 0.5f * Next(), block.Z + 0.25f + 0.5f * Next(),
                    (Next() - 0.5f) * 4f, 2f + 3f * Next(), (Next() - 0.5f) * 4f, blockType));
                count++;
                PlannedThisFrame++;
            }

            Dropped += PiecesPerBlock - count;
            return count;
        }

        // xorshift32: cosmetic randomness without allocating a System.Random per client.
        private float Next()
        {
            _rng ^= _rng << 13;
            _rng ^= _rng >> 17;
            _rng ^= _rng << 5;
            return (_rng & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
