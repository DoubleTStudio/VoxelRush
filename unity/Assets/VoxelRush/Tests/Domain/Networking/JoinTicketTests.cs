using System;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Core.Networking;

namespace VoxelRush.Tests.Domain.Networking
{
    /// <summary>VR-080: join ticket signing and game-server verification (forgery, tampering, expiry, wrong world, replay).</summary>
    public sealed class JoinTicketTests
    {
        private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
        private static readonly Guid World = Guid.NewGuid();
        private const long Now = 1_800_000_000;

        private static byte[] Issue(Guid? world = null, long issued = Now, long ttl = 60, WorldAccessRole role = WorldAccessRole.Editor, byte[]? key = null) =>
            new JoinTicket(Guid.NewGuid(), Guid.NewGuid(), world ?? World, role, issued, issued + ttl).Sign(key ?? Key);

        [Test]
        public void ValidTicket_RoundTrips()
        {
            var original = new JoinTicket(Guid.NewGuid(), Guid.NewGuid(), World, WorldAccessRole.Owner, Now, Now + 60);
            byte[] bytes = original.Sign(Key);

            Assert.That(bytes.Length, Is.EqualTo(JoinTicket.Length));
            Assert.That(JoinTicket.TryVerify(bytes, Key, Now + 10, out JoinTicket t), Is.EqualTo(TicketRejection.None));
            Assert.That((t.TicketId, t.PlayerId, t.WorldId, t.Role, t.IssuedAt, t.ExpiresAt),
                Is.EqualTo((original.TicketId, original.PlayerId, original.WorldId, original.Role, original.IssuedAt, original.ExpiresAt)));
        }

        [Test]
        public void ForgedWithAnotherKey_IsRejected()
        {
            byte[] otherKey = Enumerable.Repeat((byte)9, 32).ToArray();

            Assert.That(JoinTicket.TryVerify(Issue(key: otherKey), Key, Now, out _), Is.EqualTo(TicketRejection.BadSignature));
        }

        [Test]
        public void AnyTamperedByte_IsRejected()
        {
            byte[] good = Issue(role: WorldAccessRole.Visitor);
            for (int i = 1; i < JoinTicket.Length; i++)
            {
                byte[] bad = (byte[])good.Clone();
                bad[i] ^= 0x01;   // e.g. flip visitor -> editor, extend expiry, swap player
                Assert.That(JoinTicket.TryVerify(bad, Key, Now, out _), Is.Not.EqualTo(TicketRejection.None), $"byte {i}");
            }
        }

        [Test]
        public void Expired_NotYetValid_AndMalformed()
        {
            Assert.That(JoinTicket.TryVerify(Issue(ttl: 60), Key, Now + 60 + 31, out _), Is.EqualTo(TicketRejection.Expired));
            Assert.That(JoinTicket.TryVerify(Issue(ttl: 60), Key, Now + 60 + 29, out _), Is.EqualTo(TicketRejection.None), "30 s clock skew");
            Assert.That(JoinTicket.TryVerify(Issue(issued: Now + 120), Key, Now, out _), Is.EqualTo(TicketRejection.NotYetValid));
            Assert.That(JoinTicket.TryVerify(new byte[10], Key, Now, out _), Is.EqualTo(TicketRejection.Malformed));
            Assert.That(JoinTicket.TryVerify(null, Key, Now, out _), Is.EqualTo(TicketRejection.Malformed));
            byte[] wrongVersion = Issue();
            wrongVersion[0] = 2;
            Assert.That(JoinTicket.TryVerify(wrongVersion, Key, Now, out _), Is.EqualTo(TicketRejection.Malformed));
        }

        [Test]
        public void ShortKeys_AreRefused()
        {
            Assert.Throws<ArgumentException>(() => new JoinTicket(Guid.NewGuid(), Guid.NewGuid(), World, WorldAccessRole.Visitor, Now, Now + 1).Sign(new byte[16]));
        }

        [Test]
        public void Verifier_RejectsReplay_AndOtherWorlds_ThenForgetsExpiredIds()
        {
            var verifier = new TicketVerifier(Key, World);
            byte[] ticket = Issue();

            Assert.That(verifier.Verify(ticket, Now, out _), Is.EqualTo(TicketRejection.None));
            Assert.That(verifier.Verify(ticket, Now + 1, out _), Is.EqualTo(TicketRejection.Replayed));
            Assert.That(verifier.Verify(Issue(world: Guid.NewGuid()), Now, out _), Is.EqualTo(TicketRejection.WrongWorld));
            Assert.That(verifier.Remembered, Is.EqualTo(1));

            verifier.Verify(Issue(issued: Now + 500), Now + 500, out _);
            Assert.That(verifier.Remembered, Is.EqualTo(1), "the first ticket's id is forgotten once it can't be valid anymore");
        }
    }
}
