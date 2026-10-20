using System;
using System.Collections.Generic;
using NUnit.Framework;
using VoxelRush.Core.Networking;

namespace VoxelRush.Tests.Domain.Networking
{
    public sealed class ConnectionGateTests
    {
        private const ushort Version = 1;
        private static readonly byte[] ValidPayload = ConnectionPayload.Encode(Version);

        [Test]
        public void Payload_RoundTrips()
        {
            Assert.That(ConnectionPayload.TryDecode(ConnectionPayload.Encode(0xBEEF), out ushort version), Is.True);
            Assert.That(version, Is.EqualTo(0xBEEF));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        [TestCase(1024)]
        public void Payload_WrongLength_IsMalformed(int length)
        {
            Assert.That(ConnectionPayload.TryDecode(new byte[length], out _), Is.False);
        }

        [Test]
        public void Payload_Null_IsMalformed()
        {
            Assert.That(ConnectionPayload.TryDecode(null, out _), Is.False);
        }

        [Test]
        public void TryAdmit_ValidRequestsUpToCap_AreAdmitted()
        {
            var gate = new ConnectionGate(2, Version);

            Assert.That(gate.TryAdmit(1, ValidPayload, 0), Is.EqualTo(ConnectionRejection.None));
            Assert.That(gate.TryAdmit(2, ValidPayload, 0), Is.EqualTo(ConnectionRejection.None));
            Assert.That(gate.Count, Is.EqualTo(2));
        }

        [Test]
        public void TryAdmit_PendingApprovalsCountTowardCap()
        {
            var gate = new ConnectionGate(1, Version);

            gate.TryAdmit(1, ValidPayload, 0);

            // Client 1 is approved but not yet connected; client 2 must still be refused.
            Assert.That(gate.TryAdmit(2, ValidPayload, 0), Is.EqualTo(ConnectionRejection.ServerFull));
            Assert.That(gate.Count, Is.EqualTo(1));
        }

        [Test]
        public void TryAdmit_RejectsMalformedAndMismatchedPayloads_WithoutTakingSlots()
        {
            var gate = new ConnectionGate(8, Version);

            Assert.That(gate.TryAdmit(1, null, 0), Is.EqualTo(ConnectionRejection.MalformedPayload));
            Assert.That(gate.TryAdmit(2, new byte[] { 1, 0, 0 }, 0), Is.EqualTo(ConnectionRejection.MalformedPayload));
            Assert.That(gate.TryAdmit(3, ConnectionPayload.Encode(2), 0), Is.EqualTo(ConnectionRejection.ProtocolMismatch));
            Assert.That(gate.Count, Is.Zero);
        }

        [Test]
        public void TryAdmit_DuplicateClientId_KeepsSingleSlot()
        {
            var gate = new ConnectionGate(2, Version);

            gate.TryAdmit(7, ValidPayload, 0);
            Assert.That(gate.TryAdmit(7, ValidPayload, 1), Is.EqualTo(ConnectionRejection.None));
            Assert.That(gate.Count, Is.EqualTo(1));
        }

        [Test]
        public void Release_FreesSlotForNextClient()
        {
            var gate = new ConnectionGate(1, Version);
            gate.TryAdmit(1, ValidPayload, 0);

            Assert.That(gate.Release(1), Is.True);
            Assert.That(gate.Release(1), Is.False);
            Assert.That(gate.TryAdmit(2, ValidPayload, 0), Is.EqualTo(ConnectionRejection.None));
        }

        [Test]
        public void ExpirePending_FreesOnlyStaleUnconnectedSlots()
        {
            var gate = new ConnectionGate(3, Version, pendingTimeoutSeconds: 10);
            gate.TryAdmit(1, ValidPayload, 0);   // connects
            gate.TryAdmit(2, ValidPayload, 0);   // half-open, goes stale
            gate.TryAdmit(3, ValidPayload, 5);   // pending, still within timeout
            gate.MarkConnected(1);

            var expired = new List<ulong>();
            int count = gate.ExpirePending(10, expired);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(expired, Is.EqualTo(new ulong[] { 2 }));
            Assert.That(gate.Count, Is.EqualTo(2));
        }

        [Test]
        public void MarkConnected_AfterExpiry_ReportsMissingSlot()
        {
            var gate = new ConnectionGate(1, Version, pendingTimeoutSeconds: 10);
            gate.TryAdmit(1, ValidPayload, 0);
            gate.ExpirePending(10);

            // A late handshake must not occupy an untracked slot beyond the cap.
            Assert.That(gate.MarkConnected(1), Is.False);
            Assert.That(gate.Count, Is.Zero);
        }

        [Test]
        public void ConnectedCount_ExcludesPendingClients()
        {
            var gate = new ConnectionGate(3, Version);
            gate.TryAdmit(1, ValidPayload, 0);
            gate.TryAdmit(2, ValidPayload, 0);
            gate.MarkConnected(1);

            Assert.That(gate.Count, Is.EqualTo(2));
            Assert.That(gate.ConnectedCount, Is.EqualTo(1));
        }

        [Test]
        public void MarkConnected_AdmittedClient_ReturnsTrue()
        {
            var gate = new ConnectionGate(1, Version);
            gate.TryAdmit(1, ValidPayload, 0);

            Assert.That(gate.MarkConnected(1), Is.True);
        }

        [Test]
        public void ExpirePending_NothingStale_ReturnsZero()
        {
            var gate = new ConnectionGate(2, Version);
            gate.TryAdmit(1, ValidPayload, 0);

            Assert.That(gate.ExpirePending(1), Is.Zero);
        }

        [TestCase(0)]
        [TestCase(9)]
        public void Constructor_RejectsInvalidCap(int maxPlayers)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionGate(maxPlayers, Version));
        }

        [Test]
        public void ReasonCodes_AreStable()
        {
            Assert.That(ConnectionRejection.ServerFull.ToReasonCode(), Is.EqualTo("server_full"));
            Assert.That(ConnectionRejection.ProtocolMismatch.ToReasonCode(), Is.EqualTo("protocol_mismatch"));
            Assert.That(ConnectionRejection.MalformedPayload.ToReasonCode(), Is.EqualTo("malformed_payload"));
        }
    }
}
