using VoxelRush.Gameplay.Vehicles;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>This process's own networked car, for presentation code that must not depend on Netcode types.</summary>
    public static class LocalPlayer
    {
        public static ArcadeVehicle? Vehicle { get; internal set; }
    }
}
