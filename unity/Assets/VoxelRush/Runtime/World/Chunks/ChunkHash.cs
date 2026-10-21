namespace VoxelRush.World.Chunks
{
    /// <summary>
    /// Canonical chunk content hash (CONTRACTS ChunkSnapshot.contentHash; QUALITY_GATES: same seed/version → same hash):
    /// FNV-1a 64 over schema version, generator version, coordinate and the 4096 effective blocks, little-endian.
    /// For consistency checks, not tamper resistance (the server is authoritative).
    /// </summary>
    public static class ChunkHash
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong Compute(ChunkData chunk)
        {
            var blocks = new byte[ChunkMath.Volume];
            chunk.CopyEffectiveBlocks(blocks);
            return Compute(chunk.SchemaVersion, chunk.GeneratorVersion, chunk.Coord, blocks);
        }

        public static ulong Compute(int schemaVersion, int generatorVersion, ChunkCoord coord, byte[] blocks)
        {
            ulong hash = OffsetBasis;
            hash = AddInt(hash, schemaVersion);
            hash = AddInt(hash, generatorVersion);
            hash = AddInt(hash, coord.X);
            hash = AddInt(hash, coord.Y);
            hash = AddInt(hash, coord.Z);
            for (int i = 0; i < blocks.Length; i++)
            {
                hash = unchecked((hash ^ blocks[i]) * Prime);
            }

            return hash;
        }

        private static ulong AddInt(ulong hash, int value)
        {
            unchecked
            {
                for (int shift = 0; shift < 32; shift += 8)
                {
                    hash = (hash ^ (byte)(value >> shift)) * Prime;
                }

                return hash;
            }
        }
    }
}
