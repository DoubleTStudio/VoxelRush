using UnityEngine;
using VoxelRush.Gameplay.Vehicles;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Presentation.Input;

namespace VoxelRush.Presentation.World
{
    /// <summary>Third-person camera behind the player's own car (MVP-002); stays put while there is no car.</summary>
    [DisallowMultipleComponent]
    public sealed class ChaseCamera : MonoBehaviour
    {
        private const float SearchIntervalSeconds = 0.25f;

        [SerializeField] private float _distance = 8f;
        [SerializeField] private float _height = 3.5f;
        [SerializeField] private float _lookHeight = 1.2f;
        [SerializeField] private float _positionSharpness = 6f;
        [SerializeField] private float _yawSharpness = 4f;
        [SerializeField] private float _degreesPerScreenWidth = 270f;
        [SerializeField] private float _recenterDelaySeconds = 1.5f;
        [SerializeField] private float _recenterSharpness = 2.5f;
        [SerializeField] private float _minElevation = 5f;
        [SerializeField] private float _maxElevation = 75f;
        [SerializeField] private float _minZoom = 0.5f;
        [SerializeField] private float _maxZoom = 2.5f;

        private readonly CameraGestures _gestures = new CameraGestures();

        private Transform? _target;
        private bool _explicitTarget;
        private float _nextSearchAt;
        private Vector3 _heading = Vector3.forward;
        private float _yawOffset;
        private float _elevation = float.NaN;
        private float _zoom = 1f;
        private float _lastGestureAt = float.NegativeInfinity;

        public Transform? Target => _target;

        /// <summary>Player orbit around the car's heading, in degrees; eases back to 0 after a gesture.</summary>
        public float YawOffset => _yawOffset;

        public float Elevation => float.IsNaN(_elevation) ? BaseElevation : _elevation;

        public float ZoomScale => _zoom;

        private float BaseElevation => Mathf.Atan2(_height, _distance) * Mathf.Rad2Deg;

        /// <summary>Orbits by a gesture (degrees); the camera recenters behind the car a moment after the last one.</summary>
        public void Orbit(float yawDegrees, float elevationDegrees)
        {
            _yawOffset = Mathf.DeltaAngle(0f, _yawOffset + yawDegrees);
            _elevation = Mathf.Clamp(Elevation + elevationDegrees, _minElevation, _maxElevation);
            _lastGestureAt = Time.unscaledTime;
        }

        /// <summary>Pinch ratio above 1 (fingers spreading) moves the camera closer; zoom is kept after release.</summary>
        public void Zoom(float pinchRatio)
        {
            if (pinchRatio > 0f)
            {
                _zoom = Mathf.Clamp(_zoom / pinchRatio, _minZoom, _maxZoom);
            }
        }

        /// <summary>Follows this transform instead of searching for the owned car.</summary>
        public void SetTarget(Transform? target)
        {
            _target = target;
            _explicitTarget = target != null;
            if (target != null)
            {
                Snap();
            }
        }

        /// <summary>Moves the camera; LateUpdate calls this after the car moved, tests call it directly.</summary>
        public void Follow(float deltaTime)
        {
            if (_target == null && !_explicitTarget && Time.unscaledTime >= _nextSearchAt)
            {
                _nextSearchAt = Time.unscaledTime + SearchIntervalSeconds;
                _target = FindOwnCar();
                if (_target != null)
                {
                    Snap();
                }
            }

            if (_target == null)
            {
                return;
            }

            // Yaw only: pitch and roll from bumps and flips would shake the view.
            Vector3 forward = Vector3.ProjectOnPlane(_target.forward, Vector3.up);
            if (forward.sqrMagnitude > 1e-4f)
            {
                _heading = Vector3.Slerp(_heading, forward.normalized, Blend(_yawSharpness, deltaTime));
            }

            if (Time.unscaledTime - _lastGestureAt > _recenterDelaySeconds)
            {
                float t = Blend(_recenterSharpness, deltaTime);
                _yawOffset = Mathf.Lerp(_yawOffset, 0f, t);
                _elevation = Mathf.Lerp(Elevation, BaseElevation, t);
            }

            Vector3 desired = DesiredPosition();
            transform.position = Vector3.Lerp(transform.position, desired, Blend(_positionSharpness, deltaTime));
            transform.rotation = Quaternion.LookRotation(_target.position + Vector3.up * _lookHeight - transform.position, Vector3.up);
        }

        private void LateUpdate()
        {
            if (_target != null && _gestures.Read(out Vector2 drag, out float pinch))
            {
                float perPixel = _degreesPerScreenWidth / Mathf.Max(1, Screen.width);
                if (drag != Vector2.zero)
                {
                    // Drag right swings the camera left around the car, like turning your head right.
                    Orbit(drag.x * perPixel, -drag.y * perPixel);
                }

                Zoom(pinch);
            }

            Follow(Time.deltaTime);
        }

        private void Snap()
        {
            Vector3 forward = Vector3.ProjectOnPlane(_target!.forward, Vector3.up);
            _heading = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            transform.position = DesiredPosition();
            transform.rotation = Quaternion.LookRotation(_target.position + Vector3.up * _lookHeight - transform.position, Vector3.up);
        }

        private Vector3 DesiredPosition()
        {
            Vector3 heading = Quaternion.AngleAxis(_yawOffset, Vector3.up) * _heading;
            float radius = Mathf.Sqrt(_distance * _distance + _height * _height) * _zoom;
            float elevation = Elevation * Mathf.Deg2Rad;
            return _target!.position - heading * (Mathf.Cos(elevation) * radius) + Vector3.up * (Mathf.Sin(elevation) * radius);
        }

        // Frame-rate independent exponential smoothing.
        private static float Blend(float sharpness, float deltaTime) => 1f - Mathf.Exp(-sharpness * deltaTime);

        private static Transform? FindOwnCar()
        {
            if (LocalPlayer.Vehicle != null)
            {
                return LocalPlayer.Vehicle.transform;
            }

            // Offline scenes drive a plain local car; networked cars turn auto-simulation off.
            foreach (ArcadeVehicle vehicle in FindObjectsByType<ArcadeVehicle>(FindObjectsSortMode.None))
            {
                if (vehicle.AutoSimulate)
                {
                    return vehicle.transform;
                }
            }

            return null;
        }
    }
}
