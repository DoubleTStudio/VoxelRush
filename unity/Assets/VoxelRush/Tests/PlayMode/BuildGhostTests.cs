using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Presentation.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>BUILD-002: the phone's ghost preview, rotate, confirm and cancel, judged by the server's validator.</summary>
    public sealed class BuildGhostTests
    {
        private static readonly Color Green = new Color(0.2f, 0.85f, 0.35f);
        private static readonly Color Red = new Color(0.9f, 0.25f, 0.2f);

        private PhoneBuilder _builder = null!;
        private ClientWorldReplica _replica = null!;
        private Transform _car = null!;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return TestScenes.UnloadAll();
            SceneManager.SetActiveScene(SceneManager.CreateScene("BuildGhost"));
            var info = new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, new ChunkCoord(-1, -1, -1), new ChunkCoord(0, 0, 0));
            IBaseGenerator gen = info.CreateGenerator();
            _replica = new ClientWorldReplica(info);
            foreach (ChunkCoord c in info.RegionChunks())
            {
                Assert.That(_replica.ApplySnapshot(ChunkSnapshotMessage.Decode(ChunkSnapshotMessage.From(new ChunkData(c, gen.GeneratorVersion, gen.GenerateBlocks(c))).Encode())), Is.True);
            }

            // Car at (4.5, 0.6, 1.5): eye 1.5 m above it, as the server measures reach.
            _car = new GameObject("Car").transform;
            _car.position = new Vector3(4.5f, 0.6f, 1.5f);
            _builder = new GameObject("NetworkWorld").AddComponent<PhoneBuilder>();
            _builder.Preview(_replica, _car);
            _builder.SetMode(PhoneBuilder.Mode.Place);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            yield return TestScenes.UnloadAll();
        }

        [Test]
        public void Aim_ShowsAGreenRampGhost_RotateTurnsIt_ConfirmClearsIt()
        {
            _builder.SelectBlock(BlockIds.RampLow);
            Assert.That(_builder.AimAt(new BlockPos(4, 0, 4)), Is.EqualTo(EditRejection.None));

            Assert.That(_builder.GhostVisible, Is.True);
            Assert.That(_builder.GhostColor, Is.EqualTo(Green));
            Assert.That(_builder.GhostBlockType, Is.EqualTo(BlockIds.RampLow));
            Assert.That(_builder.GhostPosition.x, Is.EqualTo(4f).Within(0.05f));
            Assert.That(_builder.GhostPosition.z, Is.EqualTo(4f).Within(0.05f));
            Assert.That(_builder.Status, Does.Contain("PLACE"));

            _builder.Rotate();
            Assert.That(_builder.BlockType, Is.EqualTo(BlockShapes.RampId(BlockShape.RampLow, 1)));
            Assert.That(_builder.GhostBlockType, Is.EqualTo(BlockShapes.RampId(BlockShape.RampLow, 1)), "the ghost turns with the ramp");
            for (int i = 0; i < 3; i++)
            {
                _builder.Rotate();
            }

            Assert.That(_builder.BlockType, Is.EqualTo(BlockIds.RampLow), "four turns come back around");

            Assert.That(_builder.Confirm(), Is.EqualTo(EditRejection.None));
            Assert.That(_builder.Sent, Is.Zero, "previews never send");
            Assert.That(_builder.Target, Is.Null);
            Assert.That(_builder.GhostVisible, Is.False);
        }

        [Test]
        public void InvalidAim_IsRed_WithTheValidatorsReason_AndCannotBeConfirmed()
        {
            _builder.SelectBlock(BlockIds.Kicker);
            Assert.That(_builder.AimAt(new BlockPos(30, 0, 30)), Is.EqualTo(EditRejection.OutOfReach));
            Assert.That(_builder.GhostColor, Is.EqualTo(Red));
            Assert.That(_builder.Status, Is.EqualTo(EditValidator.Describe(EditRejection.OutOfReach)));
            Assert.That(_builder.Confirm(), Is.EqualTo(EditRejection.OutOfReach));
            Assert.That(_builder.Target, Is.Not.Null, "a refused confirm keeps the aim");

            Assert.That(_builder.AimAt(new BlockPos(4, -1, 4)), Is.EqualTo(EditRejection.Occupied), "the ground is already there");
            Assert.That(_builder.Status, Is.EqualTo(EditValidator.Describe(EditRejection.Occupied)));

            _builder.Cancel();
            Assert.That(_builder.GhostVisible, Is.False);
            Assert.That(_builder.Target, Is.Null);
        }

        [Test]
        public void RemoveMode_GhostsTheAimedBlocksOwnShape()
        {
            byte ramp = BlockShapes.RampId(BlockShape.RampHigh, 2);
            _replica.Get(new ChunkCoord(0, 0, 0))!.Set(new LocalBlock(4, 0, 4), ramp);
            _builder.SetMode(PhoneBuilder.Mode.Remove);

            Assert.That(_builder.AimAt(new BlockPos(4, 0, 4)), Is.EqualTo(EditRejection.None));
            Assert.That(_builder.GhostBlockType, Is.EqualTo(ramp));
            Assert.That(_builder.GhostColor, Is.Not.EqualTo(Red));
            Assert.That(_builder.AimAt(new BlockPos(4, 1, 4)), Is.EqualTo(EditRejection.NothingToRemove));
        }

        [UnityTest]
        public IEnumerator Tap_AimsThroughTheCamera_TappingTheGhostAgainConfirms()
        {
            var camera = new GameObject("Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(4.5f, 4f, 4.5f), Quaternion.LookRotation(Vector3.down, Vector3.forward));
            yield return null;
            var centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
            _builder.SelectBlock(BlockIds.Brick);

            Assert.That(_builder.TapAt(centre), Is.EqualTo(EditRejection.None));
            Assert.That(_builder.Target, Is.EqualTo(new BlockPos(4, 0, 4)), "on top of the ground block under the camera");
            Assert.That(_builder.GhostBlockType, Is.EqualTo(BlockIds.Brick));

            Assert.That(_builder.TapAt(centre), Is.EqualTo(EditRejection.None));
            Assert.That(_builder.Target, Is.Null, "second tap on the ghost confirmed it");
        }
    }
}
