using UnityEngine;

namespace VoxelRush.Presentation.Audio
{
    /// <summary>Maps a car's speed and throttle to the engine loop's pitch and volume (AUDIO-001).</summary>
    public static class EngineSound
    {
        public const float IdlePitch = 0.55f;
        public const float TopPitch = 1.9f;
        public const float ThrottleLift = 0.12f;

        // Boost runs past the tuned top speed; the pitch keeps rising until this multiple.
        public const float OverSpeed = 1.3f;

        /// <summary>Rises with speed and never dips (no simulated gear changes), lifted a little by throttle.</summary>
        public static float Pitch(float speed, float maxSpeed, float throttle) =>
            IdlePitch + (TopPitch - IdlePitch) * Mathf.Sqrt(Normalized(speed, maxSpeed)) + ThrottleLift * Mathf.Clamp01(throttle);

        public static float Volume(float speed, float maxSpeed, float throttle) =>
            Mathf.Min(1f, 0.35f + 0.3f * Normalized(speed, maxSpeed) + 0.3f * Mathf.Clamp01(throttle));

        /// <summary>Remote cars send no input: forward acceleration stands in for throttle.</summary>
        public static float EstimateThrottle(float forwardAcceleration, float speed)
        {
            float pushing = Mathf.Clamp01(forwardAcceleration / 6f);
            return Mathf.Abs(speed) > 0.5f ? Mathf.Max(0.2f, pushing) : pushing;
        }

        private static float Normalized(float speed, float maxSpeed) =>
            maxSpeed > 0f && !float.IsNaN(speed) ? Mathf.Clamp(Mathf.Abs(speed) / maxSpeed, 0f, OverSpeed) : 0f;
    }
}
