namespace VoxelRush.Core
{
    /// <summary>Product-level simulation defaults from the architecture baseline.</summary>
    public static class SimulationConstants
    {
        /// <summary>Authoritative server tick rate in Hz.</summary>
        public const int ServerTickRate = 30;

        /// <summary>Player cap per dedicated server instance.</summary>
        public const int MaxPlayersPerServer = 8;

        /// <summary>Wire protocol version sent in every message header.</summary>
        public const ushort ProtocolVersion = 1;
    }
}
