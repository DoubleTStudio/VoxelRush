using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Destruction
{
    /// <summary>What structural analysis needs to know about a block.</summary>
    public interface IStructureWorld
    {
        /// <summary>False when the block's chunk isn't loaded (treated as supporting: never collapse what we can't see).</summary>
        bool TryGetBlock(BlockPos block, out byte blockType, out bool playerBuilt);
    }

    /// <summary>
    /// Anchors and player-built blocks of an authority's chunks: a block differing from the generated base is
    /// player-built; everything else solid (natural terrain, world-authored protected blocks) is an anchor.
    /// </summary>
    public sealed class AuthorityStructureView : IStructureWorld
    {
        private readonly WorldAuthority _world;

        public AuthorityStructureView(WorldAuthority world) => _world = world;

        public bool TryGetBlock(BlockPos block, out byte blockType, out bool playerBuilt)
        {
            ChunkData? chunk = _world.GetChunk(ChunkMath.ToChunk(block));
            if (chunk == null)
            {
                blockType = BlockIds.Air;
                playerBuilt = false;
                return false;
            }

            LocalBlock local = ChunkMath.ToLocal(block);
            blockType = chunk.Get(local);
            playerBuilt = blockType != BlockIds.Air && blockType != chunk.GetBase(local) && blockType != BlockIds.Protected;
            return true;
        }
    }

    public enum SupportStatus : byte
    {
        /// <summary>Reached an anchor: stays up.</summary>
        Supported,
        /// <summary>The whole component was explored without reaching an anchor: it must collapse.</summary>
        Disconnected,
        /// <summary>The search budget ran out first; the caller retries with more budget or keeps the region blocked.</summary>
        Unresolved,
    }

    /// <summary>
    /// Resumable support search from one player-built block (DESTRUCTION Structure): breadth-first over player-built
    /// blocks until it touches an anchor (natural terrain, protected or unloaded) or exhausts the component. Work is
    /// metered in checks (blocks expanded) so a large structure can be resolved over several ticks.
    /// </summary>
    public sealed class SupportSearch
    {
        internal static readonly (int X, int Y, int Z)[] Faces = { (0, -1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0) };

        private readonly IStructureWorld _world;
        private readonly HashSet<BlockPos> _visited = new HashSet<BlockPos>();
        private readonly Queue<BlockPos> _frontier = new Queue<BlockPos>();
        private readonly List<BlockPos> _component = new List<BlockPos>();
        private int _minX, _minY, _minZ, _maxX, _maxY, _maxZ;

        public SupportSearch(IStructureWorld world) => _world = world;

        /// <summary>Supported/Disconnected when done; Unresolved while work remains.</summary>
        public SupportStatus Status { get; private set; } = SupportStatus.Supported;

        public bool IsDone => Status != SupportStatus.Unresolved;

        /// <summary>Blocks expanded since <see cref="Begin"/>.</summary>
        public int Checks { get; private set; }

        /// <summary>Every block of the unsupported component when <see cref="Status"/> is Disconnected; otherwise empty.</summary>
        public IReadOnlyList<BlockPos> Component => Status == SupportStatus.Disconnected ? _component : (IReadOnlyList<BlockPos>)Array.Empty<BlockPos>();

        public void Begin(BlockPos start)
        {
            _visited.Clear();
            _frontier.Clear();
            _component.Clear();
            Checks = 0;
            if (!_world.TryGetBlock(start, out byte type, out bool built) || type == BlockIds.Air || !built)
            {
                Status = SupportStatus.Supported;
                return;
            }

            _minX = _maxX = start.X;
            _minY = _maxY = start.Y;
            _minZ = _maxZ = start.Z;
            _visited.Add(start);
            _frontier.Enqueue(start);
            Status = SupportStatus.Unresolved;
        }

        /// <summary>Expands at most <paramref name="maxChecks"/> blocks; returns how many it used.</summary>
        public int Step(int maxChecks)
        {
            int used = 0;
            while (Status == SupportStatus.Unresolved && used < maxChecks)
            {
                if (_frontier.Count == 0)
                {
                    Status = SupportStatus.Disconnected;
                    break;
                }

                BlockPos block = _frontier.Dequeue();
                used++;
                Checks++;
                _component.Add(block);
                foreach ((int dx, int dy, int dz) in Faces)
                {
                    var next = new BlockPos(block.X + dx, block.Y + dy, block.Z + dz);
                    if (_visited.Contains(next))
                    {
                        continue;
                    }

                    if (!_world.TryGetBlock(next, out byte nextType, out bool nextBuilt) || (nextType != BlockIds.Air && !nextBuilt))
                    {
                        Finish(SupportStatus.Supported);
                        return used;
                    }

                    if (nextType == BlockIds.Air)
                    {
                        continue;
                    }

                    _visited.Add(next);
                    _frontier.Enqueue(next);
                    Grow(next);
                }
            }

            if (Status == SupportStatus.Unresolved && _frontier.Count == 0)
            {
                Status = SupportStatus.Disconnected;
            }

            return used;
        }

        /// <summary>Stops the search, treating the structure as supported (used when a job exceeds its total budget).</summary>
        public void Abandon() => Finish(SupportStatus.Supported);

        /// <summary>Within one block of anything this unfinished search has reached (edits there could change the answer).</summary>
        public bool Covers(BlockPos block) =>
            Status == SupportStatus.Unresolved
            && block.X >= _minX - 1 && block.X <= _maxX + 1
            && block.Y >= _minY - 1 && block.Y <= _maxY + 1
            && block.Z >= _minZ - 1 && block.Z <= _maxZ + 1;

        private void Finish(SupportStatus status)
        {
            Status = status;
            _frontier.Clear();
            if (status != SupportStatus.Disconnected)
            {
                _component.Clear();
            }
        }

        private void Grow(BlockPos b)
        {
            _minX = Math.Min(_minX, b.X);
            _minY = Math.Min(_minY, b.Y);
            _minZ = Math.Min(_minZ, b.Z);
            _maxX = Math.Max(_maxX, b.X);
            _maxY = Math.Max(_maxY, b.Y);
            _maxZ = Math.Max(_maxZ, b.Z);
        }
    }

    /// <summary>
    /// One-shot support checks on top of <see cref="SupportSearch"/>. Deletions need a fresh search (union-find can't
    /// split components), so after a removal each player-built neighbour's component is searched until it reaches an
    /// anchor or is exhausted.
    /// </summary>
    public sealed class SupportAnalyzer
    {
        private readonly SupportSearch _search;

        public SupportAnalyzer(IStructureWorld world) => _search = new SupportSearch(world);

        /// <summary>Blocks examined by the last <see cref="Analyze"/> call (for per-tick budgets).</summary>
        public int LastChecks { get; private set; }

        /// <summary>
        /// Searches from <paramref name="start"/> (a player-built block). On <see cref="SupportStatus.Disconnected"/>,
        /// <paramref name="component"/> holds every block of the unsupported component; running out of budget returns
        /// Unresolved with no partial component.
        /// </summary>
        public SupportStatus Analyze(BlockPos start, int maxChecks, List<BlockPos> component)
        {
            component.Clear();
            _search.Begin(start);
            _search.Step(maxChecks);
            LastChecks = _search.Checks;
            if (_search.Status == SupportStatus.Disconnected)
            {
                component.AddRange(_search.Component);
            }

            return _search.Status;
        }

        /// <summary>
        /// After <paramref name="removed"/> became air: analyses each player-built neighbour once (neighbours found in
        /// an already reported component are skipped) and returns disconnected components. Any unresolved neighbour
        /// makes the whole result unresolved.
        /// </summary>
        public SupportStatus AfterRemoval(BlockPos removed, int maxChecksPerNeighbour, List<List<BlockPos>> disconnected)
        {
            disconnected.Clear();
            var claimed = new HashSet<BlockPos>();
            SupportStatus overall = SupportStatus.Supported;
            foreach ((int dx, int dy, int dz) in SupportSearch.Faces)
            {
                var neighbour = new BlockPos(removed.X + dx, removed.Y + dy, removed.Z + dz);
                if (claimed.Contains(neighbour))
                {
                    continue;
                }

                var component = new List<BlockPos>();
                switch (Analyze(neighbour, maxChecksPerNeighbour, component))
                {
                    case SupportStatus.Disconnected:
                        disconnected.Add(component);
                        claimed.UnionWith(component);
                        overall = overall == SupportStatus.Unresolved ? overall : SupportStatus.Disconnected;
                        break;
                    case SupportStatus.Unresolved:
                        overall = SupportStatus.Unresolved;
                        break;
                }
            }

            return overall;
        }
    }
}
