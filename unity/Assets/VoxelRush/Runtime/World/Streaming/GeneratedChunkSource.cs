using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;

namespace VoxelRush.World.Streaming
{
    public sealed class GeneratedChunkSource : IChunkSource
    {
        private readonly TerrainGenerator _generator;

        public GeneratedChunkSource(TerrainGenerator generator) => _generator = generator;

        public ChunkData Load(ChunkCoord coord) => _generator.Generate(coord);
    }
}
