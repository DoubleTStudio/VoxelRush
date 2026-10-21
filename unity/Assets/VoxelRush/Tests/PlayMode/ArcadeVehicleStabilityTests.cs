#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Core.Input;
using VoxelRush.Gameplay.Vehicles;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>VR-011 DoD: the arcade car is stable over the static test track at the 30 Hz server tick and at 60 Hz.</summary>
    public sealed class ArcadeVehicleStabilityTests
    {
        private const string TrackScene = "Assets/VoxelRush/Scenes/TestTrack.unity";
        private const float TimeScale = 4f;
        private const float MaxAngularSpeed = 10f;

        private float _previousFixedStep;
        private float _previousTimeScale;
        private float _previousMaxDelta;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousFixedStep = Time.fixedDeltaTime;
            _previousTimeScale = Time.timeScale;
            _previousMaxDelta = Time.maximumDeltaTime;
            yield return TestScenes.UnloadAll();
            Debug.Log("vehicle_test_phase=load_scene");
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(TrackScene, new LoadSceneParameters(LoadSceneMode.Single));
            Debug.Log("vehicle_test_phase=scene_loaded");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.fixedDeltaTime = _previousFixedStep;
            Time.timeScale = _previousTimeScale;
            Time.maximumDeltaTime = _previousMaxDelta;
            yield return TestScenes.UnloadAll();
        }

        [UnityTest]
        public IEnumerator DrivesTrackStably_At30Hz() => DriveTrack(1f / 30f);

        [UnityTest]
        public IEnumerator DrivesTrackStably_At60Hz() => DriveTrack(1f / 60f);

        [UnityTest]
        public IEnumerator Handbrake_IncreasesYawRate_WithoutSpinningOut()
        {
            ConfigureTime(1f / 30f);
            var (car, probe) = FindCar();

            float withoutHandbrake = 0f;
            float withHandbrake = 0f;
            foreach (bool handbrake in new[] { false, true })
            {
                car.ResetTo(new Vector3(-120f, 1f, -60f), Quaternion.identity);
                car.SetInput(DrivingInput.Neutral);
                yield return Seconds(1.5f);
                car.SetInput(new DrivingInput(0f, 1f, 0f, DrivingButtons.None));
                yield return Seconds(3f);

                probe.BeginYawSample();
                car.SetInput(new DrivingInput(1f, 0.5f, 0f, handbrake ? DrivingButtons.Handbrake : DrivingButtons.None));
                yield return Seconds(1f);
                float yaw = probe.EndYawSample();
                if (handbrake)
                {
                    withHandbrake = yaw;
                }
                else
                {
                    withoutHandbrake = yaw;
                }
            }

            Assert.That(withoutHandbrake, Is.GreaterThan(0.2f), "car should turn without the handbrake");
            Assert.That(withHandbrake, Is.GreaterThan(withoutHandbrake * 1.15f), $"handbrake should rotate the car faster (without={withoutHandbrake:0.00}, with={withHandbrake:0.00} rad/s)");
            Assert.That(probe.MaxAngularSpeed, Is.LessThan(MaxAngularSpeed), "spin-out");
            Assert.That(car.Resets, Is.Zero, "car flipped");
            Assert.That(probe.SawNaN, Is.False);
        }

        /// <summary>Prediction relies on this: restore a captured state, replay the same inputs, reach the same place.</summary>
        [UnityTest]
        public IEnumerator CaptureRestoreReplay_ReproducesTrajectory()
        {
            ConfigureTime(1f / 30f);
            var (car, _) = FindCar();
            yield return Seconds(2f);

            SimulationMode previousMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            try
            {
                car.AutoSimulate = false;
                const float dt = 1f / 30f;
                var inputs = new DrivingInput[45];
                for (int i = 0; i < inputs.Length; i++)
                {
                    inputs[i] = new DrivingInput(Mathf.Sin(i * 0.2f), 1f, 0f, i > 30 ? DrivingButtons.Handbrake : DrivingButtons.None);
                }

                VehicleState start = car.CaptureState();
                VehicleState first = Run(car, inputs, dt);
                Assert.That(Vector3.Distance(first.Position, start.Position), Is.GreaterThan(3f), "car didn't move");

                Assert.That(car.RestoreState(start), Is.True);
                VehicleState second = Run(car, inputs, dt);

                Assert.That(Vector3.Distance(first.Position, second.Position), Is.LessThan(0.01f), "replay diverged in position");
                Assert.That(Quaternion.Angle(first.Rotation, second.Rotation), Is.LessThan(0.5f), "replay diverged in rotation");
                Assert.That((first.LinearVelocity - second.LinearVelocity).magnitude, Is.LessThan(0.01f), "replay diverged in velocity");
                Assert.That(car.RestoreState(new VehicleState(new Vector3(float.NaN, 0, 0), Quaternion.identity, Vector3.zero, Vector3.zero, default)), Is.False, "NaN state must be refused");
            }
            finally
            {
                car.AutoSimulate = true;
                Physics.simulationMode = previousMode;
            }
        }

        private static VehicleState Run(ArcadeVehicle car, DrivingInput[] inputs, float dt)
        {
            foreach (DrivingInput input in inputs)
            {
                car.SetInput(input);
                car.Simulate(dt);
                Physics.Simulate(dt);
            }

            return car.CaptureState();
        }

        private static IEnumerator DriveTrack(float fixedStep)
        {
            ConfigureTime(fixedStep);
            var (car, probe) = FindCar();
            VehicleTuning tuning = car.Tuning;

            // Settle: springs come to rest without jitter.
            Phase("settle", car);
            car.SetInput(DrivingInput.Neutral);
            yield return Seconds(3f);
            Assert.That(car.GroundedWheels, Is.EqualTo(4), "not resting on four wheels");
            Assert.That(Mathf.Abs(probe.Body.linearVelocity.y), Is.LessThan(0.05f), "suspension still bouncing at rest");
            Assert.That(Vector3.Dot(car.transform.up, Vector3.up), Is.GreaterThan(0.99f), "not level at rest");

            // Full throttle over speed bumps and the jump.
            Phase("throttle", car);
            car.SetInput(new DrivingInput(0f, 1f, 0f, DrivingButtons.None));
            yield return Seconds(10f);
            Assert.That(probe.SawAirborne, Is.True, "never left the ground: ramp not reached");
            Assert.That(car.transform.position.z, Is.GreaterThan(120f), "did not get past the ramp");
            Assert.That(probe.MaxHorizontalSpeed, Is.LessThanOrEqualTo(tuning.MaxSpeed + 1.5f), "exceeded top speed without boost");

            // Boost: faster than top speed but capped.
            Phase("boost", car);
            probe.ResetSpeedPeak();
            car.SetInput(new DrivingInput(0f, 1f, 0f, DrivingButtons.Boost));
            yield return Seconds(2.5f);
            Assert.That(probe.MaxHorizontalSpeed, Is.GreaterThan(tuning.MaxSpeed + 1f), "boost had no effect");
            Assert.That(probe.MaxHorizontalSpeed, Is.LessThanOrEqualTo(tuning.BoostMaxSpeed + 1.5f), "boost exceeded its cap");

            // Brake to a stop (holding brake at a standstill would reverse), then release and roll to rest.
            Phase("brake", car);
            car.SetInput(new DrivingInput(0f, 0f, 1f, DrivingButtons.None));
            float braking = 0f;
            while (car.ForwardSpeed > 1f && braking < 5f)
            {
                braking += Time.fixedDeltaTime;
                yield return new WaitForFixedUpdate();
            }

            Assert.That(car.ForwardSpeed, Is.LessThanOrEqualTo(1f), $"brakes did not stop the car within 5 s (speed {car.ForwardSpeed:0.0})");
            car.SetInput(DrivingInput.Neutral);
            yield return Seconds(3f);
            Assert.That(probe.HorizontalSpeed, Is.LessThan(0.5f), "did not come to rest after braking");

            Phase("done", car);
            Assert.That(probe.SawNaN, Is.False, "NaN in rigidbody state");
            Assert.That(probe.MinHeight, Is.GreaterThan(-0.2f), "sank into the track");
            Assert.That(probe.MaxAngularSpeed, Is.LessThan(MaxAngularSpeed), "unstable rotation");
            Assert.That(probe.MinUprightWhileGrounded, Is.GreaterThan(0.8f), "tilted badly while on four wheels");
            Assert.That(car.Resets, Is.Zero, "car flipped");
            Assert.That(car.GroundedWheels, Is.EqualTo(4), "did not end on four wheels");
        }

        private static void ConfigureTime(float fixedStep)
        {
            Time.fixedDeltaTime = fixedStep;
            Time.maximumDeltaTime = fixedStep * 8f;
            Time.timeScale = TimeScale;
        }

        private static (ArcadeVehicle Car, VehicleProbe Probe) FindCar()
        {
            var car = Object.FindAnyObjectByType<ArcadeVehicle>();
            Assert.That(car, Is.Not.Null, "TestTrack has no ArcadeVehicle");
            Assert.That(car!.enabled, Is.True, "ArcadeVehicle disabled itself (invalid tuning or wheels)");
            return (car, car.gameObject.AddComponent<VehicleProbe>());
        }

        private static WaitForSeconds Seconds(float simulated) => new WaitForSeconds(simulated);

        private static void Phase(string name, ArcadeVehicle car) =>
            Debug.Log($"vehicle_test_phase={name} t={Time.time:0.00} pos={car.transform.position} speed={car.ForwardSpeed:0.0} grounded={car.GroundedWheels}");

        /// <summary>Samples rigidbody state every physics step.</summary>
        private sealed class VehicleProbe : MonoBehaviour
        {
            private ArcadeVehicle _car = null!;
            private float _yawSum;
            private int _yawSamples;
            private bool _sampleYaw;

            public Rigidbody Body { get; private set; } = null!;

            public bool SawNaN { get; private set; }

            public bool SawAirborne { get; private set; }

            public float MinHeight { get; private set; } = float.MaxValue;

            public float MaxAngularSpeed { get; private set; }

            public float MaxHorizontalSpeed { get; private set; }

            public float MinUprightWhileGrounded { get; private set; } = 1f;

            public float HorizontalSpeed => new Vector2(Body.linearVelocity.x, Body.linearVelocity.z).magnitude;

            public void ResetSpeedPeak() => MaxHorizontalSpeed = 0f;

            public void BeginYawSample()
            {
                _yawSum = 0f;
                _yawSamples = 0;
                _sampleYaw = true;
            }

            public float EndYawSample()
            {
                _sampleYaw = false;
                return _yawSamples > 0 ? _yawSum / _yawSamples : 0f;
            }

            private void Awake()
            {
                Body = GetComponent<Rigidbody>();
                _car = GetComponent<ArcadeVehicle>();
            }

            private void FixedUpdate()
            {
                Vector3 p = Body.position;
                Vector3 v = Body.linearVelocity;
                Vector3 w = Body.angularVelocity;
                if (float.IsNaN(p.x + p.y + p.z + v.x + v.y + v.z + w.x + w.y + w.z))
                {
                    SawNaN = true;
                    return;
                }

                MinHeight = Mathf.Min(MinHeight, p.y);
                MaxAngularSpeed = Mathf.Max(MaxAngularSpeed, w.magnitude);
                MaxHorizontalSpeed = Mathf.Max(MaxHorizontalSpeed, HorizontalSpeed);
                SawAirborne |= !_car.IsGrounded;
                if (_car.GroundedWheels == 4)
                {
                    MinUprightWhileGrounded = Mathf.Min(MinUprightWhileGrounded, Vector3.Dot(transform.up, Vector3.up));
                }

                if (_sampleYaw)
                {
                    _yawSum += Mathf.Abs(w.y);
                    _yawSamples++;
                }
            }
        }
    }
}
#endif
