using System;

namespace VoxelRush.Core.Input
{
    /// <summary>Player-adjustable touch control settings (CONTROLS_ASSETS: adjustable button size and sensitivity).</summary>
    public sealed class ControlSettings
    {
        public const float MinSensitivity = 0.5f;
        public const float MaxSensitivity = 2f;
        public const float MinButtonScale = 0.75f;
        public const float MaxButtonScale = 1.5f;

        // Seconds from centre to full lock at sensitivity 1; return to centre is twice as fast.
        private const float BaseTurnSeconds = 0.25f;

        public static ControlSettings Default { get; } = new ControlSettings(1f, 1f);

        public ControlSettings(float sensitivity, float buttonScale)
        {
            if (float.IsNaN(sensitivity) || sensitivity < MinSensitivity || sensitivity > MaxSensitivity)
            {
                throw new ArgumentOutOfRangeException(nameof(sensitivity));
            }

            if (float.IsNaN(buttonScale) || buttonScale < MinButtonScale || buttonScale > MaxButtonScale)
            {
                throw new ArgumentOutOfRangeException(nameof(buttonScale));
            }

            Sensitivity = sensitivity;
            ButtonScale = buttonScale;
        }

        public float Sensitivity { get; }

        public float ButtonScale { get; }

        public SteeringFilter CreateSteeringFilter()
        {
            float turnRate = Sensitivity / BaseTurnSeconds;
            return new SteeringFilter(turnRate, turnRate * 2f);
        }
    }
}
