using UnityEngine;
using UnityEngine.InputSystem;
using VoxelRush.Core.Input;

namespace VoxelRush.Presentation.Input
{
    /// <summary>Samples the Driving action map once per frame into a <see cref="DrivingInput"/>; steering is ramped for digital buttons.</summary>
    [DisallowMultipleComponent]
    public sealed class DrivingInputReader : MonoBehaviour
    {
        private InputActionMap? _map;
        private InputAction _steer = null!;
        private InputAction _throttle = null!;
        private InputAction _brake = null!;
        private InputAction _boost = null!;
        private InputAction _handbrake = null!;
        private InputAction _buildMode = null!;
        private InputAction _look = null!;
        private SteeringFilter _steering = ControlSettings.Default.CreateSteeringFilter();

        public DrivingInput Current { get; private set; }

        /// <summary>Raw steering target before ramping (for HUD feedback and tests).</summary>
        public float RawSteer { get; private set; }

        public Vector2 Look { get; private set; }

        public bool BuildModePressedThisFrame { get; private set; }

        public void Apply(ControlSettings settings)
        {
            _steering = settings.CreateSteeringFilter();
        }

        private void Awake()
        {
            _map = DrivingControls.CreateMap();
            _steer = _map.FindAction(DrivingControls.Steer, true);
            _throttle = _map.FindAction(DrivingControls.Throttle, true);
            _brake = _map.FindAction(DrivingControls.Brake, true);
            _boost = _map.FindAction(DrivingControls.Boost, true);
            _handbrake = _map.FindAction(DrivingControls.Handbrake, true);
            _buildMode = _map.FindAction(DrivingControls.BuildMode, true);
            _look = _map.FindAction(DrivingControls.Look, true);
        }

        private void OnEnable() => _map?.Enable();

        private void OnDisable()
        {
            _map?.Disable();
            _steering.Reset();
            Current = DrivingInput.Neutral;
        }

        private void OnDestroy() => _map?.Dispose();

        private void Update()
        {
            RawSteer = _steer.ReadValue<float>();
            DrivingButtons buttons = (_boost.IsPressed() ? DrivingButtons.Boost : DrivingButtons.None)
                | (_handbrake.IsPressed() ? DrivingButtons.Handbrake : DrivingButtons.None);

            Current = new DrivingInput(
                _steering.Step(RawSteer, Time.unscaledDeltaTime),
                _throttle.ReadValue<float>(),
                _brake.ReadValue<float>(),
                buttons);
            Look = _look.ReadValue<Vector2>();
            BuildModePressedThisFrame = _buildMode.WasPressedThisFrame();
        }
    }
}
