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
    /// Bounded support search over player-built blocks (DESTRUCTION Structure). Natural terrain is exempt from
    /// collapse and acts as an anchor. Deletions need a fresh search (union-find can't split components), so after
    /// a removal each player-built neighbour's component is searched until it reaches an anchor or is exhausted.
    /// </summary>
    public sealed class SupportAnalyzer
    {
        private static readonly (int X, int Y, int Z)[] Faces = { (0, -1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0) };

        private readonly IStructureWorld _world;
        private readonly HashSet<BlockPos> _visited = new HashSet<BlockPos>();
        private readonly Queue<BlockPos> _frontier = new Queue<BlockPos>();

        public SupportAnalyzer(IStructureWorld world) => _world = world;

        /// <summary>Blocks examined by the last <see cref="Analyze"/> call (for per-tick budgets).</summary>
        public int LastChecks { get; private set; }

        /// <summary>
        /// Searches from <paramref name="start"/> (a player-built block). On <see cref="SupportStatus.Disconnected"/>,
        /// <paramref name="component"/> holds every block of the unsupported component.
        /// </summary>
        public SupportStatus Analyze(BlockPos start, int maxChecks, List<BlockPos> component)
        {
            component.Clear();
            _visited.Clear();
            _frontier.Clear();
            LastChecks = 0;

            if (!_world.TryGetBlock(start, out byte type, out bool built) || type == BlockIds.Air || !built)
            {
                return SupportStatus.Supported;
            }

            _visited.Add(start);
            _frontier.Enqueue(start);
            while (_frontier.Count > 0)
            {
                if (LastChecks >= maxChecks)
                {
                    component.Clear();
                    return SupportStatus.Unresolved;
                }

                BlockPos block = _frontier.Dequeue();
                LastChecks++;
                component.Add(block);
                foreach ((int dx, int dy, int dz) in Faces)
                {
                    var next = new BlockPos(block.X + dx, block.Y + dy, block.Z + dz);
                    if (_visited.Contains(next))
                    {
                        continue;
                    }

                    if (!_world.TryGetBlock(next, out byte nextType, out bool nextBuilt))
                    {
                        component.Clear();
                        return SupportStatus.Supported;
                    }

                    if (nextType == BlockIds.Air)
                    {
                        continue;
                    }

                    if (!nextBuilt)
                    {
                        component.Clear();
                        return SupportStatus.Supported;
                    }

                    _visited.Add(next);
                    _frontier.Enqueue(next);
                }
            }

            return SupportStatus.Disconnected;
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
            foreach ((int dx, int dy, int dz) in Faces)
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
