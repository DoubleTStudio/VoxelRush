namespace VoxelRush.Core
{
    /// <summary>Product-level simulation defaults from the architecture baseline.</summary>
    public static class SimulationConstants
    {
        /// <summary>Authoritative server tick rate in Hz.</summary>
        public const int ServerTickRate = 30;

        /// <summary>Player cap per dedicated server instance.</summary>
        public const int MaxPlayersPerServer = 8;

        /// <summary>Wire protocol version sent in every message header. 2: session id in the connection payload (VR-062); 3: payload kind + join tickets (VR-080); 4: paint on the car (COSM-001); 5: per-kind loadout and horn events on the car (COSM-002).</summary>
        public const ushort ProtocolVersion = 5;
    }
}
