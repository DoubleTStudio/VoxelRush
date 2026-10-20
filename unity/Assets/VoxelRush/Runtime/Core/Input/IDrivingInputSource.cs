namespace VoxelRush.Core.Input
{
    /// <summary>Where a locally controlled car gets its intent each tick (touch HUD, bot script, replay).</summary>
    public interface IDrivingInputSource
    {
        DrivingInput ReadDrivingInput();
    }
}
