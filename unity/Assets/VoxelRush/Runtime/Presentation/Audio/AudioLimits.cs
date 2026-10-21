using System;
using UnityEngine;
using VoxelRush.World.Editing;

namespace VoxelRush.Presentation.Audio
{
    /// <summary>Nearest-first voice selection without allocation (AUDIO-001 mobile voice budget).</summary>
    public static class VoicePicker
    {
        /// <summary>
        /// Writes the indices of up to <paramref name="max"/> smallest finite distances into <paramref name="picked"/>,
        /// nearest first (ties keep the lower index); returns how many were picked.
        /// </summary>
        public static int PickNearest(float[] sqrDistances, int count, int[] picked, int max)
        {
            int limit = Math.Min(max, picked.Length);
            int chosen = 0;
            for (int i = 0; i < count; i++)
            {
                float d = sqrDistances[i];
                if (float.IsNaN(d) || float.IsInfinity(d) || limit == 0)
                {
                    continue;
                }

                if (chosen == limit && d >= sqrDistances[picked[chosen - 1]])
                {
                    continue;
                }

                int slot = chosen < limit ? chosen++ : chosen - 1;
                while (slot > 0 && sqrDistances[picked[slot - 1]] > d)
                {
                    picked[slot] = picked[slot - 1];
                    slot--;
                }

                picked[slot] = i;
            }

            return chosen;
        }
    }

    /// <summary>Token bucket: at most <c>burst</c> at once, refilled at <c>perSecond</c>.</summary>
    public sealed class RateLimiter
    {
        private readonly float _perSecond;
        private readonly float _burst;
        private float _tokens;
        private float _last = float.NaN;

        public RateLimiter(float perSecond, float burst)
        {
            _perSecond = perSecond;
            _burst = Math.Max(1f, burst);
            _tokens = _burst;
        }

        public bool TryAcquire(float now)
        {
            if (!float.IsNaN(_last))
            {
                _tokens = Math.Min(_burst, _tokens + Math.Max(0f, now - _last) * _perSecond);
            }

            _last = now;
            if (_tokens < 1f)
            {
                return false;
            }

            _tokens -= 1f;
            return true;
        }
    }

    /// <summary>
    /// Hard-contact detection from velocity alone, so it works for the predicted own car and interpolated remote cars
    /// alike: a change against a lagging reference that braking, boost or drifting can't produce within ~0.1 s.
    /// </summary>
    public sealed class ImpactDetector
    {
        /// <summary>Steady braking or boost (≈15 m/s²) lags the reference by ≈1.5 m/s; a crash or hard landing is several times that.</summary>
        public const float Threshold = 4.5f;
        public const float FullScale = 14f;
        public const float CooldownSeconds = 0.3f;

        // Larger jumps are respawns, flips or snapshot catch-up, not contacts.
        public const float TeleportChange = 60f;
        private const float ReferenceSeconds = 0.1f;

        private Vector3 _reference;
        private bool _primed;
        private float _quietUntil = float.NegativeInfinity;

        public void Reset() => _primed = false;

        /// <summary>Feeds this frame's velocity; returns the hit strength in (0, 1], or 0 for no hit.</summary>
        public float Step(Vector3 velocity, float deltaTime, float now)
        {
            if (!_primed || !(deltaTime > 0f))
            {
                _reference = velocity;
                _primed = true;
                return 0f;
            }

            float change = (velocity - _reference).magnitude;
            if (change > TeleportChange || float.IsNaN(change))
            {
                _reference = velocity;
                return 0f;
            }

            _reference = Vector3.Lerp(_reference, velocity, 1f - Mathf.Exp(-deltaTime / ReferenceSeconds));
            if (change < Threshold || now < _quietUntil)
            {
                return 0f;
            }

            // One contact, one sound: restart the reference from the post-hit velocity.
            _reference = velocity;
            _quietUntil = now + CooldownSeconds;
            return 0.2f + 0.8f * Mathf.Clamp01((change - Threshold) / (FullScale - Threshold));
        }
    }

    public enum BuildSound
    {
        Place,
        Remove,
        Nope,
    }

    public static class BuildSounds
    {
        /// <summary>Any refusal (local pre-check or server) is a "nope"; accepted edits confirm with their own sound.</summary>
        public static BuildSound For(EditAction action, EditRejection rejection) =>
            rejection != EditRejection.None ? BuildSound.Nope : action == EditAction.Remove ? BuildSound.Remove : BuildSound.Place;
    }
}
