using System;
using System.Collections.Generic;

namespace VoxelRush.World.Chunks
{
    /// <summary>Block type id; 0 is air (VOXEL_ENGINE: byte ids for the first 255 materials). Full catalog: DES-001.</summary>
    public static class BlockIds
    {
        public const byte Air = 0;
        public const byte Grass = 1;
        public const byte Dirt = 2;
        public const byte Stone = 3;
        public const byte Wood = 4;
        public const byte Asphalt = 5;
        public const byte Reinforced = 6;
        public const byte Protected = 7;
        public const byte Brick = 8;
        public const byte Concrete = 9;
        public const byte Glass = 10;
        public const byte Sand = 11;
        public const byte Metal = 12;

        // BUILD-002 ramps: FirstRamp + variant * 4 + yaw (see BlockShapes); ids above stay unchanged.
        public const byte FirstRamp = 13;
        public const byte RampLow = 13;
        public const byte RampHigh = 17;
        public const byte Kicker = 21;
        public const byte LastRamp = 24;

        /// <summary>Ids 0..Count-1 are defined; anything above is unknown and never placeable.</summary>
        public const int Count = 25;
    }

    /// <summary>
    /// One 16³ chunk: immutable generated base blocks plus sparse edits (VOXEL_ENGINE data model).
    /// The revision increases on every effective change and orders deltas for replication and persistence.
    /// </summary>
    public sealed class ChunkData
    {
        public const int CurrentSchemaVersion = 1;

        private readonly byte[] _base;
        private readonly SortedDictionary<ushort, byte> _edits = new SortedDictionary<ushort, byte>();

        // Base with edits applied, kept in step with _edits: O(1) reads and allocation-free copies (VR-070; enumerating
        // the sorted edit map allocates). Costs 4 KiB per loaded chunk.
        private readonly byte[] _effective;

        /// <param name="baseBlocks">Generated blocks, 4096 bytes in local-index order; copied, never mutated.</param>
        public ChunkData(ChunkCoord coord, int generatorVersion, byte[] baseBlocks, ulong revision = 0, int schemaVersion = CurrentSchemaVersion)
        {
            if (baseBlocks == null || baseBlocks.Length != ChunkMath.Volume)
            {
                throw new ArgumentException($"Base blocks must be exactly {ChunkMath.Volume} bytes.", nameof(baseBlocks));
            }

            if (generatorVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(generatorVersion));
            }

            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new NotSupportedException($"Chunk schema {schemaVersion} is not supported (expected {CurrentSchemaVersion}).");
            }

            Coord = coord;
            GeneratorVersion = generatorVersion;
            SchemaVersion = schemaVersion;
            Revision = revision;
            _base = (byte[])baseBlocks.Clone();
            _effective = (byte[])baseBlocks.Clone();
        }

        /// <summary>Restores a persisted chunk: generated base plus stored edits, at the stored revision (no new revisions).</summary>
        public static ChunkData FromSnapshot(ChunkCoord coord, int generatorVersion, byte[] baseBlocks, ulong revision, IEnumerable<KeyValuePair<ushort, byte>> edits)
        {
            var chunk = new ChunkData(coord, generatorVersion, baseBlocks, revision);
            foreach (KeyValuePair<ushort, byte> edit in edits)
            {
                if (edit.Key >= ChunkMath.Volume)
                {
                    throw new ArgumentOutOfRangeException(nameof(edits), $"Edit index {edit.Key} out of range.");
                }

                if (chunk._base[edit.Key] != edit.Value)
                {
                    chunk._edits[edit.Key] = edit.Value;
                    chunk._effective[edit.Key] = edit.Value;
                }
            }

            return chunk;
        }

        /// <summary>An independent copy (same base, edits and revision), e.g. for a race's match overlay.</summary>
        public ChunkData Clone() => FromSnapshot(Coord, GeneratorVersion, (byte[])_base.Clone(), Revision, _edits);

        public ChunkCoord Coord { get; }

        public int GeneratorVersion { get; }

        public int SchemaVersion { get; }

        public ulong Revision { get; private set; }

        /// <summary>Blocks that differ from the generated base.</summary>
        public int EditCount => _edits.Count;

        public byte Get(LocalBlock local) => _effective[local.Index];

        public byte GetBase(LocalBlock local) => _base[local.Index];

        /// <summary>Sets a block; returns false (and keeps the revision) when nothing changes. Restoring the base value drops the edit.</summary>
        public bool Set(LocalBlock local, byte blockId)
        {
            ushort index = (ushort)local.Index;
            if (Get(local) == blockId)
            {
                return false;
            }

            if (_base[index] == blockId)
            {
                _edits.Remove(index);
            }
            else
            {
                _edits[index] = blockId;
            }

            _effective[index] = blockId;
            Revision++;
            return true;
        }

        /// <summary>Copies the effective blocks (base with edits applied) in local-index order.</summary>
        public void CopyEffectiveBlocks(byte[] destination)
        {
            if (destination == null || destination.Length != ChunkMath.Volume)
            {
                throw new ArgumentException($"Destination must be {ChunkMath.Volume} bytes.", nameof(destination));
            }

            Buffer.BlockCopy(_effective, 0, destination, 0, ChunkMath.Volume);
        }

        /// <summary>Sparse edits in ascending local-index order (stable for hashing and serialization).</summary>
        public IEnumerable<KeyValuePair<ushort, byte>> Edits => _edits;
    }
}
