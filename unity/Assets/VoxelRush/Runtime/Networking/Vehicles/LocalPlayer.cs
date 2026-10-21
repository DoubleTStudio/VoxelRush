using System;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Gameplay.Vehicles;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>This process's own networked car, for presentation code that must not depend on Netcode types.</summary>
    public static class LocalPlayer
    {
        private static readonly string?[] s_equipped = new string?[CosmeticCatalog.KindCount];

        public static ArcadeVehicle? Vehicle { get; internal set; }

        /// <summary>Raised with the slot whose equipped item changed, so a connected car resends it.</summary>
        public static event Action<CosmeticKind>? EquippedChanged;

        /// <summary>The player pressed the horn; the own car sends it to the server (COSM-002).</summary>
        public static event Action? HornPressed;

        /// <summary>What this player wants to show in a slot (null = nothing); the server decides what others see (COSM-001, COSM-002).</summary>
        public static string? Equipped(CosmeticKind kind) => CosmeticCatalog.IsValidKind(kind) ? s_equipped[(int)kind] : null;

        public static void Equip(CosmeticKind kind, string? sku)
        {
            if (!CosmeticCatalog.IsValidKind(kind))
            {
                return;
            }

            string? value = string.IsNullOrEmpty(sku) ? null : sku;
            if (value == s_equipped[(int)kind])
            {
                return;
            }

            s_equipped[(int)kind] = value;
            EquippedChanged?.Invoke(kind);
        }

        public static void Honk() => HornPressed?.Invoke();
    }
}
