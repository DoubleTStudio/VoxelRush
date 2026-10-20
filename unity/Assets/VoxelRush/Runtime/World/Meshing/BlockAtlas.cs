using System;

namespace VoxelRush.World.Meshing
{
    public enum BlockFace : byte
    {
        Top,
        Side,
        Bottom,
    }

    /// <summary>Maps (block id, face) to a tile index in the texture atlas; 0..255 tiles in a 16×16 atlas.</summary>
    public sealed class BlockAtlas
    {
        public const int TilesPerRow = 16;

        private readonly ushort[] _tiles = new ushort[256 * 3];

        public static BlockAtlas Default { get; } = CreateDefault();

        /// <summary>Whether a block hides the faces of its neighbours (air and future glass/water don't).</summary>
        public bool IsOpaque(byte blockId) => blockId != Chunks.BlockIds.Air;

        public int Tile(byte blockId, BlockFace face) => _tiles[blockId * 3 + (int)face];

        public BlockAtlas Set(byte blockId, int top, int side, int bottom)
        {
            if ((uint)top >= TilesPerRow * TilesPerRow || (uint)side >= TilesPerRow * TilesPerRow || (uint)bottom >= TilesPerRow * TilesPerRow)
            {
                throw new ArgumentOutOfRangeException(nameof(top));
            }

            _tiles[blockId * 3 + (int)BlockFace.Top] = (ushort)top;
            _tiles[blockId * 3 + (int)BlockFace.Side] = (ushort)side;
            _tiles[blockId * 3 + (int)BlockFace.Bottom] = (ushort)bottom;
            return this;
        }

        // Tiles: 0 grass top, 1 grass side, 2 dirt, 3 stone (atlas texture lays them out row-major from the top-left).
        private static BlockAtlas CreateDefault() => new BlockAtlas()
            .Set(Chunks.BlockIds.Grass, top: 0, side: 1, bottom: 2)
            .Set(Chunks.BlockIds.Dirt, 2, 2, 2)
            .Set(Chunks.BlockIds.Stone, 3, 3, 3);
    }
}
