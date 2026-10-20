using UnityEngine;
using VoxelRush.Core.Input;

namespace VoxelRush.Gameplay.Vehicles
{
    /// <summary>
    /// Raycast-suspension arcade car (VEHICLE_NETWORKING): four suspension rays, spring-damper, speed-scaled steering,
    /// lateral grip with handbrake drift, boost and airborne upright assist. Input arrives via <see cref="SetInput"/>
    /// so the authoritative server and client prediction drive the same code; visuals live elsewhere.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public sealed class ArcadeVehicle : MonoBehaviour
    {
        private const int FrontLeft = 0;
        private const int FrontRight = 1;
        private const int MaxRayHits = 8;

        [SerializeField] private VehicleTuning _tuning = new VehicleTuning();

        // Off when a network tick loop calls Simulate itself (server authority, client prediction/replay).
        [SerializeField] private bool _autoSimulate = true;

        // Order: front-left, front-right, rear-left, rear-right.
        [SerializeField] private Transform[] _wheels = new Transform[4];

        private readonly RaycastHit[] _hits = new RaycastHit[MaxRayHits];
        private readonly float[] _previousCompression = new float[4];
        private Rigidbody _body = null!;
        private BoostSystem _boost = null!;
        private FlipRecovery _flipRecovery = null!;
        private DrivingInput _input;
        private Vector3 _spawnPosition;
        private Quaternion _spawnRotation;

        public VehicleTuning Tuning => _tuning;

        public int GroundedWheels { get; private set; }

        public bool IsGrounded => GroundedWheels > 0;

        public float ForwardSpeed { get; private set; }

        public bool IsBoosting => _boost != null && _boost.IsBoosting;

        public float BoostEnergy => _boost != null ? _boost.Normalized : 0f;

        public int Resets { get; private set; }

        public bool AutoSimulate
        {
            get => _autoSimulate;
            set => _autoSimulate = value;
        }

        public Rigidbody Body => _body;

        public DrivingInput Input => _input;

        public void SetInput(DrivingInput input) => _input = input;

        public VehicleState CaptureState() =>
            new VehicleState(_body.position, _body.rotation, _body.linearVelocity, _body.angularVelocity, _boost.Capture());

        /// <summary>Restores a captured/authoritative state; suspension history is re-measured so replay starts calm.</summary>
        public bool RestoreState(VehicleState state)
        {
            if (!state.IsFinite)
            {
                return false;
            }

            _body.position = state.Position;
            _body.rotation = state.Rotation;
            transform.SetPositionAndRotation(state.Position, state.Rotation);
            _body.linearVelocity = state.LinearVelocity;
            _body.angularVelocity = state.AngularVelocity;
            _boost.Restore(state.Boost);
            Physics.SyncTransforms();
            RefreshSuspensionHistory();
            return true;
        }

        /// <summary>Places the car upright at a pose with zero velocity (spawn, flip recovery, server correction).</summary>
        public void ResetTo(Vector3 position, Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            Quaternion upright = forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward, Vector3.up) : Quaternion.identity;
            _body.position = position;
            _body.rotation = upright;
            transform.SetPositionAndRotation(position, upright);
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            RefreshSuspensionHistory();
        }

        private void Awake()
        {
            var issues = _tuning.Validate();
            if (issues.Count > 0)
            {
                Debug.LogError($"Invalid vehicle tuning on {name}:\n{string.Join("\n", issues)}", this);
                enabled = false;
                return;
            }

            if (_wheels == null || _wheels.Length != 4 || System.Array.Exists(_wheels, w => w == null))
            {
                Debug.LogError($"{name} needs exactly four wheel anchors (FL, FR, RL, RR).", this);
                enabled = false;
                return;
            }

            _body = GetComponent<Rigidbody>();
            _body.mass = _tuning.Mass;
            _body.centerOfMass = new Vector3(0f, _tuning.CenterOfMassHeight, 0f);
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _boost = new BoostSystem(_tuning.BoostEnergySeconds, _tuning.BoostRegenPerSecond, _tuning.BoostCooldownSeconds);
            _flipRecovery = new FlipRecovery(_tuning.FlipResetSeconds);
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;
        }

        private void FixedUpdate()
        {
            if (_autoSimulate)
            {
                Simulate(Time.fixedDeltaTime);
            }
        }

        /// <summary>Applies one step of vehicle forces; the caller then advances physics by <paramref name="dt"/>.</summary>
        public void Simulate(float dt)
        {
            if (!enabled || !(dt > 0))
            {
                return;
            }

            Transform t = transform;
            Vector3 up = t.up;
            Vector3 forward = t.forward;
            Vector3 velocity = _body.linearVelocity;
            ForwardSpeed = Vector3.Dot(velocity, forward);

            float steerAngle = VehicleMath.SteerAngleDegrees(_input.Steer, ForwardSpeed, _tuning);
            float massPerWheel = _tuning.Mass / 4f;
            float rayLength = _tuning.SuspensionRestLength + _tuning.WheelRadius;
            GroundedWheels = 0;

            for (int i = 0; i < 4; i++)
            {
                Vector3 origin = _wheels[i].position;
                if (!TryGround(origin, -up, rayLength, out RaycastHit hit))
                {
                    _previousCompression[i] = 0f;
                    continue;
                }

                GroundedWheels++;
                float compression = rayLength - hit.distance;
                float compressionVelocity = (compression - _previousCompression[i]) / dt;
                _previousCompression[i] = compression;
                _body.AddForceAtPosition(up * VehicleMath.SuspensionForce(compression, compressionVelocity, _tuning.SpringStrength, _tuning.SpringDamping), origin);

                // Tyre frame: front wheels turn with steering; forces act at the contact point.
                bool front = i == FrontLeft || i == FrontRight;
                Vector3 wheelForward = front ? Quaternion.AngleAxis(steerAngle, up) * forward : forward;
                Vector3 wheelRight = Vector3.Cross(hit.normal, wheelForward).normalized;
                float grip = front ? _tuning.FrontGrip : _input.Handbrake ? _tuning.HandbrakeRearGrip : _tuning.RearGrip;
                float lateralVelocity = Vector3.Dot(_body.GetPointVelocity(hit.point), wheelRight);
                _body.AddForceAtPosition(wheelRight * VehicleMath.LateralGripForce(lateralVelocity, grip, massPerWheel, _tuning.MaxGripAcceleration, dt), hit.point);
            }

            bool boosting = _boost.Step(_input.Boost && IsGrounded, dt);
            if (IsGrounded)
            {
                float share = GroundedWheels / 4f;
                float maxSpeed = boosting ? _tuning.BoostMaxSpeed : _tuning.MaxSpeed;
                float drive = VehicleMath.DriveForce(_input.Throttle, _input.Brake, ForwardSpeed, maxSpeed, _tuning);
                if (boosting && ForwardSpeed < _tuning.BoostMaxSpeed)
                {
                    drive += _tuning.BoostForce;
                }

                // Drive at the centre of mass's ground projection: no pitch torque from traction.
                Vector3 groundForward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
                _body.AddForce(groundForward * (drive * share));
                _body.AddForce(-up * (_tuning.Downforce * velocity.magnitude * _tuning.Mass * 0.1f));
            }
            else
            {
                // Airborne: PD torque toward world up keeps jumps landable.
                Vector3 correction = Vector3.Cross(up, Vector3.up);
                Vector3 angular = _body.angularVelocity;
                Vector3 pitchRoll = angular - Vector3.Project(angular, up);
                _body.AddTorque((correction * _tuning.AirUprightTorque - pitchRoll * _tuning.AirUprightDamping) * _tuning.Mass, ForceMode.Force);
            }

            if (_flipRecovery.Step(Vector3.Dot(up, Vector3.up), velocity.magnitude, dt))
            {
                Resets++;
                ResetTo(_body.position + Vector3.up * 1.5f, t.rotation);
            }
        }

        // Sets each wheel's previous compression to its current one, so the damper sees zero velocity next step.
        private void RefreshSuspensionHistory()
        {
            float rayLength = _tuning.SuspensionRestLength + _tuning.WheelRadius;
            for (int i = 0; i < 4; i++)
            {
                _previousCompression[i] = TryGround(_wheels[i].position, -transform.up, rayLength, out RaycastHit hit) ? rayLength - hit.distance : 0f;
            }
        }

        private bool TryGround(Vector3 origin, Vector3 direction, float length, out RaycastHit nearest)
        {
            int count = Physics.RaycastNonAlloc(origin, direction, _hits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            nearest = default;
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                // Skip the car's own colliders.
                if (_hits[i].rigidbody == _body || _hits[i].distance >= best)
                {
                    continue;
                }

                best = _hits[i].distance;
                nearest = _hits[i];
            }

            return best < float.MaxValue;
        }

        private void OnValidate()
        {
            if (_wheels == null || _wheels.Length != 4)
            {
                System.Array.Resize(ref _wheels, 4);
            }
        }
    }
}
