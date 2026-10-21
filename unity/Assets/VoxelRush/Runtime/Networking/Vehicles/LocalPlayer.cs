using System;
using VoxelRush.Gameplay.Vehicles;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>This process's own networked car, for presentation code that must not depend on Netcode types.</summary>
    public static class LocalPlayer
    {
        private static string? s_equippedPaint;

        public static ArcadeVehicle? Vehicle { get; internal set; }

        /// <summary>The paint this player wants to show (null = default); the server decides what others see (COSM-001).</summary>
        public static string? EquippedPaint
        {
            get => s_equippedPaint;
            set
            {
                string? sku = string.IsNullOrEmpty(value) ? null : value;
                if (sku == s_equippedPaint)
                {
                    return;
                }

                s_equippedPaint = sku;
                EquippedPaintChanged?.Invoke();
            }
        }

        /// <summary>Raised when <see cref="EquippedPaint"/> changes, so a connected car resends it.</summary>
        public static event Action? EquippedPaintChanged;
    }
}
