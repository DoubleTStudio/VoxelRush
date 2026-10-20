using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Streaming
{
    /// <summary>Supplies chunk content (generator now; snapshot + journal replay in M3).</summary>
    public interface IChunkSource
    {
        ChunkData Load(ChunkCoord coord);
    }

    /// <summary>Engine-side collision (and optionally render) objects for chunks.</summary>
    public interface IChunkBackend
    {
        /// <summary>Builds or replaces the collider for <paramref name="chunk"/>; neighbours in +X, −X, +Y, −Y, +Z, −Z order (null = air).</summary>
        void BuildCollider(ChunkData chunk, ChunkData?[] neighbours);

        void Remove(ChunkCoord coord);
    }

    /// <summary>Something that needs terrain around it: a car's position and horizontal velocity, in world units (1 = one block).</summary>
    public readonly struct StreamingObserver
    {
        public StreamingObserver(float x, float y, float z, float velocityX, float velocityZ)
        {
            X = x;
            Y = y;
            Z = z;
            VelocityX = velocityX;
            VelocityZ = velocityZ;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public float VelocityX { get; }

        public float VelocityZ { get; }
    }

    public enum ChunkState
    {
        Unloaded,
        Generated,
        Ready,
    }

    public sealed class StreamingSettings
    {
        public int RadiusChunks { get; set; } = 3;

        /// <summary>Extra columns kept loaded before unloading, so chunks don't thrash at the edge.</summary>
        public int UnloadHysteresisChunks { get; set; } = 2;

        /// <summary>Seconds of travel ahead that are requested (VOXEL_ENGINE: ahead of travel direction).</summary>
        public float LookaheadSeconds { get; set; } = 1.5f;

        public int MinChunkY { get; set; } = -1;

        public int MaxChunkY { get; set; } = 2;

        /// <summary>Per-tick work caps (AGENTS: no unbounded work on the simulation tick).</summary>
        public int GenerateBudget { get; set; } = 4;

        public int ColliderBudget { get; set; } = 2;

        public int UnloadBudget { get; set; } = 8;
    }
}
