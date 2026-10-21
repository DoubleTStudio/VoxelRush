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
    /// <summary>VR-023 DoD with real physics: a car at full throttle never enters a chunk whose collider isn't ready.</summary>
    public sealed class VoxelStreamingDriveTests
    {
        private float _previousTimeScale;

        private sealed class FlatGround : IChunkSource
        {
            public ChunkData Load(ChunkCoord coord)
            {
                var blocks = new byte[ChunkMath.Volume];
                if (coord.Y == 0)
                {
                    // One solid layer: surface at y = 1.
                    for (int i = 0; i < ChunkMath.Size * ChunkMath.Size; i++)
                    {
                        blocks[i] = BlockIds.Stone;
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
            SceneManager.SetActiveScene(SceneManager.CreateScene("VoxelStreamingDrive"));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = _previousTimeScale;
            yield return TestScenes.UnloadAll();
        }

        [UnityTest]
        public IEnumerator GenerousBudget_KeepsAheadOfTheCar() => Drive(generateBudget: 8, colliderBudget: 4, stepEvery: 1, expectHeld: false);

        [UnityTest]
        public IEnumerator StarvedBudget_HoldsTheCar_NeverEntersUnreadyChunk() => Drive(generateBudget: 1, colliderBudget: 1, stepEvery: 15, expectHeld: true);

        private static IEnumerator Drive(int generateBudget, int colliderBudget, int stepEvery, bool expectHeld)
        {
            Time.timeScale = 4f;
            var streamer = new GameObject("World").AddComponent<VoxelWorldStreamer>();
            streamer.Configure(new FlatGround(), minChunkY: 0, maxChunkY: 0, generateBudget, colliderBudget, autoStep: false);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/VoxelRush/Prefabs/ArcadeCar.prefab");
            var car = Object.Instantiate(prefab, new Vector3(8f, 2.5f, 8f), Quaternion.identity).GetComponent<ArcadeVehicle>();
            streamer.Register(car);
            Assert.That(car.Body.isKinematic, Is.True, "spawn must wait for its column");

            var probe = streamer.gameObject.AddComponent<StreamingProbe>();
            probe.Init(streamer, car, stepEvery);
            yield return new WaitForSeconds(12f);

            Assert.That(probe.Violation, Is.Null);
            Assert.That(probe.Spawned, Is.True, "car was never released");
            Assert.That(car.transform.position.y, Is.GreaterThan(0.5f), "fell through the voxel ground");
            Assert.That(car.Resets, Is.Zero);
            if (expectHeld)
            {
                Assert.That(streamer.GuardClamps, Is.GreaterThan(0), "starved streaming must hold the car");
            }
            else
            {
                Assert.That(streamer.GuardClamps, Is.Zero, "generous streaming shouldn't need to hold the car");
                Assert.That(probe.PathLength, Is.GreaterThan(150f), "car should have driven freely");
            }
        }

        /// <summary>Streams every Nth physics step, guards every step, drives the car and checks readiness every step.</summary>
        private sealed class StreamingProbe : MonoBehaviour
        {
            private VoxelWorldStreamer _streamer = null!;
            private ArcadeVehicle _car = null!;
            private int _stepEvery;
            private int _step;

            public string? Violation { get; private set; }

            public bool Spawned { get; private set; }

            public float PathLength { get; private set; }

            public void Init(VoxelWorldStreamer streamer, ArcadeVehicle car, int stepEvery)
            {
                _streamer = streamer;
                _car = car;
                _stepEvery = stepEvery;
            }

            private void FixedUpdate()
            {
                if (_streamer == null)
                {
                    return;
                }

                // Streaming is throttled; the guard runs every physics step, as in production under load.
                if (_step++ % _stepEvery == 0)
                {
                    _streamer.StreamStep();
                }

                _streamer.GuardStep(Time.fixedDeltaTime);

                Spawned |= !_car.Body.isKinematic;
                // Near-straight weave: keeps heading into new chunks (a wide weave just circles inside loaded ones).
                float steer = Mathf.Sin(_step * 0.01f) * 0.05f;
                _car.SetInput(Spawned ? new DrivingInput(steer, 1f, 0f, DrivingButtons.None) : DrivingInput.Neutral);
                Vector3 p = _car.transform.position;
                Vector3 v = _car.Body.linearVelocity;
                PathLength += new Vector2(v.x, v.z).magnitude * Time.fixedDeltaTime;
                if (Spawned && Violation == null && !_streamer.IsReadyAt(p))
                {
                    Violation = $"step {_step}: car at ({p.x:0.0},{p.z:0.0}) is in an unready chunk column";
                }
            }
        }
    }
}
#endif
