using System;
using System.Collections.Generic;

namespace VoxelRush.World.Chunks
{
    /// <summary>Block type id; 0 is air (VOXEL_ENGINE: byte ids for the first 255 materials).</summary>
    public static class BlockIds
    {
        public const byte Air = 0;
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
        }

        public ChunkCoord Coord { get; }

        public int GeneratorVersion { get; }

        public int SchemaVersion { get; }

        public ulong Revision { get; private set; }

        /// <summary>Blocks that differ from the generated base.</summary>
        public int EditCount => _edits.Count;

        public byte Get(LocalBlock local) => _edits.TryGetValue((ushort)local.Index, out byte edited) ? edited : _base[local.Index];

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

            Revision++;
            return true;
        }

        /// <summary>Sparse edits in ascending local-index order (stable for hashing and serialization).</summary>
        public IEnumerable<KeyValuePair<ushort, byte>> Edits => _edits;
    }
}
