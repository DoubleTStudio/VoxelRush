using System;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Generation
{
    /// <summary>
    /// Deterministic heightmap terrain (VOXEL_ENGINE: seed + chunk coord + pinned generator version).
    /// Integer/fixed-point only, so every CPU and runtime produces bit-identical chunks; golden hashes pin the output.
    /// Changing any output requires bumping <see cref="Version"/>.
    /// </summary>
    public sealed class TerrainGenerator
    {
        public const int Version = 1;

        // Fixed-point: 16 fractional bits.
        private const int FixedOne = 1 << 16;
        private const int SeaLevel = 16;
        private const int DirtDepth = 3;

        // Octaves: (lattice spacing in blocks, amplitude in blocks). Gentle hills for a racing launch map.
        private static readonly (int Spacing, int Amplitude)[] Octaves = { (64, 14), (32, 6), (16, 3), (8, 1) };

        private readonly ulong _seed;

        public TerrainGenerator(long seed) => _seed = unchecked((ulong)seed);

        /// <summary>Surface height in blocks at world column (x, z): the topmost solid block's y.</summary>
        public int SurfaceHeight(int x, int z)
        {
            long height = (long)SeaLevel * FixedOne;
            for (int octave = 0; octave < Octaves.Length; octave++)
            {
                (int spacing, int amplitude) = Octaves[octave];
                // Value noise in [-1, 1) fixed point, scaled by amplitude.
                height += ValueNoise(x, z, spacing, (ulong)octave) * amplitude;
            }

            return (int)(height >> 16);
        }

        public byte[] GenerateBlocks(ChunkCoord coord)
        {
            var blocks = new byte[ChunkMath.Volume];
            int baseX = coord.X * ChunkMath.Size;
            int baseY = coord.Y * ChunkMath.Size;
            int baseZ = coord.Z * ChunkMath.Size;
            for (int lz = 0; lz < ChunkMath.Size; lz++)
            {
                for (int lx = 0; lx < ChunkMath.Size; lx++)
                {
                    int surface = SurfaceHeight(baseX + lx, baseZ + lz);
                    for (int ly = 0; ly < ChunkMath.Size; ly++)
                    {
                        int y = baseY + ly;
                        byte id = y > surface ? BlockIds.Air
                            : y == surface ? BlockIds.Grass
                            : y > surface - DirtDepth ? BlockIds.Dirt
                            : BlockIds.Stone;
                        blocks[lx + ChunkMath.Size * (lz + ChunkMath.Size * ly)] = id;
                    }
                }
            }

            return blocks;
        }

        public ChunkData Generate(ChunkCoord coord) => new ChunkData(coord, Version, GenerateBlocks(coord));

        // Bilinear value noise on an integer lattice with smoothstep weights, all in 16.16 fixed point.
        private int ValueNoise(int x, int z, int spacing, ulong octave)
        {
            int cellX = FloorDiv(x, spacing);
            int cellZ = FloorDiv(z, spacing);
            int fx = (x - cellX * spacing) * FixedOne / spacing;
            int fz = (z - cellZ * spacing) * FixedOne / spacing;
            int wx = Smooth(fx);
            int wz = Smooth(fz);

            int a = Lattice(cellX, cellZ, octave);
            int b = Lattice(cellX + 1, cellZ, octave);
            int c = Lattice(cellX, cellZ + 1, octave);
            int d = Lattice(cellX + 1, cellZ + 1, octave);
            int top = Lerp(a, b, wx);
            int bottom = Lerp(c, d, wx);
            return Lerp(top, bottom, wz);
        }

        // Lattice value in [-1, 1) as 16.16 fixed point, from a 64-bit integer hash.
        private int Lattice(int cx, int cz, ulong octave)
        {
            ulong h = Mix(_seed ^ Mix(unchecked((ulong)(uint)cx) | ((ulong)(uint)cz << 32)) ^ Mix(octave + 0x9E3779B97F4A7C15UL));
            return (int)(h >> 47) - FixedOne;
        }

        // 3t² - 2t³ in fixed point.
        private static int Smooth(int t) => (int)((long)t * t / FixedOne * (3 * FixedOne - 2 * t) / FixedOne);

        private static int Lerp(int a, int b, int t) => a + (int)((long)(b - a) * t / FixedOne);

        private static int FloorDiv(int value, int divisor)
        {
            int q = value / divisor;
            return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? q - 1 : q;
        }

        // SplitMix64 finalizer: well-distributed, integer-only.
        private static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
