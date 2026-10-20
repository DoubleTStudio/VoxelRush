#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Presentation.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>VR-030 DoD: the ghost and feedback label explain invalid placements with the server's rules.</summary>
    public sealed class BuildTargetingTests
    {
        private BuildTargeting _targeting = null!;
        private VoxelTerrainView _world = null!;
        private Camera _camera = null!;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return TestScenes.UnloadAll();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/VoxelRush/Scenes/VoxelPreview.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            _targeting = Object.FindAnyObjectByType<BuildTargeting>();
            _world = Object.FindAnyObjectByType<VoxelTerrainView>();
            _camera = Camera.main!;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => TestScenes.UnloadAll();

        [Test]
        public void LookingDownWithinReach_IsValidAndGreen()
        {
            int surface = AimDown(20, 20, heightAboveSurface: 4);

            Assert.That(_targeting.HasTarget, Is.True);
            Assert.That(_targeting.Target, Is.EqualTo(new BlockPos(20, surface + 1, 20)), "place goes on top of the surface block");
            Assert.That(_targeting.Rejection, Is.EqualTo(EditRejection.None));
            Assert.That(_targeting.FeedbackText, Is.EqualTo("Place block"));
            Assert.That(_targeting.GhostVisible, Is.True);
            Assert.That(_targeting.GhostColor.g, Is.GreaterThan(_targeting.GhostColor.r), "valid ghost is green");
        }

        [Test]
        public void TooFar_ExplainsReach()
        {
            AimDown(20, 20, heightAboveSurface: 12);

            Assert.That(_targeting.Rejection, Is.EqualTo(EditRejection.OutOfReach));
            Assert.That(_targeting.FeedbackText, Is.EqualTo(EditValidator.Describe(EditRejection.OutOfReach)));
            Assert.That(_targeting.GhostColor.r, Is.GreaterThan(_targeting.GhostColor.g), "invalid ghost is red");
        }

        [Test]
        public void ProtectedSpawn_ExplainsProtection()
        {
            AimDown(8, 8, heightAboveSurface: 4);

            Assert.That(_targeting.Rejection, Is.EqualTo(EditRejection.ProtectedZone));
            Assert.That(_targeting.FeedbackText, Does.StartWith("Protected area"));
            Assert.That(_targeting.GhostColor.r, Is.GreaterThan(_targeting.GhostColor.g));
        }

        [Test]
        public void RemoveMode_TargetsTheHitBlock()
        {
            _targeting.Action = EditAction.Remove;
            int surface = AimDown(-20, 30, heightAboveSurface: 4);

            Assert.That(_targeting.Target, Is.EqualTo(new BlockPos(-20, surface, 30)));
            Assert.That(_targeting.Rejection, Is.EqualTo(EditRejection.None));
            Assert.That(_targeting.FeedbackText, Is.EqualTo("Remove block"));
        }

        [Test]
        public void AimingAtSky_HidesGhostAndPrompts()
        {
            _camera.transform.SetPositionAndRotation(new Vector3(20.5f, 60f, 20.5f), Quaternion.Euler(-90f, 0f, 0f));
            _targeting.Refresh();

            Assert.That(_targeting.HasTarget, Is.False);
            Assert.That(_targeting.GhostVisible, Is.False);
            Assert.That(_targeting.FeedbackText, Does.StartWith("Aim at"));
        }

        // Points the camera straight down at column (x, z) from above its surface; returns the surface height.
        private int AimDown(int x, int z, float heightAboveSurface)
        {
            int surface = 60;
            while (surface > -40 && !_world.IsSolid(new BlockPos(x, surface, z)))
            {
                surface--;
            }

            _camera.transform.SetPositionAndRotation(new Vector3(x + 0.5f, surface + 1 + heightAboveSurface, z + 0.5f), Quaternion.Euler(90f, 0f, 0f));
            _targeting.Refresh();
            return surface;
        }
    }
}
#endif
