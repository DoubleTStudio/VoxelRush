using VoxelRush.World.Chunks;

namespace VoxelRush.World.Generation
{
    /// <summary>Flat build plot: solid stone for y &lt; 0, air above, so the top surface sits at y = 0 (arena ground level).</summary>
    public sealed class FlatPlotGenerator : IBaseGenerator
    {
        public const int Version = 1;
        private const int Depth = 2;

        public int GeneratorVersion => Version;

        public byte[] GenerateBlocks(ChunkCoord coord)
        {
            var blocks = new byte[ChunkMath.Volume];
            for (int ly = 0; ly < ChunkMath.Size; ly++)
            {
                int y = coord.Y * ChunkMath.Size + ly;
                if (y >= 0 || y < -Depth)
                {
                    continue;
                }

                for (int i = 0; i < ChunkMath.Size * ChunkMath.Size; i++)
                {
                    blocks[ly * ChunkMath.Size * ChunkMath.Size + i] = y == -1 ? BlockIds.Grass : BlockIds.Stone;
                }
            }

            return blocks;
        }
    }
}
