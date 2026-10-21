using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Editing
{
    /// <summary>
    /// A race's isolated copy of the track (DESTRUCTION Race isolation, ADR-004, SAVE-007): on countdown the track
    /// region of the sandbox is locked at its current revisions and cloned into an overlay authority with no journal.
    /// Race destruction changes only the overlay; at the end the overlay is discarded and the sandbox is unlocked,
    /// provably unchanged.
    /// </summary>
    public sealed class MatchWorld
    {
        private readonly WorldAuthority _sandbox;
        private readonly IReadOnlyList<WorldBox> _region;
        private readonly Dictionary<ChunkCoord, (ulong Revision, ulong Hash)> _locked = new Dictionary<ChunkCoord, (ulong, ulong)>();
        private bool _ended;

        private MatchWorld(Guid raceId, WorldAuthority sandbox, IReadOnlyList<WorldBox> region, WorldAuthority overlay)
        {
            RaceId = raceId;
            _sandbox = sandbox;
            _region = region;
            Overlay = overlay;
        }

        public Guid RaceId { get; }

        /// <summary>Where race edits and destruction go. Never journaled; batches only go to the publish sink.</summary>
        public WorldAuthority Overlay { get; }

        /// <summary>Locked revision and content hash per track chunk.</summary>
        public IReadOnlyDictionary<ChunkCoord, (ulong Revision, ulong Hash)> LockedChunks => _locked;

        /// <param name="raceZones">Zones inside the overlay: RaceDestructible where the course allows destruction, ActiveRace/Protected elsewhere.</param>
        /// <param name="publish">Receives overlay batches for broadcasting to racers; must not be the sandbox journal.</param>
        public static MatchWorld Begin(Guid raceId, WorldAuthority sandbox, IReadOnlyList<WorldBox> trackRegion, ZoneMap raceZones, IMutationSink? publish = null)
        {
            if (trackRegion.Count == 0)
            {
                throw new ArgumentException("A race needs a track region.", nameof(trackRegion));
            }

            var overlay = new WorldAuthority(new EditValidator(raceZones), sink: publish);
            var match = new MatchWorld(raceId, sandbox, trackRegion, overlay);
            foreach (ChunkCoord coord in ChunksOf(trackRegion))
            {
                ChunkData? chunk = sandbox.GetChunk(coord);
                if (chunk == null)
                {
                    throw new InvalidOperationException($"Track chunk {coord} is not loaded; load the whole track before the countdown.");
                }

                match._locked[coord] = (chunk.Revision, ChunkHash.Compute(chunk));
                overlay.AddChunk(chunk.Clone());
            }

            sandbox.Lock(trackRegion);
            return match;
        }

        /// <summary>Track chunks the race changed (clients drop their match copies of these).</summary>
        public IReadOnlyList<ChunkCoord> TouchedChunks()
        {
            var touched = new List<ChunkCoord>();
            foreach (KeyValuePair<ChunkCoord, (ulong Revision, ulong Hash)> entry in _locked)
            {
                if (Overlay.GetChunk(entry.Key)!.Revision != entry.Value.Revision)
                {
                    touched.Add(entry.Key);
                }
            }

            return touched;
        }

        /// <summary>Discards the overlay and unlocks the sandbox, verifying the locked track is unchanged.</summary>
        public void End()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            _sandbox.Unlock(_region);
            foreach (KeyValuePair<ChunkCoord, (ulong Revision, ulong Hash)> entry in _locked)
            {
                ChunkData chunk = _sandbox.GetChunk(entry.Key) ?? throw new InvalidOperationException($"Track chunk {entry.Key} was unloaded during the race.");
                if (chunk.Revision != entry.Value.Revision || ChunkHash.Compute(chunk) != entry.Value.Hash)
                {
                    // The lock makes this unreachable; failing loudly beats silently persisting race damage.
                    throw new InvalidOperationException($"Sandbox chunk {entry.Key} changed during the race.");
                }
            }

            foreach (ChunkCoord coord in _locked.Keys)
            {
                Overlay.RemoveChunk(coord);
            }
        }

        private static IEnumerable<ChunkCoord> ChunksOf(IReadOnlyList<WorldBox> region)
        {
            var seen = new HashSet<ChunkCoord>();
            foreach (WorldBox box in region)
            {
                ChunkCoord min = ChunkMath.ToChunk(new BlockPos((int)Math.Floor(box.MinX), (int)Math.Floor(box.MinY), (int)Math.Floor(box.MinZ)));
                ChunkCoord max = ChunkMath.ToChunk(new BlockPos((int)Math.Ceiling(box.MaxX) - 1, (int)Math.Ceiling(box.MaxY) - 1, (int)Math.Ceiling(box.MaxZ) - 1));
                for (int x = min.X; x <= max.X; x++)
                {
                    for (int y = min.Y; y <= max.Y; y++)
                    {
                        for (int z = min.Z; z <= max.Z; z++)
                        {
                            var c = new ChunkCoord(x, y, z);
                            if (seen.Add(c))
                            {
                                yield return c;
                            }
                        }
                    }
                }
            }
        }
    }
}
