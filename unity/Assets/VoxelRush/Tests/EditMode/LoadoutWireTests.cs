using System;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TestTools;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Networking.Vehicles;

namespace VoxelRush.Tests.EditMode
{
    /// <summary>COSM-001/002: the owner → server loadout request decodes only bounded, checkable text.</summary>
    public sealed class LoadoutWireTests
    {
        private static LoadoutRequest Decode(byte[] bytes)
        {
            using var reader = new FastBufferReader(bytes, Allocator.Temp);
            reader.ReadNetworkSerializable(out LoadoutRequest request);
            return request;
        }

        [TestCase(CosmeticKind.Paint, "paint.chrome")]
        [TestCase(CosmeticKind.Decal, "decal.lightning")]
        [TestCase(CosmeticKind.Trail, "trail.sparks")]
        [TestCase(CosmeticKind.Horn, "horn.retro")]
        [TestCase(CosmeticKind.Flag, "flag.checkered-gold")]
        [TestCase(CosmeticKind.Horn, "")]
        public void LoadoutRequest_RoundTrips(CosmeticKind kind, string sku)
        {
            using var writer = new FastBufferWriter(64, Allocator.Temp);
            writer.WriteNetworkSerializable(new LoadoutRequest { Kind = kind, Sku = sku });

            LoadoutRequest decoded = Decode(writer.ToArray());
            Assert.That((decoded.Kind, decoded.Sku), Is.EqualTo((kind, sku)));
        }

        [Test]
        public void OverlongRequests_AreNeverRead_AndAreMalformed()
        {
            var bytes = new byte[2 + 200];
            bytes[0] = (byte)CosmeticKind.Flag;
            bytes[1] = 200;
            for (int i = 2; i < bytes.Length; i++)
            {
                bytes[i] = (byte)'a';
            }

            LoadoutRequest request = Decode(bytes);

            Assert.That(request.Sku, Is.EqualTo(LoadoutRequest.TooLong));
            Assert.That(CosmeticCatalog.IsWellFormed(CosmeticKind.Flag, request.Sku), Is.False);
        }

        [Test]
        public void NonAsciiBytes_DecodeToSomethingTheServerRefuses()
        {
            byte[] sku = Encoding.UTF8.GetBytes("decal.éclair");
            var bytes = new byte[2 + sku.Length];
            bytes[0] = (byte)CosmeticKind.Decal;
            bytes[1] = (byte)sku.Length;
            sku.CopyTo(bytes, 2);

            Assert.That(CosmeticCatalog.IsWellFormed(CosmeticKind.Decal, Decode(bytes).Sku), Is.False);
        }

        [Test]
        public void OutOfRangeKinds_DecodeAndAreRefusedAsMalformed()
        {
            byte[] sku = Encoding.ASCII.GetBytes("horn.retro");
            var bytes = new byte[2 + sku.Length];
            bytes[0] = 200;
            bytes[1] = (byte)sku.Length;
            sku.CopyTo(bytes, 2);

            LoadoutRequest request = Decode(bytes);
            using LoadoutAuthority authority = LoadoutAuthority.Development();

            Assert.That(authority.Submit(1, Guid.Empty, request.Kind, request.Sku, 0), Is.EqualTo(CosmeticVerdict.Malformed));
        }

        [Test]
        public void TruncatedRequests_ThrowInsteadOfReadingPastTheBuffer()
        {
            // Length says 10 bytes, only 3 follow: NGO's bounds check must stop the read.
            var bytes = new byte[] { (byte)CosmeticKind.Horn, 10, (byte)'h', (byte)'o', (byte)'r' };

            Assert.That(() => Decode(bytes), Throws.Exception);
        }
    }

    /// <summary>COSM-002: the server relays horn presses only for an approved horn, at most once a second.</summary>
    public sealed class LoadoutServerHornTests
    {
        [Test]
        public void HornSpam_IsRelayedAtMostOncePerSecond_AndOnlyWithAHorn()
        {
            double now = 0;
            using var server = new LoadoutServer(LoadoutAuthority.Development(), new VehicleSpawner(), _ => Guid.Empty, () => now);
            int relayed = 0;

            // Rejections warn once per client.
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("vehicle_horn_rejected .*reason=no_horn"));
            Assert.That(server.OnHornPressed(2, hornShown: false, now, () => relayed++), Is.EqualTo(HornVerdict.NoHorn));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("vehicle_horn_rejected .*reason=rate_limited"));
            for (int i = 0; i < 25; i++)
            {
                now = i * 0.1;
                server.OnHornPressed(1, hornShown: true, now, () => relayed++);
            }

            Assert.That(relayed, Is.EqualTo(3), "presses at 0, 1 and 2 s");
            Assert.That(server.HornsRelayed, Is.EqualTo(3));
            Assert.That(server.HornsLimited, Is.EqualTo(22));

            // A client refused for one reason is still reported for the other.
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("vehicle_horn_rejected ownerClientId=1 reason=no_horn"));
            server.OnHornPressed(1, hornShown: false, now, () => relayed++);
        }

        [Test]
        public void ClientsCanOnlyAsk_TheServerAloneRelaysHorns()
        {
            foreach (System.Reflection.MethodInfo method in typeof(VehicleLoadout).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            {
                var rpc = (RpcAttribute?)Attribute.GetCustomAttribute(method, typeof(RpcAttribute));
                if (rpc == null)
                {
                    continue;
                }

                Assert.That(rpc.InvokePermission, Is.Not.EqualTo(RpcInvokePermission.Everyone), method.Name);
                if (method.Name == "HornRelayRpc")
                {
                    Assert.That(rpc.InvokePermission, Is.EqualTo(RpcInvokePermission.Server), "a client must not bypass the horn rate limit");
                }
                else
                {
                    Assert.That(rpc.InvokePermission, Is.EqualTo(RpcInvokePermission.Owner), method.Name);
                }
            }
        }
    }
}
