using UnityEngine;

namespace VoxelRush.Presentation.Audio
{
    /// <summary>
    /// Fixed set of one-shot AudioSources created once; a new sound takes a free source or steals the one closest to
    /// finishing, and at most <c>perFrame</c> sounds start per frame.
    /// </summary>
    public sealed class SfxPool
    {
        public const float MinDistance = 3f;
        public const float MaxDistance = 60f;

        private readonly AudioSource[] _sources;
        private readonly float[] _endsAt;
        private readonly int _perFrame;
        private int _startedThisFrame;

        public SfxPool(Transform parent, int voices, int perFrame)
        {
            _perFrame = Mathf.Max(1, perFrame);
            _sources = new AudioSource[Mathf.Max(1, voices)];
            _endsAt = new float[_sources.Length];
            for (int i = 0; i < _sources.Length; i++)
            {
                var go = new GameObject("Sfx " + i);
                go.transform.SetParent(parent, false);
                AudioSource source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.dopplerLevel = 0f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = MinDistance;
                source.maxDistance = MaxDistance;
                source.priority = 128;
                _sources[i] = source;
                _endsAt[i] = float.NegativeInfinity;
            }
        }

        public int Voices => _sources.Length;

        public long Started { get; private set; }

        /// <summary>Sounds refused by the per-frame cap.</summary>
        public long Capped { get; private set; }

        public void BeginFrame() => _startedThisFrame = 0;

        /// <summary>Starts a sound; spatial ones play in 3D at <paramref name="position"/>, others are 2D.</summary>
        public bool Play(AudioClip clip, Vector3 position, float volume, float pitch, bool spatial, float now)
        {
            if (_startedThisFrame >= _perFrame)
            {
                Capped++;
                return false;
            }

            int pick = 0;
            for (int i = 1; i < _endsAt.Length; i++)
            {
                if (_endsAt[i] < _endsAt[pick])
                {
                    pick = i;
                }
            }

            AudioSource source = _sources[pick];
            source.Stop();
            source.transform.position = position;
            source.clip = clip;
            source.volume = Mathf.Clamp01(volume);
            source.pitch = pitch;
            source.spatialBlend = spatial ? 1f : 0f;
            source.Play();
            _endsAt[pick] = now + clip.length / Mathf.Max(0.05f, pitch);
            _startedThisFrame++;
            Started++;
            return true;
        }

        public void StopAll()
        {
            for (int i = 0; i < _sources.Length; i++)
            {
                if (_sources[i] != null)
                {
                    _sources[i].Stop();
                }

                _endsAt[i] = float.NegativeInfinity;
            }
        }
    }
}
