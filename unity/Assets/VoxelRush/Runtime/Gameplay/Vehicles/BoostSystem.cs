namespace VoxelRush.Gameplay.Vehicles
{
    /// <summary>
    /// Boost energy: drains while boosting, and after running dry (or releasing) waits a cooldown before regenerating.
    /// Deterministic given the same inputs and step sizes, so the server and a predicting client agree.
    /// </summary>
    public sealed class BoostSystem
    {
        private readonly float _capacity;
        private readonly float _regenPerSecond;
        private readonly float _cooldownSeconds;
        private float _cooldownRemaining;

        public BoostSystem(float capacitySeconds, float regenPerSecond, float cooldownSeconds)
        {
            _capacity = capacitySeconds;
            _regenPerSecond = regenPerSecond;
            _cooldownSeconds = cooldownSeconds;
            Energy = capacitySeconds;
        }

        /// <summary>Remaining boost, in seconds of boosting.</summary>
        public float Energy { get; private set; }

        public float Normalized => _capacity > 0 ? Energy / _capacity : 0f;

        public bool IsBoosting { get; private set; }

        /// <summary>Advances one step; returns whether boost applies during this step.</summary>
        public bool Step(bool wantsBoost, float deltaTime)
        {
            if (!(deltaTime > 0))
            {
                return IsBoosting;
            }

            bool wasBoosting = IsBoosting;
            IsBoosting = wantsBoost && Energy > 0 && (wasBoosting || _cooldownRemaining <= 0);
            if (IsBoosting)
            {
                Energy = Energy > deltaTime ? Energy - deltaTime : 0f;
                if (Energy <= 0)
                {
                    _cooldownRemaining = _cooldownSeconds;
                }

                return true;
            }

            if (wasBoosting)
            {
                _cooldownRemaining = _cooldownSeconds;
            }

            if (_cooldownRemaining > 0)
            {
                _cooldownRemaining = _cooldownRemaining > deltaTime ? _cooldownRemaining - deltaTime : 0f;
            }
            else
            {
                Energy = Energy + _regenPerSecond * deltaTime < _capacity ? Energy + _regenPerSecond * deltaTime : _capacity;
            }

            return false;
        }
    }
}
