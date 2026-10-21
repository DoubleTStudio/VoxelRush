using VoxelRush.Core.Race;

namespace VoxelRush.Networking.Race
{
    /// <summary>
    /// The test arena's checkpoint course (Arena.prefab: start grid at z = −40, x −21..21, bumps and ramp in the middle).
    /// Gates span the whole grid width so every lane is valid. Authored tracks replace this with track editing.
    /// </summary>
    public static class ArenaTrack
    {
        public static TrackDefinition Create() => new TrackDefinition(1, new Vec3(0f, 1.5f, -40f), new[]
        {
            new Gate(1, 0f, 2f, 0f, 0f, 1f, 30f, 6f),
            new Gate(2, 0f, 2f, 150f, 0f, 1f, 30f, 6f),
            new Gate(3, 0f, 2f, 280f, 0f, 1f, 30f, 6f),
        });
    }
}
