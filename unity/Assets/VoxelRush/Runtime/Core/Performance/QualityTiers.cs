using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Performance
{
    public enum QualityTier : byte
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }

    /// <summary>What a tier changes (QUALITY_GATES: low tier 30 fps, high tier 60 fps when sustainable). Provisional until device-tested.</summary>
    public readonly struct QualityPreset
    {
        public QualityPreset(QualityTier tier, float renderScale, float shadowDistance, int targetFrameRate, int streamingRadiusChunks, int debrisPool, int msaa, int chunkCacheChunks, int trailParticles)
        {
            Tier = tier;
            RenderScale = renderScale;
            ShadowDistance = shadowDistance;
            TargetFrameRate = targetFrameRate;
            StreamingRadiusChunks = streamingRadiusChunks;
            DebrisPool = debrisPool;
            Msaa = msaa;
            ChunkCacheChunks = chunkCacheChunks;
            TrailParticles = trailParticles;
        }

        public QualityTier Tier { get; }

        public float RenderScale { get; }

        /// <summary>0 disables main-light shadows.</summary>
        public float ShadowDistance { get; }

        public int TargetFrameRate { get; }

        public int StreamingRadiusChunks { get; }

        public int DebrisPool { get; }

        public int Msaa { get; }

        /// <summary>VOXEL_ENGINE: chunk cache capped by device tier.</summary>
        public int ChunkCacheChunks { get; }

        /// <summary>Spark-trail particles per car (COSM-002); 0 turns trails off.</summary>
        public int TrailParticles { get; }

        public float FrameBudgetMs => 1000f / TargetFrameRate;
    }

    public static class QualityPresets
    {
        public static QualityPreset For(QualityTier tier) => tier switch
        {
            QualityTier.Low => new QualityPreset(QualityTier.Low, 0.7f, 0f, 30, 2, 64, 1, 256, 0),
            QualityTier.Medium => new QualityPreset(QualityTier.Medium, 0.85f, 30f, 30, 3, 128, 1, 512, 48),
            _ => new QualityPreset(QualityTier.High, 1f, 50f, 60, 4, 192, 2, 1024, 96),
        };
    }

    /// <summary>Hardware facts used to pick a starting tier (from SystemInfo on the device).</summary>
    public readonly struct DeviceProfile
    {
        public DeviceProfile(bool isMobile, int systemMemoryMb, int graphicsMemoryMb, int processorCount)
        {
            IsMobile = isMobile;
            SystemMemoryMb = systemMemoryMb;
            GraphicsMemoryMb = graphicsMemoryMb;
            ProcessorCount = processorCount;
        }

        public bool IsMobile { get; }

        public int SystemMemoryMb { get; }

        public int GraphicsMemoryMb { get; }

        public int ProcessorCount { get; }

        /// <summary>
        /// Starting tier. Thresholds are provisional (QUALITY_GATES) and replaced by the device benchmark table; the
        /// adaptive controller corrects a wrong guess downwards within seconds.
        /// </summary>
        public QualityTier Classify()
        {
            if (!IsMobile)
            {
                return QualityTier.High;
            }

            if (SystemMemoryMb < 3500 || ProcessorCount < 6)
            {
                return QualityTier.Low;
            }

            return SystemMemoryMb < 7000 ? QualityTier.Medium : QualityTier.High;
        }
    }

    /// <summary>
    /// Steps quality down when frames run over budget or the device heats up, and back up (never above the starting
    /// tier) after a long stable period. Hysteresis and a minimum interval stop it oscillating.
    /// </summary>
    public sealed class AdaptiveQuality
    {
        public const double WindowSeconds = 3;
        public const double MinSecondsBetweenChanges = 10;
        public const double StableSecondsToStepUp = 30;
        /// <summary>Android thermal headroom at or above this means throttling is imminent (1.0 = severe).</summary>
        public const float HotHeadroom = 0.85f;

        private readonly List<float> _window = new List<float>(256);
        private double _windowStart;
        private double _lastChange = double.NegativeInfinity;
        private double _stableSince;
        private bool _started;

        public AdaptiveQuality(QualityTier start)
        {
            Ceiling = start;
            Tier = start;
        }

        /// <summary>The device's classified tier; adaptation never goes above it.</summary>
        public QualityTier Ceiling { get; }

        public QualityTier Tier { get; private set; }

        public int Changes { get; private set; }

        /// <summary>
        /// Feeds one frame. <paramref name="thermalHeadroom"/> is the platform's thermal headroom (NaN if unknown).
        /// Returns true when <see cref="Tier"/> changed.
        /// </summary>
        public bool Frame(double now, float frameMs, float thermalHeadroom = float.NaN)
        {
            if (!_started)
            {
                _started = true;
                _windowStart = now;
                _stableSince = now;
            }

            if (!float.IsNaN(frameMs) && frameMs > 0)
            {
                _window.Add(frameMs);
            }

            bool hot = !float.IsNaN(thermalHeadroom) && thermalHeadroom >= HotHeadroom;
            if (hot && Tier > QualityTier.Low && now - _lastChange >= MinSecondsBetweenChanges)
            {
                return Change(Tier - 1, now);
            }

            if (now - _windowStart < WindowSeconds || _window.Count < 10)
            {
                return false;
            }

            float p95 = Percentile95();
            float budget = QualityPresets.For(Tier).FrameBudgetMs;
            _window.Clear();
            _windowStart = now;
            if (p95 > budget * 1.15f || hot)
            {
                _stableSince = now;
                return Tier > QualityTier.Low && now - _lastChange >= MinSecondsBetweenChanges && Change(Tier - 1, now);
            }

            // Step up only with clear headroom against the next tier's budget, after a long stable stretch.
            if (Tier < Ceiling && p95 < QualityPresets.For(Tier + 1).FrameBudgetMs * 0.7f)
            {
                if (now - _stableSince >= StableSecondsToStepUp && now - _lastChange >= MinSecondsBetweenChanges)
                {
                    return Change(Tier + 1, now);
                }
            }
            else
            {
                _stableSince = now;
            }

            return false;
        }

        private bool Change(QualityTier tier, double now)
        {
            Tier = tier;
            Changes++;
            _lastChange = now;
            _stableSince = now;
            _window.Clear();
            _windowStart = now;
            return true;
        }

        private float Percentile95()
        {
            _window.Sort();
            return _window[Math.Min(_window.Count - 1, (int)Math.Ceiling(_window.Count * 0.95) - 1)];
        }
    }
}
