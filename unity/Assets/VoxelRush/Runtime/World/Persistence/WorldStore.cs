using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.World.Persistence
{
    /// <summary>
    /// File-backed world persistence for the first slice (SYSTEM.md): per-chunk edit snapshots plus one append-only
    /// mutation journal. Recovery = verified snapshot (if any) + journal records after its revision, in order.
    /// Snapshots store only edits; the base is regenerated from the pinned generator version.
    /// </summary>
    public sealed class WorldStore
    {
        public const string JournalFileName = "journal.log";
        private const uint SnapshotMagic = 0x4E535256; // "VRSN"
        private const ushort SnapshotSchema = 1;

        private readonly Func<ChunkCoord, byte[]> _baseBlocks;
        private readonly int _generatorVersion;
        private readonly List<ChunkMutationBatch> _journal = new List<ChunkMutationBatch>();

        public WorldStore(string directory, int generatorVersion, Func<ChunkCoord, byte[]> baseBlocks)
        {
            Directory = directory;
            _generatorVersion = generatorVersion;
            _baseBlocks = baseBlocks;
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "snapshots"));
        }

        public string Directory { get; }

        public string JournalPath => Path.Combine(Directory, JournalFileName);

        /// <summary>Valid journal records found at the last recovery, in append order.</summary>
        public IReadOnlyList<ChunkMutationBatch> RecoveredRecords => _journal;

        public long TruncatedBytes { get; private set; }

        public int RejectedSnapshots { get; private set; }

        public List<string> Problems { get; } = new List<string>();

        /// <summary>Reads the journal, truncating a torn or corrupt tail (an un-acknowledged write cut short by a crash).</summary>
        public void RecoverJournal()
        {
            _journal.Clear();
            if (!File.Exists(JournalPath))
            {
                return;
            }

            long valid;
            long length;
            using (var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                length = stream.Length;
                valid = JournalCodec.ReadAll(stream, _journal);
            }

            if (valid < length)
            {
                TruncatedBytes = length - valid;
                using var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.SetLength(valid);
                stream.Flush(flushToDisk: true);
            }
        }

        /// <summary>Restores one chunk: generated base, verified snapshot edits, then journal records past its revision.</summary>
        public ChunkData LoadChunk(ChunkCoord coord)
        {
            ChunkData chunk = TryReadSnapshot(coord) ?? new ChunkData(coord, _generatorVersion, _baseBlocks(coord));
            foreach (ChunkMutationBatch batch in _journal)
            {
                if (!batch.Chunk.Equals(coord) || batch.NewRevision <= chunk.Revision)
                {
                    continue;
                }

                if (batch.BaseRevision != chunk.Revision)
                {
                    // A gap means lost history: stop rather than build on an unknown state.
                    Problems.Add($"{coord}: journal gap at revision {chunk.Revision} (next record starts at {batch.BaseRevision})");
                    break;
                }

                foreach (BlockChange change in batch.Changes)
                {
                    chunk.Set(LocalBlock.FromIndex(change.Index), change.BlockType);
                }

                if (chunk.Revision != batch.NewRevision)
                {
                    // Set() skips no-op changes; keep the revision chain exact by restoring at the recorded revision.
                    chunk = ChunkData.FromSnapshot(coord, chunk.GeneratorVersion, _baseBlocks(coord), batch.NewRevision, chunk.Edits);
                }
            }

            return chunk;
        }

        /// <summary>All chunks mentioned by snapshots or the journal.</summary>
        public IEnumerable<ChunkCoord> KnownChunks()
        {
            var known = new HashSet<ChunkCoord>();
            foreach (string file in System.IO.Directory.GetFiles(Path.Combine(Directory, "snapshots"), "*.snap"))
            {
                if (TryParseName(Path.GetFileNameWithoutExtension(file), out ChunkCoord coord))
                {
                    known.Add(coord);
                }
            }

            foreach (ChunkMutationBatch batch in _journal)
            {
                known.Add(batch.Chunk);
            }

            return known;
        }

        /// <summary>Durably publishes a chunk snapshot: temp file, fsync, atomic rename, then read-back verification.</summary>
        public void WriteSnapshot(ChunkData chunk)
        {
            string final = SnapshotPath(chunk.Coord);
            string temp = final + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = EncodeSnapshot(chunk);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // Atomic swap (File.Move with overwrite isn't in .NET Standard 2.1, which Unity compiles against).
            if (File.Exists(final))
            {
                File.Replace(temp, final, null);
            }
            else
            {
                File.Move(temp, final);
            }
            ChunkData? check = TryReadSnapshot(chunk.Coord);
            if (check == null || check.Revision != chunk.Revision || ChunkHash.Compute(check) != ChunkHash.Compute(chunk))
            {
                throw new IOException($"Snapshot verification failed for {chunk.Coord}.");
            }
        }

        private ChunkData? TryReadSnapshot(ChunkCoord coord)
        {
            string path = SnapshotPath(coord);
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                if (bytes.Length < 4 || Crc32.Compute(bytes, 0, bytes.Length - 4) != JournalCodec.ReadUInt32(bytes, bytes.Length - 4))
                {
                    throw new InvalidDataException("checksum mismatch");
                }

                using var r = new BinaryReader(new MemoryStream(bytes, 0, bytes.Length - 4));
                if (r.ReadUInt32() != SnapshotMagic || r.ReadUInt16() != SnapshotSchema)
                {
                    throw new InvalidDataException("bad header");
                }

                int generatorVersion = r.ReadInt32();
                var stored = new ChunkCoord(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                ulong revision = r.ReadUInt64();
                ulong contentHash = r.ReadUInt64();
                int count = r.ReadUInt16();
                if (!stored.Equals(coord) || generatorVersion != _generatorVersion)
                {
                    throw new InvalidDataException("coordinate or generator version mismatch");
                }

                var edits = new List<KeyValuePair<ushort, byte>>(count);
                for (int i = 0; i < count; i++)
                {
                    edits.Add(new KeyValuePair<ushort, byte>(r.ReadUInt16(), r.ReadByte()));
                }

                ChunkData chunk = ChunkData.FromSnapshot(coord, generatorVersion, _baseBlocks(coord), revision, edits);
                if (ChunkHash.Compute(chunk) != contentHash)
                {
                    throw new InvalidDataException("content hash mismatch");
                }

                return chunk;
            }
            catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is ArgumentException)
            {
                // A corrupt snapshot is ignored; the chunk is rebuilt from the base and the journal.
                RejectedSnapshots++;
                Problems.Add($"{coord}: snapshot rejected ({e.Message})");
                return null;
            }
        }

        private byte[] EncodeSnapshot(ChunkData chunk)
        {
            using var body = new MemoryStream();
            using (var w = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(SnapshotMagic);
                w.Write(SnapshotSchema);
                w.Write(chunk.GeneratorVersion);
                w.Write(chunk.Coord.X);
                w.Write(chunk.Coord.Y);
                w.Write(chunk.Coord.Z);
                w.Write(chunk.Revision);
                w.Write(ChunkHash.Compute(chunk));
                w.Write((ushort)chunk.EditCount);
                foreach (KeyValuePair<ushort, byte> edit in chunk.Edits)
                {
                    w.Write(edit.Key);
                    w.Write(edit.Value);
                }
            }

            byte[] payload = body.ToArray();
            var bytes = new byte[payload.Length + 4];
            Buffer.BlockCopy(payload, 0, bytes, 0, payload.Length);
            JournalCodec.WriteUInt32(bytes, payload.Length, Crc32.Compute(payload, 0, payload.Length));
            return bytes;
        }

        // Coordinates in file names are integers only: no path traversal via storage keys (PERSISTENCE_SECURITY).
        private string SnapshotPath(ChunkCoord c) =>
            Path.Combine(Directory, "snapshots", string.Format(CultureInfo.InvariantCulture, "c_{0}_{1}_{2}.snap", c.X, c.Y, c.Z));

        private static bool TryParseName(string name, out ChunkCoord coord)
        {
            coord = default;
            string[] parts = name.Split('_');
            if (parts.Length != 4 || parts[0] != "c"
                || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y)
                || !int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z))
            {
                return false;
            }

            coord = new ChunkCoord(x, y, z);
            return true;
        }
    }
}
