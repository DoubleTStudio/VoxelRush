using NUnit.Framework;
using VoxelRush.Core.Race;

namespace VoxelRush.Tests.Domain.Race
{
    /// <summary>RACE-002: ready-up starts a race only when every connected racer is ready.</summary>
    public sealed class RaceLobbyTests
    {
        [Test]
        public void StartsOnlyWhenAllConnectedRacersAreReady()
        {
            var lobby = new RaceLobby();
            Assert.That(lobby.ShouldStart(new string[0]), Is.False, "nobody connected");

            lobby.SetReady("a", true);
            Assert.That(lobby.ShouldStart(new[] { "a", "b" }), Is.False);
            lobby.SetReady("b", true);
            Assert.That(lobby.ShouldStart(new[] { "a", "b" }), Is.True);

            lobby.SetReady("b", false);
            Assert.That(lobby.ShouldStart(new[] { "a", "b" }), Is.False, "unready blocks the start");
        }

        [Test]
        public void SoloPlayer_CanStartAlone_AndALeaverDoesNotBlockTheRest()
        {
            var lobby = new RaceLobby();
            lobby.SetReady("solo", true);
            Assert.That(lobby.ShouldStart(new[] { "solo" }), Is.True);

            lobby.SetReady("b", true);
            lobby.Remove("solo");
            Assert.That(lobby.ShouldStart(new[] { "b" }), Is.True, "the one who left is no longer counted");
            Assert.That(lobby.ReadyCount, Is.EqualTo(1));
        }

        [Test]
        public void SetReady_ReportsChangesOnly()
        {
            var lobby = new RaceLobby();
            Assert.That(lobby.SetReady("a", true), Is.True);
            Assert.That(lobby.SetReady("a", true), Is.False);
            Assert.That(lobby.SetReady("a", false), Is.True);
            Assert.That(lobby.SetReady("a", false), Is.False);
        }

        [Test]
        public void LobbyMessage_RoundTrips_AndRejectsMalformed()
        {
            Assert.That(RaceLobbyMessage.TryDecode(new RaceLobbyMessage(2, 3).Encode(), out RaceLobbyMessage m), Is.True);
            Assert.That((m.Ready, m.Racers), Is.EqualTo(((byte)2, (byte)3)));
            Assert.That(RaceLobbyMessage.TryDecode(new byte[] { 4, 3 }, out _), Is.False, "more ready than racers");
            Assert.That(RaceLobbyMessage.TryDecode(new byte[] { 1 }, out _), Is.False);
            Assert.That(RaceLobbyMessage.TryDecode(null!, out _), Is.False);
        }
    }
}
