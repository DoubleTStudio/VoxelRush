using System;
using System.Collections.Generic;
using System.IO;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;

namespace VoxelRush.World.Sync
{
    public enum GeneratorKind : byte
    {
        Terrain = 1,
        FlatPlot = 2,
    }

    /// <summary>What a client needs to rebuild base blocks locally (ADR-003: seed + pinned generator version).</summary>
    public sealed class WorldInfo
    {
        public WorldInfo(Guid worldId, GeneratorKind kind, long seed, int generatorVersion, int schemaVersion, ChunkCoord regionMin, ChunkCoord regionMax)
        {
            WorldId = worldId;
            Kind = kind;
            Seed = seed;
            GeneratorVersion = generatorVersion;
            SchemaVersion = schemaVersion;
            RegionMin = regionMin;
            RegionMax = regionMax;
        }

        public Guid WorldId { get; }

        public GeneratorKind Kind { get; }

        public long Seed { get; }

        public int GeneratorVersion { get; }

        public int SchemaVersion { get; }

        public ChunkCoord RegionMin { get; }

        public ChunkCoord RegionMax { get; }

        public IEnumerable<ChunkCoord> RegionChunks()
        {
            for (int x = RegionMin.X; x <= RegionMax.X; x++)
            {
                for (int y = RegionMin.Y; y <= RegionMax.Y; y++)
                {
                    for (int z = RegionMin.Z; z <= RegionMax.Z; z++)
                    {
                        yield return new ChunkCoord(x, y, z);
                    }
                }
            }
        }

        /// <summary>Creates the matching generator, refusing versions this build doesn't implement (tampered/mismatched world).</summary>
        public IBaseGenerator CreateGenerator()
        {
            IBaseGenerator generator = Kind switch
            {
                GeneratorKind.Terrain => new TerrainGenerator(Seed),
                GeneratorKind.FlatPlot => new FlatPlotGenerator(),
                _ => throw new NotSupportedException($"Unknown generator kind {Kind}."),
            };

            if (generator.GeneratorVersion != GeneratorVersion)
            {
                throw new NotSupportedException($"{Kind} generator v{GeneratorVersion} isn't supported by this build (v{generator.GeneratorVersion}).");
            }

            return generator;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream))
            {
                w.Write(WorldId.ToByteArray());
                w.Write((byte)Kind);
                w.Write(Seed);
                w.Write(GeneratorVersion);
                w.Write(SchemaVersion);
                WriteCoord(w, RegionMin);
                WriteCoord(w, RegionMax);
            }

            return stream.ToArray();
        }

        public static WorldInfo Decode(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            var info = new WorldInfo(new Guid(r.ReadBytes(16)), (GeneratorKind)r.ReadByte(), r.ReadInt64(), r.ReadInt32(), r.ReadInt32(), ReadCoord(r), ReadCoord(r));
            long volume = (long)(info.RegionMax.X - info.RegionMin.X + 1) * (info.RegionMax.Y - info.RegionMin.Y + 1) * (info.RegionMax.Z - info.RegionMin.Z + 1);
            if (volume <= 0 || volume > ChunkSnapshotMessage.MaxRegionChunks)
            {
                throw new InvalidDataException("Region out of bounds.");
            }

            return info;
        }

        internal static void WriteCoord(BinaryWriter w, ChunkCoord c)
        {
            w.Write(c.X);
            w.Write(c.Y);
            w.Write(c.Z);
        }

        internal static ChunkCoord ReadCoord(BinaryReader r) => new ChunkCoord(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
    }

    /// <summary>CONTRACTS ChunkSnapshot, edits-only: the client regenerates the base and verifies the content hash.</summary>
    public sealed class ChunkSnapshotMessage
    {
        public const int MaxRegionChunks = 4096;

        public ChunkSnapshotMessage(ChunkCoord coord, ulong revision, int generatorVersion, int schemaVersion, ulong contentHash, IReadOnlyList<KeyValuePair<ushort, byte>> edits)
        {
            Coord = coord;
            Revision = revision;
            GeneratorVersion = generatorVersion;
            SchemaVersion = schemaVersion;
            ContentHash = contentHash;
            Edits = edits;
        }

        public ChunkCoord Coord { get; }

        public ulong Revision { get; }

        public int GeneratorVersion { get; }

        public int SchemaVersion { get; }

        public ulong ContentHash { get; }

        public IReadOnlyList<KeyValuePair<ushort, byte>> Edits { get; }

        public static ChunkSnapshotMessage From(ChunkData chunk) =>
            new ChunkSnapshotMessage(chunk.Coord, chunk.Revision, chunk.GeneratorVersion, chunk.SchemaVersion, ChunkHash.Compute(chunk), new List<KeyValuePair<ushort, byte>>(chunk.Edits));

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream))
            {
                WorldInfo.WriteCoord(w, Coord);
                w.Write(Revision);
                w.Write(GeneratorVersion);
                w.Write(SchemaVersion);
                w.Write(ContentHash);
                w.Write((ushort)Edits.Count);
                foreach (KeyValuePair<ushort, byte> edit in Edits)
                {
                    w.Write(edit.Key);
                    w.Write(edit.Value);
                }
            }

            return stream.ToArray();
        }

        /// <summary>Bounds-checked decode of untrusted bytes.</summary>
        public static ChunkSnapshotMessage Decode(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            ChunkCoord coord = WorldInfo.ReadCoord(r);
            ulong revision = r.ReadUInt64();
            int generatorVersion = r.ReadInt32();
            int schemaVersion = r.ReadInt32();
            ulong hash = r.ReadUInt64();
            int count = r.ReadUInt16();
            if (count > ChunkMath.Volume)
            {
                throw new InvalidDataException("Too many edits.");
            }

            var edits = new List<KeyValuePair<ushort, byte>>(count);
            for (int i = 0; i < count; i++)
            {
                ushort index = r.ReadUInt16();
                if (index >= ChunkMath.Volume)
                {
                    throw new InvalidDataException("Edit index out of range.");
                }

                edits.Add(new KeyValuePair<ushort, byte>(index, r.ReadByte()));
            }

            return new ChunkSnapshotMessage(coord, revision, generatorVersion, schemaVersion, hash, edits);
        }
    }
}
