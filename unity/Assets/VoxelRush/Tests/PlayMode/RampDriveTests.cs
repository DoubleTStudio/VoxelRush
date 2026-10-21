#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Core.Input;
using VoxelRush.Gameplay.Vehicles;
using VoxelRush.Gameplay.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>
    /// BUILD-002 with real physics: the ArcadeCar drives up placed ramps on the same mesh colliders the server and
    /// predicting clients build (<see cref="WorldColliders"/>, Burst mesher), in every yaw.
    /// </summary>
    public sealed class RampDriveTests
    {
        private const int RampAt = 12;
        private float _previousTimeScale;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousTimeScale = Time.timeScale;
            yield return TestScenes.UnloadAll();
            SceneManager.SetActiveScene(SceneManager.CreateScene("RampDrive"));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = _previousTimeScale;
            yield return TestScenes.UnloadAll();
        }

        [UnityTest]
        public IEnumerator RampCollider_SlopesUnderAWheelRay()
        {
            Build(out _, (BlockIds.RampLow, 0, 1));
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();

            // Plot ground top is y = 0; the low ramp at z 12..13 rises 0 → 0.5 toward +Z.
            Assert.That(Physics.Raycast(new Vector3(8.5f, 5f, RampAt + 0.25f), Vector3.down, out RaycastHit low, 10f), Is.True);
            Assert.That(Physics.Raycast(new Vector3(8.5f, 5f, RampAt + 0.75f), Vector3.down, out RaycastHit high, 10f), Is.True);
            Assert.That(low.point.y, Is.EqualTo(0.125f).Within(0.01f));
            Assert.That(high.point.y, Is.EqualTo(0.375f).Within(0.01f));
            Assert.That(Vector3.Angle(low.normal, new Vector3(0f, 2f, -1f)), Is.LessThan(1f), "slope faces up and back toward the car");
        }

        /// <summary>Low + high pieces: a two-block 1:2 ramp up onto a one-block platform.</summary>
        [UnityTest]
        public IEnumerator Car_DrivesUpALongRamp_OntoAPlatform([Values(0, 1, 2, 3)] int yaw)
        {
            yield return Drive(yaw, platform: true, (BlockIds.RampLow, 0, 1), (BlockIds.RampHigh, 1, 1));
        }

        /// <summary>The 3:4 kicker launches the car, which lands upright and drives on.</summary>
        [UnityTest]
        public IEnumerator Car_JumpsOffAKicker([Values(0, 1, 2, 3)] int yaw)
        {
            yield return Drive(yaw, platform: false, (BlockIds.Kicker, 0, 1));
        }

        // Ramps along the drive line (pieces: variant id, z offset from RampAt, height in blocks), turned by yaw about the
        // course centre, so every yaw is driven uphill with the car spawned on the matching approach.
        private static IEnumerator Drive(int yaw, bool platform, params (byte Variant, int Dz, int Height)[] pieces)
        {
            Time.timeScale = 3f;
            var blocks = new List<(int X, int Z, int Y, byte Id)>();
            int length = 0;
            foreach ((byte variant, int dz, int _) in pieces)
            {
                for (int x = 6; x <= 10; x++)
                {
                    blocks.Add((x, RampAt + dz, 0, BlockShapes.Rotate(variant, 0)));
                }

                length = Math.Max(length, dz + 1);
            }

            if (platform)
            {
                for (int x = 6; x <= 10; x++)
                {
                    for (int z = RampAt + length; z < RampAt + length + 8; z++)
                    {
                        blocks.Add((x, z, 0, BlockIds.Stone));
                    }
                }
            }

            // Turn the whole course about (8.5, 16.5) so yaw k climbs toward that yaw's uphill direction.
            var turned = new List<(int X, int Y, int Z, byte Id)>();
            foreach ((int x, int z, int y, byte id) in blocks)
            {
                (int tx, int tz) = Turn(x, z, yaw);
                turned.Add((tx, y, tz, BlockShapes.Rotate(id, yaw)));
            }

            WorldColliders colliders = BuildWorld(turned);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/VoxelRush/Prefabs/ArcadeCar.prefab");
            (float sx, float sz) = TurnF(8.5f, 2f, yaw);
            Quaternion heading = Quaternion.Euler(0f, 90f * yaw, 0f);
            var car = UnityEngine.Object.Instantiate(prefab, new Vector3(sx, 1.2f, sz), heading).GetComponent<ArcadeVehicle>();
            var probe = colliders.gameObject.AddComponent<DriveProbe>();
            probe.Init(car, heading * Vector3.forward, RampAt + length + 10);
            car.gameObject.AddComponent<BodyContacts>().Probe = probe;
            float deadline = Time.time + 12f;
            yield return new WaitUntil(() => probe.Stopped || Time.time > deadline);

            Debug.Log($"ramp drive yaw={yaw} platform={platform} maxY={probe.MaxY:0.00} progress={probe.Progress:0.0} minSpeedOnRamp={probe.MinSpeedOnRamp:0.0} entrySpeed={probe.EntrySpeed:0.0} bodyContacts={probe.BodyContacts} airborne={probe.AirborneSteps} resets={car.Resets}");
            Assert.That(car.Resets, Is.Zero, "no flip/stuck recovery");
            Assert.That(probe.Progress, Is.GreaterThan(RampAt + length + 6), "the car got past the ramp");
            Assert.That(probe.MaxY, Is.GreaterThan(1.3f), "the car body rose with the ramp");
            Assert.That(probe.BodyContacts, Is.Zero, "only the suspension rays touch the ramp, never the car body");
            // A 3:4 kicker kept no body contact but bled to under half the entry speed; 5:8 keeps ~80%.
            Assert.That(probe.MinSpeedOnRamp, Is.GreaterThan(0.7f * probe.EntrySpeed), "the ramp carries the car instead of braking it");
            Assert.That(Vector3.Dot(car.transform.up, Vector3.up), Is.GreaterThan(0.9f), "upright at the end");
            Assert.That(probe.Stopped, Is.True, "braked to a stop after the course");
            Assert.That(car.transform.position.y, Is.GreaterThan(0.5f), "resting on the ground, not inside it");
            if (!platform)
            {
                Assert.That(probe.AirborneSteps, Is.GreaterThan(5), "the kicker launched the car");
            }
        }

        private static (int X, int Z) Turn(int x, int z, int yaw)
        {
            // Quarter turns of the cell centre (x + 0.5, z + 0.5) about (8.5, 16.5): (dx, dz) → (dz, -dx).
            int dx = x - 8, dz = z - 16;
            for (int k = 0; k < yaw; k++)
            {
                (dx, dz) = (dz, -dx);
            }

            return (8 + dx, 16 + dz);
        }

        private static (float X, float Z) TurnF(float x, float z, int yaw)
        {
            float dx = x - 8.5f, dz = z - 16.5f;
            for (int k = 0; k < yaw; k++)
            {
                (dx, dz) = (dz, -dx);
            }

            return (8.5f + dx, 16.5f + dz);
        }

        private static WorldColliders BuildWorld(List<(int X, int Y, int Z, byte Id)> blocks)
        {
            var info = new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, new ChunkCoord(-1, -1, -1), new ChunkCoord(2, 0, 3));
            var gen = new FlatPlotGenerator();
            var chunks = new Dictionary<ChunkCoord, ChunkData>();
            foreach (ChunkCoord c in info.RegionChunks())
            {
                chunks[c] = new ChunkData(c, gen.GeneratorVersion, gen.GenerateBlocks(c));
            }

            foreach ((int x, int y, int z, byte id) in blocks)
            {
                var pos = new BlockPos(x, y, z);
                chunks[ChunkMath.ToChunk(pos)].Set(ChunkMath.ToLocal(pos), id);
            }

            var colliders = new GameObject("Colliders").AddComponent<WorldColliders>();
            colliders.Bind(() => info, c => chunks.TryGetValue(c, out ChunkData chunk) ? chunk : null);
            colliders.Refresh(all: true);
            return colliders;
        }

        private static void Build(out WorldColliders colliders, params (byte Id, int Dz, int Height)[] pieces)
        {
            var blocks = new List<(int X, int Y, int Z, byte Id)>();
            foreach ((byte id, int dz, int _) in pieces)
            {
                blocks.Add((8, 0, RampAt + dz, id));
            }

            colliders = BuildWorld(blocks);
        }

        private sealed class BodyContacts : MonoBehaviour
        {
            public DriveProbe? Probe { get; set; }

            // Speculative (CCD) contacts near the lip carry no impulse; only real pushes count.
            private void OnCollisionStay(Collision collision)
            {
                if (Probe != null && Probe.OnRamp && collision.impulse.sqrMagnitude > 0f)
                {
                    Probe.BodyContacts++;
                }
            }
        }

        /// <summary>Full throttle along the course, then brakes; records height, progress and speed over the ramp.</summary>
        private sealed class DriveProbe : MonoBehaviour
        {
            private ArcadeVehicle _car = null!;
            private Vector3 _start;
            private Vector3 _forward;
            private float _brakeAt;

            public bool Stopped { get; private set; }

            public float MaxY { get; private set; }

            public float Progress { get; private set; }

            public float MinSpeedOnRamp { get; private set; } = float.MaxValue;

            public int AirborneSteps { get; private set; }

            public float EntrySpeed { get; private set; }

            /// <summary>Physics steps with the car body touching the world while it crosses the ramp.</summary>
            public int BodyContacts { get; set; }

            // From the bumper reaching the ramp until the rear wheels have left it.
            public bool OnRamp => Progress > RampAt - 3 && Progress < RampAt + 2.5f;

            public void Init(ArcadeVehicle car, Vector3 forward, float brakeAt)
            {
                _car = car;
                _start = car.transform.position;
                _forward = forward;
                _brakeAt = brakeAt;
            }

            private void FixedUpdate()
            {
                if (_car == null)
                {
                    return;
                }

                Vector3 p = _car.transform.position;
                // Distance along the course from the spawn line at z = 2 (in the unturned frame).
                Progress = 2f + Vector3.Dot(p - _start, _forward);
                bool braking = Progress > _brakeAt;
                _car.SetInput(braking ? new DrivingInput(0f, 0f, 1f, DrivingButtons.None) : new DrivingInput(0f, 1f, 0f, DrivingButtons.None));
                Stopped |= braking && _car.IsGrounded && _car.Body.linearVelocity.magnitude < 0.5f;
                MaxY = Mathf.Max(MaxY, p.y);
                if (OnRamp)
                {
                    EntrySpeed = EntrySpeed > 0f ? EntrySpeed : _car.Body.linearVelocity.magnitude;
                    MinSpeedOnRamp = Mathf.Min(MinSpeedOnRamp, _car.Body.linearVelocity.magnitude);
                }

                if (Progress > RampAt && !_car.IsGrounded)
                {
                    AirborneSteps++;
                }
            }
        }
    }
}
#endif
