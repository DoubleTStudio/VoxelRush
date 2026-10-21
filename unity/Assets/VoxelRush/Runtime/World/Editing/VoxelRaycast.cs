using System;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Editing
{
    public readonly struct VoxelHit
    {
        public VoxelHit(BlockPos block, int normalX, int normalY, int normalZ, float distance)
        {
            Block = block;
            NormalX = normalX;
            NormalY = normalY;
            NormalZ = normalZ;
            Distance = distance;
        }

        /// <summary>The solid block that was hit (target for Remove).</summary>
        public BlockPos Block { get; }

        /// <summary>Face normal of the entry face; zero when the ray starts inside a block.</summary>
        public int NormalX { get; }

        public int NormalY { get; }

        public int NormalZ { get; }

        public float Distance { get; }

        /// <summary>The empty cell in front of the hit face (target for Place).</summary>
        public BlockPos Adjacent => new BlockPos(Block.X + NormalX, Block.Y + NormalY, Block.Z + NormalZ);
    }

    /// <summary>
    /// Grid traversal (Amanatides–Woo) over voxels: visits each cell the ray crosses in order, so targeting works on
    /// chunk data alone (no colliders) and identically on client (feedback) and server (validation).
    /// </summary>
    public static class VoxelRaycast
    {
        public static bool Cast(
            float originX, float originY, float originZ,
            float directionX, float directionY, float directionZ,
            float maxDistance, Func<BlockPos, bool> isSolid, out VoxelHit hit)
        {
            hit = default;
            float length = (float)Math.Sqrt(directionX * directionX + directionY * directionY + directionZ * directionZ);
            if (!(length > 1e-6f) || !(maxDistance > 0) || float.IsNaN(originX + originY + originZ))
            {
                return false;
            }

            float dx = directionX / length, dy = directionY / length, dz = directionZ / length;
            int x = (int)Math.Floor(originX), y = (int)Math.Floor(originY), z = (int)Math.Floor(originZ);
            int stepX = Math.Sign(dx), stepY = Math.Sign(dy), stepZ = Math.Sign(dz);
            float tDeltaX = stepX != 0 ? Math.Abs(1f / dx) : float.PositiveInfinity;
            float tDeltaY = stepY != 0 ? Math.Abs(1f / dy) : float.PositiveInfinity;
            float tDeltaZ = stepZ != 0 ? Math.Abs(1f / dz) : float.PositiveInfinity;
            float tMaxX = stepX != 0 ? (stepX > 0 ? x + 1 - originX : originX - x) * tDeltaX : float.PositiveInfinity;
            float tMaxY = stepY != 0 ? (stepY > 0 ? y + 1 - originY : originY - y) * tDeltaY : float.PositiveInfinity;
            float tMaxZ = stepZ != 0 ? (stepZ > 0 ? z + 1 - originZ : originZ - z) * tDeltaZ : float.PositiveInfinity;

            int nx = 0, ny = 0, nz = 0;
            float t = 0f;
            // Bounded: a ray can't cross more than ~3 cells per unit of distance.
            int maxSteps = (int)Math.Ceiling(maxDistance * 3) + 3;
            for (int i = 0; i < maxSteps && t <= maxDistance; i++)
            {
                var cell = new BlockPos(x, y, z);
                if (isSolid(cell))
                {
                    hit = new VoxelHit(cell, nx, ny, nz, t);
                    return true;
                }

                if (tMaxX < tMaxY && tMaxX < tMaxZ)
                {
                    x += stepX;
                    t = tMaxX;
                    tMaxX += tDeltaX;
                    nx = -stepX; ny = 0; nz = 0;
                }
                else if (tMaxY < tMaxZ)
                {
                    y += stepY;
                    t = tMaxY;
                    tMaxY += tDeltaY;
                    nx = 0; ny = -stepY; nz = 0;
                }
                else
                {
                    z += stepZ;
                    t = tMaxZ;
                    tMaxZ += tDeltaZ;
                    nx = 0; ny = 0; nz = -stepZ;
                }
            }

            return false;
        }
    }
}
