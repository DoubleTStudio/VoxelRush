namespace VoxelRush.Gameplay.Vehicles
{
    /// <summary>Requests a reset after the car stays upside-down/on its side and nearly stationary (GAMEPLAY_RULES: reset when stuck).</summary>
    public sealed class FlipRecovery
    {
        // Upright below this dot(up, worldUp) counts as flipped (~70° tilt).
        public const float UprightThreshold = 0.35f;
        public const float StuckSpeed = 1.5f;

        private readonly float _resetSeconds;
        private float _stuckSeconds;

        public FlipRecovery(float resetSeconds) => _resetSeconds = resetSeconds;

        public bool Step(float uprightDot, float speed, float deltaTime)
        {
            bool stuck = uprightDot < UprightThreshold && speed < StuckSpeed;
            _stuckSeconds = stuck && deltaTime > 0 ? _stuckSeconds + deltaTime : 0f;
            if (_stuckSeconds < _resetSeconds)
            {
                return false;
            }

            _stuckSeconds = 0f;
            return true;
        }
    }
}
