using NUnit.Framework;
using VoxelRush.Core.Performance;

namespace VoxelRush.Tests.Domain.Performance
{
    /// <summary>VR-071: tier presets, starting tier and adaptive step-down/up.</summary>
    public sealed class QualityTierTests
    {
        [Test]
        public void Presets_MatchQualityGateTargets()
        {
            Assert.That(QualityPresets.For(QualityTier.Low).TargetFrameRate, Is.EqualTo(30));
            Assert.That(QualityPresets.For(QualityTier.High).TargetFrameRate, Is.EqualTo(60));
            Assert.That(QualityPresets.For(QualityTier.Low).ShadowDistance, Is.Zero);
            Assert.That(QualityPresets.For(QualityTier.Low).RenderScale, Is.LessThan(QualityPresets.For(QualityTier.High).RenderScale));
            Assert.That(QualityPresets.For(QualityTier.Low).ChunkCacheChunks, Is.LessThan(QualityPresets.For(QualityTier.Medium).ChunkCacheChunks));
        }

        [TestCase(true, 2048, 4, QualityTier.Low)]
        [TestCase(true, 4096, 4, QualityTier.Low)]
        [TestCase(true, 4096, 8, QualityTier.Medium)]
        [TestCase(true, 8192, 8, QualityTier.High)]
        [TestCase(false, 2048, 2, QualityTier.High)]
        public void Classify(bool mobile, int memoryMb, int cores, QualityTier expected)
        {
            Assert.That(new DeviceProfile(mobile, memoryMb, 1024, cores).Classify(), Is.EqualTo(expected));
        }

        private static double Feed(AdaptiveQuality q, double from, double seconds, float frameMs, float thermal = float.NaN)
        {
            double t = from;
            for (; t < from + seconds; t += frameMs / 1000.0)
            {
                q.Frame(t, frameMs, thermal);
            }

            return t;
        }

        // COSM-002: spark trails are cut first on weak or hot devices.
        [Test]
        public void TrailParticles_AreOffAtLowTier_AndBoundedAbove()
        {
            Assert.That(QualityPresets.For(QualityTier.Low).TrailParticles, Is.Zero);
            Assert.That(QualityPresets.For(QualityTier.Medium).TrailParticles, Is.InRange(1, QualityPresets.For(QualityTier.High).TrailParticles));
            Assert.That(QualityPresets.For(QualityTier.High).TrailParticles, Is.LessThanOrEqualTo(128));
        }

        [Test]
        public void OverBudget_StepsDown_OneTierAtATime_WithMinimumInterval()
        {
            var q = new AdaptiveQuality(QualityTier.High);   // 60 fps budget 16.7 ms

            double t = Feed(q, 0, 4, 22f);
            Assert.That(q.Tier, Is.EqualTo(QualityTier.Medium), "p95 22 ms > 16.7 · 1.15");

            t = Feed(q, t, 5, 40f);
            Assert.That(q.Tier, Is.EqualTo(QualityTier.Medium), "no second drop within 10 s");

            Feed(q, t, 8, 40f);
            Assert.That(q.Tier, Is.EqualTo(QualityTier.Low));
            Assert.That(q.Changes, Is.EqualTo(2));
        }

        [Test]
        public void ThermalHeadroom_StepsDownImmediately_EvenWithFastFrames()
        {
            var q = new AdaptiveQuality(QualityTier.High);

            q.Frame(0, 8f, thermalHeadroom: 0.9f);

            Assert.That(q.Tier, Is.EqualTo(QualityTier.Medium));
        }

        [Test]
        public void StableHeadroom_StepsBackUp_ButNeverAboveTheCeiling()
        {
            var q = new AdaptiveQuality(QualityTier.Medium);
            double t = Feed(q, 0, 4, 45f);
            Assert.That(q.Tier, Is.EqualTo(QualityTier.Low));

            t = Feed(q, t, 20, 9f);
            Assert.That(q.Tier, Is.EqualTo(QualityTier.Low), "needs 30 s of stability");
            t = Feed(q, t, 15, 9f);
            Assert.That(q.Tier, Is.EqualTo(QualityTier.Medium));

            Feed(q, t, 60, 5f);
            Assert.That(q.Tier, Is.EqualTo(QualityTier.Medium), "ceiling = classified tier");
        }

        [Test]
        public void FrameStats_Percentiles()
        {
            var frames = new float[100];
            for (int i = 0; i < 100; i++)
            {
                frames[i] = i + 1;   // 1..100 ms
            }

            FrameStats s = FrameStats.Summarize(frames, 33.3f);

            Assert.That((s.Frames, s.P50, s.P95, s.P99, s.Max), Is.EqualTo((100, 50f, 95f, 99f, 100f)));
            Assert.That(s.OverBudgetPercent, Is.EqualTo(67f));
            Assert.That(s.MeanFps, Is.EqualTo(1000.0 / 50.5).Within(1e-9));
            Assert.That(FrameStats.Summarize(new float[0], 33f).Frames, Is.Zero);
        }

        [Test]
        public void SteadyOnBudget_StaysPut()
        {
            var q = new AdaptiveQuality(QualityTier.Low);
            Feed(q, 0, 60, 30f);

            Assert.That((q.Tier, q.Changes), Is.EqualTo((QualityTier.Low, 0)));
        }
    }
}
