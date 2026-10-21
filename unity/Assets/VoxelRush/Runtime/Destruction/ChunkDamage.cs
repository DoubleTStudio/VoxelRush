using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.Destruction
{
    public enum DamageOutcome : byte
    {
        /// <summary>Air, unknown or indestructible: nothing changed.</summary>
        Ignored,
        Damaged,
        Destroyed,
    }

    public readonly struct DamageResult
    {
        public DamageResult(DamageOutcome outcome, ushort remainingHp)
        {
            Outcome = outcome;
            RemainingHp = remainingHp;
        }

        public DamageOutcome Outcome { get; }

        public ushort RemainingHp { get; }
    }

    /// <summary>
    /// Sparse per-chunk damage (DESTRUCTION HP): only damaged blocks have an entry; an undamaged block is at its
    /// material's max HP. Damage is an integer amount computed by the server (never a client value).
    /// </summary>
    public sealed class ChunkDamage
    {
        private readonly SortedDictionary<ushort, ushort> _hp = new SortedDictionary<ushort, ushort>();
        private readonly MaterialCatalog _materials;

        public ChunkDamage(MaterialCatalog materials) => _materials = materials;

        /// <summary>Blocks below full HP.</summary>
        public int DamagedCount => _hp.Count;

        public IEnumerable<KeyValuePair<ushort, ushort>> Entries => _hp;

        public ushort GetHp(ChunkData chunk, LocalBlock local)
        {
            byte id = chunk.Get(local);
            if (id == BlockIds.Air || !_materials.TryGet(id, out BlockMaterial material))
            {
                return 0;
            }

            return _hp.TryGetValue((ushort)local.Index, out ushort hp) ? hp : material.MaxHp;
        }

        /// <summary>
        /// Applies integer damage. At HP ≤ 0 the block becomes air (revision advances via <see cref="ChunkData.Set"/>)
        /// and its HP entry is dropped. Negative or zero damage is ignored (no healing through this path).
        /// </summary>
        public DamageResult Apply(ChunkData chunk, LocalBlock local, int damage)
        {
            byte id = chunk.Get(local);
            if (damage <= 0 || id == BlockIds.Air || !_materials.TryGet(id, out BlockMaterial material) || material.IsIndestructible)
            {
                return new DamageResult(DamageOutcome.Ignored, GetHp(chunk, local));
            }

            var index = (ushort)local.Index;
            int current = _hp.TryGetValue(index, out ushort hp) ? hp : material.MaxHp;
            int remaining = current - damage;
            if (remaining <= 0)
            {
                _hp.Remove(index);
                chunk.Set(local, BlockIds.Air);
                return new DamageResult(DamageOutcome.Destroyed, 0);
            }

            _hp[index] = (ushort)remaining;
            return new DamageResult(DamageOutcome.Damaged, (ushort)remaining);
        }

        /// <summary>A placed or replaced block starts undamaged.</summary>
        public void OnBlockReplaced(LocalBlock local) => _hp.Remove((ushort)local.Index);

        /// <summary>Restores persisted damage; entries for air or out-of-range HP are dropped.</summary>
        public void Restore(ChunkData chunk, IEnumerable<KeyValuePair<ushort, ushort>> entries)
        {
            _hp.Clear();
            foreach (KeyValuePair<ushort, ushort> entry in entries)
            {
                if (entry.Key >= ChunkMath.Volume)
                {
                    continue;
                }

                byte id = chunk.Get(LocalBlock.FromIndex(entry.Key));
                if (_materials.TryGet(id, out BlockMaterial m) && !m.IsIndestructible && entry.Value > 0 && entry.Value < m.MaxHp)
                {
                    _hp[entry.Key] = entry.Value;
                }
            }
        }
    }
}
