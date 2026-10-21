using System;
using UnityEngine;

namespace VoxelRush.Presentation.Audio
{
    /// <summary>
    /// Procedural sound effects (AUDIO-001): every clip is synthesised once at startup from fixed seeds, so the game
    /// ships no audio assets and has no audio licensing to track. Mono 22.05 kHz keeps the whole bank under 300 KB.
    /// </summary>
    public static class SfxSynth
    {
        public const int SampleRate = 22050;
        public const float EngineLoopSeconds = 1f;
        public const float BoostLoopSeconds = 1f;
        public const float ImpactSeconds = 0.35f;
        public const float PlaceSeconds = 0.12f;
        public const float RemoveSeconds = 0.14f;
        public const float NopeSeconds = 0.3f;
        public const float CrunchSeconds = 0.28f;

        /// <summary>Firing fundamental of the engine loop at pitch 1.</summary>
        public const float EngineBaseHz = 60f;

        // Loops are generated this much longer and crossfaded into their start, so the noise layers wrap without a click.
        private const float LoopFadeSeconds = 0.05f;
        private const float TwoPi = Mathf.PI * 2f;

        public static int Length(float seconds) => Mathf.RoundToInt(seconds * SampleRate);

        /// <summary>Harmonic engine drone with low rumble; every partial completes whole cycles over the loop.</summary>
        public static float[] EngineLoop()
        {
            int n = Length(EngineLoopSeconds);
            var raw = new float[n + Length(LoopFadeSeconds)];
            var noise = new Noise(11);
            var weights = new float[9];
            for (int k = 1; k < weights.Length; k++)
            {
                weights[k] = 1f / Mathf.Pow(k, 0.9f);
            }

            float rumble = 0f;
            for (int i = 0; i < raw.Length; i++)
            {
                float phase = TwoPi * EngineBaseHz * i / SampleRate;
                float v = 0f;
                for (int k = 1; k < weights.Length; k++)
                {
                    v += Mathf.Sin(k * phase + k * k * 0.7f) * weights[k];
                }

                // Uneven firing: alternate cycles slightly louder.
                v *= 0.85f + 0.15f * Mathf.Sin(phase * 0.5f);
                rumble += 0.08f * (noise.Next() - rumble);
                raw[i] = v + rumble * 1.5f;
            }

            return Normalize(CrossfadeLoop(raw, n), 0.8f);
        }

        /// <summary>Turbo-ish hiss and fluttering whistle layered over the engine while boosting.</summary>
        public static float[] BoostLoop()
        {
            int n = Length(BoostLoopSeconds);
            var raw = new float[n + Length(LoopFadeSeconds)];
            var noise = new Noise(23);
            float low = 0f;
            for (int i = 0; i < raw.Length; i++)
            {
                float t = (float)i / SampleRate;
                float x = noise.Next();
                low += 0.15f * (x - low);
                float hiss = x - low;
                float whistle = Mathf.Sin(TwoPi * 880f * t + 0.6f * Mathf.Sin(TwoPi * 6f * t));
                raw[i] = hiss * 0.7f + whistle * 0.25f;
            }

            return Normalize(CrossfadeLoop(raw, n), 0.6f);
        }

        /// <summary>Low falling thump plus a short noise burst.</summary>
        public static float[] Impact()
        {
            var s = new float[Length(ImpactSeconds)];
            var noise = new Noise(37);
            float phase = 0f;
            float burst = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float t = (float)i / SampleRate;
                phase += TwoPi * (45f + 90f * Mathf.Exp(-t * 18f)) / SampleRate;
                burst += 0.25f * (noise.Next() - burst);
                s[i] = Attack(t, 0.002f) * (Mathf.Sin(phase) * Mathf.Exp(-t * 14f) + burst * 1.6f * Mathf.Exp(-t * 30f));
            }

            return Normalize(s, 0.95f);
        }

        /// <summary>Two rising notes: the server accepted a placed block.</summary>
        public static float[] Place()
        {
            var s = new float[Length(PlaceSeconds)];
            for (int i = 0; i < s.Length; i++)
            {
                float t = (float)i / SampleRate;
                bool second = t >= 0.05f;
                float local = second ? t - 0.05f : t;
                float hz = second ? 990f : 660f;
                float tone = Mathf.Sin(TwoPi * hz * local) + 0.2f * Mathf.Sin(TwoPi * 3f * hz * local);
                s[i] = tone * Attack(local, 0.003f) * Mathf.Exp(-local * 30f);
            }

            return Normalize(s, 0.6f);
        }

        /// <summary>Falling pop: the server accepted a removal.</summary>
        public static float[] Remove()
        {
            var s = new float[Length(RemoveSeconds)];
            var noise = new Noise(41);
            float phase = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float t = (float)i / SampleRate;
                phase += TwoPi * Mathf.Lerp(720f, 300f, t / RemoveSeconds) / SampleRate;
                float click = t < 0.01f ? noise.Next() * (1f - t / 0.01f) * 0.5f : 0f;
                s[i] = Attack(t, 0.002f) * Mathf.Sin(phase) * Mathf.Exp(-t * 20f) + click;
            }

            return Normalize(s, 0.6f);
        }

        /// <summary>Two low buzzes: the edit was refused.</summary>
        public static float[] Nope()
        {
            var s = new float[Length(NopeSeconds)];
            for (int i = 0; i < s.Length; i++)
            {
                float t = (float)i / SampleRate;
                float local = t < 0.15f ? t : t - 0.16f;
                if (local < 0f || local > 0.11f)
                {
                    continue;
                }

                float phase = TwoPi * 150f * local;
                // Soft square from odd partials.
                float buzz = Mathf.Sin(phase) + Mathf.Sin(3f * phase) / 3f + Mathf.Sin(5f * phase) / 5f + Mathf.Sin(7f * phase) / 7f;
                s[i] = buzz * Attack(local, 0.004f) * Mathf.Clamp01((0.11f - local) / 0.02f);
            }

            return Normalize(s, 0.6f);
        }

        /// <summary>Crackling band-limited noise over a thud: a block broke.</summary>
        public static float[] Crunch()
        {
            var s = new float[Length(CrunchSeconds)];
            var noise = new Noise(53);
            float fast = 0f;
            float slow = 0f;
            float grain = 1f;
            int hold = 0;
            for (int i = 0; i < s.Length; i++)
            {
                float t = (float)i / SampleRate;
                if (--hold <= 0)
                {
                    // Sample-and-hold amplitude reads as many small fragments.
                    hold = 60 + (int)(Mathf.Abs(noise.Next()) * 120f);
                    grain = 0.2f + 0.8f * Mathf.Abs(noise.Next());
                }

                float x = noise.Next();
                fast += 0.35f * (x - fast);
                slow += 0.03f * (x - slow);
                float thud = Mathf.Sin(TwoPi * 70f * t) * Mathf.Exp(-t * 25f);
                s[i] = Attack(t, 0.002f) * ((fast - slow) * 2.2f * grain * Mathf.Exp(-t * 12f) + thud * 0.6f);
            }

            return Normalize(s, 0.85f);
        }

        /// <summary>Wraps samples into a mono clip; the caller owns and destroys it.</summary>
        public static AudioClip ToClip(string name, float[] samples)
        {
            AudioClip clip = AudioClip.Create(name, samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        public static float Peak(float[] samples)
        {
            float peak = 0f;
            foreach (float v in samples)
            {
                peak = Math.Max(peak, Math.Abs(v));
            }

            return peak;
        }

        public static float Rms(float[] samples)
        {
            double sum = 0;
            foreach (float v in samples)
            {
                sum += v * v;
            }

            return samples.Length == 0 ? 0f : (float)Math.Sqrt(sum / samples.Length);
        }

        private static float Attack(float t, float seconds) => t >= seconds ? 1f : t / seconds;

        // Fades the tail past the loop length into the start, so sample n-1 runs on into sample 0.
        private static float[] CrossfadeLoop(float[] raw, int length)
        {
            int fade = raw.Length - length;
            var loop = new float[length];
            Array.Copy(raw, loop, length);
            for (int i = 0; i < fade; i++)
            {
                float w = (float)i / fade;
                loop[i] = raw[i] * w + raw[length + i] * (1f - w);
            }

            return loop;
        }

        private static float[] Normalize(float[] samples, float peak)
        {
            float max = Peak(samples);
            if (max > 1e-6f)
            {
                float gain = peak / max;
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] *= gain;
                }
            }

            return samples;
        }

        // Deterministic xorshift, so every device synthesises the same sounds.
        private struct Noise
        {
            private uint _state;

            public Noise(uint seed) => _state = seed * 2654435761u + 1u;

            public float Next()
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return _state / (float)uint.MaxValue * 2f - 1f;
            }
        }
    }
}
