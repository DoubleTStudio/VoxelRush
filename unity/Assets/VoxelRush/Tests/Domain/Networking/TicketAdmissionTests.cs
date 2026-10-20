using System;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Core;
using VoxelRush.Core.Networking;

namespace VoxelRush.Tests.Domain.Networking
{
    /// <summary>VR-080: a ticket-requiring game server admits only valid, unreplayed tickets for its world.</summary>
    public sealed class TicketAdmissionTests
    {
        private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        private static readonly Guid World = Guid.NewGuid();
        private long _now = 1_800_000_000;

        private ConnectionGate Gate() =>
            new ConnectionGate(8, SimulationConstants.ProtocolVersion, tickets: new TicketVerifier(Key, World), unixNow: () => _now);

        private byte[] Payload(Guid? player = null, Guid? world = null, WorldAccessRole role = WorldAccessRole.Editor, long ttl = 60, byte[]? key = null) =>
            ConnectionPayload.EncodeTicket(SimulationConstants.ProtocolVersion,
                new JoinTicket(Guid.NewGuid(), player ?? Guid.NewGuid(), world ?? World, role, _now, _now + ttl).Sign(key ?? Key));

        [Test]
        public void ValidTicket_Admits_WithThePlayersIdAndRole()
        {
            ConnectionGate gate = Gate();
            var player = Guid.NewGuid();

            Assert.That(gate.TryAdmit(1, Payload(player, role: WorldAccessRole.Visitor), 0), Is.EqualTo(ConnectionRejection.None));
            Assert.That(gate.SessionOf(1), Is.EqualTo(player));
            Assert.That(gate.RoleOf(1), Is.EqualTo(WorldAccessRole.Visitor));
            Assert.That(gate.RoleOf(99), Is.EqualTo(WorldAccessRole.Visitor), "unknown clients get the least privilege");
        }

        [Test]
        public void SessionPayload_IsRefused_WhenTicketsAreRequired()
        {
            Assert.That(Gate().TryAdmit(1, ConnectionPayload.Encode(SimulationConstants.ProtocolVersion, Guid.NewGuid()), 0), Is.EqualTo(ConnectionRejection.TicketRequired));
        }

        [Test]
        public void ForgedExpiredWrongWorldAndReplayedTickets_AreRefused_WithoutTakingSlots()
        {
            ConnectionGate gate = Gate();
            byte[] once = Payload();

            Assert.That(gate.TryAdmit(1, Payload(key: Enumerable.Repeat((byte)7, 32).ToArray()), 0), Is.EqualTo(ConnectionRejection.TicketInvalid));
            Assert.That(gate.TryAdmit(2, Payload(world: Guid.NewGuid()), 0), Is.EqualTo(ConnectionRejection.TicketWrongWorld));
            Assert.That(gate.TryAdmit(3, once, 0), Is.EqualTo(ConnectionRejection.None));
            Assert.That(gate.TryAdmit(4, once, 0), Is.EqualTo(ConnectionRejection.TicketReplayed), "a captured ticket can't be reused");
            byte[] old = Payload(ttl: 10);
            _now += 100;
            Assert.That(gate.TryAdmit(5, old, 0), Is.EqualTo(ConnectionRejection.TicketExpired));
            Assert.That(gate.Count, Is.EqualTo(1));
        }

        [Test]
        public void SamePlayer_OnTwoConnections_IsRefused_ButANewTicketAfterLeavingWorks()
        {
            ConnectionGate gate = Gate();
            var player = Guid.NewGuid();

            Assert.That(gate.TryAdmit(1, Payload(player), 0), Is.EqualTo(ConnectionRejection.None));
            Assert.That(gate.TryAdmit(2, Payload(player), 0), Is.EqualTo(ConnectionRejection.SessionInUse));
            gate.Release(1);
            Assert.That(gate.TryAdmit(3, Payload(player), 0), Is.EqualTo(ConnectionRejection.None), "reconnect with a fresh ticket");
            Assert.That(gate.SessionOf(3), Is.EqualTo(player), "same player id, so the race resumes");
        }

        [Test]
        public void DevelopmentServer_RefusesTickets_AndGivesSessionsEditorRole()
        {
            var gate = new ConnectionGate(8, SimulationConstants.ProtocolVersion);

            Assert.That(gate.TryAdmit(1, Payload(), 0), Is.EqualTo(ConnectionRejection.MalformedPayload));
            Assert.That(gate.TryAdmit(2, ConnectionPayload.Encode(SimulationConstants.ProtocolVersion, Guid.NewGuid()), 0), Is.EqualTo(ConnectionRejection.None));
            Assert.That(gate.RoleOf(2), Is.EqualTo(WorldAccessRole.Editor));
            Assert.That(ConnectionRejection.TicketReplayed.ToReasonCode(), Is.EqualTo("ticket_replayed"));
        }
    }
}
