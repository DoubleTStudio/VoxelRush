using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Persistence
{
    /// <summary>Durable off-machine snapshot storage (the API's presigned object store). Failures throw <see cref="IOException"/>.</summary>
    public interface ISnapshotRemote
    {
        /// <summary>The newest committed snapshot of a chunk, or null when there is none.</summary>
        byte[]? Download(ChunkCoord coord);

        void Upload(ChunkCoord coord, ulong revision, byte[] bytes);
    }

    /// <summary>
    /// SAVE-003 cold restore and publish. On start, newer remote snapshots replace local ones (a fresh machine gets the
    /// world back); on clean shutdown, after the journal is closed, chunks past their snapshot are snapshotted and every
    /// snapshot not yet uploaded is uploaded. Uploaded revisions are tracked locally so failed uploads retry next time.
    /// </summary>
    public sealed class SnapshotSync
    {
        public const string UploadedIndexName = "uploaded.idx";

        private readonly WorldStore _store;
        private readonly ISnapshotRemote _remote;
        private readonly Dictionary<ChunkCoord, ulong> _uploaded = new Dictionary<ChunkCoord, ulong>();

        public SnapshotSync(WorldStore store, ISnapshotRemote remote)
        {
            _store = store;
            _remote = remote;
            LoadIndex();
        }

        /// <summary>Why the last upload failed (for the shutdown log), or null.</summary>
        public string? LastError { get; private set; }

        private string IndexPath => Path.Combine(_store.Directory, "snapshots", UploadedIndexName);

        public const string EpochFileName = "epoch";

        /// <summary>
        /// SAVE-009: after a restore the world is on a new epoch and this disk's journal and snapshots belong to the old
        /// timeline. Deletes them (other files such as the reward outbox stay) and records the epoch. Returns true if wiped.
        /// </summary>
        public static bool AlignEpoch(string directory, int epoch)
        {
            string marker = Path.Combine(directory, EpochFileName);
            int local = File.Exists(marker) && int.TryParse(File.ReadAllText(marker).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int e) ? e : 0;
            bool wipe = local != epoch;
            if (wipe)
            {
                File.Delete(Path.Combine(directory, WorldStore.JournalFileName));
                string snapshots = Path.Combine(directory, "snapshots");
                if (System.IO.Directory.Exists(snapshots))
                {
                    System.IO.Directory.Delete(snapshots, recursive: true);
                }
            }

            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(marker, epoch.ToString(CultureInfo.InvariantCulture));
            return wipe;
        }

        /// <summary>Imports remote snapshots newer than local ones. Throws when the remote is unreachable: fail closed
        /// rather than serve (and later publish) an older world.</summary>
        public int Restore(IEnumerable<ChunkCoord> chunks)
        {
            int imported = 0;
            foreach (ChunkCoord coord in chunks)
            {
                byte[]? bytes = _remote.Download(coord);
                if (bytes != null && _store.ImportSnapshot(coord, bytes))
                {
                    imported++;
                    _uploaded[coord] = _store.SnapshotRevision(coord) ?? 0;
                }
            }

            if (imported > 0)
            {
                SaveIndex();
            }

            return imported;
        }

        /// <summary>Snapshots and uploads; call only after the journal flushed cleanly. Upload failures are counted, not thrown.</summary>
        public (int Written, int Uploaded, int Failed) Publish(IEnumerable<ChunkData> chunks)
        {
            int written = 0, uploaded = 0, failed = 0;
            foreach (ChunkData chunk in chunks)
            {
                if (chunk.Revision == 0)
                {
                    continue;
                }

                if (_store.SnapshotRevision(chunk.Coord) != chunk.Revision)
                {
                    _store.WriteSnapshot(chunk);
                    written++;
                }

                if (_uploaded.TryGetValue(chunk.Coord, out ulong done) && done == chunk.Revision)
                {
                    continue;
                }

                try
                {
                    _remote.Upload(chunk.Coord, chunk.Revision, _store.ReadSnapshotBytes(chunk.Coord)!);
                    _uploaded[chunk.Coord] = chunk.Revision;
                    uploaded++;
                    SaveIndex();
                }
                catch (IOException e)
                {
                    failed++;
                    LastError = e.Message;
                }
            }

            return (written, uploaded, failed);
        }

        private void LoadIndex()
        {
            if (!File.Exists(IndexPath))
            {
                return;
            }

            foreach (string line in File.ReadAllLines(IndexPath))
            {
                string[] p = line.Split(' ');
                if (p.Length == 4 && int.TryParse(p[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
                    && int.TryParse(p[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y)
                    && int.TryParse(p[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z)
                    && ulong.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out ulong revision))
                {
                    _uploaded[new ChunkCoord(x, y, z)] = revision;
                }
            }
        }

        private void SaveIndex()
        {
            var lines = new List<string>(_uploaded.Count);
            foreach (KeyValuePair<ChunkCoord, ulong> e in _uploaded)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", e.Key.X, e.Key.Y, e.Key.Z, e.Value));
            }

            // A lost index only means re-uploading; temp + rename keeps it from being half-written.
            string temp = IndexPath + ".tmp";
            File.WriteAllLines(temp, lines);
            if (File.Exists(IndexPath))
            {
                File.Replace(temp, IndexPath, null);
            }
            else
            {
                File.Move(temp, IndexPath);
            }
        }
    }
}
