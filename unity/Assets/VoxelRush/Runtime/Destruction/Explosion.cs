using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Destruction
{
    /// <summary>DESTRUCTION Sources/Explosion: bounded radius, linear falloff, max block budget.</summary>
    public sealed class ExplosionModel
    {
        public ExplosionModel(float maxRadius = 4f, int maxDamage = 400, int maxBlocks = 256)
        {
            if (!(maxRadius > 0) || maxRadius > 8 || maxDamage < 1 || maxBlocks < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRadius));
            }

            MaxRadius = maxRadius;
            MaxDamage = maxDamage;
            MaxBlocks = maxBlocks;
        }

        public float MaxRadius { get; }

        public int MaxDamage { get; }

        /// <summary>Most blocks one explosion may damage; nearest blocks win.</summary>
        public int MaxBlocks { get; }

        /// <summary>Integer damage at distance <paramref name="distance"/>; at least 1 anywhere inside the radius.</summary>
        public static int Falloff(int damage, float distance, float radius) =>
            distance > radius ? 0 : Math.Max(1, (int)Math.Ceiling(damage * (1.0 - distance / radius)));
    }

    /// <summary>Per-chunk damage maps for the world (sparse; chunks without damage have no entry).</summary>
    public sealed class DamageLedger
    {
        private readonly Dictionary<ChunkCoord, ChunkDamage> _byChunk = new Dictionary<ChunkCoord, ChunkDamage>();
        private readonly MaterialCatalog _materials;

        public DamageLedger(MaterialCatalog materials) => _materials = materials;

        public MaterialCatalog Materials => _materials;

        public ChunkDamage For(ChunkCoord coord)
        {
            if (!_byChunk.TryGetValue(coord, out ChunkDamage? damage))
            {
                damage = new ChunkDamage(_materials);
                _byChunk[coord] = damage;
            }

            return damage;
        }

        public bool Forget(ChunkCoord coord) => _byChunk.Remove(coord);
    }

    public readonly struct ExplosionOutcome
    {
        public ExplosionOutcome(EditRejection rejection, int damaged, int destroyed, bool budgetHit, ServerCommitResult commit)
        {
            Rejection = rejection;
            Damaged = damaged;
            Destroyed = destroyed;
            BudgetHit = budgetHit;
            Commit = commit;
        }

        public EditRejection Rejection { get; }

        /// <summary>Blocks hit (damaged or destroyed), ≤ the model's budget.</summary>
        public int Damaged { get; }

        public int Destroyed { get; }

        public bool BudgetHit { get; }

        /// <summary>The journaled removals, one batch per chunk.</summary>
        public ServerCommitResult Commit { get; }
    }

    /// <summary>
    /// Server-side explosion: picks the nearest damageable blocks within the bounded radius (deterministic order,
    /// capped by the block budget), checks zones, then commits all removals as one multi-chunk operation. Nothing
    /// changes unless the commit can be made durable.
    /// </summary>
    public sealed class ExplosionResolver
    {
        private readonly ExplosionModel _model;
        private readonly ZoneMap _zones;
        private readonly DamageLedger _ledger;
        private readonly List<Candidate> _candidates = new List<Candidate>();

        public ExplosionResolver(ExplosionModel model, ZoneMap zones, DamageLedger ledger)
        {
            _model = model;
            _zones = zones;
            _ledger = ledger;
        }

        public ExplosionOutcome Explode(WorldAuthority world, Guid operationId, string actorId, float x, float y, float z, float radius, int damage, uint serverTick)
        {
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) || !IsFinite(radius) || radius <= 0 || damage <= 0)
            {
                return Rejected(EditRejection.InvalidIndex);
            }

            radius = Math.Min(radius, _model.MaxRadius);
            damage = Math.Min(damage, _model.MaxDamage);
            Collect(world, x, y, z, radius);
            _candidates.Sort();

            bool budgetHit = _candidates.Count > _model.MaxBlocks;
            int count = Math.Min(_candidates.Count, _model.MaxBlocks);

            // Plan first (no mutation), so a refused commit leaves HP and blocks untouched.
            var removals = new SortedDictionary<ChunkCoord, List<BlockChange>>(ChunkOrder.Instance);
            var hits = new List<(ChunkData Chunk, LocalBlock Local, int Amount)>(count);
            int destroyed = 0;
            for (int i = 0; i < count; i++)
            {
                Candidate c = _candidates[i];
                ChunkData chunk = world.GetChunk(ChunkMath.ToChunk(c.Block))!;
                LocalBlock local = ChunkMath.ToLocal(c.Block);
                int amount = ExplosionModel.Falloff(damage, c.Distance, radius);
                hits.Add((chunk, local, amount));
                if (_ledger.For(chunk.Coord).GetHp(chunk, local) <= amount)
                {
                    if (!removals.TryGetValue(chunk.Coord, out List<BlockChange>? list))
                    {
                        list = new List<BlockChange>();
                        removals[chunk.Coord] = list;
                    }

                    list.Add(new BlockChange((ushort)local.Index, BlockIds.Air));
                    destroyed++;
                }
            }

            var changes = new List<ChunkChanges>(removals.Count);
            foreach (KeyValuePair<ChunkCoord, List<BlockChange>> entry in removals)
            {
                changes.Add(new ChunkChanges(entry.Key, entry.Value));
            }

            ServerCommitResult commit = world.CommitServerChanges(operationId, actorId, changes, serverTick);
            if (!commit.Accepted)
            {
                return new ExplosionOutcome(commit.Rejection, 0, 0, budgetHit, commit);
            }

            if (!commit.IsDuplicate)
            {
                foreach ((ChunkData chunk, LocalBlock local, int amount) in hits)
                {
                    // Destroyed blocks are already air (committed above); this drops their HP entries or damages the rest.
                    ChunkDamage ledger = _ledger.For(chunk.Coord);
                    if (chunk.Get(local) == BlockIds.Air)
                    {
                        ledger.OnBlockReplaced(local);
                    }
                    else
                    {
                        ledger.Apply(chunk, local, amount, removeOnDestroy: false);
                    }
                }
            }

            return new ExplosionOutcome(EditRejection.None, count, destroyed, budgetHit, commit);
        }

        private void Collect(WorldAuthority world, float x, float y, float z, float radius)
        {
            _candidates.Clear();
            int r = (int)Math.Ceiling(radius);
            int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y), cz = (int)Math.Floor(z);
            float radiusSq = radius * radius;
            for (int bx = cx - r; bx <= cx + r; bx++)
            {
                for (int by = cy - r; by <= cy + r; by++)
                {
                    for (int bz = cz - r; bz <= cz + r; bz++)
                    {
                        float dx = bx + 0.5f - x, dy = by + 0.5f - y, dz = bz + 0.5f - z;
                        float distSq = dx * dx + dy * dy + dz * dz;
                        if (distSq > radiusSq)
                        {
                            continue;
                        }

                        var block = new BlockPos(bx, by, bz);
                        if (!world.IsLoaded(block)
                            || !_ledger.Materials.TryGet(world.GetBlock(block), out BlockMaterial material) || material.IsIndestructible
                            || _zones.Authorize(block, WorldRole.Visitor, ZoneAction.Damage) != EditRejection.None)
                        {
                            continue;
                        }

                        _candidates.Add(new Candidate(block, distSq, (float)Math.Sqrt(distSq)));
                    }
                }
            }
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static ExplosionOutcome Rejected(EditRejection rejection) =>
            new ExplosionOutcome(rejection, 0, 0, false, new ServerCommitResult(rejection, Array.Empty<ChunkMutationBatch>(), false));

        private readonly struct Candidate : IComparable<Candidate>
        {
            public Candidate(BlockPos block, float distSq, float distance)
            {
                Block = block;
                DistSq = distSq;
                Distance = distance;
            }

            public BlockPos Block { get; }

            public float DistSq { get; }

            public float Distance { get; }

            // Nearest first; ties broken by position so the budget cut is deterministic.
            public int CompareTo(Candidate other)
            {
                int c = DistSq.CompareTo(other.DistSq);
                if (c != 0)
                {
                    return c;
                }

                c = Block.Y.CompareTo(other.Block.Y);
                if (c != 0)
                {
                    return c;
                }

                c = Block.X.CompareTo(other.Block.X);
                return c != 0 ? c : Block.Z.CompareTo(other.Block.Z);
            }
        }

        private sealed class ChunkOrder : IComparer<ChunkCoord>
        {
            public static readonly ChunkOrder Instance = new ChunkOrder();

            public int Compare(ChunkCoord a, ChunkCoord b)
            {
                int c = a.X.CompareTo(b.X);
                if (c != 0)
                {
                    return c;
                }

                c = a.Y.CompareTo(b.Y);
                return c != 0 ? c : a.Z.CompareTo(b.Z);
            }
        }
    }
}
