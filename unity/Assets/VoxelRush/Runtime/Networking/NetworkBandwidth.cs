using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Networking
{
    /// <summary>Server-side meter of game payload bytes sent to each client (vehicle snapshots, world and race messages).</summary>
    public static class NetworkBandwidth
    {
        // Serialized VehicleSnapshot: 2 uint + 4 vectors/quaternion (13 floats) + 2 floats + bool.
        public const int VehicleSnapshotBytes = 4 + 4 + 12 + 16 + 12 + 12 + 4 + 4 + 1;

        public static BandwidthMeter Meter { get; } = new BandwidthMeter();
    }
}
