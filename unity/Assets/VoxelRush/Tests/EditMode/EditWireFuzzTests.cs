using System;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Networking.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Tests.EditMode
{
    /// <summary>VR-082: the client → server edit message (most exposed decoder) never throws on hostile bytes.</summary>
    public sealed class EditWireFuzzTests
    {
        [Test]
        public void ResultCodeLimit_TracksTheEnum()
        {
            Assert.That((int)EditRejectionLimits.Max, Is.EqualTo(Enum.GetValues(typeof(EditRejection)).Cast<EditRejection>().Max(v => (int)v)), "update EditRejectionLimits.Max with new codes");
        }

        [Test]
        public void EditIntents_AndResults_RejectHostileBytesWithoutThrowing()
        {
            byte[] intent = EditWire.EncodeIntent(new EditIntent(Guid.NewGuid(), new ChunkCoord(-3, 0, 7), 4095, 12, EditAction.Place, BlockIds.Stone));
            byte[] result = EditWire.EncodeResult(Guid.NewGuid(), EditRejection.RaceLocked, 99);
            Assert.That(EditWire.TryDecodeIntent(intent, out _), Is.True);
            Assert.That(EditWire.TryDecodeResult(result, out _, out _, out _), Is.True);

            var rng = new Random(82);
            int accepted = 0;
            for (int i = 0; i < 20000; i++)
            {
                byte[] seed = i % 2 == 0 ? intent : result;
                byte[] b = (i % 3) switch
                {
                    0 => Enumerable.Range(0, rng.Next(0, 64)).Select(_ => (byte)rng.Next(256)).ToArray(),
                    1 => seed.Take(rng.Next(seed.Length + 1)).ToArray(),
                    _ => seed.Select(x => rng.Next(8) == 0 ? (byte)rng.Next(256) : x).ToArray(),
                };

                Assert.DoesNotThrow(() =>
                {
                    // Decoding is only framing; the authority validates index, action, revision and permissions.
                    if (EditWire.TryDecodeIntent(b, out _))
                    {
                        accepted++;
                    }

                    if (EditWire.TryDecodeResult(b, out _, out EditRejection rejection, out _))
                    {
                        Assert.That(EditValidator.ReasonCode(rejection), Is.Not.Empty, "every decodable code is describable");
                    }
                });
            }

            TestContext.WriteLine($"{accepted} mutated intents decoded");
        }
    }
}
