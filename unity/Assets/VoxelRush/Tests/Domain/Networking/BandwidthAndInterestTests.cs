using System;
using NUnit.Framework;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Hosting;
using VoxelRush.Core.Networking;

namespace VoxelRush.Tests.Domain.Networking
{
    /// <summary>VR-072: snapshot interest tiers, bandwidth meter and transport simulation options.</summary>
    public sealed class BandwidthAndInterestTests
    {
        [TestCase(0f, false, 2)]
        [TestCase(79f, false, 2)]
        [TestCase(80f, false, 4)]
        [TestCase(199f, false, 4)]
        [TestCase(500f, false, 6)]
        [TestCase(500f, true, 2)]
        public void Interest_Tiers(float distance, bool owner, int everyTicks)
        {
            Assert.That(SnapshotInterest.EveryTicks(distance, owner), Is.EqualTo(everyTicks));
        }

        [Test]
        public void Interest_FarCarsGetAThird_AndStayUnderTheTeleportThreshold()
        {
            int near = 0, far = 0;
            for (uint tick = 0; tick < 300; tick++)
            {
                near += SnapshotInterest.ShouldSend(tick, 10, false) ? 1 : 0;
                far += SnapshotInterest.ShouldSend(tick, 400, false) ? 1 : 0;
            }

            Assert.That((near, far), Is.EqualTo((150, 50)));
            Assert.That(36f * SnapshotInterest.EveryTicks(400, false) / 30f, Is.LessThan(10f), "boosting car between far updates");
        }

        [Test]
        public void Meter_ReportsPerClientRates()
        {
            var meter = new BandwidthMeter();
            meter.Flush(0);
            meter.Record(1, 1000);
            meter.Record(1, 1000);
            meter.Record(2, 500);
            meter.Record(3, 0);

            (int clients, double mean, double max) = meter.Flush(2);

            Assert.That(clients, Is.EqualTo(2));
            Assert.That(max, Is.EqualTo(1000));
            Assert.That(mean, Is.EqualTo(625));
            Assert.That(meter.TotalBytes, Is.EqualTo(2500));
            Assert.That(meter.Flush(3).Clients, Is.Zero, "windows reset");
            Assert.That(meter.PeakClientBytesPerSecond, Is.EqualTo(1000));
        }

        [Test]
        public void TransportSimulation_Parses_AndRejectsBadValues()
        {
            Assert.That(TransportSimulationOptions.TryParse(new[] { "-simTransportLossPercent", "2", "-simTransportDelayMs", "25" }, out TransportSimulationOptions o, out _), Is.True);
            Assert.That((o.LossPercent, o.DelayMs, o.IsActive), Is.EqualTo((2, 25, true)));
            Assert.That(TransportSimulationOptions.TryParse(Array.Empty<string>(), out o, out _) && !o.IsActive, Is.True);
            Assert.That(TransportSimulationOptions.TryParse(new[] { "-simTransportLossPercent", "60" }, out _, out _), Is.False);
            Assert.That(TransportSimulationOptions.TryParse(new[] { "-simTransportLossPercent", "-1" }, out _, out _), Is.False);
            Assert.That(TransportSimulationOptions.TryParse(new[] { "-simTransportDelayMs" }, out _, out string error), Is.False);
            Assert.That(error, Does.Contain("requires"));
        }
    }
}
