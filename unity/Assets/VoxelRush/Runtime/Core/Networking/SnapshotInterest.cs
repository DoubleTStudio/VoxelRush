namespace VoxelRush.Core.Networking
{
    /// <summary>
    /// Distance-based snapshot rates for remote cars (VR-072 interest management): what a client sees close up gets
    /// the full rate; distant cars update less often. A car's owner always gets every snapshot (reconciliation).
    /// Rates stay within what interpolation absorbs: at 36 m/s boost a far car moves 7.2 m between updates, under
    /// the 10 m teleport threshold.
    /// </summary>
    public static class SnapshotInterest
    {
        public const int BaseEveryTicks = 2;
        public const float NearMeters = 80f;
        public const float MidMeters = 200f;

        public static int EveryTicks(float distanceMeters, bool isOwner) =>
            isOwner || distanceMeters < NearMeters ? BaseEveryTicks : distanceMeters < MidMeters ? BaseEveryTicks * 2 : BaseEveryTicks * 3;

        public static bool ShouldSend(uint tick, float distanceMeters, bool isOwner) => tick % (uint)EveryTicks(distanceMeters, isOwner) == 0;
    }
}
