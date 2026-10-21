using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.World.Persistence
{
    /// <summary>
    /// File-backed world persistence (SYSTEM.md): per-chunk edit snapshots plus one append-only mutation journal.
    /// Recovery = newest verified snapshot (latest, else the previous generation, else the generated base) + journal
    /// records after its revision, in order. Snapshots store only edits, deflated; the base is regenerated from the
    /// pinned generator version. Compaction never drops records the older snapshot generation still needs (SAVE-003).
    /// </summary>
    public sealed class WorldStore
    {
        public const string JournalFileName = "journal.log";
        private const uint SnapshotMagic = 0x4E535256; // "VRSN"
        // 1: raw edits; 2: deflated (varint index gap, block) pairs (SAVE-003). Both are readable.
        private const ushort SnapshotSchema = 2;

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
            // A corrupt latest snapshot falls back to the previous generation; the journal covers the difference.
            ChunkData chunk = TryReadSnapshot(coord, SnapshotPath(coord)) ?? TryReadSnapshot(coord, PreviousPath(coord))
                ?? new ChunkData(coord, _generatorVersion, _baseBlocks(coord));
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

        /// <summary>
        /// Durably publishes a chunk snapshot: temp file, fsync, atomic rename (the old snapshot becomes the fallback
        /// generation), then read-back verification.
        /// </summary>
        public void WriteSnapshot(ChunkData chunk) => InstallSnapshot(chunk, EncodeSnapshot(chunk));

        /// <summary>The newest verified snapshot file of a chunk (for upload to object storage), or null.</summary>
        public byte[]? ReadSnapshotBytes(ChunkCoord coord) =>
            TryReadSnapshot(coord, SnapshotPath(coord)) != null ? File.ReadAllBytes(SnapshotPath(coord)) : null;

        /// <summary>Revision of the newest verified snapshot of a chunk, or null.</summary>
        public ulong? SnapshotRevision(ChunkCoord coord) => TryReadSnapshot(coord, SnapshotPath(coord))?.Revision;

        /// <summary>
        /// Installs snapshot bytes from elsewhere (cold restore from object storage) after full verification. Refuses
        /// anything that doesn't checksum, decode, hash and match this chunk and generator, or isn't newer than ours.
        /// </summary>
        public bool ImportSnapshot(ChunkCoord coord, byte[] bytes)
        {
            ChunkData? chunk = Decode(coord, bytes, out string? problem);
            if (chunk == null)
            {
                RejectedSnapshots++;
                Problems.Add($"{coord}: imported snapshot rejected ({problem})");
                return false;
            }

            if (SnapshotRevision(coord) is ulong local && local >= chunk.Revision)
            {
                return false;
            }

            InstallSnapshot(chunk, bytes);
            return true;
        }

        /// <summary>
        /// Rewrites the journal without records that both retained snapshot generations already cover. Only call while
        /// no <see cref="JournalWriter"/> has the file open (start-up or after shutdown). Returns records dropped.
        /// </summary>
        public int CompactJournal()
        {
            RecoverJournal();
            var keep = new List<ChunkMutationBatch>(_journal.Count);
            var covered = new Dictionary<ChunkCoord, ulong>();
            foreach (ChunkMutationBatch batch in _journal)
            {
                if (!covered.TryGetValue(batch.Chunk, out ulong floor))
                {
                    floor = CoveredRevision(batch.Chunk);
                    covered[batch.Chunk] = floor;
                }

                if (batch.NewRevision > floor)
                {
                    keep.Add(batch);
                }
            }

            int dropped = _journal.Count - keep.Count;
            if (dropped == 0)
            {
                return 0;
            }

            string temp = JournalPath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (ChunkMutationBatch batch in keep)
                {
                    byte[] record = JournalCodec.Encode(batch);
                    stream.Write(record, 0, record.Length);
                }

                stream.Flush(flushToDisk: true);
            }

            File.Replace(temp, JournalPath, null);
            _journal.Clear();
            _journal.AddRange(keep);
            return dropped;
        }

        // Only records up to the OLDER generation are redundant: if the latest snapshot is lost, the previous one plus the
        // journal must still rebuild it. With a single generation nothing is dropped yet.
        private ulong CoveredRevision(ChunkCoord coord)
        {
            ulong? latest = TryReadSnapshot(coord, SnapshotPath(coord))?.Revision;
            ulong? previous = TryReadSnapshot(coord, PreviousPath(coord))?.Revision;
            return latest is ulong l && previous is ulong p ? Math.Min(l, p) : 0;
        }

        private void InstallSnapshot(ChunkData chunk, byte[] bytes)
        {
            string final = SnapshotPath(chunk.Coord);
            string temp = final + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // Atomic swap keeping the old file as the fallback generation (File.Move with overwrite isn't in
            // .NET Standard 2.1, which Unity compiles against). A corrupt old file is not worth keeping.
            if (File.Exists(final))
            {
                string previous = PreviousPath(chunk.Coord);
                bool keepOld = Decode(chunk.Coord, File.ReadAllBytes(final), out _) != null;
                if (keepOld && File.Exists(previous))
                {
                    File.Delete(previous);
                }

                File.Replace(temp, final, keepOld ? previous : null);
            }
            else
            {
                File.Move(temp, final);
            }

            ChunkData? check = TryReadSnapshot(chunk.Coord, final);
            if (check == null || check.Revision != chunk.Revision || ChunkHash.Compute(check) != ChunkHash.Compute(chunk))
            {
                throw new IOException($"Snapshot verification failed for {chunk.Coord}.");
            }
        }

        private ChunkData? TryReadSnapshot(ChunkCoord coord, string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            ChunkData? chunk = Decode(coord, File.ReadAllBytes(path), out string? problem);
            if (chunk == null)
            {
                RejectedSnapshots++;
                Problems.Add($"{coord}: snapshot rejected ({problem})");
            }

            return chunk;
        }

        private ChunkData? Decode(ChunkCoord coord, byte[] bytes, out string? problem)
        {
            problem = null;
            try
            {
                if (bytes.Length < 4 || Crc32.Compute(bytes, 0, bytes.Length - 4) != JournalCodec.ReadUInt32(bytes, bytes.Length - 4))
                {
                    throw new InvalidDataException("checksum mismatch");
                }

                using var r = new BinaryReader(new MemoryStream(bytes, 0, bytes.Length - 4));
                ushort schema = r.ReadUInt32() == SnapshotMagic ? r.ReadUInt16() : (ushort)0;
                if (schema != 1 && schema != SnapshotSchema)
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

                if (count > ChunkMath.Volume)
                {
                    throw new InvalidDataException("too many edits");
                }

                var list = new List<KeyValuePair<ushort, byte>>(count);
                if (schema == SnapshotSchema)
                {
                    // Bounded inflate: an index gap below 4096 takes at most two varint bytes, so 3 bytes per edit.
                    byte[] raw = Inflate(r.BaseStream, count * 3);
                    int at = 0;
                    int index = -1;
                    for (int i = 0; i < count; i++)
                    {
                        int gap = 0;
                        for (int shift = 0; ; shift += 7)
                        {
                            if (at >= raw.Length || shift > 7)
                            {
                                throw new InvalidDataException("bad edit gap");
                            }

                            byte b = raw[at++];
                            gap |= (b & 0x7F) << shift;
                            if ((b & 0x80) == 0)
                            {
                                break;
                            }
                        }

                        index += gap;
                        if (gap == 0 || index >= ChunkMath.Volume || at >= raw.Length)
                        {
                            throw new InvalidDataException("bad edit index");
                        }

                        list.Add(new KeyValuePair<ushort, byte>((ushort)index, raw[at++]));
                    }

                    if (at != raw.Length)
                    {
                        throw new InvalidDataException("trailing edit bytes");
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        list.Add(new KeyValuePair<ushort, byte>(r.ReadUInt16(), r.ReadByte()));
                    }
                }

                ChunkData chunk = ChunkData.FromSnapshot(coord, generatorVersion, _baseBlocks(coord), revision, list);
                if (ChunkHash.Compute(chunk) != contentHash)
                {
                    throw new InvalidDataException("content hash mismatch");
                }

                return chunk;
            }
            catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is ArgumentException)
            {
                // A corrupt snapshot is ignored; the chunk is rebuilt from the other generation, the base and the journal.
                problem = e.Message;
                return null;
            }
        }

        // Inflates at most `limit` bytes; more is a decompression bomb, not a snapshot.
        private static byte[] Inflate(Stream source, int limit)
        {
            using var inflate = new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true);
            var raw = new MemoryStream();
            var buffer = new byte[4096];
            int n;
            while ((n = inflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (raw.Length + n > limit)
                {
                    throw new InvalidDataException("edits too large");
                }

                raw.Write(buffer, 0, n);
            }

            return raw.ToArray();
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
            }

            using (var deflate = new DeflateStream(body, CompressionLevel.Optimal, leaveOpen: true))
            {
                // Edits are in ascending index order; gaps are small where players build, so they deflate well.
                int previous = -1;
                foreach (KeyValuePair<ushort, byte> edit in chunk.Edits)
                {
                    int gap = edit.Key - previous;
                    previous = edit.Key;
                    for (; gap >= 0x80; gap >>= 7)
                    {
                        deflate.WriteByte((byte)(gap | 0x80));
                    }

                    deflate.WriteByte((byte)gap);
                    deflate.WriteByte(edit.Value);
                }
            }

            byte[] payload = body.ToArray();
            var bytes = new byte[payload.Length + 4];
            Buffer.BlockCopy(payload, 0, bytes, 0, payload.Length);
            JournalCodec.WriteUInt32(bytes, payload.Length, Crc32.Compute(payload, 0, payload.Length));
            return bytes;
        }

        private string PreviousPath(ChunkCoord c) => SnapshotPath(c) + ".prev";

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
