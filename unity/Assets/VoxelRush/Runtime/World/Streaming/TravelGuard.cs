using System;

namespace VoxelRush.World.Streaming
{
    /// <summary>
    /// Server-side safety net (VOXEL_ENGINE Collision: collision-relevant chunks must be ready before entry): removes the
    /// horizontal velocity component that would carry a body into a chunk column that isn't ready within this step.
    /// </summary>
    public static class TravelGuard
    {
        /// <param name="margin">Extra metres checked beyond this step's travel (the car's half-length).</param>
        /// <returns>True when the velocity was clamped.</returns>
        public static bool Clamp(float x, float z, ref float velocityX, ref float velocityZ, float deltaTime, float margin, Func<float, float, bool> columnReady)
        {
            bool clamped = false;
            float reachX = velocityX * deltaTime + Math.Sign(velocityX) * margin;
            if (velocityX != 0 && !columnReady(x + reachX, z))
            {
                velocityX = 0;
                clamped = true;
            }

            float reachZ = velocityZ * deltaTime + Math.Sign(velocityZ) * margin;
            if (velocityZ != 0 && !columnReady(x, z + reachZ))
            {
                velocityZ = 0;
                clamped = true;
            }

            // Diagonal into an unready corner column.
            if (velocityX != 0 && velocityZ != 0 && !columnReady(x + reachX, z + reachZ))
            {
                velocityX = 0;
                velocityZ = 0;
                clamped = true;
            }

            return clamped;
        }
    }
}
