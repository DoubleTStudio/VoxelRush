using System;
using System.Collections.Generic;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>DES-002: zone authorization (GAMEPLAY_RULES Zones) and its enforcement by the authority.</summary>
    public sealed class ZonePolicyTests
    {
        private static readonly WorldBox Everywhere = new WorldBox(-1000, -1000, -1000, 1000, 1000, 1000);

        private static EditRejection Check(ZoneKind kind, WorldRole role, ZoneAction action, bool visitorsCanBuild = false) =>
            new ZoneMap(new[] { new Zone(Everywhere, kind) }, visitorsCanBuild: visitorsCanBuild).Authorize(new BlockPos(0, 0, 0), role, action);

        [TestCase(ZoneKind.Sandbox, WorldRole.Owner, ZoneAction.Place, EditRejection.None)]
        [TestCase(ZoneKind.Sandbox, WorldRole.Editor, ZoneAction.Remove, EditRejection.None)]
        [TestCase(ZoneKind.Sandbox, WorldRole.Visitor, ZoneAction.Place, EditRejection.NotPermitted)]
        [TestCase(ZoneKind.Sandbox, WorldRole.Visitor, ZoneAction.Damage, EditRejection.None)]
        [TestCase(ZoneKind.RacePreparation, WorldRole.Editor, ZoneAction.Place, EditRejection.None)]
        [TestCase(ZoneKind.RacePreparation, WorldRole.Visitor, ZoneAction.Remove, EditRejection.NotPermitted)]
        [TestCase(ZoneKind.ActiveRace, WorldRole.Owner, ZoneAction.Place, EditRejection.RaceLocked)]
        [TestCase(ZoneKind.ActiveRace, WorldRole.Owner, ZoneAction.Damage, EditRejection.RaceLocked)]
        [TestCase(ZoneKind.RaceDestructible, WorldRole.Visitor, ZoneAction.Damage, EditRejection.None)]
        [TestCase(ZoneKind.RaceDestructible, WorldRole.Owner, ZoneAction.Place, EditRejection.RaceLocked)]
        [TestCase(ZoneKind.Protected, WorldRole.Owner, ZoneAction.Place, EditRejection.ProtectedZone)]
        [TestCase(ZoneKind.Protected, WorldRole.Owner, ZoneAction.Damage, EditRejection.ProtectedZone)]
        public void Matrix(ZoneKind kind, WorldRole role, ZoneAction action, EditRejection expected)
        {
            Assert.That(Check(kind, role, action), Is.EqualTo(expected));
        }

        [Test]
        public void PublicSandbox_LetsVisitorsBuild()
        {
            Assert.That(Check(ZoneKind.Sandbox, WorldRole.Visitor, ZoneAction.Place, visitorsCanBuild: true), Is.EqualTo(EditRejection.None));
            Assert.That(Check(ZoneKind.RacePreparation, WorldRole.Visitor, ZoneAction.Place, visitorsCanBuild: true), Is.EqualTo(EditRejection.NotPermitted), "track prep stays editor-only");
        }

        [Test]
        public void Overlap_ResolvesToHighestPrecedence()
        {
            var map = new ZoneMap(new[]
            {
                new Zone(new WorldBox(0, 0, 0, 100, 50, 100), ZoneKind.RacePreparation),
                new Zone(new WorldBox(10, 0, 10, 20, 50, 20), ZoneKind.Protected),
            });

            Assert.That(map.KindAt(new BlockPos(15, 1, 15)), Is.EqualTo(ZoneKind.Protected), "checkpoint inside the track area");
            Assert.That(map.KindAt(new BlockPos(50, 1, 50)), Is.EqualTo(ZoneKind.RacePreparation));
            Assert.That(map.KindAt(new BlockPos(500, 1, 500)), Is.EqualTo(ZoneKind.Sandbox), "default");
        }

        [Test]
        public void Validator_ReportsZoneReason_SameAsServer()
        {
            var zones = new ZoneMap(new[] { new Zone(Everywhere, ZoneKind.ActiveRace) });
            var validator = new EditValidator(zones);
            var world = new FlatWorld();

            Assert.That(validator.Validate(EditAction.Place, new BlockPos(2, 1, 2), BlockIds.Stone, 2.5f, 3, 2.5f, world, Array.Empty<WorldBox>(), WorldRole.Owner),
                Is.EqualTo(EditRejection.RaceLocked));
            Assert.That(EditValidator.Describe(EditRejection.RaceLocked), Does.Contain("locked"));
        }

        [Test]
        public void DoD_Authority_RejectsUnauthorizedEdits_AcceptsAuthorizedOnes()
        {
            var chunk = new ChunkCoord(0, 0, 0);
            var zones = new ZoneMap(new[]
            {
                new Zone(new WorldBox(0, 0, 0, 4, 16, 4), ZoneKind.Protected),          // spawn pad
                new Zone(new WorldBox(8, 0, 8, 16, 16, 16), ZoneKind.ActiveRace),      // live track
            }, visitorsCanBuild: false);
            var roles = new Dictionary<string, WorldRole> { ["owner"] = WorldRole.Owner, ["guest"] = WorldRole.Visitor };
            var authority = new WorldAuthority(new EditValidator(zones), roleOf: actor => roles.TryGetValue(actor, out WorldRole r) ? r : WorldRole.Visitor);
            authority.AddChunk(new ChunkData(chunk, 1, new byte[ChunkMath.Volume]));

            EditResult Place(string actor, int x, int z) =>
                authority.Submit(actor, new EditIntent(Guid.NewGuid(), chunk, new LocalBlock(x, 1, z).Index, authority.GetChunk(chunk)!.Revision, EditAction.Place, BlockIds.Stone),
                    x + 0.5f, 3, z + 0.5f, Array.Empty<WorldBox>(), 1, 0);

            Assert.That(Place("guest", 6, 6).Rejection, Is.EqualTo(EditRejection.NotPermitted), "visitor in a private sandbox");
            Assert.That(Place("owner", 1, 1).Rejection, Is.EqualTo(EditRejection.ProtectedZone), "even the owner can't edit the spawn pad");
            Assert.That(Place("owner", 10, 10).Rejection, Is.EqualTo(EditRejection.RaceLocked), "live track is locked");
            Assert.That(Place("mallory", 6, 6).Rejection, Is.EqualTo(EditRejection.NotPermitted), "unknown actors are visitors");
            Assert.That(Place("owner", 6, 6).Accepted, Is.True, "authorized edit in the sandbox");
            Assert.That(authority.GetChunk(chunk)!.Revision, Is.EqualTo(1), "only the authorized edit changed the world");
        }

        private sealed class FlatWorld : IEditWorld
        {
            public bool IsLoaded(BlockPos block) => true;

            public byte GetBlock(BlockPos block) => block.Y <= 0 ? BlockIds.Stone : BlockIds.Air;
        }
    }
}
