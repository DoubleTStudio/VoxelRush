using NUnit.Framework;
using UnityEngine;
using VoxelRush.Networking.Vehicles;

namespace VoxelRush.Tests.EditMode
{
    public sealed class SnapshotInterpolatorTests
    {
        [Test]
        public void Sample_InterpolatesBetweenSnapshots_AtDelayedTime()
        {
            var interp = new SnapshotInterpolator(delaySeconds: 0.1);
            // Server time == local time for simplicity.
            interp.Add(1.0, new Vector3(0, 0, 0), Quaternion.identity, 1.0);
            interp.Add(1.1, new Vector3(10, 0, 0), Quaternion.identity, 1.1);

            Assert.That(interp.TrySample(1.15, out Vector3 p, out _), Is.True);
            Assert.That(p.x, Is.EqualTo(5f).Within(1e-3f));
        }

        [Test]
        public void Add_OutOfOrderSnapshot_IsDropped()
        {
            var interp = new SnapshotInterpolator();
            interp.Add(2.0, Vector3.one, Quaternion.identity, 2.0);
            interp.Add(1.9, Vector3.zero, Quaternion.identity, 2.01);

            Assert.That(interp.Count, Is.EqualTo(1));
            Assert.That(interp.DroppedOutOfOrder, Is.EqualTo(1));
        }

        [Test]
        public void Sample_Teleport_SnapsInsteadOfSweeping()
        {
            var interp = new SnapshotInterpolator(delaySeconds: 0.1, teleportMeters: 10f);
            interp.Add(1.0, Vector3.zero, Quaternion.identity, 1.0);
            interp.Add(1.1, new Vector3(100, 0, 0), Quaternion.identity, 1.1);

            interp.TrySample(1.15, out Vector3 p, out _);
            Assert.That(p.x, Is.EqualTo(100f));
        }

        [Test]
        public void Sample_Starved_ExtrapolatesBrieflyThenHolds()
        {
            var interp = new SnapshotInterpolator(delaySeconds: 0.1);
            interp.Add(1.0, new Vector3(0, 0, 0), Quaternion.identity, 1.0);
            interp.Add(1.1, new Vector3(1, 0, 0), Quaternion.identity, 1.1);

            interp.TrySample(1.25, out Vector3 near, out _);
            interp.TrySample(5.0, out Vector3 far, out _);

            Assert.That(near.x, Is.EqualTo(1.5f).Within(1e-3f), "extrapolates along last velocity");
            Assert.That(far.x, Is.EqualTo(2f).Within(1e-3f), "caps extrapolation at 0.1 s");
        }

        [Test]
        public void SparseSnapshots_StretchTheDelay_SoTheTimelineStaysBetweenSnapshots()
        {
            // A distant car sent every 0.2 s (interest tier) with a 0.12 s base delay.
            var interp = new SnapshotInterpolator(delaySeconds: 0.12);
            for (int i = 0; i < 5; i++)
            {
                interp.Add(1.0 + i * 0.2, new Vector3(i * 4, 0, 0), Quaternion.identity, 1.0 + i * 0.2);
            }

            Assert.That(interp.EffectiveDelay(), Is.EqualTo(0.3).Within(1e-6));
            interp.TrySample(1.85, out Vector3 p, out _);
            Assert.That(p.x, Is.EqualTo(11f).Within(1e-3f), "interpolated at 1.55 s, not extrapolated");

            var dense = new SnapshotInterpolator(delaySeconds: 0.12);
            dense.Add(1.0, Vector3.zero, Quaternion.identity, 1.0);
            dense.Add(1.0667, Vector3.one, Quaternion.identity, 1.0667);
            Assert.That(dense.EffectiveDelay(), Is.EqualTo(0.12), "full-rate cars keep the configured delay");
        }

        [Test]
        public void Sample_Empty_ReturnsFalse()
        {
            Assert.That(new SnapshotInterpolator().TrySample(0, out _, out _), Is.False);
        }
    }
}
