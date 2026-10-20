#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Core.Input;
using VoxelRush.Gameplay.Vehicles;
using VoxelRush.Gameplay.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Streaming;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>DES-012 DoD with real physics: a wall blown open just ahead of a car must not stop it (no stale collider).</summary>
    public sealed class DestructionColliderTests
    {
        private const int WallZ = 64;
        private const int StreamEvery = 15;

        private float _previousTimeScale;

        /// <summary>Flat ground (surface y = 1) with a 3-high wall across chunk column x 0..15 at z = 64..65.</summary>
        private sealed class WalledGround : IChunkSource
        {
            public ChunkData Load(ChunkCoord coord)
            {
                var blocks = new byte[ChunkMath.Volume];
                if (coord.Y == 0)
                {
                    for (int i = 0; i < ChunkMath.Size * ChunkMath.Size; i++)
                    {
                        blocks[i] = BlockIds.Stone;
                    }

                    if (coord.X == 0 && coord.Z == WallZ / ChunkMath.Size)
                    {
                        for (int x = 0; x < ChunkMath.Size; x++)
                        {
                            for (int y = 1; y <= 3; y++)
                            {
                                for (int z = 0; z < 2; z++)
                                {
                                    blocks[new LocalBlock(x, y, z).Index] = BlockIds.Brick;
                                }
                            }
                        }
                    }
                }

                return new ChunkData(coord, 1, blocks);
            }
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousTimeScale = Time.timeScale;
            yield return TestScenes.UnloadAll();
            SceneManager.SetActiveScene(SceneManager.CreateScene("DestructionCollider"));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = _previousTimeScale;
            yield return TestScenes.UnloadAll();
        }

        [UnityTest]
        public IEnumerator DoD_BlownOpenWall_CarDrivesThrough()
        {
            WallProbe probe = null!;
            yield return Drive(critical: true, p => probe = p);

            Assert.That(probe.Removed, Is.True, "wall was never removed");
            Assert.That(probe.Car.transform.position.z, Is.GreaterThan(WallZ + 10), "car stopped at a collider of removed blocks");
            Assert.That(probe.MinSpeedAfterRemoval, Is.GreaterThan(15f), "no impact with the opened wall");
            Assert.That(probe.Streamer.Streamer.CriticalRebuilds, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator Control_WithoutCriticalRebuild_CarHitsTheStaleWall()
        {
            WallProbe probe = null!;
            yield return Drive(critical: false, p => probe = p);

            Assert.That(probe.Removed, Is.True);
            Assert.That(probe.MinSpeedAfterRemoval, Is.LessThan(10f), "the throttled background refresh was too late: the car hit removed blocks");
        }

        private static IEnumerator Drive(bool critical, System.Action<WallProbe> expose)
        {
            Time.timeScale = 4f;
            var streamer = new GameObject("World").AddComponent<VoxelWorldStreamer>();
            streamer.Configure(new WalledGround(), minChunkY: 0, maxChunkY: 0, generateBudget: 8, colliderBudget: 4, autoStep: false);
            if (!critical)
            {
                streamer.Streamer.Settings.CriticalColliderBudget = 0;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/VoxelRush/Prefabs/ArcadeCar.prefab");
            var car = Object.Instantiate(prefab, new Vector3(8f, 2.5f, 8f), Quaternion.identity).GetComponent<ArcadeVehicle>();
            streamer.Register(car);
            var probe = streamer.gameObject.AddComponent<WallProbe>();
            probe.Init(streamer, car);
            expose(probe);
            yield return new WaitForSeconds(9f);
        }

        /// <summary>Streams every 15th physics step, guards every step; removes the wall ~5 m before the bumper reaches it.</summary>
        private sealed class WallProbe : MonoBehaviour
        {
            private int _step;

            public VoxelWorldStreamer Streamer { get; private set; } = null!;

            public ArcadeVehicle Car { get; private set; } = null!;

            public bool Removed { get; private set; }

            public float MinSpeedAfterRemoval { get; private set; } = float.MaxValue;

            public void Init(VoxelWorldStreamer streamer, ArcadeVehicle car)
            {
                Streamer = streamer;
                Car = car;
            }

            private void FixedUpdate()
            {
                if (Streamer == null)
                {
                    return;
                }

                if (_step++ % StreamEvery == 0)
                {
                    Streamer.StreamStep();
                }

                // Right after a streaming step, so the background refresh can't help for another 14 steps (~11 m).
                if (!Removed && Car.transform.position.z >= WallZ - 7.5f && _step % StreamEvery == 1)
                {
                    for (int x = 0; x < ChunkMath.Size; x++)
                    {
                        for (int y = 1; y <= 3; y++)
                        {
                            Streamer.SetBlock(new BlockPos(x, y, WallZ), BlockIds.Air);
                            Streamer.SetBlock(new BlockPos(x, y, WallZ + 1), BlockIds.Air);
                        }
                    }

                    Removed = true;
                }

                Streamer.GuardStep(Time.fixedDeltaTime);
                bool spawned = !Car.Body.isKinematic;
                Car.SetInput(spawned ? new DrivingInput(0f, 1f, 0f, DrivingButtons.None) : DrivingInput.Neutral);
                if (Removed && Car.transform.position.z < WallZ + 6)
                {
                    MinSpeedAfterRemoval = Mathf.Min(MinSpeedAfterRemoval, Car.Body.linearVelocity.magnitude);
                }
            }
        }
    }
}
#endif
