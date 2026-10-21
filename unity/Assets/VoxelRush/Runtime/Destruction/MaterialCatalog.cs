using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.Destruction
{
    /// <summary>Material properties of one block type (GAMEPLAY_RULES Damage).</summary>
    public readonly struct BlockMaterial
    {
        /// <summary>Hit points meaning "cannot be damaged" (protected infrastructure).</summary>
        public const ushort Indestructible = ushort.MaxValue;

        public BlockMaterial(byte id, string name, ushort maxHp, bool opaque, bool buildable)
        {
            Id = id;
            Name = name;
            MaxHp = maxHp;
            Opaque = opaque;
            Buildable = buildable;
        }

        public byte Id { get; }

        public string Name { get; }

        public ushort MaxHp { get; }

        public bool Opaque { get; }

        /// <summary>Players may place it (protected infrastructure is world-authored only).</summary>
        public bool Buildable { get; }

        public bool IsIndestructible => MaxHp == Indestructible;
    }

    /// <summary>
    /// The block material table. Values named in GAMEPLAY_RULES are fixed; the rest are designer placeholders
    /// (marked) to be tuned with playtests. Lookups are by byte id and allocation-free.
    /// </summary>
    public sealed class MaterialCatalog
    {
        private readonly BlockMaterial?[] _byId = new BlockMaterial?[256];

        public MaterialCatalog(IEnumerable<BlockMaterial> materials)
        {
            foreach (BlockMaterial m in materials)
            {
                if (m.Id == BlockIds.Air)
                {
                    throw new ArgumentException("Air is not a material.", nameof(materials));
                }

                if (_byId[m.Id] != null)
                {
                    throw new ArgumentException($"Duplicate material id {m.Id}.", nameof(materials));
                }

                if (m.MaxHp == 0 || string.IsNullOrWhiteSpace(m.Name))
                {
                    throw new ArgumentException($"Material {m.Id} needs a name and positive HP.", nameof(materials));
                }

                _byId[m.Id] = m;
                Count++;
            }
        }

        public static MaterialCatalog Default { get; } = new MaterialCatalog(WithRamps(new[]
        {
            // GAMEPLAY_RULES: dirt 30, grass 35, wood 50, asphalt 100, stone 120, reinforced 300, protected infinite.
            new BlockMaterial(BlockIds.Grass, "Grass", 35, opaque: true, buildable: true),
            new BlockMaterial(BlockIds.Dirt, "Dirt", 30, true, true),
            new BlockMaterial(BlockIds.Stone, "Stone", 120, true, true),
            new BlockMaterial(BlockIds.Wood, "Wood", 50, true, true),
            new BlockMaterial(BlockIds.Asphalt, "Asphalt", 100, true, true),
            new BlockMaterial(BlockIds.Reinforced, "Reinforced", 300, true, true),
            new BlockMaterial(BlockIds.Protected, "Protected", BlockMaterial.Indestructible, true, buildable: false),
            // Placeholders (not in the spec), to complete the 12 MVP block types.
            new BlockMaterial(BlockIds.Brick, "Brick", 90, true, true),
            new BlockMaterial(BlockIds.Concrete, "Concrete", 150, true, true),
            new BlockMaterial(BlockIds.Glass, "Glass", 20, opaque: false, buildable: true),
            new BlockMaterial(BlockIds.Sand, "Sand", 25, true, true),
            new BlockMaterial(BlockIds.Metal, "Metal", 200, true, true),
        }));

        // BUILD-002 placeholders: concrete-like ramps, one entry per yaw so HP never depends on orientation.
        private static IEnumerable<BlockMaterial> WithRamps(IEnumerable<BlockMaterial> blocks)
        {
            foreach (BlockMaterial block in blocks)
            {
                yield return block;
            }

            for (int yaw = 0; yaw < BlockShapes.Yaws; yaw++)
            {
                yield return new BlockMaterial(BlockShapes.RampId(BlockShape.RampLow, yaw), "Low ramp", 60, true, true);
                yield return new BlockMaterial(BlockShapes.RampId(BlockShape.RampHigh, yaw), "High ramp", 100, true, true);
                yield return new BlockMaterial(BlockShapes.RampId(BlockShape.Kicker, yaw), "Kicker", 80, true, true);
            }
        }

        public int Count { get; }

        public bool TryGet(byte blockId, out BlockMaterial material)
        {
            BlockMaterial? found = _byId[blockId];
            material = found ?? default;
            return found.HasValue;
        }

        public BlockMaterial Get(byte blockId) =>
            _byId[blockId] ?? throw new KeyNotFoundException($"No material for block id {blockId}.");
    }
}
