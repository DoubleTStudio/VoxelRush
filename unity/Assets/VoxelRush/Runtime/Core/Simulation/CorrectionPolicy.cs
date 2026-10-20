namespace VoxelRush.Core.Simulation
{
    public enum CorrectionKind
    {
        None,
        Blend,
        Snap,
    }

    /// <summary>
    /// Decides how a predicting client reacts to server divergence (VEHICLE_NETWORKING: blend small errors,
    /// hard-correct severe ones). Thresholds in metres and degrees.
    /// </summary>
    public sealed class CorrectionPolicy
    {
        public CorrectionPolicy(float ignoreMeters = 0.05f, float snapMeters = 3f, float ignoreDegrees = 2f, float snapDegrees = 45f)
        {
            IgnoreMeters = ignoreMeters;
            SnapMeters = snapMeters;
            IgnoreDegrees = ignoreDegrees;
            SnapDegrees = snapDegrees;
        }

        public float IgnoreMeters { get; }

        public float SnapMeters { get; }

        public float IgnoreDegrees { get; }

        public float SnapDegrees { get; }

        public CorrectionKind Decide(float positionErrorMeters, float rotationErrorDegrees)
        {
            if (float.IsNaN(positionErrorMeters) || float.IsNaN(rotationErrorDegrees)
                || positionErrorMeters >= SnapMeters || rotationErrorDegrees >= SnapDegrees)
            {
                return CorrectionKind.Snap;
            }

            return positionErrorMeters <= IgnoreMeters && rotationErrorDegrees <= IgnoreDegrees
                ? CorrectionKind.None
                : CorrectionKind.Blend;
        }
    }
}
