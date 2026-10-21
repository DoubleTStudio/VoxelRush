using System;
using System.Collections.Generic;
using NUnit.Framework;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Tests.Domain.World
{
    public sealed class VoxelRaycastTests
    {
        private static Func<BlockPos, bool> Only(params BlockPos[] solid) => p => Array.IndexOf(solid, p) >= 0;

        [Test]
        public void AxisRay_HitsFirstSolidWithEntryNormal()
        {
            Assert.That(VoxelRaycast.Cast(0.5f, 0.5f, 0.5f, 1, 0, 0, 10, Only(new BlockPos(4, 0, 0), new BlockPos(6, 0, 0)), out VoxelHit hit), Is.True);
            Assert.That(hit.Block, Is.EqualTo(new BlockPos(4, 0, 0)));
            Assert.That((hit.NormalX, hit.NormalY, hit.NormalZ), Is.EqualTo((-1, 0, 0)));
            Assert.That(hit.Adjacent, Is.EqualTo(new BlockPos(3, 0, 0)), "place target is in front of the hit face");
            Assert.That(hit.Distance, Is.EqualTo(3.5f).Within(1e-4f));
        }

        [Test]
        public void DownwardRay_InNegativeCoordinates()
        {
            Assert.That(VoxelRaycast.Cast(-0.5f, 5.2f, -7.5f, 0, -1, 0, 10, Only(new BlockPos(-1, 0, -8)), out VoxelHit hit), Is.True);
            Assert.That(hit.Block, Is.EqualTo(new BlockPos(-1, 0, -8)));
            Assert.That(hit.NormalY, Is.EqualTo(1));
            Assert.That(hit.Adjacent, Is.EqualTo(new BlockPos(-1, 1, -8)));
        }

        [Test]
        public void DiagonalRay_VisitsEveryCrossedCellInOrder()
        {
            // z = x + 0.5 crosses (2,·,2) then (2,·,3); both must be visited, in that order.
            var visited = new List<BlockPos>();
            VoxelRaycast.Cast(0.25f, 0.5f, 0.75f, 1, 0, 1, 10, p => { visited.Add(p); return p.Equals(new BlockPos(2, 0, 3)); }, out VoxelHit hit);

            Assert.That(hit.Block, Is.EqualTo(new BlockPos(2, 0, 3)));
            Assert.That(visited.IndexOf(new BlockPos(2, 0, 2)), Is.GreaterThanOrEqualTo(0).And.LessThan(visited.IndexOf(new BlockPos(2, 0, 3))));
            for (int i = 1; i < visited.Count; i++)
            {
                int steps = Math.Abs(visited[i].X - visited[i - 1].X) + Math.Abs(visited[i].Y - visited[i - 1].Y) + Math.Abs(visited[i].Z - visited[i - 1].Z);
                Assert.That(steps, Is.EqualTo(1), "traversal moves one face-adjacent cell at a time");
            }
        }

        [Test]
        public void RayStartingInsideSolid_HitsImmediatelyWithZeroNormal()
        {
            Assert.That(VoxelRaycast.Cast(0.5f, 0.5f, 0.5f, 0, 0, 1, 10, Only(new BlockPos(0, 0, 0)), out VoxelHit hit), Is.True);
            Assert.That(hit.Distance, Is.Zero);
            Assert.That((hit.NormalX, hit.NormalY, hit.NormalZ), Is.EqualTo((0, 0, 0)));
        }

        [Test]
        public void Miss_BeyondMaxDistance_OrDegenerateDirection()
        {
            Assert.That(VoxelRaycast.Cast(0.5f, 0.5f, 0.5f, 1, 0, 0, 3, Only(new BlockPos(5, 0, 0)), out _), Is.False);
            Assert.That(VoxelRaycast.Cast(0.5f, 0.5f, 0.5f, 0, 0, 0, 10, _ => true, out _), Is.False);
            Assert.That(VoxelRaycast.Cast(float.NaN, 0, 0, 1, 0, 0, 10, _ => true, out _), Is.False);
        }

        [Test]
        public void Cast_IsBoundedEvenThroughEmptySpace()
        {
            int visited = 0;
            VoxelRaycast.Cast(0.5f, 0.5f, 0.5f, 1, 1, 1, 50, _ => { visited++; return false; }, out _);

            Assert.That(visited, Is.LessThanOrEqualTo(50 * 3 + 3));
        }
    }

    public sealed class EditValidatorTests
    {
        private sealed class World : IEditWorld
        {
            public readonly Dictionary<BlockPos, byte> Blocks = new Dictionary<BlockPos, byte>();
            public bool Loaded = true;

            public bool IsLoaded(BlockPos block) => Loaded;

            public byte GetBlock(BlockPos block) => Blocks.TryGetValue(block, out byte id) ? id : BlockIds.Air;
        }

        private static readonly WorldBox Spawn = new WorldBox(-8, -10, -8, 8, 50, 8);
        private static readonly IReadOnlyList<WorldBox> NoOccupants = Array.Empty<WorldBox>();
        private readonly EditValidator _validator = new EditValidator(new[] { Spawn });

        // NUnit reuses the fixture instance across tests: rebuild mutable state per test.
        private World _world = new World();

        [SetUp]
        public void ResetWorld() => _world = new World();

        private EditRejection Place(int x, int y, int z, float eyeX = 20, float eyeY = 2, float eyeZ = 20, IReadOnlyList<WorldBox>? occupants = null) =>
            _validator.Validate(EditAction.Place, new BlockPos(x, y, z), BlockIds.Stone, eyeX, eyeY, eyeZ, _world, occupants ?? NoOccupants);

        [Test]
        public void ValidPlacement_IsAccepted()
        {
            Assert.That(Place(22, 1, 20), Is.EqualTo(EditRejection.None));
        }

        [Test]
        public void Reach_IsMeasuredToTheBlockSurface_SixMetresDefault()
        {
            Assert.That(Place(26, 2, 20), Is.EqualTo(EditRejection.None), "6 m to the near face");
            Assert.That(Place(27, 2, 20), Is.EqualTo(EditRejection.OutOfReach));
        }

        [Test]
        public void ProtectedZone_RejectsEvenWhenInReach()
        {
            Assert.That(Place(7, 1, 7, eyeX: 9, eyeY: 2, eyeZ: 9), Is.EqualTo(EditRejection.ProtectedZone));
        }

        [Test]
        public void Occupied_RejectsPlacingIntoSolid()
        {
            _world.Blocks[new BlockPos(22, 1, 20)] = BlockIds.Dirt;

            Assert.That(Place(22, 1, 20), Is.EqualTo(EditRejection.Occupied));
        }

        [Test]
        public void Remove_RequiresASolidBlock()
        {
            Assert.That(_validator.Validate(EditAction.Remove, new BlockPos(22, 1, 20), 0, 20, 2, 20, _world, NoOccupants), Is.EqualTo(EditRejection.NothingToRemove));
            _world.Blocks[new BlockPos(22, 1, 20)] = BlockIds.Dirt;
            Assert.That(_validator.Validate(EditAction.Remove, new BlockPos(22, 1, 20), 0, 20, 2, 20, _world, NoOccupants), Is.EqualTo(EditRejection.None));
        }

        [Test]
        public void Placement_ThatWouldTrapAPlayer_IsRejected()
        {
            var car = new WorldBox(21.2f, 1f, 19f, 23f, 2.5f, 23f);

            Assert.That(Place(22, 1, 20, occupants: new[] { car }), Is.EqualTo(EditRejection.OverlapsPlayer));
            Assert.That(Place(22, 3, 20, occupants: new[] { car }), Is.EqualTo(EditRejection.None), "above the car is fine");
        }

        [Test]
        public void UnloadedChunk_IsRejected()
        {
            _world.Loaded = false;

            Assert.That(Place(22, 1, 20), Is.EqualTo(EditRejection.ChunkNotLoaded));
        }

        [Test]
        public void PlacingAir_IsInvalidBlockType()
        {
            Assert.That(_validator.Validate(EditAction.Place, new BlockPos(22, 1, 20), BlockIds.Air, 20, 2, 20, _world, NoOccupants), Is.EqualTo(EditRejection.InvalidBlockType));
        }

        [Test]
        public void CheckOrder_IsStable_ReachBeforeZone()
        {
            // Far away and protected: reach is reported, the same on client and server.
            Assert.That(Place(0, 1, 0, eyeX: 100, eyeY: 2, eyeZ: 100), Is.EqualTo(EditRejection.OutOfReach));
        }

        [Test]
        public void EveryRejection_HasMessageAndStableCode()
        {
            foreach (EditRejection rejection in Enum.GetValues(typeof(EditRejection)))
            {
                Assert.That(EditValidator.Describe(rejection), Is.Not.Empty);
                Assert.That(EditValidator.ReasonCode(rejection), Does.Match("^[a-z_]+$"));
            }

            Assert.That(EditValidator.ReasonCode(EditRejection.ProtectedZone), Is.EqualTo("protected_zone"));
        }
    }
}
