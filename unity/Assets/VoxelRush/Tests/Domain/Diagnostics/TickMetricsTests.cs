using System;
using NUnit.Framework;
using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Tests.Domain.Diagnostics
{
    public sealed class TickMetricsTests
    {
        [Test]
        public void Flush_ComputesNearestRankPercentilesAndRate()
        {
            var metrics = new TickMetrics(budgetMilliseconds: 33.3);
            for (int i = 1; i <= 100; i++)
            {
                metrics.Record(i, i / 30.0);
            }

            TickWindow window = metrics.Flush(100 / 30.0 + 1 / 30.0);

            Assert.That(window.Ticks, Is.EqualTo(100));
            Assert.That(window.P50, Is.EqualTo(50));
            Assert.That(window.P95, Is.EqualTo(95));
            Assert.That(window.P99, Is.EqualTo(99));
            Assert.That(window.Max, Is.EqualTo(100));
            Assert.That(window.Overruns, Is.EqualTo(67), "durations 34..100 exceed 33.3 ms");
            Assert.That(window.TicksPerSecond, Is.EqualTo(30).Within(0.5));
        }

        [Test]
        public void Flush_StartsANewWindow_ButKeepsTotals()
        {
            var metrics = new TickMetrics(10);
            metrics.Record(20, 0);
            metrics.Flush(1);
            metrics.Record(1, 1.5);

            TickWindow second = metrics.Flush(2);

            Assert.That(second.Ticks, Is.EqualTo(1));
            Assert.That(second.Overruns, Is.Zero);
            Assert.That(metrics.TotalTicks, Is.EqualTo(2));
            Assert.That(metrics.TotalOverruns, Is.EqualTo(1));
        }

        [Test]
        public void Record_BeyondCapacity_CountsButDoesNotGrow()
        {
            var metrics = new TickMetrics(10, capacity: 4);
            for (int i = 0; i < 10; i++)
            {
                metrics.Record(50, i);
            }

            TickWindow window = metrics.Flush(10);

            Assert.That(window.Ticks, Is.EqualTo(10));
            Assert.That(window.Overruns, Is.EqualTo(10));
            Assert.That(window.Max, Is.EqualTo(50));
        }

        [Test]
        public void Flush_EmptyWindow_IsZeros()
        {
            TickWindow window = new TickMetrics(10).Flush(5);

            Assert.That(window.Ticks, Is.Zero);
            Assert.That(window.P99, Is.Zero);
            Assert.That(window.TicksPerSecond, Is.Zero);
        }

        [Test]
        public void Constructor_RejectsInvalidBudget()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickMetrics(0));
        }
    }
}
