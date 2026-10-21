using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Sync
{
    /// <summary>One cached chunk version: what the client tells the server it already has.</summary>
    public readonly struct CachedChunk
    {
        public CachedChunk(ChunkCoord coord, ulong revision, ulong contentHash)
        {
            Coord = coord;
            Revision = revision;
            ContentHash = contentHash;
        }

        public ChunkCoord Coord { get; }

        public ulong Revision { get; }

        public ulong ContentHash { get; }
    }

    /// <summary>
    /// SAVE-008 disposable client chunk cache (SAVE_GAME: device-local, content-addressed, never authoritative). One
    /// directory per world version (world id + generator kind, seed and version + chunk schema + cache format); opening
    /// a world deletes that world's directories for other versions. Entries are only used after the server confirms
    /// their (revision, content hash) and the client re-verifies the hash; anything unreadable is deleted.
    /// Bounded: least-recently-used other worlds are evicted when the cache grows past its budget.
    /// </summary>
    public sealed class ChunkCache
    {
        public const int FormatVersion = 1;
        public const int MaxManifestEntries = 2000;

        private readonly string _root;
        private readonly long _maxBytes;

        public ChunkCache(string root, WorldInfo world, long maxBytes = 32L * 1024 * 1024)
        {
            _root = root;
            _maxBytes = maxBytes;
            string prefix = world.WorldId.ToString("N");
            Directory = Path.Combine(root, prefix + "-" + VersionKey(world));
            System.IO.Directory.CreateDirectory(root);
            foreach (string other in System.IO.Directory.GetDirectories(root, prefix + "-*"))
            {
                if (!string.Equals(other, Directory, StringComparison.Ordinal))
                {
                    // Stale: the world's generator, schema or our cache format changed.
                    System.IO.Directory.Delete(other, recursive: true);
                    Invalidated++;
                }
            }

            System.IO.Directory.CreateDirectory(Directory);
            System.IO.Directory.SetLastWriteTimeUtc(Directory, DateTime.UtcNow);
        }

        public string Directory { get; }

        /// <summary>Stale version directories deleted when this world was opened.</summary>
        public int Invalidated { get; }

        public int Dropped { get; private set; }

        /// <summary>What this device has for the world (bounded), read from entry headers.</summary>
        public List<CachedChunk> Manifest()
        {
            var list = new List<CachedChunk>();
            foreach (string file in System.IO.Directory.GetFiles(Directory, "*.chunk"))
            {
                if (list.Count >= MaxManifestEntries)
                {
                    break;
                }

                ChunkSnapshotMessage? entry = Read(file);
                if (entry != null)
                {
                    list.Add(new CachedChunk(entry.Coord, entry.Revision, entry.ContentHash));
                }
            }

            return list;
        }

        /// <summary>The cached snapshot if it is exactly this version; otherwise drops the entry and returns null.</summary>
        public ChunkSnapshotMessage? TryLoad(ChunkCoord coord, ulong revision, ulong contentHash)
        {
            string path = PathFor(coord);
            ChunkSnapshotMessage? entry = File.Exists(path) ? Read(path) : null;
            if (entry != null && entry.Coord.Equals(coord) && entry.Revision == revision && entry.ContentHash == contentHash)
            {
                return entry;
            }

            Drop(coord);
            return null;
        }

        public void Drop(ChunkCoord coord)
        {
            string path = PathFor(coord);
            if (File.Exists(path))
            {
                File.Delete(path);
                Dropped++;
            }
        }

        /// <summary>Stores the replica's current chunks (on leaving the world) and enforces the size budget.</summary>
        public int Store(IEnumerable<ChunkData> chunks)
        {
            int written = 0;
            foreach (ChunkData chunk in chunks)
            {
                if (written >= MaxManifestEntries)
                {
                    break;
                }

                // Temp + rename: a crash mid-write leaves no torn entry under the real name.
                string path = PathFor(chunk.Coord);
                File.WriteAllBytes(path + ".tmp", ChunkSnapshotMessage.From(chunk).Encode());
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(path + ".tmp", path);
                written++;
            }

            System.IO.Directory.SetLastWriteTimeUtc(Directory, DateTime.UtcNow);
            EnforceBudget();
            return written;
        }

        private void EnforceBudget()
        {
            var worlds = new List<(string Path, DateTime Used, long Bytes)>();
            long total = 0;
            foreach (string dir in System.IO.Directory.GetDirectories(_root))
            {
                long bytes = 0;
                foreach (string file in System.IO.Directory.GetFiles(dir))
                {
                    bytes += new FileInfo(file).Length;
                }

                total += bytes;
                worlds.Add((dir, System.IO.Directory.GetLastWriteTimeUtc(dir), bytes));
            }

            worlds.Sort((a, b) => a.Used.CompareTo(b.Used));
            foreach ((string path, DateTime _, long bytes) in worlds)
            {
                if (total <= _maxBytes)
                {
                    break;
                }

                if (!string.Equals(path, Directory, StringComparison.Ordinal))
                {
                    System.IO.Directory.Delete(path, recursive: true);
                    total -= bytes;
                }
            }
        }

        private ChunkSnapshotMessage? Read(string path)
        {
            try
            {
                return ChunkSnapshotMessage.Decode(File.ReadAllBytes(path));
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException)
            {
                File.Delete(path);
                Dropped++;
                return null;
            }
        }

        private string PathFor(ChunkCoord c) =>
            Path.Combine(Directory, string.Format(CultureInfo.InvariantCulture, "c_{0}_{1}_{2}.chunk", c.X, c.Y, c.Z));

        private static string VersionKey(WorldInfo w)
        {
            string key = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}|{5}", w.WorldId, w.Kind, w.Seed, w.GeneratorVersion, w.SchemaVersion, FormatVersion);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
            {
                sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }

    /// <summary>Wire format for cache manifests and "your copy is current" confirmations: (coord, revision, hash) list.</summary>
    public static class CachedChunkWire
    {
        public const int EntryBytes = 28;

        public static byte[] Encode(IReadOnlyList<CachedChunk> entries)
        {
            int count = Math.Min(entries.Count, ChunkCache.MaxManifestEntries);
            var bytes = new byte[2 + count * EntryBytes];
            bytes[0] = (byte)count;
            bytes[1] = (byte)(count >> 8);
            for (int i = 0; i < count; i++)
            {
                int o = 2 + i * EntryBytes;
                CachedChunk e = entries[i];
                Write(bytes, o, BitConverter.GetBytes(e.Coord.X));
                Write(bytes, o + 4, BitConverter.GetBytes(e.Coord.Y));
                Write(bytes, o + 8, BitConverter.GetBytes(e.Coord.Z));
                Write(bytes, o + 12, BitConverter.GetBytes(e.Revision));
                Write(bytes, o + 20, BitConverter.GetBytes(e.ContentHash));
            }

            return bytes;
        }

        /// <summary>Untrusted input: the length must match the declared count exactly.</summary>
        public static bool TryDecode(byte[]? bytes, out List<CachedChunk> entries)
        {
            entries = new List<CachedChunk>();
            if (bytes == null || bytes.Length < 2)
            {
                return false;
            }

            int count = bytes[0] | (bytes[1] << 8);
            if (count > ChunkCache.MaxManifestEntries || bytes.Length != 2 + count * EntryBytes)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                int o = 2 + i * EntryBytes;
                entries.Add(new CachedChunk(new ChunkCoord(BitConverter.ToInt32(bytes, o), BitConverter.ToInt32(bytes, o + 4), BitConverter.ToInt32(bytes, o + 8)),
                    BitConverter.ToUInt64(bytes, o + 12), BitConverter.ToUInt64(bytes, o + 20)));
            }

            return true;
        }

        private static void Write(byte[] into, int offset, byte[] value) => Buffer.BlockCopy(value, 0, into, offset, value.Length);
    }
}
