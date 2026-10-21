using System.Text;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Networking.Vehicles;

namespace VoxelRush.Tests.EditMode
{
    /// <summary>COSM-001: the owner → server paint request decodes only bounded, checkable text.</summary>
    public sealed class PaintWireTests
    {
        private static PaintRequest Decode(byte[] bytes)
        {
            using var reader = new FastBufferReader(bytes, Allocator.Temp);
            reader.ReadNetworkSerializable(out PaintRequest request);
            return request;
        }

        [Test]
        public void PaintRequest_RoundTrips()
        {
            using var writer = new FastBufferWriter(64, Allocator.Temp);
            writer.WriteNetworkSerializable(new PaintRequest { Sku = "paint.chrome" });

            Assert.That(Decode(writer.ToArray()).Sku, Is.EqualTo("paint.chrome"));
        }

        [Test]
        public void OverlongRequests_AreNeverRead_AndAreMalformed()
        {
            var bytes = new byte[1 + 200];
            bytes[0] = 200;
            for (int i = 1; i < bytes.Length; i++)
            {
                bytes[i] = (byte)'a';
            }

            PaintRequest request = Decode(bytes);

            Assert.That(request.Sku, Is.EqualTo(PaintRequest.TooLong));
            Assert.That(PaintCatalog.IsWellFormed(request.Sku), Is.False);
        }

        [Test]
        public void NonAsciiBytes_DecodeToSomethingTheServerRefuses()
        {
            byte[] sku = Encoding.UTF8.GetBytes("paint.rouge-é");
            var bytes = new byte[1 + sku.Length];
            bytes[0] = (byte)sku.Length;
            sku.CopyTo(bytes, 1);

            Assert.That(PaintCatalog.IsWellFormed(Decode(bytes).Sku), Is.False);
        }
    }
}
