using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VoxelRush.Core.Race
{
    /// <summary>
    /// Durable queue of reward grants waiting for the backend (SAVE-006): a grant is written (and flushed) before any
    /// send, and removed only after the backend confirms it, so a crash re-sends instead of losing it. Re-sending is
    /// safe because grant keys are idempotent on the backend. One line per grant: key, player, xp.
    /// </summary>
    public sealed class RewardOutbox
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private readonly List<RewardGrant> _pending = new List<RewardGrant>();

        public RewardOutbox(string path)
        {
            _path = path;
            if (!File.Exists(path))
            {
                return;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                string[] parts = line.Split(' ');
                // Skip a torn last line from a crash mid-write; it will be re-added only if the race result is.
                if (parts.Length == 3 && Guid.TryParse(parts[1], out Guid player) && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int xp) && xp > 0)
                {
                    _pending.Add(new RewardGrant(parts[0], player, xp));
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _pending.Count;
                }
            }
        }

        public void Add(IEnumerable<RewardGrant> grants)
        {
            lock (_gate)
            {
                using (var file = new FileStream(_path, FileMode.Append, FileAccess.Write))
                using (var w = new StreamWriter(file))
                {
                    foreach (RewardGrant g in grants)
                    {
                        if (g.GrantKey.IndexOf(' ') >= 0 || _pending.Exists(p => p.GrantKey == g.GrantKey))
                        {
                            continue;
                        }

                        _pending.Add(g);
                        w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} {1:N} {2}", g.GrantKey, g.PlayerId, g.Xp));
                    }

                    w.Flush();
                    file.Flush(flushToDisk: true);
                }
            }
        }

        public IReadOnlyList<RewardGrant> Snapshot()
        {
            lock (_gate)
            {
                return _pending.ToArray();
            }
        }

        /// <summary>Drops a confirmed (or permanently rejected) grant; rewrites the file atomically.</summary>
        public void Complete(string grantKey)
        {
            lock (_gate)
            {
                if (_pending.RemoveAll(g => g.GrantKey == grantKey) == 0)
                {
                    return;
                }

                string temp = _path + ".tmp";
                using (var w = new StreamWriter(temp))
                {
                    foreach (RewardGrant g in _pending)
                    {
                        w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} {1:N} {2}", g.GrantKey, g.PlayerId, g.Xp));
                    }
                }

                if (File.Exists(_path))
                {
                    File.Replace(temp, _path, null);
                }
                else
                {
                    File.Move(temp, _path);
                }
            }
        }
    }
}
